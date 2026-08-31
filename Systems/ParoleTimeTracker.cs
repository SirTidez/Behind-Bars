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
    /// <remarks>
    /// The tracker is a lazily-created singleton and subscribes to the fallback
    /// <see cref="GameTimeManager"/> for its lifetime. Its countdown therefore follows that
    /// manager's Unity-scaled sampling rather than an unscaled wall clock. There is no public
    /// tracker shutdown/reset path today; the private unsubscribe helper is retained for
    /// lifecycle symmetry but is not called by the visible code path.
    /// </remarks>
    public class ParoleTimeTracker
    {
        private static ParoleTimeTracker? _instance;

        /// <summary>
        /// Gets the lazily-created process-wide parole countdown tracker.
        /// </summary>
        public static ParoleTimeTracker Instance => _instance ??= new ParoleTimeTracker();

        /// <summary>
        /// Represents an active parole period being tracked
        /// </summary>
        private class ActiveParole
        {
            /// <summary>Stable runtime key used to replace and remove this record.</summary>
            public string PlayerKey { get; set; }
            /// <summary>Player object whose parole period is being counted down.</summary>
            public Player Player { get; set; }
            /// <summary>Remaining duration in fallback game minutes.</summary>
            public float RemainingGameMinutes { get; set; }
            /// <summary>Original duration in fallback game minutes, used for diagnostics.</summary>
            public float TotalGameMinutes { get; set; }
            /// <summary>Optional callback invoked immediately before this record is removed at expiry.</summary>
            public System.Action<Player>? OnComplete { get; set; }
        }

        // Records are keyed by the same runtime identity used by the rest of the parole
        // systems so reconnect/replacement operations do not depend on object references.
        private Dictionary<string, ActiveParole> _activeParoles = new();
        // Guards the single event subscription; it is not a teardown indicator for the
        // singleton because no public reset currently invokes UnsubscribeFromGameTimeEvents.
        private bool _isSubscribed = false;

        private ParoleTimeTracker()
        {
            SubscribeToGameTimeEvents();
        }

        /// <summary>
        /// Subscribe to game time events
        /// </summary>
        /// <remarks>
        /// The handler reference is the named instance method, so repeated initialization is
        /// idempotent and the same reference can be removed if a future owner adds teardown.
        /// </remarks>
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
        /// <remarks>
        /// This method removes the stable handler from <see cref="GameTimeManager"/> but does
        /// not clear active records. It is currently private and has no visible caller.
        /// </remarks>
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
        /// <param name="gameMinute">The sampled minute value; the current implementation does not use the value.</param>
        /// <remarks>
        /// Each event callback represents one elapsed fallback game-minute step, so every
        /// active record is decremented by exactly 1 rather than by the numeric event
        /// argument. Completed callbacks run before the corresponding records are removed.
        /// If a publisher skips a minute, this tracker does not reconstruct the skipped steps.
        /// </remarks>
        private void OnGameMinuteChanged(int gameMinute)
        {
            // Decrement all active parole periods by 1 game minute
            var completedParoles = new List<string>();

            foreach (var parole in _activeParoles.Values)
            {
                parole.RemainingGameMinutes -= 1f;

                if (parole.RemainingGameMinutes <= 0f)
                {
                    completedParoles.Add(parole.PlayerKey);
                }
            }

            // Trigger completion callbacks
            foreach (string playerKey in completedParoles)
            {
                if (_activeParoles.TryGetValue(playerKey, out var parole))
                {
                    ModLogger.Info($"Parole period completed for {parole.Player.name} ({parole.TotalGameMinutes} game minutes served)");
                    parole.OnComplete?.Invoke(parole.Player);
                    _activeParoles.Remove(playerKey);
                }
            }
        }

        /// <summary>
        /// Start tracking a parole period for a player
        /// </summary>
        /// <param name="player">The player on parole</param>
        /// <param name="paroleGameMinutes">Parole duration in game minutes</param>
        /// <param name="onComplete">Callback when parole is complete</param>
        /// <remarks>
        /// A null player is ignored. Durations are stored as supplied without validation, and
        /// starting a new period for the same runtime key replaces the previous record. The
        /// optional callback is invoked on the game-time event path when the stored duration
        /// reaches zero.
        /// </remarks>
        public void StartTracking(Player player, float paroleGameMinutes, System.Action<Player>? onComplete = null)
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot track parole for null player");
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            // Remove any existing parole for this player
            if (_activeParoles.ContainsKey(playerKey))
            {
                ModLogger.Warn($"Replacing existing parole for {player.name}");
                _activeParoles.Remove(playerKey);
            }

            var parole = new ActiveParole
            {
                PlayerKey = playerKey,
                Player = player,
                RemainingGameMinutes = paroleGameMinutes,
                TotalGameMinutes = paroleGameMinutes,
                OnComplete = onComplete
            };

            _activeParoles[playerKey] = parole;
            ModLogger.Debug($"Started tracking parole for {player.name}: {paroleGameMinutes} game minutes ({GameTimeManager.FormatGameTime(paroleGameMinutes)})");
        }

        /// <summary>
        /// Stop tracking parole for a player (e.g., parole revoked)
        /// </summary>
        public void StopTracking(Player player)
        {
            if (player == null)
            {
                return;
            }

            if (_activeParoles.Remove(GetPlayerRuntimeKey(player)))
            {
                ModLogger.Info($"Stopped tracking parole for {player.name}");
            }
        }

        /// <summary>
        /// Get remaining parole time for a player in game minutes
        /// </summary>
        public float GetRemainingTime(Player player)
        {
            if (player == null)
            {
                return 0f;
            }

            if (_activeParoles.TryGetValue(GetPlayerRuntimeKey(player), out var parole))
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
            return player != null && _activeParoles.ContainsKey(GetPlayerRuntimeKey(player));
        }

        /// <summary>
        /// Get all active parole periods
        /// </summary>
        /// <returns>The number of currently tracked runtime records.</returns>
        public int GetActiveParoleCount()
        {
            return _activeParoles.Count;
        }

        /// <summary>
        /// Resolve the shared runtime identity used as the tracker dictionary key.
        /// </summary>
        /// <param name="player">Player whose identity should be resolved.</param>
        /// <returns>A stable key, or an empty string for a null player.</returns>
        private static string GetPlayerRuntimeKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Behind_Bars.Core.ResolvePlayerKey(player);
        }
    }
}

