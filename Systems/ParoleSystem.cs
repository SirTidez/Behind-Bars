using System.Collections;
using System;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Crimes;
using Behind_Bars.Systems.Jail;
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
    /// Handles parole monitoring, random searches, violations, and completion
    /// Integrates with RapSheet/LSI system for risk assessment
    /// </summary>
    public class ParoleSystem
    {
        #region Events

        /// <summary>
        /// Event fired when parole starts for a player
        /// </summary>
        public static event System.Action<Player> OnParoleStarted;

        /// <summary>
        /// Event fired when parole ends for a player (completed, revoked, or expired)
        /// </summary>
        public static event System.Action<Player> OnParoleEnded;

        #endregion
        private const float PAROLE_DURATION = 600f; // 10 minutes default
        private const float SEARCH_INTERVAL_MIN = 30f; // Minimum time between searches
        private const float SEARCH_INTERVAL_MAX = 120f; // Maximum time between searches
        private const float SEARCH_RADIUS = 50f; // How close PO needs to be to search
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
            public Player Player { get; set; }
            public ParoleStatus Status { get; set; }
            public float StartGameTimeMinutes { get; set; } // Game time when parole started (game minutes)
            public float DurationGameMinutes { get; set; } // Total parole duration (game minutes)
            public float TimeRemainingGameMinutes { get; set; } // Remaining time (game minutes)
            public int ViolationCount { get; set; }
            public float LastSearchGameTimeMinutes { get; set; } // Last search time (game minutes)
            public float NextSearchGameTimeMinutes { get; set; } // Next search time (game minutes)
            public List<string> Violations { get; set; } = new();
        }

        private class DailyCheckInRequirement
        {
            public int DayIndex { get; set; }
            public int WindowStartMinuteOfDay { get; set; }
            public int WindowEndMinuteOfDay { get; set; }
            public bool ReminderSent { get; set; }
            public bool Completed { get; set; }
        }

        private class PendingOfficerText
        {
            public string Message { get; set; }
            public int Attempts { get; set; }
            public float NextAttemptTime { get; set; }
        }

        private Dictionary<Player, ParoleRuntimeRecord> _paroleRecords = new();
        private Dictionary<Player, DailyCheckInRequirement> _dailyCheckInRequirements = new();
        private Dictionary<Player, int> _dailyCheckInScheduledDay = new();
        private Dictionary<Player, PendingOfficerText> _pendingOfficerTexts = new();
        private HashSet<Player> _activeCheckInSessions = new();
        private HashSet<Player> _playersWithActiveWarrants = new();
        private Dictionary<Player, float> _lastWarrantEnforcementTime = new();
        private GameObject? _paroleOfficerPrefab;
        private Transform? _paroleOfficerInstance;
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

        /// <summary>
        /// Start parole supervision for a player
        /// Creates runtime tracking and initializes RapSheet/LSI integration
        /// </summary>
        public void StartParole(Player player, float durationGameMinutes = PAROLE_DURATION, bool showUI = true)
        {
            ModLogger.Info($"Starting parole for {player.name} for {durationGameMinutes} game minutes ({GameTimeManager.FormatGameTime(durationGameMinutes)})");
            EnsureDayPassSubscription();

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float searchIntervalMin = GameTimeManager.RealSecondsToGameMinutes(SEARCH_INTERVAL_MIN);
            float searchIntervalMax = GameTimeManager.RealSecondsToGameMinutes(SEARCH_INTERVAL_MAX);

            ClearParoleRuntimeFlags(player);

            var record = new ParoleRuntimeRecord
            {
                Player = player,
                Status = ParoleStatus.Active,
                StartGameTimeMinutes = currentGameTime,
                DurationGameMinutes = durationGameMinutes,
                TimeRemainingGameMinutes = durationGameMinutes,
                ViolationCount = 0,
                LastSearchGameTimeMinutes = 0f,
                NextSearchGameTimeMinutes = currentGameTime + UnityEngine.Random.Range(searchIntervalMin, searchIntervalMax)
            };

            _paroleRecords[player] = record;

            // Initialize RapSheet ParoleRecord and perform LSI assessment (convert to game minutes)
            InitializeParoleTracking(player, durationGameMinutes);

            // Start tracking with ParoleTimeTracker
            ParoleTimeTracker.Instance.StartTracking(player, durationGameMinutes, OnParoleComplete);

            // Start parole monitoring
            MelonCoroutines.Start(MonitorParole(record));

            // NOTE: Parole officer spawning is now handled by DynamicParoleOfficerManager
            // The old SpawnParoleOfficer() call has been removed

            // Emit parole started event
            OnParoleStarted?.Invoke(player);
            ModLogger.Debug($"ParoleSystem: Emitted OnParoleStarted event for {player.name}");

            // Ensure dynamic parole manager exists and process immediate spawn update.
            EnsureDynamicParoleOfficerManager();
            if (DynamicParoleOfficerManager.Instance != null)
            {
                ModLogger.Info($"ParoleSystem: Triggering immediate parole officer spawn update for {player.name}");
                DynamicParoleOfficerManager.Instance.ForceUpdate();
            }
            else
            {
                ModLogger.Warn("ParoleSystem: DynamicParoleOfficerManager still unavailable after ensure call");
            }

            // Notify supervising officer to start intake process
            NotifySupervisingOfficerOfParoleStart(player);

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

            var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            var paroleRecord = rapSheet?.CurrentParoleRecord;
            if (paroleRecord == null || !paroleRecord.IsOnParole() || paroleRecord.IsPaused())
            {
                return;
            }

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            int currentDayIndex = GetCurrentDayIndex();
            int currentMinuteOfDay = GetCurrentMinuteOfDay();

            if (_paroleRecords.TryGetValue(player, out var existingRecord) && existingRecord != null)
            {
                if (existingRecord.Status == ParoleStatus.Active)
                {
                    if (!JailTimeTracker.Instance.IsInJail(player))
                    {
                        ScheduleDailyCheckIn(player, currentDayIndex, currentMinuteOfDay);
                    }

                    return;
                }

                _paroleRecords.Remove(player);
            }

            var paroleStatus = paroleRecord.GetParoleStatus();
            float remainingGameMinutes = Mathf.Max(0f, paroleStatus.remainingTime);
            if (remainingGameMinutes <= 0f)
            {
                paroleRecord.CheckAndEndExpiredParole();
                RapSheetManager.Instance.MarkRapSheetChanged(player);
                return;
            }

            float searchIntervalMin = GameTimeManager.RealSecondsToGameMinutes(SEARCH_INTERVAL_MIN);
            float searchIntervalMax = GameTimeManager.RealSecondsToGameMinutes(SEARCH_INTERVAL_MAX);

            var runtimeRecord = new ParoleRuntimeRecord
            {
                Player = player,
                Status = ParoleStatus.Active,
                StartGameTimeMinutes = currentGameTime,
                DurationGameMinutes = remainingGameMinutes,
                TimeRemainingGameMinutes = remainingGameMinutes,
                ViolationCount = paroleRecord.GetViolationCount(),
                LastSearchGameTimeMinutes = 0f,
                NextSearchGameTimeMinutes = currentGameTime + UnityEngine.Random.Range(searchIntervalMin, searchIntervalMax)
            };

            _paroleRecords[player] = runtimeRecord;

            ParoleTimeTracker.Instance.StopTracking(player);
            ParoleTimeTracker.Instance.StartTracking(player, remainingGameMinutes, OnParoleComplete);
            MelonCoroutines.Start(MonitorParole(runtimeRecord));

            ModLogger.Info($"ParoleSystem: Restored runtime parole tracking for loaded player {player.name} ({remainingGameMinutes:F1} game minutes remaining)");

            if (!JailTimeTracker.Instance.IsInJail(player))
            {
                ScheduleDailyCheckIn(player, currentDayIndex, currentMinuteOfDay);
            }
        }

        /// <summary>
        /// Notify supervising officer that a player has started parole
        /// </summary>
        private void NotifySupervisingOfficerOfParoleStart(Player player)
        {
            try
            {
                EnsureDynamicParoleOfficerManager();

                var npcManager = PrisonNPCManager.Instance;
                if (npcManager != null)
                {
                    var supervisingOfficer = npcManager.GetSupervisingOfficer();
                    if (supervisingOfficer != null)
                    {
                        supervisingOfficer.HandleParoleIntake(player);
                        ModLogger.Debug($"Notified supervising officer of parole start for {player.name}");
                    }
                    else
                    {
                        ModLogger.Debug($"Supervising officer not available yet for {player.name} - will be handled when spawned");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error notifying supervising officer of parole start: {ex.Message}");
            }
        }

        private void EnsureDynamicParoleOfficerManager()
        {
            try
            {
                if (DynamicParoleOfficerManager.Instance != null)
                    return;

                var existing = BBHelpers.FindObjectOfTypeSafe<DynamicParoleOfficerManager>();
                if (existing != null)
                {
                    existing.Initialize();
                    return;
                }

                var managerObj = new GameObject("DynamicParoleOfficerManager");
                var manager = BBHelpers.AddComponentSafe<DynamicParoleOfficerManager>(managerObj);
                if (manager != null)
                {
                    manager.Initialize();
                    ModLogger.Info("ParoleSystem: Created DynamicParoleOfficerManager on demand");
                }
                else
                {
                    ModLogger.Error("ParoleSystem: Failed to create DynamicParoleOfficerManager on demand");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleSystem: Error ensuring DynamicParoleOfficerManager: {ex.Message}");
            }
        }

        private bool IsAuthorityForParoleActions()
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
            if (!IsAuthorityForParoleActions())
            {
                return;
            }

            if (_paroleRecords.Count == 0)
            {
                return;
            }

            int currentDayIndex = GetCurrentDayIndex();
            int currentMinuteOfDay = GetCurrentMinuteOfDay();

            foreach (var record in _paroleRecords.Values)
            {
                if (record == null || record.Player == null)
                {
                    continue;
                }

                if (record.Status != ParoleStatus.Active)
                {
                    continue;
                }

                if (JailTimeTracker.Instance.IsInJail(record.Player))
                {
                    continue;
                }

                ProcessExpiredDailyCheckIn(record.Player, currentDayIndex, currentMinuteOfDay);
                ScheduleDailyCheckIn(record.Player, currentDayIndex, currentMinuteOfDay);
            }
        }

        private void ScheduleDailyCheckIn(Player player, int currentDayIndex, int currentMinuteOfDay)
        {
            if (_dailyCheckInScheduledDay.TryGetValue(player, out int lastScheduledDay) && lastScheduledDay == currentDayIndex)
            {
                return;
            }

            if (_dailyCheckInRequirements.TryGetValue(player, out var existingRequirement))
            {
                if (existingRequirement.DayIndex == currentDayIndex)
                {
                    _dailyCheckInScheduledDay[player] = currentDayIndex;
                    return;
                }

                if (existingRequirement.Completed)
                {
                    _dailyCheckInRequirements.Remove(player);
                }
            }

            float windowMinutes = GetDailyCheckInWindowMinutes(player);
            if (!TryBuildDailyCheckInWindow(currentMinuteOfDay, windowMinutes, out int windowStartMinuteOfDay, out int windowEndMinuteOfDay))
            {
                _dailyCheckInScheduledDay[player] = currentDayIndex;
                ModLogger.Warn($"ParoleSystem: Could not schedule check-in window for {player.name} on day index {currentDayIndex} within 10:00-20:00");
                return;
            }

            _dailyCheckInRequirements[player] = new DailyCheckInRequirement
            {
                DayIndex = currentDayIndex,
                WindowStartMinuteOfDay = windowStartMinuteOfDay,
                WindowEndMinuteOfDay = windowEndMinuteOfDay,
                ReminderSent = false,
                Completed = false
            };
            _dailyCheckInScheduledDay[player] = currentDayIndex;

            SendDailyCheckInInstructionText(player, windowStartMinuteOfDay, windowEndMinuteOfDay);
            ModLogger.Info($"ParoleSystem: Scheduled daily check-in for {player.name} (day index {currentDayIndex}, {FormatMinuteOfDay(windowStartMinuteOfDay)} - {FormatMinuteOfDay(windowEndMinuteOfDay)})");
        }

        private float GetDailyCheckInWindowMinutes(Player player)
        {
            var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            if (rapSheet == null)
            {
                return LOW_LSI_CHECKIN_WINDOW_MINUTES;
            }

            return rapSheet.LSILevel switch
            {
                LSILevel.High => HIGH_LSI_CHECKIN_WINDOW_MINUTES,
                LSILevel.Severe => HIGH_LSI_CHECKIN_WINDOW_MINUTES,
                _ => LOW_LSI_CHECKIN_WINDOW_MINUTES
            };
        }

        private bool TryBuildDailyCheckInWindow(int currentMinuteOfDay, float windowMinutes, out int windowStartMinuteOfDay, out int windowEndMinuteOfDay)
        {
            windowStartMinuteOfDay = 0;
            windowEndMinuteOfDay = 0;

            int windowLengthMinutes = Mathf.RoundToInt(windowMinutes);
            int earliestStart = CHECKIN_START_HOUR_24 * 60;
            int latestEnd = CHECKIN_END_HOUR_24 * 60;
            int latestStart = latestEnd - windowLengthMinutes;

            int minStart = Mathf.Max(currentMinuteOfDay, earliestStart);
            if (minStart > latestStart)
            {
                return false;
            }

            windowStartMinuteOfDay = UnityEngine.Random.Range(minStart, latestStart + 1);
            windowEndMinuteOfDay = windowStartMinuteOfDay + windowLengthMinutes;
            return true;
        }

        private string GetPlayerDisplayName(Player player)
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

        private void SendDailyCheckInInstructionText(Player player, int windowStartMinuteOfDay, int windowEndMinuteOfDay)
        {
            string playerName = GetPlayerDisplayName(player);
            string windowStartText = FormatMinuteOfDay(windowStartMinuteOfDay);
            string windowEndText = FormatMinuteOfDay(windowEndMinuteOfDay);
            string message =
                $"{playerName}, this is your supervising officer. Your check-in appointment today is scheduled between " +
                $"{windowStartText} and {windowEndText}. Report during this window.";

            SendSupervisingOfficerText(player, message);
        }

        private string FormatMinuteOfDay(int minuteOfDay)
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

        private int GetCurrentDayIndex()
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

        private int GetCurrentMinuteOfDay()
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

        private string FormatWindowText(DailyCheckInRequirement requirement)
        {
            return $"{FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} and {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}";
        }

        public DailyCheckInStatus GetDailyCheckInStatus(Player player, out string windowText, bool applyConsequences = true)
        {
            windowText = string.Empty;

            if (player == null)
            {
                return DailyCheckInStatus.NoScheduledWindow;
            }

            if (!_dailyCheckInRequirements.TryGetValue(player, out var requirement) || requirement == null)
            {
                return DailyCheckInStatus.NoScheduledWindow;
            }

            windowText = FormatWindowText(requirement);

            int currentDayIndex = GetCurrentDayIndex();
            int currentMinuteOfDay = GetCurrentMinuteOfDay();

            if (currentDayIndex > requirement.DayIndex ||
                (currentDayIndex == requirement.DayIndex && currentMinuteOfDay > requirement.WindowEndMinuteOfDay))
            {
                if (applyConsequences)
                {
                    HandleMissedDailyCheckIn(player, requirement);
                    _dailyCheckInRequirements.Remove(player);
                }

                return DailyCheckInStatus.MissedWindow;
            }

            if (currentDayIndex < requirement.DayIndex)
            {
                return DailyCheckInStatus.NoScheduledWindow;
            }

            if (currentMinuteOfDay < requirement.WindowStartMinuteOfDay)
            {
                return DailyCheckInStatus.TooEarly;
            }

            return DailyCheckInStatus.Allowed;
        }

        public bool TryBeginCheckInSession(Player player, out DailyCheckInStatus status, out string windowText)
        {
            status = GetDailyCheckInStatus(player, out windowText, applyConsequences: true);
            if (status != DailyCheckInStatus.Allowed)
            {
                return false;
            }

            _activeCheckInSessions.Add(player);
            return true;
        }

        public void EndCheckInSession(Player player)
        {
            if (player == null)
            {
                return;
            }

            _activeCheckInSessions.Remove(player);
        }

        public bool NotifyDailyCheckInCompleted(Player player)
        {
            if (player == null)
            {
                return false;
            }

            _activeCheckInSessions.Remove(player);

            if (_dailyCheckInRequirements.TryGetValue(player, out var requirement) && requirement != null)
            {
                requirement.Completed = true;
                _dailyCheckInRequirements.Remove(player);
                ModLogger.Info($"ParoleSystem: Daily check-in completed for {player.name}");
                return true;
            }

            return false;
        }

        private void ProcessExpiredDailyCheckIn(Player player, int currentDayIndex, int currentMinuteOfDay)
        {
            if (player == null || _activeCheckInSessions.Contains(player))
            {
                return;
            }

            if (!_dailyCheckInRequirements.TryGetValue(player, out var requirement) || requirement == null)
            {
                return;
            }

            if (requirement.Completed)
            {
                _dailyCheckInRequirements.Remove(player);
                return;
            }

            bool missed = currentDayIndex > requirement.DayIndex ||
                          (currentDayIndex == requirement.DayIndex && currentMinuteOfDay > requirement.WindowEndMinuteOfDay);
            if (!missed)
            {
                return;
            }

            HandleMissedDailyCheckIn(player, requirement);
            _dailyCheckInRequirements.Remove(player);
        }

        private void ProcessUpcomingCheckInReminder(Player player, int currentDayIndex, int currentMinuteOfDay)
        {
            if (player == null)
            {
                return;
            }

            if (!_dailyCheckInRequirements.TryGetValue(player, out var requirement) || requirement == null)
            {
                return;
            }

            if (requirement.Completed || requirement.ReminderSent)
            {
                return;
            }

            if (requirement.DayIndex != currentDayIndex)
            {
                return;
            }

            int reminderMinute = requirement.WindowStartMinuteOfDay - 60;
            if (currentMinuteOfDay < reminderMinute || currentMinuteOfDay >= requirement.WindowStartMinuteOfDay)
            {
                return;
            }

            requirement.ReminderSent = true;

            string message =
                $"{GetPlayerDisplayName(player)}, reminder: your parole check-in window starts in one hour. " +
                $"Report between {FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} and {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}.";
            SendSupervisingOfficerText(player, message);
        }

        private void HandleMissedDailyCheckIn(Player player, DailyCheckInRequirement requirement)
        {
            var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            if (rapSheet?.CurrentParoleRecord == null)
            {
                return;
            }

            var paroleRecord = rapSheet.CurrentParoleRecord;
            paroleRecord.RecordMissedCheckIn();

            int missedCheckIns = paroleRecord.GetMissedCheckIns();

            if (missedCheckIns <= 1)
            {
                LSILevel previousLsi = rapSheet.LSILevel;
                LSILevel escalatedLsi = EscalateLSILevel(previousLsi);
                rapSheet.LSILevel = escalatedLsi;
                RapSheetManager.Instance.MarkRapSheetChanged(player);

                string message =
                    $"{GetPlayerDisplayName(player)}, you missed your required check-in window ({FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} to {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}). " +
                    $"First offense recorded: compliance score decreased and supervision level increased to {escalatedLsi}.";
                SendSupervisingOfficerText(player, message);

                ModLogger.Warn($"ParoleSystem: First missed daily check-in recorded for {player.name}");
                return;
            }

            var violation = new ViolationRecord(
                ViolationType.MissedCheckIn,
                $"Missed scheduled daily check-in window {FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} - {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}",
                2.5f);

            rapSheet.AddParoleViolation(violation);
            RapSheetManager.Instance.MarkRapSheetChanged(player);

            IssueAgentWarrant(player);

            string violationMessage =
                $"{GetPlayerDisplayName(player)}, this is your second missed check-in. You are now in parole violation. " +
                "Agent warrant issued. You will remain wanted until your next arrest.";
            SendSupervisingOfficerText(player, violationMessage);

            ModLogger.Warn($"ParoleSystem: Escalated missed check-in violation for {player.name}");
        }

        private LSILevel EscalateLSILevel(LSILevel currentLevel)
        {
            return currentLevel switch
            {
                LSILevel.None => LSILevel.Minimum,
                LSILevel.Minimum => LSILevel.Medium,
                LSILevel.Medium => LSILevel.High,
                LSILevel.High => LSILevel.Severe,
                _ => LSILevel.Severe
            };
        }

        private void IssueAgentWarrant(Player player)
        {
            if (player == null)
            {
                return;
            }

            bool isNewWarrant = _playersWithActiveWarrants.Add(player);
            TriggerPolicePursuitForWarrant(player);

            if (isNewWarrant)
            {
                ModLogger.Warn($"ParoleSystem: Active warrant issued for {player.name}");
            }
        }

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
            if (player == null || !_playersWithActiveWarrants.Contains(player))
            {
                return;
            }

            if (JailTimeTracker.Instance.IsInJail(player))
            {
                _playersWithActiveWarrants.Remove(player);
                _lastWarrantEnforcementTime.Remove(player);
                ModLogger.Info($"ParoleSystem: Cleared active warrant for {player.name} after arrest");
                return;
            }

            float now = Time.time;
            if (_lastWarrantEnforcementTime.TryGetValue(player, out float lastEnforcementTime) &&
                now - lastEnforcementTime < ACTIVE_WARRANT_ENFORCEMENT_INTERVAL_SECONDS)
            {
                return;
            }

            _lastWarrantEnforcementTime[player] = now;

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

            if (_pendingOfficerTexts.TryGetValue(player, out var existing) && existing != null && existing.Message == message)
            {
                return;
            }

            _pendingOfficerTexts[player] = new PendingOfficerText
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

            if (!_pendingOfficerTexts.TryGetValue(player, out var pending) || pending == null)
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
                _pendingOfficerTexts.Remove(player);
                return;
            }

            if (pending.Attempts >= TEXT_MESSAGE_RETRY_MAX_ATTEMPTS)
            {
                _pendingOfficerTexts.Remove(player);
                ModLogger.Warn($"ParoleSystem: Failed to deliver supervising officer text after retries for {player.name}");
                return;
            }

            _pendingOfficerTexts[player] = pending;
        }

        private bool SendSupervisingOfficerText(Player player, string message, bool allowRetryQueue = true)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            try
            {
                EnsureDynamicParoleOfficerManager();

                var supervisingOfficer = PrisonNPCManager.Instance?.GetSupervisingOfficer();
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
                        _pendingOfficerTexts.Remove(player);
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

            _dailyCheckInRequirements.Remove(player);
            _dailyCheckInScheduledDay.Remove(player);
            _pendingOfficerTexts.Remove(player);
            _activeCheckInSessions.Remove(player);
            _playersWithActiveWarrants.Remove(player);
            _lastWarrantEnforcementTime.Remove(player);
        }

        /// <summary>
        /// Callback when parole period completes via game time tracking
        /// </summary>
        private void OnParoleComplete(Player player)
        {
            if (_paroleRecords.TryGetValue(player, out var record))
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
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
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
                    RapSheetManager.Instance.MarkRapSheetChanged(player);
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
        /// Monitor active parole and conduct periodic searches
        /// </summary>
        private IEnumerator MonitorParole(ParoleRuntimeRecord record)
        {
            ModLogger.Debug($"Monitoring parole for {record.Player.name}");

            while (record.Status == ParoleStatus.Active)
            {
                // Update time remaining from ParoleTimeTracker
                record.TimeRemainingGameMinutes = ParoleTimeTracker.Instance.GetRemainingTime(record.Player);

                // Check if parole is complete
                if (record.TimeRemainingGameMinutes <= 0)
                {
                    break;
                }

                // Check if it's time for a random search (using game time)
                float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();

                if (IsAuthorityForParoleActions())
                {
                    int currentDayIndex = GetCurrentDayIndex();
                    int currentMinuteOfDay = GetCurrentMinuteOfDay();
                    ProcessUpcomingCheckInReminder(record.Player, currentDayIndex, currentMinuteOfDay);
                    ProcessExpiredDailyCheckIn(record.Player, currentDayIndex, currentMinuteOfDay);
                    EnforceActiveWarrant(record.Player);
                    ProcessPendingOfficerText(record.Player);
                }

                if (currentGameTime >= record.NextSearchGameTimeMinutes)
                {
                    yield return ConductRandomSearch(record);
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
        /// Conduct a random search if parole officer is in range
        /// </summary>
        private IEnumerator ConductRandomSearch(ParoleRuntimeRecord record)
        {
            ModLogger.Debug($"Conducting random search on {record.Player.name}");

            // Check if parole officer is close enough
            if (_paroleOfficerInstance != null && record.Player != null)
            {
                float distance = Vector3.Distance(_paroleOfficerInstance.position, record.Player.transform.position);

                if (distance <= SEARCH_RADIUS)
                {
                    // Conduct the search
                    yield return PerformBodySearch(record);
                }
                else
                {
                    ModLogger.Info($"Parole Officer too far from {record.Player.name} for search");
                }
            }

            // Schedule next search (using game time)
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float searchIntervalMin = GameTimeManager.RealSecondsToGameMinutes(SEARCH_INTERVAL_MIN);
            float searchIntervalMax = GameTimeManager.RealSecondsToGameMinutes(SEARCH_INTERVAL_MAX);
            record.LastSearchGameTimeMinutes = currentGameTime;
            record.NextSearchGameTimeMinutes = currentGameTime + UnityEngine.Random.Range(searchIntervalMin, searchIntervalMax);
        }

        /// <summary>
        /// Perform body search on parolee
        /// </summary>
        private IEnumerator PerformBodySearch(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Performing body search on {record.Player.name}");

            // TODO: Implement actual search mechanics
            // This could involve:
            // 1. Showing search UI
            // 2. Checking player inventory for contraband
            // 3. Applying consequences for violations
            // 4. Recording search results

            // Simulate search time
            yield return new WaitForSeconds(2f);

            // Check for violations during search
            bool hasViolations = CheckForSearchViolations(record);

            if (hasViolations)
            {
                record.ViolationCount++;
                record.Violations.Add($"Contraband found during search at {Time.time}");
                ModLogger.Info($"Search violation found for {record.Player.name}. Total violations: {record.ViolationCount}");

                // Apply violation consequences
                yield return HandleParoleViolation(record);
            }
            else
            {
                ModLogger.Info($"Search completed for {record.Player.name} - no violations found");
            }
        }

        /// <summary>
        /// Check player inventory for contraband during search
        /// </summary>
        private bool CheckForSearchViolations(ParoleRuntimeRecord record)
        {
            bool foundViolations = false;
            var violationDetails = new System.Text.StringBuilder();

            if (record.Player?.Inventory == null)
                return false;

            ModLogger.Info($"Checking inventory for contraband items for {record.Player.name}");

            try
            {
                // Get all inventory slots from PlayerInventory instance
                var playerInventory = record.Player.Inventory;
                if (playerInventory == null)
                {
                    ModLogger.Info($"Player {record.Player.name} has no inventory instance");
                    return false;
                }

                // Inventory checking disabled - use fallback random detection for now
                return UnityEngine.Random.Range(0f, 1f) < 0.2f; // 20% chance
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error during contraband search: {ex.Message}");
                // Fall back to random chance if inventory check fails
                return UnityEngine.Random.Range(0f, 1f) < 0.2f;
            }
        }

        /// <summary>
        /// Check if an item name indicates it's a drug-related item
        /// </summary>
        private bool IsDrugItem(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return false;

            var name = itemName.ToLower();

            return name.Contains("weed") ||
                   name.Contains("cannabis") ||
                   name.Contains("marijuana") ||
                   name.Contains("cocaine") ||
                   name.Contains("coke") ||
                   name.Contains("meth") ||
                   name.Contains("crystal") ||
                   name.Contains("heroin") ||
                   name.Contains("opium") ||
                   name.Contains("pill") ||
                   name.Contains("drug") ||
                   name.Contains("narcotic") ||
                   name.Contains("substance");
        }

        /// <summary>
        /// Check if an item name indicates it's a weapon
        /// </summary>
        private bool IsWeaponItem(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return false;

            var name = itemName.ToLower();

            return name.Contains("gun") ||
                   name.Contains("pistol") ||
                   name.Contains("rifle") ||
                   name.Contains("shotgun") ||
                   name.Contains("knife") ||
                   name.Contains("blade") ||
                   name.Contains("weapon") ||
                   name.Contains("taser") ||
                   name.Contains("baton") ||
                   name.Contains("sword") ||
                   name.Contains("axe") ||
                   name.Contains("hammer") && name.Contains("war"); // war hammer, etc.
        }

        /// <summary>
        /// Handle parole violation consequences
        /// </summary>
        private IEnumerator HandleParoleViolation(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Handling parole violation for {record.Player.name}");

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
                ParoleTimeTracker.Instance.StopTracking(record.Player);
                ParoleTimeTracker.Instance.StartTracking(record.Player, record.DurationGameMinutes, OnParoleComplete);

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
            try
            {
                var npcManager = PrisonNPCManager.Instance;
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
            ParoleTimeTracker.Instance.StopTracking(record.Player);

            // End parole in RapSheet and archive it
            try
            {
                var rapSheet = RapSheetManager.Instance.GetRapSheet(record.Player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    rapSheet.CurrentParoleRecord.EndParole();
                    // Move current parole record to past records
                    rapSheet.ArchiveCurrentParoleRecord();
                    RapSheetManager.Instance.MarkRapSheetChanged(record.Player);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error ending parole in RapSheet: {ex.Message}");
            }

            // Hide parole status UI
            try
            {
                var uiManager = BehindBarsUIManager.Instance;
                if (uiManager != null)
                {
                    uiManager.HideParoleStatus();
                    ModLogger.Info($"Parole status UI hidden for {record.Player.name} (revoked)");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to hide parole status UI: {ex.Message}");
            }

            // Remove from active parole
            _paroleRecords.Remove(record.Player);
            ClearParoleRuntimeFlags(record.Player);

            // Emit parole ended event
            OnParoleEnded?.Invoke(record.Player);
            ModLogger.Debug($"ParoleSystem: Emitted OnParoleEnded event for {record.Player.name} (revoked)");

            // TODO: Implement revocation consequences
            // This could involve:
            // 1. Immediate arrest by parole officer
            // 2. Transfer to jail system
            // 3. Harsher sentencing

            yield return new WaitForSeconds(1f);

            // Hand off to jail system
            var jailSystem = Core.Instance?.GetJailSystem();
            if (jailSystem != null && record.Player != null)
            {
                yield return jailSystem.HandlePlayerArrest(record.Player);
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
            
            if (_paroleRecords.TryGetValue(player, out var record))
            {
                CompleteParole(record);
            }
            else
            {
                ModLogger.Warn($"No active parole record found for {player.name} to complete");
                // Still try to end parole in RapSheet if it exists
                try
                {
                    var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                    if (rapSheet?.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                    {
                        rapSheet.CurrentParoleRecord.EndParole();
                        rapSheet.ArchiveCurrentParoleRecord();
                        RapSheetManager.Instance.MarkRapSheetChanged(player);
                        ModLogger.Info($"Completed parole in RapSheet for {player.name}");
                        
                        // Emit parole ended event
                        OnParoleEnded?.Invoke(player);
                        ModLogger.Debug($"ParoleSystem: Emitted OnParoleEnded event for {player.name} (completed via CompleteParoleForPlayer)");
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
        /// Complete parole successfully
        /// </summary>
        private void CompleteParole(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Parole completed successfully for {record.Player.name}");

            record.Status = ParoleStatus.Completed;
            record.TimeRemainingGameMinutes = 0f;

            // Stop tracking with ParoleTimeTracker
            ParoleTimeTracker.Instance.StopTracking(record.Player);

            // Clear release time grace period (parole is complete)
            ParoleSearchSystem.Instance.ClearReleaseTime(record.Player);

            // End parole in RapSheet and archive it
            try
            {
                var rapSheet = RapSheetManager.Instance.GetRapSheet(record.Player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    rapSheet.CurrentParoleRecord.EndParole();
                    // Move current parole record to past records
                    rapSheet.ArchiveCurrentParoleRecord();
                    RapSheetManager.Instance.MarkRapSheetChanged(record.Player);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error ending parole in RapSheet: {ex.Message}");
            }

            // Hide parole status UI
            try
            {
                var uiManager = BehindBarsUIManager.Instance;
                if (uiManager != null)
                {
                    uiManager.HideParoleStatus();
                    ModLogger.Info($"Parole status UI hidden for {record.Player.name}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to hide parole status UI: {ex.Message}");
            }

            // Emit parole ended event
            OnParoleEnded?.Invoke(record.Player);
            ModLogger.Debug($"ParoleSystem: Emitted OnParoleEnded event for {record.Player.name} (completed)");

            // TODO: Implement parole completion rewards
            // This could involve:
            // 1. Clearing criminal record
            // 2. Restoring full rights
            // 3. Positive reputation boost
            // 4. Achievement unlock

            // Remove from active parole
            _paroleRecords.Remove(record.Player);
            ClearParoleRuntimeFlags(record.Player);

            // Check if we can despawn parole officer
            if (_paroleRecords.Count == 0)
            {
                DespawnParoleOfficer();
            }
        }

        /// <summary>
        /// Spawn parole officer NPC (placeholder)
        /// </summary>
        private void SpawnParoleOfficer()
        {
            ModLogger.Info("Parole Officer NPC spawning removed - feature not implemented");

            // NOTE: NPC spawning functionality has been removed from this mod
            // The parole system will continue to work without the physical NPC
            // Players will still be subject to parole rules and violations
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
            _paroleRecords.TryGetValue(player, out var record);
            return record;
        }

        /// <summary>
        /// Check if player is currently on active parole
        /// </summary>
        public bool IsPlayerOnParole(Player player)
        {
            return _paroleRecords.ContainsKey(player) &&
                   _paroleRecords[player].Status == ParoleStatus.Active;
        }

        /// <summary>
        /// Extend parole duration for a player (in game minutes)
        /// </summary>
        public void ExtendParole(Player player, float additionalGameMinutes)
        {
            if (_paroleRecords.TryGetValue(player, out var record))
            {
                record.DurationGameMinutes += additionalGameMinutes;
                record.TimeRemainingGameMinutes += additionalGameMinutes;
                
                // Update ParoleTimeTracker with new duration
                ParoleTimeTracker.Instance.StopTracking(player);
                ParoleTimeTracker.Instance.StartTracking(player, record.DurationGameMinutes, OnParoleComplete);
                
                ModLogger.Info($"Extended parole for {player.name} by {additionalGameMinutes} game minutes ({GameTimeManager.FormatGameTime(additionalGameMinutes)})");
            }
        }
    }
}
