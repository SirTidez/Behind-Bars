using System.Collections.Generic;
using Behind_Bars.Helpers;
using UnityEngine;
using MelonLoader;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Tracks parole supervision periods using game time events instead of real-time
    /// Decrements parole time when game time passes and triggers completion when time expires
    /// </summary>
    public class ParoleTimeTracker
    {
        private static ParoleTimeTracker? _instance;
        public static ParoleTimeTracker Instance => _instance ??= new ParoleTimeTracker();

        /// <summary>
        /// Represents an active parole period being tracked
        /// </summary>
        private class ActiveParole
        {
            public Player Player { get; set; }
            public float RemainingGameMinutes { get; set; }
            public float TotalGameMinutes { get; set; }
            public System.Action<Player>? OnComplete { get; set; }
        }

        private Dictionary<Player, ActiveParole> _activeParoles = new();
        private bool _isSubscribed = false;

        private ParoleTimeTracker()
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
            ModLogger.Debug("ParoleTimeTracker subscribed to game time events");
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
            ModLogger.Info("ParoleTimeTracker unsubscribed from game time events");
        }

        /// <summary>
        /// Called when a game minute passes
        /// </summary>
        private void OnGameMinuteChanged(int gameMinute)
        {
            // Decrement all active parole periods by 1 game minute
            var completedParoles = new List<Player>();

            foreach (var parole in _activeParoles.Values)
            {
                parole.RemainingGameMinutes -= 1f;

                if (parole.RemainingGameMinutes <= 0f)
                {
                    completedParoles.Add(parole.Player);
                }
            }

            // Trigger completion callbacks
            foreach (var player in completedParoles)
            {
                if (_activeParoles.TryGetValue(player, out var parole))
                {
                    ModLogger.Info($"Parole period completed for {player.name} ({parole.TotalGameMinutes} game minutes served)");
                    parole.OnComplete?.Invoke(player);
                    _activeParoles.Remove(player);
                }
            }
        }

        /// <summary>
        /// Start tracking a parole period for a player
        /// </summary>
        /// <param name="player">The player on parole</param>
        /// <param name="paroleGameMinutes">Parole duration in game minutes</param>
        /// <param name="onComplete">Callback when parole is complete</param>
        public void StartTracking(Player player, float paroleGameMinutes, System.Action<Player>? onComplete = null)
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot track parole for null player");
                return;
            }

            // Remove any existing parole for this player
            if (_activeParoles.ContainsKey(player))
            {
                ModLogger.Warn($"Replacing existing parole for {player.name}");
                _activeParoles.Remove(player);
            }

            var parole = new ActiveParole
            {
                Player = player,
                RemainingGameMinutes = paroleGameMinutes,
                TotalGameMinutes = paroleGameMinutes,
                OnComplete = onComplete
            };

            _activeParoles[player] = parole;
            ModLogger.Debug($"Started tracking parole for {player.name}: {paroleGameMinutes} game minutes ({GameTimeManager.FormatGameTime(paroleGameMinutes)})");
        }

        /// <summary>
        /// Stop tracking parole for a player (e.g., parole revoked)
        /// </summary>
        public void StopTracking(Player player)
        {
            if (_activeParoles.Remove(player))
            {
                ModLogger.Info($"Stopped tracking parole for {player.name}");
            }
        }

        /// <summary>
        /// Get remaining parole time for a player in game minutes
        /// </summary>
        public float GetRemainingTime(Player player)
        {
            if (_activeParoles.TryGetValue(player, out var parole))
            {
                return Mathf.Max(0f, parole.RemainingGameMinutes);
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
        /// Check if a player has an active parole period being tracked
        /// </summary>
        public bool IsTracking(Player player)
        {
            return _activeParoles.ContainsKey(player);
        }

        /// <summary>
        /// Get all active parole periods
        /// </summary>
        public int GetActiveParoleCount()
        {
            return _activeParoles.Count;
        }
    }
}

