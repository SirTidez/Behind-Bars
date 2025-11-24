using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using UnityEngine;
using MelonLoader;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Tracks jail sentences using game time events instead of real-time
    /// Decrements sentences when game time passes and triggers release when complete
    /// </summary>
    public class JailTimeTracker
    {
        private static JailTimeTracker? _instance;
        public static JailTimeTracker Instance => _instance ??= new JailTimeTracker();

        /// <summary>
        /// Represents an active jail sentence being tracked
        /// </summary>
        private class ActiveSentence
        {
            public Player Player { get; set; }
            public float RemainingGameMinutes { get; set; }
            public float TotalGameMinutes { get; set; }
            public System.Action<Player>? OnComplete { get; set; }
        }

        /// <summary>
        /// Represents a completed jail sentence (for tracking after completion)
        /// </summary>
        private class CompletedSentence
        {
            public float OriginalSentenceTime { get; set; }
            public float TimeServed { get; set; }
        }

        private Dictionary<Player, ActiveSentence> _activeSentences = new();
        private Dictionary<Player, CompletedSentence> _completedSentences = new(); // Store sentence data for completed/stopped sentences
        private HashSet<Player> _inJailStatus = new(); // Track if player is actively in jail (separate from sentence tracking)
        private bool _isSubscribed = false;
        
        // Real-time tracking fallback (in case game time events don't fire)
        private Dictionary<Player, float> _sentenceStartTimes = new(); // Real-time when sentence started
        private object? _realTimeUpdateCoroutine = null;

        private JailTimeTracker()
        {
            SubscribeToGameTimeEvents();
            StartRealTimeTracking(); // Start real-time fallback tracking
        }

        /// <summary>
        /// Subscribe to game time events
        /// </summary>
        private void SubscribeToGameTimeEvents()
        {
            if (_isSubscribed)
            {
                return;
            }

            var gameTimeManager = GameTimeManager.Instance;
            gameTimeManager.OnGameMinuteChanged += OnGameMinuteChanged;
            _isSubscribed = true;
            ModLogger.Debug("JailTimeTracker subscribed to game time events");
        }

        /// <summary>
        /// Unsubscribe from game time events
        /// </summary>
        private void UnsubscribeFromGameTimeEvents()
        {
            if (!_isSubscribed)
            {
                return;
            }

            var gameTimeManager = GameTimeManager.Instance;
            gameTimeManager.OnGameMinuteChanged -= OnGameMinuteChanged;
            _isSubscribed = false;
            ModLogger.Info("JailTimeTracker unsubscribed from game time events");
        }

        /// <summary>
        /// Called when a game minute passes
        /// </summary>
        private void OnGameMinuteChanged(int gameMinute)
        {
            // Decrement all active sentences by 1 game minute
            var completedSentences = new List<Player>();

            foreach (var sentence in _activeSentences.Values)
            {
                sentence.RemainingGameMinutes -= 1f;

                if (sentence.RemainingGameMinutes <= 0f)
                {
                    completedSentences.Add(sentence.Player);
                }
            }

            // Trigger completion callbacks
            foreach (var player in completedSentences)
            {
                if (_activeSentences.TryGetValue(player, out var sentence))
                {
                    ModLogger.Info($"Jail sentence completed for {player.name} ({sentence.TotalGameMinutes} game minutes served)");
                    
                    // Store the original sentence time and time served before removing from active tracking
                    _completedSentences[player] = new CompletedSentence
                    {
                        OriginalSentenceTime = sentence.TotalGameMinutes,
                        TimeServed = sentence.TotalGameMinutes // Full sentence served
                    };
                    
                    sentence.OnComplete?.Invoke(player);
                    _activeSentences.Remove(player);
                    _sentenceStartTimes.Remove(player); // Clean up real-time tracking
                }
            }
        }
        
        /// <summary>
        /// Start real-time tracking coroutine as a fallback
        /// This ensures time is tracked even if game time events don't fire
        /// </summary>
        private void StartRealTimeTracking()
        {
            if (_realTimeUpdateCoroutine != null)
            {
                return; // Already started
            }
            
            _realTimeUpdateCoroutine = MelonCoroutines.Start(RealTimeUpdateLoop());
            ModLogger.Debug("JailTimeTracker real-time tracking fallback started");
        }
        
        /// <summary>
        /// Real-time update loop that decrements sentences every real second
        /// This is a fallback in case game time events don't fire correctly
        /// 1 real second = 1 game minute
        /// </summary>
        private IEnumerator RealTimeUpdateLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(1f); // Update every real second
                
                // Update all active sentences using real-time tracking
                var completedSentences = new List<Player>();
                float currentTime = Time.time;
                
                foreach (var kvp in _activeSentences.ToList())
                {
                    Player player = kvp.Key;
                    ActiveSentence sentence = kvp.Value;
                    
                    // Calculate elapsed real-time since sentence started
                    if (_sentenceStartTimes.TryGetValue(player, out float startTime))
                    {
                        float elapsedRealSeconds = currentTime - startTime;
                        float elapsedGameMinutes = elapsedRealSeconds; // 1 real second = 1 game minute
                        
                        // Update remaining time based on elapsed time
                        float expectedRemaining = sentence.TotalGameMinutes - elapsedGameMinutes;
                        
                        // Only update if the real-time calculation shows less remaining time
                        // This prevents time from going backwards if game time events are also firing
                        if (expectedRemaining < sentence.RemainingGameMinutes)
                        {
                            sentence.RemainingGameMinutes = Mathf.Max(0f, expectedRemaining);
                            ModLogger.Debug($"[JAIL TRACKING] Real-time update for {player.name}: {sentence.RemainingGameMinutes:F1} game minutes remaining (elapsed: {elapsedGameMinutes:F1} game minutes)");
                        }
                    }
                    else
                    {
                        // No start time recorded - use game time event decrement only
                        // This shouldn't happen, but handle gracefully
                        ModLogger.Warn($"[JAIL TRACKING] No start time recorded for {player.name} - using game time events only");
                    }
                    
                    if (sentence.RemainingGameMinutes <= 0f)
                    {
                        completedSentences.Add(player);
                    }
                }
                
                // Trigger completion callbacks for sentences that completed via real-time tracking
                foreach (var player in completedSentences)
                {
                    if (_activeSentences.TryGetValue(player, out var sentence))
                    {
                        ModLogger.Info($"Jail sentence completed (real-time tracking) for {player.name} ({sentence.TotalGameMinutes} game minutes served)");
                        
                        // Store the original sentence time and time served before removing from active tracking
                        _completedSentences[player] = new CompletedSentence
                        {
                            OriginalSentenceTime = sentence.TotalGameMinutes,
                            TimeServed = sentence.TotalGameMinutes // Full sentence served
                        };
                        
                        sentence.OnComplete?.Invoke(player);
                        _activeSentences.Remove(player);
                        _sentenceStartTimes.Remove(player);
                    }
                }
            }
        }

        /// <summary>
        /// Start tracking a jail sentence for a player
        /// </summary>
        /// <param name="player">The player serving the sentence</param>
        /// <param name="sentenceGameMinutes">Sentence duration in game minutes</param>
        /// <param name="onComplete">Callback when sentence is complete</param>
        public void StartTracking(Player player, float sentenceGameMinutes, System.Action<Player>? onComplete = null)
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot track sentence for null player");
                return;
            }

            // Remove any existing sentence for this player
            if (_activeSentences.ContainsKey(player))
            {
                ModLogger.Warn($"Replacing existing sentence for {player.name}");
                _activeSentences.Remove(player);
            }

            var sentence = new ActiveSentence
            {
                Player = player,
                RemainingGameMinutes = sentenceGameMinutes,
                TotalGameMinutes = sentenceGameMinutes,
                OnComplete = onComplete
            };

            _activeSentences[player] = sentence;
            _sentenceStartTimes[player] = Time.time; // Record start time for real-time tracking
            ModLogger.Info($"Started tracking jail sentence for {player.name}: {sentenceGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentenceGameMinutes)})");
        }

        /// <summary>
        /// Stop tracking a sentence for a player (e.g., early release)
        /// </summary>
        public void StopTracking(Player player)
        {
            if (_activeSentences.TryGetValue(player, out var sentence))
            {
                // Calculate actual time served before storing
                float timeServed = sentence.TotalGameMinutes - sentence.RemainingGameMinutes;
                
                // Log detailed information for debugging
                ModLogger.Debug($"[JAIL TRACKING] StopTracking called for {player.name}:");
                ModLogger.Debug($"  Total sentence: {sentence.TotalGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentence.TotalGameMinutes)})");
                ModLogger.Debug($"  Remaining: {sentence.RemainingGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentence.RemainingGameMinutes)})");
                ModLogger.Debug($"  Time served: {timeServed} game minutes ({GameTimeManager.FormatGameTime(timeServed)})");
                
                // Store both original sentence time and time served for early releases
                _completedSentences[player] = new CompletedSentence
                {
                    OriginalSentenceTime = sentence.TotalGameMinutes,
                    TimeServed = timeServed
                };
                
                _activeSentences.Remove(player);
                _sentenceStartTimes.Remove(player); // Clean up real-time tracking
                ModLogger.Info($"Stopped tracking jail sentence for {player.name} - served {timeServed:F1} of {sentence.TotalGameMinutes:F1} game minutes ({GameTimeManager.FormatGameTime(timeServed)} / {GameTimeManager.FormatGameTime(sentence.TotalGameMinutes)})");
            }
            else
            {
                ModLogger.Warn($"StopTracking called for {player.name} but no active sentence found");
            }
        }

        /// <summary>
        /// Get the original sentence time for a player (in game minutes)
        /// Checks both active and completed sentences
        /// </summary>
        public float GetOriginalSentenceTime(Player player)
        {
            // First check active sentences
            if (_activeSentences.TryGetValue(player, out var sentence))
            {
                return sentence.TotalGameMinutes;
            }
            
            // Then check completed sentences
            if (_completedSentences.TryGetValue(player, out var completed))
            {
                return completed.OriginalSentenceTime;
            }
            
            return 0f;
        }

        /// <summary>
        /// Get the actual time served for a player (in game minutes)
        /// This is the original sentence minus remaining time
        /// For completed sentences, returns the original sentence time
        /// For early releases, returns the actual time served
        /// Uses real-time tracking as fallback if game time events aren't working
        /// </summary>
        public float GetTimeServed(Player player)
        {
            // Check active sentences first
            if (_activeSentences.TryGetValue(player, out var sentence))
            {
                // Try to calculate from real-time tracking first (more reliable)
                float timeServed = 0f;
                if (_sentenceStartTimes.TryGetValue(player, out float startTime))
                {
                    float elapsedRealSeconds = Time.time - startTime;
                    float elapsedGameMinutes = elapsedRealSeconds; // 1 real second = 1 game minute
                    timeServed = Mathf.Min(elapsedGameMinutes, sentence.TotalGameMinutes);
                    ModLogger.Debug($"[JAIL TRACKING] GetTimeServed (active, real-time) for {player.name}: {timeServed:F1} game minutes ({GameTimeManager.FormatGameTime(timeServed)}) - elapsed: {elapsedRealSeconds:F1} real seconds");
                }
                else
                {
                    // Fallback to game time event calculation
                    timeServed = sentence.TotalGameMinutes - sentence.RemainingGameMinutes;
                    ModLogger.Debug($"[JAIL TRACKING] GetTimeServed (active, game-time) for {player.name}: {timeServed:F1} game minutes ({GameTimeManager.FormatGameTime(timeServed)})");
                }
                return timeServed;
            }
            
            // Check if this was a completed or stopped sentence
            if (_completedSentences.TryGetValue(player, out var completed))
            {
                ModLogger.Debug($"[JAIL TRACKING] GetTimeServed (completed) for {player.name}: {completed.TimeServed:F1} game minutes ({GameTimeManager.FormatGameTime(completed.TimeServed)})");
                return completed.TimeServed;
            }
            
            ModLogger.Warn($"[JAIL TRACKING] GetTimeServed for {player.name}: No tracking data found, returning 0");
            return 0f;
        }

        /// <summary>
        /// Clear completed sentence record for a player (called after release summary is shown)
        /// </summary>
        public void ClearCompletedSentence(Player player)
        {
            _completedSentences.Remove(player);
        }

        /// <summary>
        /// Get remaining sentence time for a player in game minutes
        /// </summary>
        public float GetRemainingTime(Player player)
        {
            if (_activeSentences.TryGetValue(player, out var sentence))
            {
                return Mathf.Max(0f, sentence.RemainingGameMinutes);
            }
            return 0f;
        }

        /// <summary>
        /// Get formatted remaining time string
        /// </summary>
        public string GetFormattedRemainingTime(Player player)
        {
            float remaining = GetRemainingTime(player);
            return GameTimeManager.FormatGameTime(remaining);
        }

        /// <summary>
        /// Check if a player has an active sentence being tracked
        /// </summary>
        public bool IsTracking(Player player)
        {
            return _activeSentences.ContainsKey(player);
        }

        /// <summary>
        /// Get all active sentences
        /// </summary>
        public int GetActiveSentenceCount()
        {
            return _activeSentences.Count;
        }

        #region Jail Status Tracking

        /// <summary>
        /// Mark a player as being in jail
        /// Called immediately when arrest begins, before sentence tracking starts
        /// </summary>
        public void SetInJail(Player player)
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot set jail status for null player");
                return;
            }

            if (!_inJailStatus.Contains(player))
            {
                _inJailStatus.Add(player);
                ModLogger.Info($"Marked player {player.name} as in jail");
            }
        }

        /// <summary>
        /// Clear jail status for a player
        /// Called when player is released from jail
        /// </summary>
        public void ClearInJail(Player player)
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot clear jail status for null player");
                return;
            }

            if (_inJailStatus.Remove(player))
            {
                ModLogger.Info($"Cleared jail status for player {player.name}");
            }
        }

        /// <summary>
        /// Check if a player is actively in jail
        /// This is separate from sentence tracking - tracks jail status from arrest to release
        /// </summary>
        public bool IsInJail(Player player)
        {
            if (player == null)
            {
                return false;
            }

            return _inJailStatus.Contains(player);
        }

        #endregion
    }
}

