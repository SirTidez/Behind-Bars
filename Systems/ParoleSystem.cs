using System.Collections;
using System;
using System.ComponentModel;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Crimes;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.Parole;
using Behind_Bars.Systems.Parole.Conditions;
using Behind_Bars.UI;
using UnityEngine;
using MelonLoader;
using System.Collections.Generic;
using Behind_Bars.Systems.NPCs;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppFishNet;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.PlayerScripts;
#else
using FishNet;
using ScheduleOne.GameTime;
using ScheduleOne.Law;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Manages active parole supervision for released players
    /// Handles parole monitoring, officer reminders, violations, and completion
    /// Integrates with RapSheet/LSI system for risk assessment
    /// </summary>
    /// <remarks>
    /// Runtime records live in memory while the current parole record in the RapSheet carries
    /// persisted state. Native Schedule I time is preferred for day/minute-of-day scheduling;
    /// the lightweight mod clock is only a fallback. Server authority gates the enforcement
    /// actions, but the current network resolver intentionally fails open when no manager or
    /// an exception is encountered.
    /// </remarks>
    public class ParoleSystem
    {
        #region Events

        /// <summary>
        /// Manager-owned event fired when parole starts for a player.
        /// </summary>
        public event System.Action<Player>? ParoleStarted;

        /// <summary>
        /// Manager-owned event fired when parole ends for a player (completed, revoked, or expired).
        /// </summary>
        public event System.Action<Player>? ParoleEnded;

        /// <summary>
        /// Compatibility shim for legacy static consumers while the parole lifecycle bridge
        /// is being migrated onto the manager-owned <see cref="ParoleStarted"/> event.
        /// No current in-repo runtime path subscribes to this event; it remains only as a
        /// temporary legacy surface for callers that have not yet moved onto the manager graph.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Compatibility-only legacy surface. Subscribe to the managed ParoleSystem.ParoleStarted event through BehindBarsSystemManager instead.", false)]
        public static event System.Action<Player>? OnParoleStarted;

        /// <summary>
        /// Compatibility shim for legacy static consumers while the parole lifecycle bridge
        /// is being migrated onto the manager-owned <see cref="ParoleEnded"/> event.
        /// No current in-repo runtime path subscribes to this event; it remains only as a
        /// temporary legacy surface for callers that have not yet moved onto the manager graph.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Compatibility-only legacy surface. Subscribe to the managed ParoleSystem.ParoleEnded event through BehindBarsSystemManager instead.", false)]
        public static event System.Action<Player>? OnParoleEnded;

        #endregion
        private const float PAROLE_DURATION = 600f; // 10 minutes default
        private const float LOW_LSI_CHECKIN_WINDOW_MINUTES = 180f; // 3 in-game hours
        private const float HIGH_LSI_CHECKIN_WINDOW_MINUTES = 60f; // 1 in-game hour
        private const float ACTIVE_WARRANT_ENFORCEMENT_INTERVAL_SECONDS = 2f;
        private const int TEXT_MESSAGE_RETRY_MAX_ATTEMPTS = 8;
        private const float TEXT_MESSAGE_RETRY_INTERVAL_SECONDS = 3f;
        private const int CHECKIN_START_HOUR_24 = 10;
        private const int CHECKIN_END_HOUR_24 = 20;
        private const int GAME_MINUTES_PER_DAY = 1440;

        /// <summary>
        /// Result of evaluating the manager-owned daily check-in window.
        /// </summary>
        public enum DailyCheckInStatus
        {
            Allowed = 0,
            NoScheduledWindow = 1,
            TooEarly = 2,
            MissedWindow = 3
        }

        /// <summary>
        /// Parole supervision status
        /// </summary>
        public enum ParoleStatus
        {
            None = 0,
            Active = 1,
            Violation = 2,
            Completed = 3,
            Revoked = 4
        }

        /// <summary>
        /// Runtime parole tracking record (in-memory)
        /// Separate from ParoleRecord in RapSheet which handles persistent storage
        /// Now uses game time units (game minutes) instead of real-time seconds
        /// </summary>
        public class ParoleRuntimeRecord
        {
            /// <summary>Stable runtime identity used as the active-record dictionary key.</summary>
            public string PlayerKey { get; set; }
            /// <summary>Live scene player associated with this runtime record.</summary>
            public Player Player { get; set; }
            /// <summary>Current in-memory lifecycle state.</summary>
            public ParoleStatus Status { get; set; }
            /// <summary>Fallback game-minute value sampled when the runtime record began.</summary>
            public float StartGameTimeMinutes { get; set; } // Game time when parole started (game minutes)
            /// <summary>Total supervision duration in game minutes.</summary>
            public float DurationGameMinutes { get; set; } // Total parole duration (game minutes)
            /// <summary>Remaining supervision duration reported by <see cref="ParoleTimeTracker"/>.</summary>
            public float TimeRemainingGameMinutes { get; set; } // Remaining time (game minutes)
            /// <summary>Number of formal violations recorded in this runtime session.</summary>
            public int ViolationCount { get; set; }
            /// <summary>In-memory descriptions of violations associated with this runtime record.</summary>
            public List<string> Violations { get; set; } = new();
        }

        private class PendingOfficerText
        {
            /// <summary>Latest message awaiting a supervising-officer delivery attempt.</summary>
            public string Message { get; set; }
            /// <summary>Number of retry attempts already made after the initial queue.</summary>
            public int Attempts { get; set; }
            /// <summary>Next retry deadline in Unity's scaled <see cref="Time.time"/> domain.</summary>
            public float NextAttemptTime { get; set; }
        }

        // Runtime records and retry/warrant maps are process-local and keyed by the shared
        // player identity; persisted RapSheet state is restored separately on load.
        private Dictionary<string, ParoleRuntimeRecord> _paroleRecords = new();
        private Dictionary<string, PendingOfficerText> _pendingOfficerTexts = new();
        private HashSet<string> _playersWithActiveWarrants = new();
        // Time.time is used for the local enforcement throttle, so this is scaled time.
        private Dictionary<string, float> _lastWarrantEnforcementTime = new();
        // Retained legacy field; dynamic parole officer ownership is now delegated to NpcManager.
        private GameObject? _paroleOfficerPrefab;
        // Stable named handler used for safe subscription/unsubscription across runtimes.
        private bool _isSubscribedToDayPass;
        private readonly Action _onDayPassHandler;
        // MelonCoroutines returns a runtime-specific interop handle.
        private object _timeManagerSubscriptionCoroutine;

        /// <summary>
        /// Create an in-memory parole system and begin best-effort native day-pass wiring.
        /// </summary>
        public ParoleSystem()
        {
            _onDayPassHandler = HandleDayPassForParoleCheckIns;
            EnsureDayPassSubscription();
        }

        /// <summary>
        /// Remove the native day-pass listener owned by this parole system.
        /// </summary>
        /// <remarks>
        /// Shutdown currently unsubscribes only the day-pass handler. It does not cancel a
        /// pending subscription coroutine, clear runtime records, clear active warrants or
        /// queued officer text, or detach lifecycle-event subscribers; those states therefore
        /// remain until their normal completion paths or the instance is discarded.
        /// </remarks>
        public void Shutdown()
        {
            UnsubscribeFromDayPass();
        }

        private ParoleConditionManager GetParoleConditionManager()
        {
            return Core.ResolveParoleConditionManager();
        }

        private ParoleFeeSystem GetParoleFeeSystem()
        {
            return Core.ResolveParoleFeeSystem();
        }

        private HomeVisitSystem GetHomeVisitSystem()
        {
            return Core.ResolveHomeVisitSystem();
        }

        /// <summary>
        /// Raise the authoritative manager-owned parole-start lifecycle event and then mirror it
        /// to the temporary static compatibility shim.
        /// </summary>
        private void RaiseParoleStarted(Player player)
        {
            ParoleStarted?.Invoke(player);
            OnParoleStarted?.Invoke(player);
        }

        /// <summary>
        /// Raise the authoritative manager-owned parole-end lifecycle event and then mirror it
        /// to the temporary static compatibility shim.
        /// </summary>
        private void RaiseParoleEnded(Player player)
        {
            ParoleEnded?.Invoke(player);
            OnParoleEnded?.Invoke(player);
        }

        /// <summary>
        /// Start the deferred native day-pass subscription if one is not already active.
        /// </summary>
        /// <remarks>
        /// The guard prevents duplicate coroutines and duplicate event handlers while the
        /// native <see cref="TimeManager"/> is still coming online.
        /// </remarks>
        private void EnsureDayPassSubscription()
        {
            if (_isSubscribedToDayPass || _timeManagerSubscriptionCoroutine != null)
            {
                return;
            }

            _timeManagerSubscriptionCoroutine = MelonCoroutines.Start(WaitForTimeManagerAndSubscribe());
        }

        /// <summary>
        /// Retry native <see cref="TimeManager"/> lookup and attach the stable day-pass handler.
        /// </summary>
        /// <remarks>
        /// This makes at most 600 attempts with a 0.5-second Unity-scaled wait between them.
        /// The current coroutine is not cancelled by <see cref="Shutdown"/>; it clears its
        /// handle on success or exhaustion and logs a warning when the native manager never
        /// becomes available.
        /// </remarks>
        private IEnumerator WaitForTimeManagerAndSubscribe()
        {
            int attempts = 0;

            while (!_isSubscribedToDayPass && attempts < 600)
            {
                TimeManager timeManager = null;

                try
                {
                    timeManager = TimeManager.Instance;
                }
                catch
                {
                    // TimeManager not available yet.
                }

                if (timeManager != null)
                {
                    timeManager.onDayPass += _onDayPassHandler;
                    _isSubscribedToDayPass = true;
                    _timeManagerSubscriptionCoroutine = null;
                    ModLogger.Debug("ParoleSystem subscribed to TimeManager.onDayPass");
                    yield break;
                }

                attempts++;
                yield return new WaitForSeconds(0.5f);
            }

            _timeManagerSubscriptionCoroutine = null;

            if (!_isSubscribedToDayPass)
            {
                ModLogger.Warn("ParoleSystem failed to subscribe to TimeManager.onDayPass");
            }
        }

        /// <summary>
        /// Remove the stable day-pass handler from the native time manager when subscribed.
        /// </summary>
        /// <remarks>
        /// The operation is idempotent and preserves the handler reference used at subscribe
        /// time. Lookup failures are logged and leave the current subscription flag unchanged.
        /// </remarks>
        private void UnsubscribeFromDayPass()
        {
            if (!_isSubscribedToDayPass)
            {
                return;
            }

            try
            {
                var timeManager = TimeManager.Instance;
                if (timeManager != null)
                {
                    timeManager.onDayPass -= _onDayPassHandler;
                }

                _isSubscribedToDayPass = false;
                ModLogger.Debug("ParoleSystem unsubscribed from TimeManager.onDayPass");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"ParoleSystem: Failed to unsubscribe from TimeManager.onDayPass: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve the shared runtime identity used by parole maps and collaborators.
        /// </summary>
        /// <param name="player">Player whose identity should be resolved.</param>
        /// <returns>A stable key, or an empty string for a null player.</returns>
        internal string GetPlayerRuntimeKeyInternal(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Core.ResolvePlayerKey(player);
        }

        private string GetPlayerRuntimeKey(Player player) => GetPlayerRuntimeKeyInternal(player);

        /// <summary>
        /// Gets the live backing map of runtime parole records.
        /// </summary>
        /// <remarks>
        /// This internal surface exposes the dictionary itself for the manager's day-pass
        /// iteration; callers must not mutate it while a monitoring or completion path is
        /// enumerating the records.
        /// </remarks>
        internal Dictionary<string, ParoleRuntimeRecord> ActiveParoleRecords => _paroleRecords;

        /// <summary>
        /// Start parole supervision for a player
        /// Creates runtime tracking and initializes RapSheet/LSI integration
        /// </summary>
        /// <param name="player">Player being released into parole supervision.</param>
        /// <param name="durationGameMinutes">Supervision duration in game minutes.</param>
        /// <param name="showUI">Compatibility parameter retained for callers; the current implementation does not read it.</param>
        /// <remarks>
        /// The active runtime record is created before RapSheet/condition setup, tracking, and
        /// monitoring are started. The native parole record remains the persistence authority;
        /// the runtime record and <see cref="ParoleTimeTracker"/> provide countdown state. The
        /// dynamic NPC manager owns officer spawning. Lifecycle notification is raised only
        /// after those startup handoffs have been attempted.
        /// </remarks>
        public void StartParole(Player player, float durationGameMinutes = PAROLE_DURATION, bool showUI = true)
        {
            ModLogger.Info($"Starting parole for {player.name} for {durationGameMinutes} game minutes ({GameTimeManager.FormatGameTime(durationGameMinutes)})");
            EnsureDayPassSubscription();
            string playerKey = GetPlayerRuntimeKey(player);

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();

            ClearParoleRuntimeFlags(player);

            var record = new ParoleRuntimeRecord
            {
                PlayerKey = playerKey,
                Player = player,
                Status = ParoleStatus.Active,
                StartGameTimeMinutes = currentGameTime,
                DurationGameMinutes = durationGameMinutes,
                TimeRemainingGameMinutes = durationGameMinutes,
                ViolationCount = 0
            };

            _paroleRecords[playerKey] = record;

            // Initialize RapSheet ParoleRecord and perform LSI assessment (convert to game minutes)
            InitializeParoleTracking(player, durationGameMinutes);

            // Initialize parole conditions based on crimes and LSI
            try
            {
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet != null)
                {
                    var paroleConditionManager = GetParoleConditionManager();
                    var paroleFeeSystem = GetParoleFeeSystem();
                    var homeVisitSystem = GetHomeVisitSystem();

                    paroleConditionManager.InitializeConditions(rapSheet);

                    // Initialize fee schedule
                    paroleFeeSystem.InitializeFees(player, rapSheet);

                    // Schedule first home visit
                    homeVisitSystem.ScheduleNextVisit(player, rapSheet);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initializing parole conditions: {ex.Message}");
            }

            // Start tracking with ParoleTimeTracker
            Core.ResolveParoleManager().StartTracking(player, durationGameMinutes, OnParoleComplete);

            // Start parole monitoring
            MelonCoroutines.Start(MonitorParole(record));

            // NOTE: Parole officer spawning is now handled by DynamicParoleOfficerManager
            // The old SpawnParoleOfficer() call has been removed

            // Ensure the scene-bound parole NPC manager exists before emitting the parole-start event.
            // This keeps the manager-graph forwarding path authoritative without changing the runtime flow.
            Core.Instance?.NpcManager?.EnsureDynamicParoleOfficerManager();

            // Emit parole started event
            RaiseParoleStarted(player);
            ModLogger.Debug($"ParoleSystem: Emitted parole-start lifecycle event for {player.name}");

            // Process an immediate spawn update after the lifecycle handoff.
            if (Core.Instance?.NpcManager == null)
            {
                ModLogger.Warn("ParoleSystem: NpcManager still unavailable after parole-start handoff");
            }

            // NOTE: RecordReleaseTime is now called in ReleaseManager.WaitForParoleConditionsAcknowledgment()
            // after the player dismisses the parole conditions UI. This ensures the grace period
            // starts only after the player acknowledges their conditions, not immediately when parole starts.
        }

        /// <summary>
        /// Restore runtime tracking for an already-active persisted parole term after a load.
        /// </summary>
        /// <param name="player">Loaded scene player to reattach.</param>
        /// <remarks>
        /// Only the current authority restores a non-paused persisted term. Existing active
        /// runtime records are reused, expired terms are ended in the RapSheet, and a new
        /// tracker/monitor pair is created only when the persisted remaining duration is
        /// positive. A daily check-in is scheduled only when the loaded player is not in jail.
        /// </remarks>
        public void EnsureRuntimeParoleTrackingForLoadedPlayer(Player player)
        {
            if (player == null)
            {
                return;
            }

            if (!IsAuthorityForParoleActions())
            {
                return;
            }

            EnsureDayPassSubscription();

            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            var paroleRecord = rapSheet?.CurrentParoleRecord;
            if (paroleRecord == null || !paroleRecord.IsOnParole() || paroleRecord.IsPaused())
            {
                return;
            }

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            int currentDayIndex = GetCurrentDayIndexInternal();
            int currentMinuteOfDay = GetCurrentMinuteOfDayInternal();
            string playerKey = GetPlayerRuntimeKey(player);

            if (paroleRecord.HasActiveAgentWarrant())
            {
                _playersWithActiveWarrants.Add(playerKey);
            }

            if (_paroleRecords.TryGetValue(playerKey, out var existingRecord) && existingRecord != null)
            {
                if (existingRecord.Status == ParoleStatus.Active)
                {
                    if (!Core.ResolveJailTimeTracker().IsInJail(player))
                    {
                        Core.ResolveParoleManager().ScheduleDailyCheckIn(player, currentDayIndex, currentMinuteOfDay);
                    }

                    return;
                }

                _paroleRecords.Remove(playerKey);
            }

            var paroleStatus = paroleRecord.GetParoleStatus();
            float remainingGameMinutes = Mathf.Max(0f, paroleStatus.remainingTime);
            if (remainingGameMinutes <= 0f)
            {
                paroleRecord.CheckAndEndExpiredParole();
                Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
                return;
            }

            var runtimeRecord = new ParoleRuntimeRecord
            {
                PlayerKey = playerKey,
                Player = player,
                Status = ParoleStatus.Active,
                StartGameTimeMinutes = currentGameTime,
                DurationGameMinutes = remainingGameMinutes,
                TimeRemainingGameMinutes = remainingGameMinutes,
                ViolationCount = paroleRecord.GetViolationCount()
            };

            _paroleRecords[playerKey] = runtimeRecord;

            Core.ResolveParoleManager().StopTracking(player);
            Core.ResolveParoleManager().StartTracking(player, remainingGameMinutes, OnParoleComplete);
            MelonCoroutines.Start(MonitorParole(runtimeRecord));

            ModLogger.Info($"ParoleSystem: Restored runtime parole tracking for loaded player {player.name} ({remainingGameMinutes:F1} game minutes remaining)");

            if (!Core.ResolveJailTimeTracker().IsInJail(player))
            {
                Core.ResolveParoleManager().ScheduleDailyCheckIn(player, currentDayIndex, currentMinuteOfDay);
            }
        }

        /// <summary>
        /// Determine whether this process should perform authoritative parole actions.
        /// </summary>
        /// <returns>The native server flag, or <see langword="true"/> when authority cannot be resolved.</returns>
        /// <remarks>
        /// On Mono and IL2CPP the appropriate FishNet namespace is selected at compile time.
        /// The current policy is fail-open: a missing network manager or resolver exception
        /// returns <see langword="true"/>, allowing local enforcement to continue rather than
        /// suppressing parole actions.
        /// </remarks>
        internal bool IsAuthorityForParoleActionsInternal()
        {
            try
            {
#if !MONO
                var networkManager = InstanceFinder.NetworkManager;
#else
                var networkManager = FishNet.InstanceFinder.NetworkManager;
#endif
                if (networkManager == null)
                {
                    return true;
                }

                return networkManager.IsServer;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Forward a native day-pass notification to the manager-owned check-in scheduler.
        /// </summary>
        private void HandleDayPassForParoleCheckIns()
        {
            Core.ResolveParoleManager().HandleDayPassForParoleCheckIns();
        }

        /// <summary>
        /// Normalize a player name for officer-facing text.
        /// </summary>
        /// <param name="player">Player whose display name should be normalized.</param>
        /// <returns>The trimmed name without a trailing numeric runtime suffix.</returns>
        internal string GetPlayerDisplayNameInternal(Player player)
        {
            string rawName = player?.name ?? "Parolee";
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return "Parolee";
            }

            string trimmedName = rawName.Trim();
            int idStartIndex = trimmedName.LastIndexOf(" (", StringComparison.Ordinal);
            if (idStartIndex > 0 && trimmedName.EndsWith(")", StringComparison.Ordinal))
            {
                string idSlice = trimmedName.Substring(idStartIndex + 2, trimmedName.Length - idStartIndex - 3);
                if (long.TryParse(idSlice, out _))
                {
                    return trimmedName.Substring(0, idStartIndex).Trim();
                }
            }

            return trimmedName;
        }

        /// <summary>
        /// Format a minute-of-day value as a normalized 12-hour clock time.
        /// </summary>
        /// <param name="minuteOfDay">Minute value to normalize across a 24-hour day.</param>
        /// <returns>A player-facing time such as <c>10:05 AM</c>.</returns>
        internal string FormatMinuteOfDayInternal(int minuteOfDay)
        {
            int normalized = minuteOfDay % GAME_MINUTES_PER_DAY;
            if (normalized < 0)
            {
                normalized += GAME_MINUTES_PER_DAY;
            }

            int hour24 = normalized / 60;
            int minute = normalized % 60;
            int hour12 = hour24 % 12;
            if (hour12 == 0)
            {
                hour12 = 12;
            }

            string designator = hour24 >= 12 ? "PM" : "AM";
            return $"{hour12}:{minute:00} {designator}";
        }

        /// <summary>
        /// Read the native day index, falling back to the lightweight mod clock if unavailable.
        /// </summary>
        /// <returns>Native <see cref="TimeManager.DayIndex"/> or a zero-based fallback day.</returns>
        internal int GetCurrentDayIndexInternal()
        {
            try
            {
                var timeManager = TimeManager.Instance;
                if (timeManager != null)
                {
                    return timeManager.DayIndex;
                }
            }
            catch
            {
            }

            return Mathf.Max(0, GameTimeManager.Instance.GetCurrentGameDay() - 1);
        }

        /// <summary>
        /// Read the native minute of day, falling back to the lightweight mod clock if unavailable.
        /// </summary>
        /// <returns>Current native or fallback minute after midnight.</returns>
        internal int GetCurrentMinuteOfDayInternal()
        {
            try
            {
                var timeManager = TimeManager.Instance;
                if (timeManager != null)
                {
                    int currentTime = timeManager.CurrentTime;
                    int hour = Mathf.Clamp(currentTime / 100, 0, 23);
                    int minute = Mathf.Clamp(currentTime % 100, 0, 59);
                    return hour * 60 + minute;
                }
            }
            catch
            {
            }

            int fallbackHour = GameTimeManager.Instance.GetCurrentGameHour();
            int fallbackMinute = GameTimeManager.Instance.GetCurrentGameMinute();
            return Mathf.Clamp(fallbackHour, 0, 23) * 60 + Mathf.Clamp(fallbackMinute, 0, 59);
        }

        /// <summary>
        /// Resolve the current daily check-in status through the manager-owned seam.
        /// </summary>
        /// <param name="player">Parolee whose scheduled window should be evaluated.</param>
        /// <param name="windowText">Player-facing window text when a schedule exists.</param>
        /// <param name="applyConsequences">Whether a missed window should record its consequence.</param>
        /// <returns>The current check-in status.</returns>
        public DailyCheckInStatus GetDailyCheckInStatus(Player player, out string windowText, bool applyConsequences = true)
        {
            var status = Core.ResolveParoleManager().GetDailyCheckInStatus(player, out windowText, applyConsequences);
            return (DailyCheckInStatus)(int)status;
        }

        /// <summary>
        /// Attempt to enter a daily check-in session through the manager-owned seam.
        /// </summary>
        /// <param name="player">Parolee attempting the check-in.</param>
        /// <param name="status">Status explaining whether entry was allowed.</param>
        /// <param name="windowText">Player-facing scheduled window text.</param>
        /// <returns><see langword="true"/> when the session guard was acquired.</returns>
        public bool TryBeginCheckInSession(Player player, out DailyCheckInStatus status, out string windowText)
        {
            bool allowed = Core.ResolveParoleManager().TryBeginCheckInSession(player, out var managerStatus, out windowText);
            status = (DailyCheckInStatus)(int)managerStatus;
            return allowed;
        }

        /// <summary>
        /// Release the manager-owned active check-in session guard.
        /// </summary>
        /// <param name="player">Parolee whose session should end.</param>
        public void EndCheckInSession(Player player)
        {
            Core.ResolveParoleManager().EndCheckInSession(player);
        }

        /// <summary>
        /// Mark the current check-in complete and apply its rapport/LSI follow-up.
        /// </summary>
        /// <param name="player">Parolee who completed the scheduled appointment.</param>
        /// <returns><see langword="true"/> when an in-memory requirement was completed.</returns>
        public bool NotifyDailyCheckInCompleted(Player player)
        {
            return Core.ResolveParoleManager().NotifyDailyCheckInCompleted(player);
        }

        /// <summary>
        /// Record an active parole warrant and ask the native law system to begin pursuit.
        /// </summary>
        /// <param name="player">Parolee for whom the warrant is being issued.</param>
        /// <param name="cause">Parole-specific cause preserved for later custody processing.</param>
        /// <remarks>
        /// The native pursuit transport currently uses a <c>WitnessIntimidation</c> crime so
        /// police response can start. The actual parole cause is stored separately on the
        /// jail-system pending-cause map, while the RapSheet flag and local key set prevent
        /// duplicate warrant logging.
        /// </remarks>
        internal void IssueAgentWarrantInternal(Player player, ViolationType cause = ViolationType.Other)
        {
            if (player == null)
            {
                return;
            }

            // The native law system needs an ordinary Crime instance to begin its normal
            // police response. Preserve the actual parole charge separately so custody does
            // not later record that transport crime (currently Witness Intimidation) as the
            // reason for the warrant arrest.
            Core.Instance?.JailSystem?.RegisterPendingParoleArrestCause(player, cause);

            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                rapSheet.CurrentParoleRecord.SetActiveAgentWarrant(true);
                Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
            }

            bool isNewWarrant = _playersWithActiveWarrants.Add(GetPlayerRuntimeKey(player));
            TriggerPolicePursuitForWarrant(player);

            if (isNewWarrant)
            {
                ModLogger.Warn($"ParoleSystem: Active warrant issued for {player.name}");
            }
        }

        private bool IsAuthorityForParoleActions() => IsAuthorityForParoleActionsInternal();

        /// <summary>
        /// Trigger the native police pursuit used to enforce an active parole warrant.
        /// </summary>
        /// <param name="player">Wanted parolee to pass to the law and crime systems.</param>
        /// <remarks>
        /// This is a best-effort bridge: failure is logged, while the RapSheet/local warrant
        /// markers remain owned by the caller.
        /// </remarks>
        private void TriggerPolicePursuitForWarrant(Player player)
        {
            try
            {
                var lawManager = LawManager.Instance;
                if (lawManager != null)
                {
                    lawManager.PoliceCalled(player, new WitnessIntimidation());
                }

                if (player?.CrimeData != null)
                {
                    player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleSystem: Failed to trigger warrant pursuit for {player?.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Reassert the arresting pursuit level for a wanted parolee at a throttled interval.
        /// </summary>
        /// <param name="player">Loaded scene player to enforce.</param>
        /// <remarks>
        /// The throttle uses Unity's scaled <see cref="Time.time"/>. Entering jail clears the
        /// local warrant marker and persisted RapSheet flag; otherwise the method only nudges
        /// pursuit back to <c>Arresting</c> when another system has changed it.
        /// </remarks>
        private void EnforceActiveWarrant(Player player)
        {
            string playerKey = GetPlayerRuntimeKey(player);

            if (player == null || !_playersWithActiveWarrants.Contains(playerKey))
            {
                return;
            }

            if (Core.ResolveJailTimeTracker().IsInJail(player))
            {
                _playersWithActiveWarrants.Remove(playerKey);
                _lastWarrantEnforcementTime.Remove(playerKey);
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    rapSheet.CurrentParoleRecord.SetActiveAgentWarrant(false);
                    Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
                }
                ModLogger.Info($"ParoleSystem: Cleared active warrant for {player.name} after arrest");
                return;
            }

            float now = Time.time;
            if (_lastWarrantEnforcementTime.TryGetValue(playerKey, out float lastEnforcementTime) &&
                now - lastEnforcementTime < ACTIVE_WARRANT_ENFORCEMENT_INTERVAL_SECONDS)
            {
                return;
            }

            _lastWarrantEnforcementTime[playerKey] = now;

            if (player.CrimeData != null && player.CrimeData.CurrentPursuitLevel != PlayerCrimeData.EPursuitLevel.Arresting)
            {
                player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
            }
        }

        /// <summary>
        /// Queue one supervising-officer message for a later delivery attempt.
        /// </summary>
        /// <param name="player">Player whose officer message should be retried.</param>
        /// <param name="message">Non-empty message to retain.</param>
        /// <remarks>
        /// A same-player, same-message entry is de-duplicated. Retry deadlines use Unity's
        /// scaled <see cref="Time.time"/> and are consumed by <see cref="ProcessPendingOfficerText"/>.
        /// </remarks>
        private void QueueOfficerTextRetry(Player player, string message)
        {
            if (player == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            if (_pendingOfficerTexts.TryGetValue(playerKey, out var existing) && existing != null && existing.Message == message)
            {
                return;
            }

            _pendingOfficerTexts[playerKey] = new PendingOfficerText
            {
                Message = message,
                Attempts = 0,
                NextAttemptTime = Time.time + TEXT_MESSAGE_RETRY_INTERVAL_SECONDS
            };
        }

        /// <summary>
        /// Attempt delivery of a queued officer message when its scaled retry deadline arrives.
        /// </summary>
        /// <param name="player">Player whose queued message should be processed.</param>
        /// <remarks>
        /// Attempts are spaced by the configured scaled-time interval and removed on success or
        /// after the maximum attempt count. A failed attempt may leave the record queued for a
        /// later monitor tick.
        /// </remarks>
        private void ProcessPendingOfficerText(Player player)
        {
            if (player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            if (!_pendingOfficerTexts.TryGetValue(playerKey, out var pending) || pending == null)
            {
                return;
            }

            if (Time.time < pending.NextAttemptTime)
            {
                return;
            }

            pending.Attempts++;
            pending.NextAttemptTime = Time.time + TEXT_MESSAGE_RETRY_INTERVAL_SECONDS;

            if (SendSupervisingOfficerText(player, pending.Message, allowRetryQueue: false))
            {
                _pendingOfficerTexts.Remove(playerKey);
                return;
            }

            if (pending.Attempts >= TEXT_MESSAGE_RETRY_MAX_ATTEMPTS)
            {
                _pendingOfficerTexts.Remove(playerKey);
                ModLogger.Warn($"ParoleSystem: Failed to deliver supervising officer text after retries for {player.name}");
                return;
            }

            _pendingOfficerTexts[playerKey] = pending;
        }

        /// <summary>
        /// Route a supervising-officer text message through the parole domain.
        /// NPC-side parole behaviors should use this instead of resolving text ownership themselves.
        /// </summary>
        public bool SendSupervisingOfficerText(Player player, string message, bool allowRetryQueue = true)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            try
            {
                var npcManager = Core.Instance?.NpcManager;
                npcManager?.EnsureDynamicParoleOfficerManager();

                var supervisingOfficer = npcManager?.GetSupervisingOfficer();
                if (supervisingOfficer == null)
                {
                    if (allowRetryQueue)
                    {
                        QueueOfficerTextRetry(player, message);
                    }

                    ModLogger.Warn($"ParoleSystem: Supervising officer unavailable for text message: {message}");
                    return false;
                }

                if (supervisingOfficer.TrySendNPCTextMessage(message))
                {
                    if (player != null)
                    {
                        _pendingOfficerTexts.Remove(GetPlayerRuntimeKey(player));
                    }

                    return true;
                }

                if (allowRetryQueue)
                {
                    QueueOfficerTextRetry(player, message);
                }

                supervisingOfficer.TrySendNPCMessage(message, 5f);
                return false;
            }
            catch (Exception ex)
            {
                if (allowRetryQueue)
                {
                    QueueOfficerTextRetry(player, message);
                }

                ModLogger.Error($"ParoleSystem: Failed to send supervising officer text to {player?.name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clear transient check-in, officer-text, warrant, and persisted warrant flags for a player.
        /// </summary>
        /// <param name="player">Player whose parole runtime flags should be cleared.</param>
        /// <remarks>
        /// This helper does not remove the active runtime parole record or end the RapSheet
        /// parole term; completion and revocation own those operations separately.
        /// </remarks>
        private void ClearParoleRuntimeFlags(Player player)
        {
            if (player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            Core.ResolveParoleManager().ClearCheckInState(player);
            _pendingOfficerTexts.Remove(playerKey);
            _playersWithActiveWarrants.Remove(playerKey);
            _lastWarrantEnforcementTime.Remove(playerKey);

            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                rapSheet.CurrentParoleRecord.SetActiveAgentWarrant(false);
                Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
            }
        }

        /// <summary>
        /// Callback when parole period completes via game time tracking
        /// </summary>
        private void OnParoleComplete(Player player)
        {
            if (_paroleRecords.TryGetValue(GetPlayerRuntimeKey(player), out var record))
            {
                if (record.Status == ParoleStatus.Active)
                {
                    CompleteParole(record);
                }
            }
        }

        /// <summary>
        /// Initialize parole tracking in RapSheet system with LSI assessment
        /// This integrates the ParoleSystem with the RapSheet/LSI tracking
        /// </summary>
        private void InitializeParoleTracking(Player player, float duration)
        {
            try
            {
                // Get cached rap sheet (loads from file only once)
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet == null)
                {
                    ModLogger.Warn($"[LSI] Failed to get rap sheet for {player.name}");
                    return;
                }

                // Start parole with initial LSI assessment
                bool success = rapSheet.StartParoleWithAssessment(duration);

                if (success)
                {
                    ModLogger.Info($"[LSI] Parole tracking initialized for {player.name} - LSI Level: {rapSheet.LSILevel}");

                    // Mark rap sheet as changed - game's save system handles saving automatically
                    Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
                }
                else
                {
                    ModLogger.Warn($"[LSI] Failed to start parole tracking for {player.name}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[LSI] Error initializing parole tracking for {player.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Monitor active parole, officer reminders, and condition enforcement
        /// </summary>
        /// <param name="record">Live runtime record to monitor until it completes or changes state.</param>
        /// <remarks>
        /// The monitor exits without completing the term when the scene-bound player is not
        /// live; loaded-save recovery reattaches a monitor later. Remaining duration comes
        /// from <see cref="ParoleTimeTracker"/>. Authority-gated check-ins, warrants, officer
        /// retries, and condition checks run once per loop. The loop's one-second
        /// <see cref="WaitForSeconds"/> delay is Unity-scaled, not wall-clock time.
        /// </remarks>
        private IEnumerator MonitorParole(ParoleRuntimeRecord record)
        {
            if (!HasLiveParolePlayer(record))
            {
                yield break;
            }

            ModLogger.Debug($"Monitoring parole for {record.Player.name}");

            while (record.Status == ParoleStatus.Active)
            {
                // The parole record survives scene transitions, but Player is a scene
                // object. Do not treat a destroyed menu-transition player as a completed
                // parole term; the runtime record will be reattached after the next load.
                if (!HasLiveParolePlayer(record))
                {
                    yield break;
                }

                // Update time remaining from ParoleTimeTracker
                record.TimeRemainingGameMinutes = Core.ResolveParoleManager().GetRemainingTime(record.Player);

                // Check if parole is complete
                if (record.TimeRemainingGameMinutes <= 0)
                {
                    break;
                }

                float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();

                if (IsAuthorityForParoleActions())
                {
                    int currentDayIndex = GetCurrentDayIndexInternal();
                    int currentMinuteOfDay = GetCurrentMinuteOfDayInternal();
                    var paroleManager = Core.ResolveParoleManager();
                    paroleManager.ProcessDailyCheckInInstruction(record.Player, currentDayIndex);
                    paroleManager.ProcessUpcomingCheckInReminder(record.Player, currentDayIndex, currentMinuteOfDay);
                    paroleManager.ProcessExpiredDailyCheckIn(record.Player, currentDayIndex, currentMinuteOfDay);
                    EnforceActiveWarrant(record.Player);
                    ProcessPendingOfficerText(record.Player);

                    // Process parole condition enforcement
                    try
                    {
                        var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(record.Player);
                        if (rapSheet != null)
                        {
                            var paroleConditionManager = GetParoleConditionManager();
                            var homeVisitSystem = GetHomeVisitSystem();
                            var paroleFeeSystem = GetParoleFeeSystem();

                            // Electronic monitoring curfew check (Severe LSI only - always-on)
                            if (rapSheet.LSILevel == LSILevel.Severe &&
                                rapSheet.CurrentParoleRecord?.IsConditionActive("curfew") == true)
                            {
                                CheckElectronicCurfew(record.Player, rapSheet, currentMinuteOfDay);
                            }

                            // Check and process home visits
                            homeVisitSystem.CheckAndProcessHomeVisit(record.Player, rapSheet);

                            // Check and assess fees
                            paroleFeeSystem.CheckAndAssessFees(record.Player, rapSheet);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Error in parole condition monitoring: {ex.Message}");
                    }
                }

                // Check for parole violations
                //yield return CheckForViolations(record);

                yield return new WaitForSeconds(1f); // Check every scaled Unity second.
            }

            // Parole completed or violated
            if (record.Status == ParoleStatus.Active && HasLiveParolePlayer(record))
            {
                CompleteParole(record);
            }
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        /// <summary>
        /// Check that a runtime parole record still references a live scene player.
        /// </summary>
        /// <param name="record">Runtime record to inspect.</param>
        /// <returns><see langword="true"/> when the record and its player reference are valid.</returns>
        private static bool HasLiveParolePlayer(ParoleRuntimeRecord record)
        {
            return record != null && record.Player != null;
        }

        /// <summary>
        /// Handle parole violation consequences
        /// </summary>
        /// <param name="record">Runtime record receiving the violation consequence.</param>
        /// <remarks>
        /// This coroutine contains the older contraband-violation ladder, including extension
        /// or revocation. The current monitor leaves its <c>CheckForViolations</c> call
        /// commented out, so this is an incomplete scaffold unless another caller invokes it.
        /// </remarks>
        private IEnumerator HandleParoleViolation(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Handling parole violation for {record.Player.name}");

            // Adjust rapport for violation
            try
            {
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(record.Player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    rapSheet.CurrentParoleRecord.AdjustRapport(-15f);
                    rapSheet.CurrentParoleRecord.ResetHighComplianceDays();
                    Core.ResolveRapSheetManager().MarkRapSheetChanged(record.Player);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error adjusting rapport for violation: {ex.Message}");
            }

            // Notify supervising officer if available
            NotifySupervisingOfficerOfViolation(record.Player, "Contraband found during search");

            // Determine violation severity
            if (record.ViolationCount >= 3)
            {
                // Major violation - revoke parole
                record.Status = ParoleStatus.Revoked;
                ModLogger.Info($"Parole revoked for {record.Player.name} due to multiple violations");

                // TODO: Implement parole revocation consequences
                // This could involve:
                // 1. Immediate arrest
                // 2. Extended jail time
                // 3. Increased fines
                // 4. Permanent record

                yield return HandleParoleRevocation(record);
            }
            else
            {
                // Minor violation - extend parole (in game minutes)
                float extension = record.DurationGameMinutes * 0.2f; // 20% extension
                record.DurationGameMinutes += extension;
                record.TimeRemainingGameMinutes += extension;

                try
                {
                    var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(record.Player);
                    if (rapSheet?.CurrentParoleRecord?.ExtendActiveParole(extension) == true)
                    {
                        Core.ResolveRapSheetManager().MarkRapSheetChanged(record.Player);
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"ParoleSystem: Failed to persist parole extension for {record.Player.name}: {ex.Message}");
                }

                // Update ParoleTimeTracker with new duration
                Core.ResolveParoleManager().StopTracking(record.Player);
                Core.ResolveParoleManager().StartTracking(record.Player, record.DurationGameMinutes, OnParoleComplete);

                ModLogger.Info($"Parole extended for {record.Player.name} by {extension} game minutes ({GameTimeManager.FormatGameTime(extension)}) due to violation");

                // TODO: Show violation warning to player
                yield return new WaitForSeconds(1f);
            }
        }

        /// <summary>
        /// Notify supervising officer of a violation
        /// </summary>
        private void NotifySupervisingOfficerOfViolation(Player player, string violationType)
        {
            if (player == null) {
                ModLogger.Error("NotifySupervisingOfficerOfViolation: Player is null");
                return;
            }

            try
            {
                var npcManager = Core.Instance?.NpcManager;
                if (npcManager != null)
                {
                    var supervisingOfficer = npcManager.GetSupervisingOfficer();
                    if (supervisingOfficer != null)
                    {
                        supervisingOfficer.HandleViolation(player, violationType);
                        ModLogger.Debug($"Notified supervising officer of violation '{violationType}' for {player.name}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error notifying supervising officer of violation: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle parole revocation (send back to jail)
        /// </summary>
        /// <param name="record">Active runtime record being revoked.</param>
        /// <remarks>
        /// Revocation stops the tracker, ends and archives the persisted parole record, hides
        /// the status UI, clears transient flags, raises the end event, then waits one scaled
        /// second before handing the player to the jail manager. The final arrest is therefore
        /// a delayed manager handoff, not an immediate operation in this method.
        /// </remarks>
        private IEnumerator HandleParoleRevocation(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Handling parole revocation for {record.Player.name}");

            // Stop tracking with ParoleTimeTracker
            Core.ResolveParoleManager().StopTracking(record.Player);

            // End parole in RapSheet and archive it
            try
            {
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(record.Player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    rapSheet.CurrentParoleRecord.EndParole();
                    // Move current parole record to past records
                    rapSheet.ArchiveCurrentParoleRecord();
                    Core.ResolveRapSheetManager().MarkRapSheetChanged(record.Player);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error ending parole in RapSheet: {ex.Message}");
            }

            // Hide parole status UI
            try
            {
                var uiManager = Core.ResolveUIManager();
                uiManager.HideParoleStatus();
                ModLogger.Info($"Parole status UI hidden for {record.Player.name} (revoked)");
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to hide parole status UI: {ex.Message}");
            }

            // Remove from active parole
            _paroleRecords.Remove(record.PlayerKey);
            ClearParoleRuntimeFlags(record.Player);

            // Emit parole ended event
            RaiseParoleEnded(record.Player);
            ModLogger.Debug($"ParoleSystem: Emitted parole-end lifecycle event for {record.Player.name} (revoked)");

            // TODO: Implement revocation consequences
            // This could involve:
            // 1. Immediate arrest by parole officer
            // 2. Transfer to jail system
            // 3. Harsher sentencing

            yield return new WaitForSeconds(1f);

            // Hand off to the jail manager seam
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null && record.Player != null)
            {
                yield return jailManager.HandleImmediateArrest(record.Player);
            }
        }

        /// <summary>
        /// Complete parole for a player (public method for external calls)
        /// </summary>
        /// <param name="player">Player whose active or persisted parole should be completed.</param>
        /// <remarks>
        /// An active runtime record uses the normal completion path. When no runtime record is
        /// present, the method still attempts to end/archive an active RapSheet record and
        /// raise the end event, then clears transient runtime flags.
        /// </remarks>
        public void CompleteParoleForPlayer(Player player)
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot complete parole for null player");
                return;
            }

            if (_paroleRecords.TryGetValue(GetPlayerRuntimeKey(player), out var record))
            {
                CompleteParole(record);
            }
            else
            {
                ModLogger.Warn($"No active parole record found for {player.name} to complete");
                // Still try to end parole in RapSheet if it exists
                try
                {
                    var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                    if (rapSheet?.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                    {
                        var completingRecord = rapSheet.CurrentParoleRecord;
                        completingRecord.EndParole();
                        try
                        {
                            ParoleCompletionRewards.GrantCompletionRewards(
                                player,
                                rapSheet,
                                completingRecord);
                        }
                        catch (System.Exception rewardEx)
                        {
                            ModLogger.Error($"Error granting parole completion rewards for {player.name}: {rewardEx.Message}");
                        }
                        rapSheet.ArchiveCurrentParoleRecord();
                        Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
                        ModLogger.Info($"Completed parole in RapSheet for {player.name}");

                        // Emit parole ended event
                        RaiseParoleEnded(player);
                        ModLogger.Debug($"ParoleSystem: Emitted parole-end lifecycle event for {player.name} (completed via CompleteParoleForPlayer)");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error completing parole in RapSheet: {ex.Message}");
                }
            }

            ClearParoleRuntimeFlags(player);
        }

        /// <summary>
        /// Electronic monitoring curfew check (Severe LSI only).
        /// Called every tick from MonitorParole for always-on curfew detection.
        /// </summary>
        /// <param name="player">Scene player being monitored.</param>
        /// <param name="rapSheet">Player RapSheet containing the active curfew condition.</param>
        /// <param name="currentMinuteOfDay">Native/fallback current minute used for curfew evaluation.</param>
        private void CheckElectronicCurfew(Player player, RapSheet rapSheet, int currentMinuteOfDay)
        {
            if (rapSheet?.CurrentParoleRecord == null) return;

            if (!CurfewCondition.IsPastCurfew(rapSheet.LSILevel, currentMinuteOfDay))
                return;

            // Check if player is at home (at home = not a violation)
            if (PlayerHomeDetector.IsPlayerAtHome(player))
                return;

            var paroleRecord = rapSheet.CurrentParoleRecord;

            // Throttle: only check once per 5 game minutes to avoid spam
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float lastCurfewCheck = paroleRecord.GetLastInteractionGameTime();
            if (currentGameTime - lastCurfewCheck < 5f)
                return;

            ReportCurfewViolation(player, rapSheet,
                $"electronic monitoring at {FormatMinuteOfDayInternal(currentMinuteOfDay)}");
        }

        /// <summary>
        /// Applies the one authoritative curfew-enforcement ladder.  Both electronic
        /// monitoring and an officer witness call this method so warnings persist and
        /// cannot diverge from formal violations or warrant escalation.
        /// </summary>
        internal void ReportCurfewViolation(Player player, RapSheet rapSheet, string source)
        {
            if (player == null || rapSheet?.CurrentParoleRecord == null)
            {
                return;
            }

            var paroleRecord = rapSheet.CurrentParoleRecord;
            string playerKey = GetPlayerRuntimeKey(player);
            if (_playersWithActiveWarrants.Contains(playerKey))
            {
                return;
            }

            int warningCount = paroleRecord.RecordConditionWarning("curfew");
            string curfewTime = CurfewCondition.GetCurfewDisplayTime(rapSheet.LSILevel);

            if (warningCount == 1)
            {
                paroleRecord.AdjustComplianceScore(-5f);
                paroleRecord.AdjustRapport(-5f);
                SendSupervisingOfficerText(player,
                    $"Curfew warning: you are outside past your {curfewTime} curfew ({source}). Return home immediately.");
                ModLogger.Info($"[CURFEW] Warning 1 recorded for {player.name} via {source}");
            }
            else
            {
                bool issueWarrant = warningCount >= 3;
                float severity = issueWarrant ? 2.5f : 1.5f;
                string details = issueWarrant
                    ? $"Repeated curfew violation (warning {warningCount}) detected via {source}"
                    : $"Curfew violation detected via {source}";

                rapSheet.AddParoleViolation(new ViolationRecord(ViolationType.CurfewViolation, details, severity));
                paroleRecord.AdjustComplianceScore(issueWarrant ? -10f : -5f);
                paroleRecord.AdjustRapport(issueWarrant ? -15f : -10f);

                if (issueWarrant)
                {
                    IssueAgentWarrantInternal(player, ViolationType.CurfewViolation);
                    SendSupervisingOfficerText(player,
                        "Repeated curfew violation. A parole warrant has been issued.");
                    ModLogger.Warn($"[CURFEW] Warrant escalation recorded for {player.name} via {source}");
                }
                else
                {
                    SendSupervisingOfficerText(player,
                        "Second curfew warning. A formal parole violation has been recorded.");
                    ModLogger.Info($"[CURFEW] Formal violation recorded for {player.name} via {source}");
                }
            }

            paroleRecord.RecordInteraction();
            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
        }

        /// <summary>
        /// Evaluate LSI step-down eligibility.
        /// Called after successful daily check-ins.
        /// Current enforcement requires compliance >= 80 and three consecutive successful
        /// check-in calls. The recent-violation criterion is not enforceable yet because
        /// violation records expose DateTime rather than game-time age; the current loop
        /// computes an unused cutoff and breaks after the first record without rejecting the
        /// step-down.
        /// </summary>
        /// <param name="player">Parolee whose compliance streak should be evaluated.</param>
        public void EvaluateLSIStepDown(Player player)
        {
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            if (rapSheet?.CurrentParoleRecord == null) return;

            var paroleRecord = rapSheet.CurrentParoleRecord;
            float compliance = paroleRecord.GetComplianceScore();

            // Check compliance threshold
            if (compliance < 80f)
            {
                paroleRecord.ResetHighComplianceDays();
                return;
            }

            // The intended recent-violation check cannot compare DateTime records with the
            // native game clock yet. Keep the calculated cutoff visible as documentation of
            // the missing bridge; it is currently unused.
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float threeDaysAgo = currentGameTime - (GAME_MINUTES_PER_DAY * 3);
            foreach (var violation in paroleRecord.GetViolations())
            {
                // ViolationRecord uses DateTime, not game time. The loop currently exits
                // after the first record but does not reject the step-down; this is a
                // deliberate documentation of the incomplete timing gate, not enforcement.
                break;
            }

            // Increment streak
            paroleRecord.IncrementHighComplianceDays();
            int streak = paroleRecord.GetConsecutiveHighComplianceDays();

            ModLogger.Debug($"[STEP-DOWN] {player.name} compliance streak: {streak}/3 days (compliance: {compliance:F1})");

            if (streak >= 3)
            {
                // Check if we've already stepped down the maximum amount
                if (rapSheet.ComplianceLSIReduction >= 40)
                {
                    ModLogger.Debug($"[STEP-DOWN] {player.name} already at maximum step-down (reduction: {rapSheet.ComplianceLSIReduction})");
                    return;
                }

                // Apply step-down
                LSILevel oldLevel = rapSheet.LSILevel;
                rapSheet.ApplyLSIStepDown();
                rapSheet.UpdateLSILevel();
                LSILevel newLevel = rapSheet.LSILevel;
                paroleRecord.IncrementLSIStepDownCount();
                paroleRecord.ResetHighComplianceDays();

                Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

                if (oldLevel != newLevel)
                {
                    SendSupervisingOfficerText(player,
                        $"Good news - your supervision level has been reduced from {oldLevel} to {newLevel} due to sustained compliance. Keep it up.");
                    ModLogger.Info($"[STEP-DOWN] LSI stepped down for {player.name}: {oldLevel} → {newLevel}");
                }
                else
                {
                    SendSupervisingOfficerText(player,
                        "Your good behavior has been noted. Supervision intensity reduced.");
                    ModLogger.Info($"[STEP-DOWN] LSI reduction applied for {player.name} but level unchanged ({newLevel}). Reduction: {rapSheet.ComplianceLSIReduction}");
                }
            }
        }

        /// <summary>
        /// Complete parole successfully
        /// </summary>
        /// <param name="record">Live runtime record whose term has reached completion.</param>
        /// <remarks>
        /// Completion marks the runtime state, stops the tracker, clears release grace, ends
        /// and archives RapSheet state, hides UI, raises the end event, grants rewards, clears
        /// transient flags, and finally removes the runtime record. The officer despawn helper
        /// is a compatibility no-op because dynamic NPC ownership lives elsewhere.
        /// </remarks>
        private void CompleteParole(ParoleRuntimeRecord record)
        {
            if (!HasLiveParolePlayer(record))
            {
                return;
            }

            ModLogger.Info($"Parole completed successfully for {record.Player.name}");

            record.Status = ParoleStatus.Completed;
            record.TimeRemainingGameMinutes = 0f;

            // Stop tracking with ParoleTimeTracker
            Core.ResolveParoleManager().StopTracking(record.Player);

            // Clear release time grace period (parole is complete)
            ParoleSearchSystem.Instance.ClearReleaseTime(record.Player);

            // End parole, claim rewards while the completing record is still current,
            // then archive that exact rewarded record.
            try
            {
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(record.Player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    var completingRecord = rapSheet.CurrentParoleRecord;
                    completingRecord.EndParole();

                    try
                    {
                        ParoleCompletionRewards.GrantCompletionRewards(
                            record.Player,
                            rapSheet,
                            completingRecord);
                    }
                    catch (System.Exception rewardEx)
                    {
                        ModLogger.Error($"Error granting parole completion rewards for {record.Player.name}: {rewardEx.Message}");
                    }

                    // Move current parole record to past records
                    rapSheet.ArchiveCurrentParoleRecord();
                    Core.ResolveRapSheetManager().MarkRapSheetChanged(record.Player);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error ending parole in RapSheet: {ex.Message}");
            }

            // Hide parole status UI
            try
            {
                var uiManager = Core.ResolveUIManager();
                uiManager.HideParoleStatus();
                ModLogger.Info($"Parole status UI hidden for {record.Player.name}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to hide parole status UI: {ex.Message}");
            }

            // Emit parole ended event
            RaiseParoleEnded(record.Player);
            ModLogger.Debug($"ParoleSystem: Emitted parole-end lifecycle event for {record.Player.name} (completed)");

            // Remove from active parole
            _paroleRecords.Remove(record.PlayerKey);
            ClearParoleRuntimeFlags(record.Player);

            // Check if we can despawn parole officer
            if (_paroleRecords.Count == 0)
            {
                DespawnParoleOfficer();
            }
        }

        /// <summary>
        /// Retain the legacy parole-officer despawn hook for compatibility.
        /// </summary>
        /// <remarks>
        /// DynamicParoleOfficerManager/NpcManager owns the current officer lifecycle, so this
        /// method intentionally performs no despawn operation.
        /// </remarks>
        private void DespawnParoleOfficer()
        {
            ModLogger.Info("Parole Officer NPC despawning removed - feature not implemented");

            // NOTE: NPC despawning functionality has been removed from this mod. No cleanup
            // is needed here because this class does not own a spawned officer.
        }

        /// <summary>
        /// Get parole record for player
        /// </summary>
        public ParoleRuntimeRecord? GetParoleRecord(Player player)
        {
            _paroleRecords.TryGetValue(GetPlayerRuntimeKey(player), out var record);
            return record;
        }

        /// <summary>
        /// Check if player is currently on active parole
        /// </summary>
        public bool IsPlayerOnParole(Player player)
        {
            string playerKey = GetPlayerRuntimeKey(player);
            return _paroleRecords.ContainsKey(playerKey) &&
                   _paroleRecords[playerKey].Status == ParoleStatus.Active;
        }

        /// <summary>
        /// Extend parole duration for a player (in game minutes)
        /// </summary>
        /// <param name="player">Player whose active runtime term should be extended.</param>
        /// <param name="additionalGameMinutes">Additional duration in game minutes.</param>
        /// <remarks>
        /// This public compatibility path updates the in-memory runtime record and restarts
        /// <see cref="ParoleTimeTracker"/>. It does not persist the extension to RapSheet;
        /// the violation-handling path has its own persistence step.
        /// </remarks>
        public void ExtendParole(Player player, float additionalGameMinutes)
        {
            if (_paroleRecords.TryGetValue(GetPlayerRuntimeKey(player), out var record))
            {
                record.DurationGameMinutes += additionalGameMinutes;
                record.TimeRemainingGameMinutes += additionalGameMinutes;

                // Update ParoleTimeTracker with new duration
                Core.ResolveParoleManager().StopTracking(player);
                Core.ResolveParoleManager().StartTracking(player, record.DurationGameMinutes, OnParoleComplete);

                ModLogger.Info($"Extended parole for {player.name} by {additionalGameMinutes} game minutes ({GameTimeManager.FormatGameTime(additionalGameMinutes)})");
            }
        }
    }
}

namespace Behind_Bars.Systems.NPCs
{
}
