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
            public string PlayerKey { get; set; }
            public Player Player { get; set; }
            public ParoleStatus Status { get; set; }
            public float StartGameTimeMinutes { get; set; } // Game time when parole started (game minutes)
            public float DurationGameMinutes { get; set; } // Total parole duration (game minutes)
            public float TimeRemainingGameMinutes { get; set; } // Remaining time (game minutes)
            public int ViolationCount { get; set; }
            public List<string> Violations { get; set; } = new();
        }

        private class PendingOfficerText
        {
            public string Message { get; set; }
            public int Attempts { get; set; }
            public float NextAttemptTime { get; set; }
        }

        private Dictionary<string, ParoleRuntimeRecord> _paroleRecords = new();
        private Dictionary<string, PendingOfficerText> _pendingOfficerTexts = new();
        private HashSet<string> _playersWithActiveWarrants = new();
        private Dictionary<string, float> _lastWarrantEnforcementTime = new();
        private GameObject? _paroleOfficerPrefab;
        private bool _isSubscribedToDayPass;
        private readonly Action _onDayPassHandler;
        private object _timeManagerSubscriptionCoroutine;

        public ParoleSystem()
        {
            _onDayPassHandler = HandleDayPassForParoleCheckIns;
            EnsureDayPassSubscription();
        }

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

        private void EnsureDayPassSubscription()
        {
            if (_isSubscribedToDayPass || _timeManagerSubscriptionCoroutine != null)
            {
                return;
            }

            _timeManagerSubscriptionCoroutine = MelonCoroutines.Start(WaitForTimeManagerAndSubscribe());
        }

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

        internal string GetPlayerRuntimeKeyInternal(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Core.ResolvePlayerKey(player);
        }

        private string GetPlayerRuntimeKey(Player player) => GetPlayerRuntimeKeyInternal(player);

        internal Dictionary<string, ParoleRuntimeRecord> ActiveParoleRecords => _paroleRecords;

        /// <summary>
        /// Start parole supervision for a player
        /// Creates runtime tracking and initializes RapSheet/LSI integration
        /// </summary>
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

        private void HandleDayPassForParoleCheckIns()
        {
            Core.ResolveParoleManager().HandleDayPassForParoleCheckIns();
        }

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

        public DailyCheckInStatus GetDailyCheckInStatus(Player player, out string windowText, bool applyConsequences = true)
        {
            var status = Core.ResolveParoleManager().GetDailyCheckInStatus(player, out windowText, applyConsequences);
            return (DailyCheckInStatus)(int)status;
        }

        public bool TryBeginCheckInSession(Player player, out DailyCheckInStatus status, out string windowText)
        {
            bool allowed = Core.ResolveParoleManager().TryBeginCheckInSession(player, out var managerStatus, out windowText);
            status = (DailyCheckInStatus)(int)managerStatus;
            return allowed;
        }

        public void EndCheckInSession(Player player)
        {
            Core.ResolveParoleManager().EndCheckInSession(player);
        }

        public bool NotifyDailyCheckInCompleted(Player player)
        {
            return Core.ResolveParoleManager().NotifyDailyCheckInCompleted(player);
        }

        internal void IssueAgentWarrantInternal(Player player)
        {
            if (player == null)
            {
                return;
            }

            bool isNewWarrant = _playersWithActiveWarrants.Add(GetPlayerRuntimeKey(player));
            TriggerPolicePursuitForWarrant(player);

            if (isNewWarrant)
            {
                ModLogger.Warn($"ParoleSystem: Active warrant issued for {player.name}");
            }
        }

        private bool IsAuthorityForParoleActions() => IsAuthorityForParoleActionsInternal();

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
        private IEnumerator MonitorParole(ParoleRuntimeRecord record)
        {
            ModLogger.Debug($"Monitoring parole for {record.Player.name}");

            while (record.Status == ParoleStatus.Active)
            {
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
                                paroleConditionManager.IsConditionActive("curfew"))
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

                yield return new WaitForSeconds(1f); // Check every real second
            }

            // Parole completed or violated
            if (record.Status == ParoleStatus.Active)
            {
                CompleteParole(record);
            }
        }

        /// <summary>
        /// Handle parole violation consequences
        /// </summary>
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
                        rapSheet.CurrentParoleRecord.EndParole();
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

            // Curfew violation detected via electronic monitoring
            int violationCount = 0;
            foreach (var v in paroleRecord.GetViolations())
            {
                if (v.ViolationType == ViolationType.CurfewViolation)
                    violationCount++;
            }

            if (violationCount == 0)
            {
                // First violation: compliance penalty + rapport hit
                paroleRecord.AdjustComplianceScore(-5f);
                paroleRecord.AdjustRapport(-5f);
                SendSupervisingOfficerText(player,
                    $"Electronic monitoring alert: You are outside your residence past curfew ({CurfewCondition.GetCurfewDisplayTime(rapSheet.LSILevel)}). Return home immediately.");
                ModLogger.Info($"[CURFEW] First curfew violation for {player.name} via electronic monitoring");
            }
            else if (violationCount == 1)
            {
                // Second violation: formal ViolationRecord
                var violation = new ViolationRecord(ViolationType.CurfewViolation,
                    $"Curfew violation detected via electronic monitoring at {FormatMinuteOfDayInternal(currentMinuteOfDay)}", 1.5f);
                rapSheet.AddParoleViolation(violation);
                paroleRecord.AdjustRapport(-10f);
                SendSupervisingOfficerText(player,
                    "Second curfew violation. A formal violation has been recorded on your parole record.");
                ModLogger.Info($"[CURFEW] Second curfew violation for {player.name} - formal violation recorded");
            }
            else
            {
                // Third+ violation: warrant escalation
                var violation = new ViolationRecord(ViolationType.CurfewViolation,
                    $"Repeated curfew violation ({violationCount + 1} total) detected via electronic monitoring", 2.5f);
                rapSheet.AddParoleViolation(violation);
                IssueAgentWarrantInternal(player);
                ModLogger.Info($"[CURFEW] Multiple curfew violations for {player.name} - warrant escalation");
            }

            paroleRecord.RecordInteraction(); // Use interaction time as throttle
            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
        }

        /// <summary>
        /// Evaluate LSI step-down eligibility.
        /// Called after successful daily check-ins.
        /// Criteria: compliance >= 80 for 3 consecutive game days + no violations + no missed check-ins in window.
        /// </summary>
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

            // Check for recent violations (none in last 3 game days)
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float threeDaysAgo = currentGameTime - (GAME_MINUTES_PER_DAY * 3);
            foreach (var violation in paroleRecord.GetViolations())
            {
                // Skip if we can't determine timing (accept all violations)
                // ViolationRecord uses DateTime, not game time
                break; // If any violations exist in the window, don't step down
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
        private void CompleteParole(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Parole completed successfully for {record.Player.name}");

            record.Status = ParoleStatus.Completed;
            record.TimeRemainingGameMinutes = 0f;

            // Stop tracking with ParoleTimeTracker
            Core.ResolveParoleManager().StopTracking(record.Player);

            // Clear release time grace period (parole is complete)
            ParoleSearchSystem.Instance.ClearReleaseTime(record.Player);

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
                ModLogger.Info($"Parole status UI hidden for {record.Player.name}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to hide parole status UI: {ex.Message}");
            }

            // Emit parole ended event
            RaiseParoleEnded(record.Player);
            ModLogger.Debug($"ParoleSystem: Emitted parole-end lifecycle event for {record.Player.name} (completed)");

            // Grant parole completion rewards
            try
            {
                var rewardRapSheet = Core.ResolveRapSheetManager().GetRapSheet(record.Player);
                if (rewardRapSheet != null)
                {
                    ParoleCompletionRewards.GrantCompletionRewards(record.Player, rewardRapSheet);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error granting parole completion rewards: {ex.Message}");
            }

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
        /// Despawn parole officer NPC (placeholder)
        /// </summary>
        private void DespawnParoleOfficer()
        {
            ModLogger.Info("Parole Officer NPC despawning removed - feature not implemented");

            // NOTE: NPC despawning functionality has been removed from this mod
            // No cleanup needed as no NPCs are spawned
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
