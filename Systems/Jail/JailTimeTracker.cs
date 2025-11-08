using System.Collections.Generic;
using System.Collections;
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

        private Dictionary<Player, ActiveSentence> _activeSentences = new();
        private bool _isSubscribed = false;

        private JailTimeTracker()
        {
            SubscribeToGameTimeEvents();
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
            ModLogger.Info("JailTimeTracker subscribed to game time events");
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
                    sentence.OnComplete?.Invoke(player);
                    _activeSentences.Remove(player);
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
            ModLogger.Info($"Started tracking jail sentence for {player.name}: {sentenceGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentenceGameMinutes)})");
        }

        /// <summary>
        /// Stop tracking a sentence for a player (e.g., early release)
        /// </summary>
        public void StopTracking(Player player)
        {
            if (_activeSentences.Remove(player))
            {
                ModLogger.Info($"Stopped tracking jail sentence for {player.name}");
            }
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
    }
}

