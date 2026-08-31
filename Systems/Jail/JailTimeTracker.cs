using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using UnityEngine;
using UnityEngine.Events;
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

        /// <summary>
        /// Gets the process-lifetime sentence tracker. Scene-owned subscriptions and
        /// player references are still established and cleared per gameplay session.
        /// </summary>
        public static JailTimeTracker Instance => _instance ??= new JailTimeTracker();

        /// <summary>
        /// Represents an active jail sentence being tracked
        /// </summary>
        private class ActiveSentence
        {
            public string PlayerKey { get; set; }
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

        // Active and completed records are keyed by the stable player runtime key. An
        // active record owns the callback/player reference until completion or stop;
        // completed records retain only the summary needed by release reporting.
        private Dictionary<string, ActiveSentence> _activeSentences = new();
        private Dictionary<string, CompletedSentence> _completedSentences = new(); // Store sentence data for completed/stopped sentences
        private HashSet<string> _inJailStatus = new(); // Track if player is actively in jail (separate from sentence tracking)

        // These flags make event subscription idempotent across repeated initialization
        // attempts. They are reset only by the matching unsubscribe/session teardown.
        private bool _isSubscribed = false;
        private bool _isSubscribedToReleaseCompleted = false;
        private bool _sceneTrackingActive;
        private int _trackingGeneration;
        private Player? _arrestSubscribedPlayer = null;
        private object? _playerArrestSubscriptionCoroutine = null;

        // Real-time tracking fallback (in case game time events don't fire). This is
        // sentence accounting state and intentionally remains distinct from the jail
        // recreation UI's wall-clock countdown conversion.
        private Dictionary<string, float> _sentenceStartTimes = new(); // Real-time when sentence started
        private object? _realTimeUpdateCoroutine = null;

        // Performance: Pool list to avoid allocation every update
        private readonly List<string> _completedSentencesPool = new();
#if !MONO
        private readonly Action _playerArrestedListener;
#else
        private readonly System.Action _playerArrestedListener;
#endif

        private JailTimeTracker()
        {
#if !MONO
            _playerArrestedListener = new Action(OnPlayerArrested);
#else
            _playerArrestedListener = new System.Action(OnPlayerArrested);
#endif
        }

        /// <summary>
        /// Starts the listeners and fallback timer that belong to the current Main-scene
        /// jail session. The tracker itself is process-lifetime, but it must never retain
        /// a player or invoke custody callbacks after that player's scene is unloaded.
        /// </summary>
        public void BeginGameplaySession()
        {
            if (_sceneTrackingActive)
            {
                EnsurePlayerArrestSubscription();
                return;
            }

            _sceneTrackingActive = true;
            _trackingGeneration++;
            SubscribeToGameTimeEvents();
            SubscribeToArrestReleaseEvents();
            StartRealTimeTracking();
            ModLogger.Debug($"JailTimeTracker started gameplay session {_trackingGeneration}");
        }

        /// <summary>
        /// Drops all scene-owned sentence state and detaches the callbacks that can hold
        /// destroyed player/native scene objects alive after Main transitions to Menu.
        /// Sentence completion is deliberately not raised during this cancellation.
        /// </summary>
        public void EndGameplaySession()
        {
            bool hadSceneState = _sceneTrackingActive || _activeSentences.Count > 0 ||
                                 _inJailStatus.Count > 0 || _realTimeUpdateCoroutine != null ||
                                 _playerArrestSubscriptionCoroutine != null || _isSubscribed ||
                                 _isSubscribedToReleaseCompleted;

            _sceneTrackingActive = false;
            _trackingGeneration++;
            StopRealTimeTracking();
            UnsubscribeFromArrestReleaseEvents();
            UnsubscribeFromGameTimeEvents();

            _activeSentences.Clear();
            _completedSentences.Clear();
            _inJailStatus.Clear();
            _sentenceStartTimes.Clear();
            _completedSentencesPool.Clear();

            if (hadSceneState)
            {
                ModLogger.Info("JailTimeTracker cleared sentence and custody callbacks for Main-scene exit");
            }
        }
        
        #region Arrest/Release Event Subscriptions
        
        /// <summary>
        /// Subscribe to arrest and release events for jail status tracking.
        /// Subscribes to ReleaseManager.OnReleaseCompleted immediately,
        /// and starts a coroutine to subscribe to Player.local.onArrested when available.
        /// </summary>
        private void SubscribeToArrestReleaseEvents()
        {
            SubscribeToReleaseCompletedEvent();
            EnsurePlayerArrestSubscription();
        }

        /// <summary>
        /// Subscribe to ReleaseManager.OnReleaseCompleted once for this tracker instance.
        /// </summary>
        private void SubscribeToReleaseCompletedEvent()
        {
            if (_isSubscribedToReleaseCompleted)
            {
                return;
            }

            // Guard against duplicate subscription if initialization is retried.
            ReleaseManager.OnReleaseCompleted -= OnPlayerReleased;
            ReleaseManager.OnReleaseCompleted += OnPlayerReleased;
            ModLogger.Debug("JailTimeTracker subscribed to ReleaseManager.OnReleaseCompleted");

            _isSubscribedToReleaseCompleted = true;
        }

        /// <summary>
        /// Unsubscribe from ReleaseManager.OnReleaseCompleted.
        /// </summary>
        private void UnsubscribeFromReleaseCompletedEvent()
        {
            if (!_isSubscribedToReleaseCompleted)
            {
                return;
            }

            ReleaseManager.OnReleaseCompleted -= OnPlayerReleased;
            _isSubscribedToReleaseCompleted = false;
            ModLogger.Debug("JailTimeTracker unsubscribed from ReleaseManager.OnReleaseCompleted");
        }

        /// <summary>
        /// Ensure the tracker is attached to the current Player.Local onArrested event.
        /// Starts the retry coroutine only when no valid player is currently available.
        /// </summary>
        private void EnsurePlayerArrestSubscription()
        {
            if (TryAttachPlayerArrestListener(GetLocalPlayer()))
            {
                StopWaitingForPlayerArrestSubscription();
                return;
            }

            StartWaitingForPlayerArrestSubscription();
        }

        /// <summary>
        /// Start retrying until Player.Local becomes available.
        /// </summary>
        private void StartWaitingForPlayerArrestSubscription()
        {
            if (_playerArrestSubscriptionCoroutine != null)
            {
                return;
            }

            _playerArrestSubscriptionCoroutine = MelonCoroutines.Start(WaitForPlayerAndSubscribe());
        }

        /// <summary>
        /// Stop the retry coroutine if it is active.
        /// </summary>
        private void StopWaitingForPlayerArrestSubscription()
        {
            if (_playerArrestSubscriptionCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(_playerArrestSubscriptionCoroutine);
            _playerArrestSubscriptionCoroutine = null;
        }

        /// <summary>
        /// Attach the stable arrest listener delegate to the provided local player.
        /// Rebinds cleanly if Player.Local has changed.
        /// </summary>
        private bool TryAttachPlayerArrestListener(Player? localPlayer)
        {
            if (!IsValidLocalPlayer(localPlayer))
            {
                return false;
            }

            if (ReferenceEquals(_arrestSubscribedPlayer, localPlayer))
            {
                return true;
            }

            DetachPlayerArrestListener();

            try
            {
#if !MONO
                localPlayer.remove_onArrested(_playerArrestedListener);
                localPlayer.add_onArrested(_playerArrestedListener);
#else
                localPlayer.onArrested -= _playerArrestedListener;
                localPlayer.onArrested += _playerArrestedListener;
#endif
                _arrestSubscribedPlayer = localPlayer;
                ModLogger.Info($"JailTimeTracker subscribed to Player.local.onArrested for {localPlayer.name}");
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"JailTimeTracker: Failed to subscribe to Player.local.onArrested: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Detach the stable arrest listener delegate from the currently subscribed player.
        /// </summary>
        private void DetachPlayerArrestListener()
        {
            if (!IsValidLocalPlayer(_arrestSubscribedPlayer))
            {
                _arrestSubscribedPlayer = null;
                return;
            }

            try
            {
#if !MONO
                _arrestSubscribedPlayer.remove_onArrested(_playerArrestedListener);
#else
                _arrestSubscribedPlayer.onArrested -= _playerArrestedListener;
#endif
                ModLogger.Debug($"JailTimeTracker unsubscribed from Player.local.onArrested for {_arrestSubscribedPlayer.name}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"JailTimeTracker: Failed to unsubscribe from Player.local.onArrested: {ex.Message}");
            }
            finally
            {
                _arrestSubscribedPlayer = null;
            }
        }

        /// <summary>
        /// Unsubscribe all arrest/release listeners and stop any retry coroutine.
        /// </summary>
        private void UnsubscribeFromArrestReleaseEvents()
        {
            StopWaitingForPlayerArrestSubscription();
            DetachPlayerArrestListener();
            UnsubscribeFromReleaseCompletedEvent();
        }

        /// <summary>
        /// Cleanup hook for explicit teardown if the tracker lifecycle becomes externally managed.
        /// </summary>
        public void Shutdown()
        {
            EndGameplaySession();
        }

        /// <summary>
        /// Coroutine that waits for Player.Local to become available, then subscribes to onArrested
        /// </summary>
        private IEnumerator WaitForPlayerAndSubscribe()
        {
            ModLogger.Debug("JailTimeTracker waiting for Player.Local to subscribe to onArrested...");

            int generation = _trackingGeneration;
            int attempts = 0;
            const int maxAttempts = 300; // 30 seconds max wait
            
            while (_sceneTrackingActive && generation == _trackingGeneration && attempts < maxAttempts)
            {
                try
                {
                    if (TryAttachPlayerArrestListener(GetLocalPlayer()))
                    {
                        _playerArrestSubscriptionCoroutine = null;
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    // Player.Local not available yet
                    if (attempts % 50 == 0) // Log every 5 seconds
                    {
                        ModLogger.Debug($"JailTimeTracker still waiting for Player.Local... ({attempts / 10}s elapsed, error: {ex.Message})");
                    }
                }
                
                attempts++;
                yield return new WaitForSeconds(0.1f);
            }

            _playerArrestSubscriptionCoroutine = null;
            ModLogger.Warn("JailTimeTracker: Gave up waiting for Player.Local after 30 seconds");
        }
        
        /// <summary>
        /// Event handler called when Player.local.onArrested fires
        /// </summary>
        private void OnPlayerArrested()
        {
            if (!_sceneTrackingActive)
            {
                return;
            }

            try
            {
#if !MONO
                var player = Player.Local;
#else
                var player = Player.Local;
#endif
                if (player != null)
                {
                    ModLogger.Info($"JailTimeTracker: OnPlayerArrested event received for {player.name}");
                    SetInJail(player);
                }
                else
                {
                    ModLogger.Warn("JailTimeTracker: OnPlayerArrested called but Player.Local is null");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"JailTimeTracker: Error in OnPlayerArrested: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Event handler called when ReleaseManager.OnReleaseCompleted fires
        /// </summary>
        private void OnPlayerReleased(Player player, ReleaseManager.ReleaseType releaseType)
        {
            if (!_sceneTrackingActive)
            {
                return;
            }

            try
            {
                if (player != null)
                {
                    ModLogger.Info($"JailTimeTracker: OnPlayerReleased event received for {player.name} (type: {releaseType})");
                    ClearInJail(player);
                    EnsurePlayerArrestSubscription();
                }
                else
                {
                    ModLogger.Warn("JailTimeTracker: OnPlayerReleased called but player is null");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"JailTimeTracker: Error in OnPlayerReleased: {ex.Message}");
            }
        }
        
        #endregion

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
        /// Decrements each active sentence once when the native game-minute event fires.
        /// The event value is informational; the tracker treats each callback as one
        /// game minute and separately reconciles against its real-time fallback.
        /// </summary>
        /// <param name="gameMinute">Native minute value associated with the event.</param>
        private void OnGameMinuteChanged(int gameMinute)
        {
            if (!_sceneTrackingActive)
            {
                return;
            }

            // Decrement all active sentences by 1 game minute
            var completedSentences = new List<string>();

            foreach (var sentence in _activeSentences.Values)
            {
                sentence.RemainingGameMinutes -= 1f;

                if (sentence.RemainingGameMinutes <= 0f)
                {
                    completedSentences.Add(sentence.PlayerKey);
                }
            }

            // Trigger completion callbacks
            foreach (string playerKey in completedSentences)
            {
                if (_activeSentences.TryGetValue(playerKey, out var sentence))
                {
                    ModLogger.Info($"Jail sentence completed for {sentence.Player.name} ({sentence.TotalGameMinutes} game minutes served)");
                    
                    // Store the original sentence time and time served before removing from active tracking
                    _completedSentences[playerKey] = new CompletedSentence
                    {
                        OriginalSentenceTime = sentence.TotalGameMinutes,
                        TimeServed = sentence.TotalGameMinutes // Full sentence served
                    };
                    
                    sentence.OnComplete?.Invoke(sentence.Player);
                    _activeSentences.Remove(playerKey);
                    _sentenceStartTimes.Remove(playerKey); // Clean up real-time tracking
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
            
            _realTimeUpdateCoroutine = MelonCoroutines.Start(RealTimeUpdateLoop(_trackingGeneration));
            ModLogger.Debug("JailTimeTracker real-time tracking fallback started");
        }

        /// <summary>
        /// Stops the globally-owned Melon coroutine before its player references can cross
        /// a scene boundary.
        /// </summary>
        private void StopRealTimeTracking()
        {
            if (_realTimeUpdateCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(_realTimeUpdateCoroutine);
            _realTimeUpdateCoroutine = null;
        }
        
        /// <summary>
        /// Reconciles active sentence records once per real second when game-minute events
        /// are missing or lagging. The fallback treats one real second as one game minute,
        /// never increases remaining time, and uses a generation token to stop after a
        /// scene transition. The pooled completion list avoids a per-tick allocation.
        /// </summary>
        private IEnumerator RealTimeUpdateLoop(int generation)
        {
            while (_sceneTrackingActive && generation == _trackingGeneration)
            {
                yield return new WaitForSeconds(1f); // Update every real second

                if (!_sceneTrackingActive || generation != _trackingGeneration)
                {
                    yield break;
                }

                // Performance: Reuse pooled list instead of allocating new one
                _completedSentencesPool.Clear();
                float currentTime = Time.time;

                // Performance: Iterate directly without ToList() to avoid dictionary copy
                foreach (var kvp in _activeSentences)
                {
                    string playerKey = kvp.Key;
                    ActiveSentence sentence = kvp.Value;
                    Player player = sentence.Player;

                    // Calculate elapsed real-time since sentence started
                    if (_sentenceStartTimes.TryGetValue(playerKey, out float startTime))
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
                        _completedSentencesPool.Add(playerKey);
                    }
                }

                // Trigger completion callbacks for sentences that completed via real-time tracking
                foreach (string playerKey in _completedSentencesPool)
                {
                    if (_activeSentences.TryGetValue(playerKey, out var sentence))
                    {
                        ModLogger.Info($"Jail sentence completed (real-time tracking) for {sentence.Player.name} ({sentence.TotalGameMinutes} game minutes served)");

                        // Store the original sentence time and time served before removing from active tracking
                        _completedSentences[playerKey] = new CompletedSentence
                        {
                            OriginalSentenceTime = sentence.TotalGameMinutes,
                            TimeServed = sentence.TotalGameMinutes // Full sentence served
                        };

                        sentence.OnComplete?.Invoke(sentence.Player);
                        _activeSentences.Remove(playerKey);
                        _sentenceStartTimes.Remove(playerKey);
                    }
                }
            }
        }

        /// <summary>
        /// Starts tracking a sentence in game-minute units for the active gameplay session.
        /// Starting a second sentence for the same player replaces the active record but
        /// keeps the session-level event and fallback subscriptions unchanged.
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

            if (!_sceneTrackingActive)
            {
                ModLogger.Warn("Cannot start jail sentence tracking outside an active gameplay scene");
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            // Remove any existing sentence for this player
            if (_activeSentences.ContainsKey(playerKey))
            {
                ModLogger.Warn($"Replacing existing sentence for {player.name}");
                _activeSentences.Remove(playerKey);
            }

            var sentence = new ActiveSentence
            {
                PlayerKey = playerKey,
                Player = player,
                RemainingGameMinutes = sentenceGameMinutes,
                TotalGameMinutes = sentenceGameMinutes,
                OnComplete = onComplete
            };

            _activeSentences[playerKey] = sentence;
            _sentenceStartTimes[playerKey] = Time.time; // Record start time for real-time tracking
            ModLogger.Info($"Started tracking jail sentence for {player.name}: {sentenceGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentenceGameMinutes)})");
        }

        /// <summary>
        /// Extends an existing active sentence in game-minute units without restarting its
        /// elapsed-time accounting. Returns false when the player has no active record or
        /// the requested penalty is not positive.
        /// </summary>
        /// <param name="player">Player whose active sentence should be extended.</param>
        /// <param name="additionalGameMinutes">Additional sentence duration.</param>
        /// <param name="reason">Human-readable reason included in diagnostics.</param>
        /// <returns>True when the active record was extended.</returns>
        public bool AddPenaltyTime(Player player, float additionalGameMinutes, string reason)
        {
            if (player == null || additionalGameMinutes <= 0f)
            {
                return false;
            }

            if (!_activeSentences.TryGetValue(GetPlayerRuntimeKey(player), out var sentence))
            {
                return false;
            }

            sentence.RemainingGameMinutes += additionalGameMinutes;
            sentence.TotalGameMinutes += additionalGameMinutes;
            ModLogger.Info($"Extended {player.name}'s active sentence by {additionalGameMinutes:F0} game minutes for {reason}");
            return true;
        }

        /// <summary>
        /// Stops tracking a player, records the original sentence and time served for
        /// release reporting, and removes the real-time start marker. This does not invoke
        /// the completion callback; the release flow owns any early-release notification.
        /// </summary>
        /// <param name="player">Player whose active sentence should be stopped.</param>
        public void StopTracking(Player player)
        {
            if (player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            if (_activeSentences.TryGetValue(playerKey, out var sentence))
            {
                // Calculate actual time served before storing
                float timeServed = sentence.TotalGameMinutes - sentence.RemainingGameMinutes;
                
                // Log detailed information for debugging
                ModLogger.Debug($"[JAIL TRACKING] StopTracking called for {player.name}:");
                ModLogger.Debug($"  Total sentence: {sentence.TotalGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentence.TotalGameMinutes)})");
                ModLogger.Debug($"  Remaining: {sentence.RemainingGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentence.RemainingGameMinutes)})");
                ModLogger.Debug($"  Time served: {timeServed} game minutes ({GameTimeManager.FormatGameTime(timeServed)})");
                
                // Store both original sentence time and time served for early releases
                _completedSentences[playerKey] = new CompletedSentence
                {
                    OriginalSentenceTime = sentence.TotalGameMinutes,
                    TimeServed = timeServed
                };
                
                _activeSentences.Remove(playerKey);
                _sentenceStartTimes.Remove(playerKey); // Clean up real-time tracking
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
            if (player == null)
            {
                return 0f;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            // First check active sentences
            if (_activeSentences.TryGetValue(playerKey, out var sentence))
            {
                return sentence.TotalGameMinutes;
            }
            
            // Then check completed sentences
            if (_completedSentences.TryGetValue(playerKey, out var completed))
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
            if (player == null)
            {
                return 0f;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            // Check active sentences first
            if (_activeSentences.TryGetValue(playerKey, out var sentence))
            {
                // Try to calculate from real-time tracking first (more reliable)
                float timeServed = 0f;
                if (_sentenceStartTimes.TryGetValue(playerKey, out float startTime))
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
            if (_completedSentences.TryGetValue(playerKey, out var completed))
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
            if (player == null)
            {
                return;
            }

            _completedSentences.Remove(GetPlayerRuntimeKey(player));
        }

        /// <summary>
        /// Gets the active sentence time remaining in game-minute units. A player without
        /// an active record returns zero; completed records are intentionally excluded.
        /// </summary>
        public float GetRemainingTime(Player player)
        {
            if (player == null)
            {
                return 0f;
            }

            if (_activeSentences.TryGetValue(GetPlayerRuntimeKey(player), out var sentence))
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
            return player != null && _activeSentences.ContainsKey(GetPlayerRuntimeKey(player));
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

            string playerKey = GetPlayerRuntimeKey(player);

            if (!_inJailStatus.Contains(playerKey))
            {
                _inJailStatus.Add(playerKey);
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

            if (_inJailStatus.Remove(GetPlayerRuntimeKey(player)))
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

            return _inJailStatus.Contains(GetPlayerRuntimeKey(player));
        }

        private static string GetPlayerRuntimeKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Core.ResolvePlayerKey(player);
        }

        private static Player? GetLocalPlayer()
        {
            try
            {
                return Player.Local;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsValidLocalPlayer(Player? player)
        {
#if !MONO
            return player != null && player.Pointer != IntPtr.Zero;
#else
            return player != null;
#endif
        }

        #endregion
    }
}

