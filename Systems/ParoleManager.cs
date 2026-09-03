using System;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Parole;
using MelonLoader;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.GameTime;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Top-level parole ownership manager shell.
    /// Owns the root <see cref="ParoleSystem"/> instance, accepts optional collaborator services for
    /// condition, fee, and home-visit support, and intentionally does not implement parole behavior
    /// itself. The manager's role is wiring and lifecycle ownership only: it owns the core system,
    /// collaborates with injected support services, and does not take ownership of those collaborators.
    /// </summary>
    public class ParoleManager
    {
        private const float LowLsiCheckInWindowMinutes = 180f;
        private const float HighLsiCheckInWindowMinutes = 60f;
        private const int CheckInStartHour24 = 10;
        private const int CheckInEndHour24 = 20;
        private const int GameMinutesPerDay = 1440;
        // The first regular appointment is deliberately separated from the mandatory
        // release explanation.  The latter introduces the report location; it must not
        // immediately turn into a second check-in obligation.
        private const float FirstCheckInMinimumDelayGameMinutes = 4f * 60f;

        private sealed class DailyCheckInRequirement
        {
            /// <summary>Persisted/native day index on which this appointment is valid.</summary>
            public int DayIndex { get; set; }
            /// <summary>Inclusive appointment-window start in minutes after midnight.</summary>
            public int WindowStartMinuteOfDay { get; set; }
            /// <summary>Appointment-window end in minutes after midnight.</summary>
            public int WindowEndMinuteOfDay { get; set; }
            /// <summary>Whether the one-hour reminder has already been sent.</summary>
            public bool ReminderSent { get; set; }
            /// <summary>Whether the appointment-day instruction has already been sent.</summary>
            public bool InstructionSent { get; set; }
            /// <summary>Whether this transient requirement has been completed by the player.</summary>
            public bool Completed { get; set; }
        }

        /// <summary>
        /// Manager-owned daily check-in status surface.
        /// This mirrors the legacy parole-system values without exposing the underlying
        /// ParoleSystem type to check-in callers.
        /// </summary>
        public enum CheckInStatus
        {
            Allowed = 0,
            NoScheduledWindow = 1,
            TooEarly = 2,
            MissedWindow = 3
        }

        /// <summary>
        /// Gets the owned parole system instance.
        /// </summary>
        public ParoleSystem ParoleSystem { get; }

        /// <summary>
        /// Gets the parole-time tracker collaborator used for runtime tracking.
        /// </summary>
        public ParoleTimeTracker ParoleTimeTracker { get; }

        /// <summary>
        /// Gets the optional parole condition service collaborator.
        /// </summary>
        public ParoleConditionManager? ConditionManager { get; private set; }

        /// <summary>
        /// Gets the optional parole fee service collaborator.
        /// </summary>
        public ParoleFeeSystem? FeeSystem { get; private set; }

        /// <summary>
        /// Gets the optional home visit service collaborator.
        /// </summary>
        public HomeVisitSystem? HomeVisitSystem { get; private set; }

        // Transient in-memory requirements keyed by the shared player identity. The
        // authoritative schedule is mirrored to the current RapSheet parole record.
        private readonly System.Collections.Generic.Dictionary<string, DailyCheckInRequirement> dailyCheckInRequirements = new();
        // Prevents scheduling the same player more than once during a native day, including
        // when a requirement is temporarily absent from the in-memory map.
        private readonly System.Collections.Generic.Dictionary<string, int> dailyCheckInScheduledDay = new();
        // Guards the check-in interaction so day-pass processing does not mark an active
        // session as missed while the player is completing it.
        private readonly System.Collections.Generic.HashSet<string> activeCheckInSessions = new();

        /// <summary>
        /// Creates a parole manager shell that owns the supplied parole system.
        /// </summary>
        /// <param name="paroleSystem">The owned parole system instance.</param>
        public ParoleManager(ParoleSystem paroleSystem)
        {
            ParoleSystem = paroleSystem ?? throw new ArgumentNullException(nameof(paroleSystem));
            ParoleTimeTracker = ParoleTimeTracker.Instance;
        }

        /// <summary>
        /// Attaches optional support services without transferring ownership.
        /// This is a safe wiring step only; it does not initialize or mutate parole behavior.
        /// </summary>
        /// <param name="conditionManager">Optional parole condition service collaborator.</param>
        /// <param name="feeSystem">Optional parole fee service collaborator.</param>
        /// <param name="homeVisitSystem">Optional home visit service collaborator.</param>
        public void AttachSupportServices(
            ParoleConditionManager? conditionManager,
            ParoleFeeSystem? feeSystem,
            HomeVisitSystem? homeVisitSystem)
        {
            ConditionManager = conditionManager;
            FeeSystem = feeSystem;
            HomeVisitSystem = homeVisitSystem;
        }

        /// <summary>
        /// Resets collaborator references to a clean scaffold state.
        /// The owned parole system remains valid and untouched.
        /// </summary>
        /// <remarks>
        /// The current shutdown only drops optional service references. It does not clear
        /// daily requirements, scheduled-day guards, active sessions, or their persisted
        /// RapSheet schedules, so this manager should not be reused across lifetimes without
        /// an explicit state-clear operation.
        /// </remarks>
        public void Shutdown()
        {
            ConditionManager = null;
            FeeSystem = null;
            HomeVisitSystem = null;
        }

        /// <summary>
        /// Start tracking a parole period through the manager-owned parole runtime seam.
        /// </summary>
        public void StartTracking(Player player, float paroleGameMinutes, System.Action<Player>? onComplete = null)
        {
            ParoleTimeTracker.StartTracking(player, paroleGameMinutes, onComplete);
        }

        /// <summary>
        /// Stop tracking a parole period through the manager-owned parole runtime seam.
        /// </summary>
        public void StopTracking(Player player)
        {
            ParoleTimeTracker.StopTracking(player);
        }

        /// <summary>
        /// Get the remaining parole time through the manager-owned parole runtime seam.
        /// </summary>
        public float GetRemainingTime(Player player)
        {
            return ParoleTimeTracker.GetRemainingTime(player);
        }

        /// <summary>
        /// Get the formatted remaining parole time through the manager-owned parole runtime seam.
        /// </summary>
        public string GetFormattedRemainingTime(Player player)
        {
            return ParoleTimeTracker.GetFormattedRemainingTime(player);
        }

        /// <summary>
        /// Check whether a player currently has an active parole period.
        /// </summary>
        public bool IsTracking(Player player)
        {
            return ParoleTimeTracker.IsTracking(player);
        }

        /// <summary>
        /// Route a supervising-officer text message through the manager-owned parole seam.
        /// </summary>
        public bool SendSupervisingOfficerText(Player player, string message, bool allowRetryQueue = true)
        {
            return ParoleSystem.SendSupervisingOfficerText(player, message, allowRetryQueue);
        }

        /// <summary>
        /// Start parole through the manager-owned parole seam.
        /// </summary>
        public void StartParole(Player player, float durationGameMinutes, bool showUI = true)
        {
            ParoleSystem.StartParole(player, durationGameMinutes, showUI);
        }

        /// <summary>
        /// Ensure the owned parole system restores runtime tracking for a loaded player.
        /// This keeps loaded-save recovery behind the manager seam while the underlying
        /// parole system remains the compatibility-backed implementation.
        /// </summary>
        public void EnsureRuntimeParoleTrackingForLoadedPlayer(Player player)
        {
            ParoleSystem.EnsureRuntimeParoleTrackingForLoadedPlayer(player);
        }

        /// <summary>
        /// Process check-in expiry and scheduling for active parole records at a day-pass boundary.
        /// </summary>
        /// <remarks>
        /// Only the authority proceeds. Players without an active record or currently in jail
        /// are skipped; each remaining player is checked for an expired requirement before a
        /// new window is scheduled.
        /// </remarks>
        internal void HandleDayPassForParoleCheckIns()
        {
            if (!IsAuthorityForParoleActions())
            {
                return;
            }

            if (ParoleSystem.ActiveParoleRecords.Count == 0)
            {
                return;
            }

            int currentDayIndex = GetCurrentDayIndex();
            int currentMinuteOfDay = GetCurrentMinuteOfDay();

            foreach (var record in ParoleSystem.ActiveParoleRecords.Values)
            {
                if (record == null || record.Player == null || record.Status != ParoleSystem.ParoleStatus.Active)
                {
                    continue;
                }

                if (Core.ResolveJailTimeTracker().IsInJail(record.Player))
                {
                    continue;
                }

                ProcessExpiredDailyCheckIn(record.Player, currentDayIndex, currentMinuteOfDay);
                ScheduleDailyCheckIn(record.Player, currentDayIndex, currentMinuteOfDay);
                ProcessDailyCheckInInstruction(record.Player, currentDayIndex);
            }
        }

        /// <summary>
        /// Ensure that one daily check-in requirement exists for the supplied native day.
        /// </summary>
        /// <param name="player">Active parolee for whom the window is being scheduled.</param>
        /// <param name="currentDayIndex">Current native day index.</param>
        /// <param name="currentMinuteOfDay">Current native minute of day.</param>
        /// <remarks>
        /// Scheduling is idempotent per player/day. A persisted RapSheet schedule is restored
        /// before a new random window is built; newly built requirements are persisted before
        /// the officer instruction is sent. The first appointment is delayed by the separate
        /// four-game-hour release rule when no prior check-in exists.
        /// </remarks>
        internal void ScheduleDailyCheckIn(Player player, int currentDayIndex, int currentMinuteOfDay)
        {
            string playerKey = GetPlayerRuntimeKey(player);

            if (dailyCheckInScheduledDay.TryGetValue(playerKey, out int lastScheduledDay) && lastScheduledDay == currentDayIndex)
            {
                return;
            }

            if (dailyCheckInRequirements.TryGetValue(playerKey, out var existingRequirement))
            {
                if (existingRequirement.DayIndex == currentDayIndex)
                {
                    dailyCheckInScheduledDay[playerKey] = currentDayIndex;
                    return;
                }

                if (existingRequirement.Completed)
                {
                    dailyCheckInRequirements.Remove(playerKey);
                }
            }

            if (TryRestorePersistedDailyCheckIn(player, out var persistedRequirement))
            {
                dailyCheckInRequirements[playerKey] = persistedRequirement;
                dailyCheckInScheduledDay[playerKey] = persistedRequirement.DayIndex;
                return;
            }

            float windowMinutes = GetDailyCheckInWindowMinutes(player);
            int earliestWindowStartMinute = GetEarliestCheckInStartMinute(player, currentMinuteOfDay);
            if (earliestWindowStartMinute >= GameMinutesPerDay ||
                !TryBuildDailyCheckInWindow(earliestWindowStartMinute, windowMinutes, out int windowStartMinuteOfDay, out int windowEndMinuteOfDay))
            {
                dailyCheckInScheduledDay[playerKey] = currentDayIndex;
                ModLogger.Info($"ParoleManager: Deferring {player.name}'s first regular check-in beyond day index {currentDayIndex}; it cannot begin until four game-hours after release");
                return;
            }

            dailyCheckInRequirements[playerKey] = new DailyCheckInRequirement
            {
                DayIndex = currentDayIndex,
                WindowStartMinuteOfDay = windowStartMinuteOfDay,
                WindowEndMinuteOfDay = windowEndMinuteOfDay,
                ReminderSent = false,
                InstructionSent = false,
                Completed = false
            };
            dailyCheckInScheduledDay[playerKey] = currentDayIndex;
            PersistDailyCheckInRequirement(player, dailyCheckInRequirements[playerKey]);

            ModLogger.Info($"ParoleManager: Scheduled daily check-in for {player.name} (day index {currentDayIndex}, {FormatMinuteOfDay(windowStartMinuteOfDay)} - {FormatMinuteOfDay(windowEndMinuteOfDay)})");
        }

        /// <summary>Sends the appointment instruction no earlier than its scheduled native day.</summary>
        internal void ProcessDailyCheckInInstruction(Player player, int currentDayIndex)
        {
            if (player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            if (!dailyCheckInRequirements.TryGetValue(playerKey, out var requirement) || requirement == null)
            {
                if (!TryRestorePersistedDailyCheckIn(player, out requirement))
                {
                    return;
                }

                dailyCheckInRequirements[playerKey] = requirement;
            }

            if (requirement.Completed || requirement.InstructionSent || currentDayIndex < requirement.DayIndex)
            {
                return;
            }

            requirement.InstructionSent = true;
            PersistDailyCheckInRequirement(player, requirement);
            SendDailyCheckInInstructionText(player, requirement.WindowStartMinuteOfDay, requirement.WindowEndMinuteOfDay);
        }

        /// <summary>
        /// Send the one-hour reminder when a scheduled check-in window is approaching.
        /// </summary>
        /// <param name="player">Active parolee to notify.</param>
        /// <param name="currentDayIndex">Current native day index.</param>
        /// <param name="currentMinuteOfDay">Current native minute of day.</param>
        /// <remarks>
        /// The reminder is de-duplicated by <see cref="DailyCheckInRequirement.ReminderSent"/>
        /// and persisted before the officer text is queued.
        /// </remarks>
        internal void ProcessUpcomingCheckInReminder(Player player, int currentDayIndex, int currentMinuteOfDay)
        {
            if (player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            if (!dailyCheckInRequirements.TryGetValue(playerKey, out var requirement) || requirement == null)
            {
                if (!TryRestorePersistedDailyCheckIn(player, out requirement))
                {
                    return;
                }

                dailyCheckInRequirements[playerKey] = requirement;
            }

            if (requirement.Completed || requirement.ReminderSent || requirement.DayIndex != currentDayIndex)
            {
                return;
            }

            int reminderMinute = requirement.WindowStartMinuteOfDay - 60;
            if (currentMinuteOfDay < reminderMinute || currentMinuteOfDay >= requirement.WindowStartMinuteOfDay)
            {
                return;
            }

            requirement.ReminderSent = true;
            PersistDailyCheckInRequirement(player, requirement);

            string message =
                $"{GetPlayerDisplayName(player)}, reminder: your parole check-in window starts in one hour. " +
                $"Report between {FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} and {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}.";
            SendSupervisingOfficerText(player, message);
        }

        /// <summary>
        /// Apply consequences and clear a daily requirement whose appointment window has passed.
        /// </summary>
        /// <param name="player">Active parolee whose window is being evaluated.</param>
        /// <param name="currentDayIndex">Current native day index.</param>
        /// <param name="currentMinuteOfDay">Current native minute of day.</param>
        /// <remarks>
        /// An active check-in session is protected from expiry processing. Completed schedules
        /// are simply cleared; missed schedules go through <see cref="HandleMissedDailyCheckIn"/>
        /// before transient and persisted state is removed.
        /// </remarks>
        internal void ProcessExpiredDailyCheckIn(Player player, int currentDayIndex, int currentMinuteOfDay)
        {
            string playerKey = GetPlayerRuntimeKey(player);

            if (player == null || activeCheckInSessions.Contains(playerKey))
            {
                return;
            }

            if (!dailyCheckInRequirements.TryGetValue(playerKey, out var requirement) || requirement == null)
            {
                if (!TryRestorePersistedDailyCheckIn(player, out requirement))
                {
                    return;
                }

                dailyCheckInRequirements[playerKey] = requirement;
            }

            if (requirement.Completed)
            {
                dailyCheckInRequirements.Remove(playerKey);
                ClearPersistedDailyCheckIn(player);
                return;
            }

            bool missed = currentDayIndex > requirement.DayIndex ||
                          (currentDayIndex == requirement.DayIndex && currentMinuteOfDay > requirement.WindowEndMinuteOfDay);
            if (!missed)
            {
                return;
            }

            HandleMissedDailyCheckIn(player, requirement);
            dailyCheckInRequirements.Remove(playerKey);
            ClearPersistedDailyCheckIn(player);
        }

        /// <summary>
        /// Resolve whether a parolee can begin a daily check-in session through the manager-owned parole seam.
        /// </summary>
        public bool TryBeginCheckInSession(Player player, out CheckInStatus status, out string windowText)
        {
            status = GetDailyCheckInStatus(player, out windowText, applyConsequences: true);
            if (status != CheckInStatus.Allowed)
            {
                return false;
            }

            activeCheckInSessions.Add(GetPlayerRuntimeKey(player));
            return true;
        }

        /// <summary>
        /// End an active daily check-in session through the manager-owned parole seam.
        /// </summary>
        public void EndCheckInSession(Player player)
        {
            if (player == null)
            {
                return;
            }

            activeCheckInSessions.Remove(GetPlayerRuntimeKey(player));
        }

        /// <summary>
        /// Mark a daily check-in as completed through the manager-owned parole seam.
        /// </summary>
        public bool NotifyDailyCheckInCompleted(Player player)
        {
            if (player == null)
            {
                return false;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            activeCheckInSessions.Remove(playerKey);

            if (dailyCheckInRequirements.TryGetValue(playerKey, out var requirement) && requirement != null)
            {
                requirement.Completed = true;
                dailyCheckInRequirements.Remove(playerKey);
                ClearPersistedDailyCheckIn(player);
                ModLogger.Info($"ParoleManager: Daily check-in completed for {player.name}");

                try
                {
                    var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                    if (rapSheet?.CurrentParoleRecord != null)
                    {
                        rapSheet.CurrentParoleRecord.AdjustRapport(3f);
                        Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
                    }

                    EvaluateLSIStepDown(player);
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"Error in post-check-in processing: {ex.Message}");
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolve the current daily check-in status through the manager-owned parole seam.
        /// </summary>
        public CheckInStatus GetDailyCheckInStatus(Player player, out string windowText, bool applyConsequences = true)
        {
            windowText = string.Empty;

            if (player == null)
            {
                return CheckInStatus.NoScheduledWindow;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            if (!dailyCheckInRequirements.TryGetValue(playerKey, out var requirement) || requirement == null)
            {
                if (!TryRestorePersistedDailyCheckIn(player, out requirement))
                {
                    return CheckInStatus.NoScheduledWindow;
                }

                dailyCheckInRequirements[playerKey] = requirement;
            }

            windowText = $"{FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} and {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}";

            int currentDayIndex = GetCurrentDayIndex();
            int currentMinuteOfDay = GetCurrentMinuteOfDay();

            if (currentDayIndex > requirement.DayIndex ||
                (currentDayIndex == requirement.DayIndex && currentMinuteOfDay > requirement.WindowEndMinuteOfDay))
            {
                if (applyConsequences)
                {
                    HandleMissedDailyCheckIn(player, requirement);
                    dailyCheckInRequirements.Remove(playerKey);
                    ClearPersistedDailyCheckIn(player);
                }

                return CheckInStatus.MissedWindow;
            }

            if (currentDayIndex < requirement.DayIndex)
            {
                return CheckInStatus.NoScheduledWindow;
            }

            if (currentMinuteOfDay < requirement.WindowStartMinuteOfDay)
            {
                return CheckInStatus.TooEarly;
            }

            return CheckInStatus.Allowed;
        }

        /// <summary>
        /// Clear transient and persisted daily check-in state for one player.
        /// </summary>
        /// <param name="player">Player whose requirement, day guard, session, and RapSheet schedule are cleared.</param>
        internal void ClearCheckInState(Player player)
        {
            if (player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            dailyCheckInRequirements.Remove(playerKey);
            dailyCheckInScheduledDay.Remove(playerKey);
            activeCheckInSessions.Remove(playerKey);
            ClearPersistedDailyCheckIn(player);
        }

        /// <summary>
        /// Evaluate LSI step-down eligibility through the manager-owned parole seam.
        /// </summary>
        public void EvaluateLSIStepDown(Player player)
        {
            ParoleSystem.EvaluateLSIStepDown(player);
        }

        /// <summary>
        /// Rehydrate a check-in requirement from the current RapSheet parole record, when present.
        /// </summary>
        /// <param name="player">Player whose persisted parole schedule should be read.</param>
        /// <param name="requirement">Rehydrated requirement, or <see langword="null"/> when none is persisted.</param>
        /// <returns><see langword="true"/> when a persisted schedule was available.</returns>
        private bool TryRestorePersistedDailyCheckIn(Player player, out DailyCheckInRequirement requirement)
        {
            requirement = null;
            var paroleRecord = Core.ResolveRapSheetManager().GetRapSheet(player)?.CurrentParoleRecord;
            if (paroleRecord == null ||
                !paroleRecord.TryGetDailyCheckInSchedule(
                    out int dayIndex,
                    out int startMinuteOfDay,
                    out int endMinuteOfDay,
                    out bool reminderSent,
                    out bool instructionSent))
            {
                return false;
            }

            requirement = new DailyCheckInRequirement
            {
                DayIndex = dayIndex,
                WindowStartMinuteOfDay = startMinuteOfDay,
                WindowEndMinuteOfDay = endMinuteOfDay,
                ReminderSent = reminderSent,
                InstructionSent = instructionSent,
                Completed = false
            };
            return true;
        }

        /// <summary>
        /// Mirror an in-memory requirement to the player's current RapSheet parole record.
        /// </summary>
        /// <param name="player">Player whose persisted record should be updated.</param>
        /// <param name="requirement">Requirement values to persist.</param>
        /// <remarks>
        /// Missing RapSheet/parole records are treated as a no-op. The reminder flag is
        /// persisted separately through the record's marker method.
        /// </remarks>
        private void PersistDailyCheckInRequirement(Player player, DailyCheckInRequirement requirement)
        {
            if (player == null || requirement == null)
            {
                return;
            }

            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            var paroleRecord = rapSheet?.CurrentParoleRecord;
            if (paroleRecord == null)
            {
                return;
            }

            paroleRecord.SetDailyCheckInSchedule(
                requirement.DayIndex,
                requirement.WindowStartMinuteOfDay,
                requirement.WindowEndMinuteOfDay);
            if (requirement.ReminderSent)
            {
                paroleRecord.MarkDailyCheckInReminderSent();
            }
            if (requirement.InstructionSent)
            {
                paroleRecord.MarkDailyCheckInInstructionSent();
            }

            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
        }

        /// <summary>
        /// Remove the persisted daily check-in schedule from the current parole record.
        /// </summary>
        /// <param name="player">Player whose persisted schedule should be cleared.</param>
        /// <remarks>Missing RapSheet/parole records are treated as a no-op.</remarks>
        private void ClearPersistedDailyCheckIn(Player player)
        {
            if (player == null)
            {
                return;
            }

            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            if (rapSheet?.CurrentParoleRecord == null)
            {
                return;
            }

            rapSheet.CurrentParoleRecord.ClearDailyCheckInSchedule();
            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
        }

        /// <summary>
        /// Resolve the appointment-window length from the player's current LSI level.
        /// </summary>
        /// <param name="player">Parolee whose RapSheet LSI level should be read.</param>
        /// <returns>60 game minutes for high/severe LSI, otherwise 180 game minutes.</returns>
        private float GetDailyCheckInWindowMinutes(Player player)
        {
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            if (rapSheet == null)
            {
                return LowLsiCheckInWindowMinutes;
            }

            return rapSheet.LSILevel switch
            {
                LSILevel.High => HighLsiCheckInWindowMinutes,
                LSILevel.Severe => HighLsiCheckInWindowMinutes,
                _ => LowLsiCheckInWindowMinutes
            };
        }

        /// <summary>
        /// Build a random same-day check-in window inside the 10:00 AM-8:00 PM schedule.
        /// </summary>
        /// <param name="currentMinuteOfDay">Earliest minute at which the window may begin.</param>
        /// <param name="windowMinutes">Required window length in game minutes.</param>
        /// <param name="windowStartMinuteOfDay">Selected start minute when successful.</param>
        /// <param name="windowEndMinuteOfDay">Selected end minute when successful.</param>
        /// <returns><see langword="false"/> when no same-day window can fit.</returns>
        /// <remarks>
        /// The random start is inclusive at both bounds after rounding the requested length;
        /// windows do not wrap past midnight.
        /// </remarks>
        private bool TryBuildDailyCheckInWindow(int currentMinuteOfDay, float windowMinutes, out int windowStartMinuteOfDay, out int windowEndMinuteOfDay)
        {
            windowStartMinuteOfDay = 0;
            windowEndMinuteOfDay = 0;

            int windowLengthMinutes = Mathf.RoundToInt(windowMinutes);
            int earliestStart = CheckInStartHour24 * 60;
            int latestEnd = CheckInEndHour24 * 60;
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

        /// <summary>
        /// Keeps the first regular check-in at least four game-hours after the parole term
        /// begins.  Parole starts only after the release conditions are acknowledged, so
        /// this persists cleanly across a save/load without a separate transient timer.
        /// </summary>
        private int GetEarliestCheckInStartMinute(Player player, int currentMinuteOfDay)
        {
            var paroleRecord = Core.ResolveRapSheetManager().GetRapSheet(player)?.CurrentParoleRecord;
            if (paroleRecord == null || paroleRecord.GetCheckInCount() > 0)
            {
                return currentMinuteOfDay;
            }

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float earliestGameTime = paroleRecord.GetParoleStartTime() + FirstCheckInMinimumDelayGameMinutes;
            int remainingDelayMinutes = Mathf.CeilToInt(earliestGameTime - currentGameTime);
            if (remainingDelayMinutes <= 0)
            {
                return currentMinuteOfDay;
            }

            int earliestMinute = currentMinuteOfDay + remainingDelayMinutes;
            ModLogger.Info($"ParoleManager: First regular check-in for {player.name} is held until at least {remainingDelayMinutes} more game minutes after release");
            return earliestMinute;
        }

        /// <summary>
        /// Send the first officer instruction describing a newly scheduled appointment window.
        /// </summary>
        /// <param name="player">Parolee receiving the instruction.</param>
        /// <param name="windowStartMinuteOfDay">Window start in minutes after midnight.</param>
        /// <param name="windowEndMinuteOfDay">Window end in minutes after midnight.</param>
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

        /// <summary>
        /// Delegate the authority decision to the owned parole system.
        /// </summary>
        /// <returns>The parole system's current authority result.</returns>
        private bool IsAuthorityForParoleActions()
        {
            return ParoleSystem.IsAuthorityForParoleActionsInternal();
        }

        /// <summary>
        /// Resolve the shared runtime key used by all manager-owned per-player maps.
        /// </summary>
        /// <param name="player">Player whose key should be resolved.</param>
        /// <returns>A stable key, or an empty string for a null player.</returns>
        private string GetPlayerRuntimeKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Core.ResolvePlayerKey(player);
        }

        /// <summary>
        /// Normalize the display name used in supervising-officer messages.
        /// </summary>
        /// <param name="player">Player whose name should be formatted.</param>
        /// <returns>The trimmed name without a trailing numeric runtime suffix.</returns>
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

        /// <summary>
        /// Format a minute-of-day value as a normalized 12-hour clock time.
        /// </summary>
        /// <param name="minuteOfDay">Minute value, normalized across a 24-hour day.</param>
        /// <returns>A player-facing time such as <c>10:05 AM</c>.</returns>
        private string FormatMinuteOfDay(int minuteOfDay)
        {
            int normalized = minuteOfDay % GameMinutesPerDay;
            if (normalized < 0)
            {
                normalized += GameMinutesPerDay;
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
        /// Read the current native day index, falling back to the mod clock if unavailable.
        /// </summary>
        /// <returns>The native day index, or the fallback clock's zero-based day.</returns>
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

        /// <summary>
        /// Read the current native minute of day, falling back to the mod clock if unavailable.
        /// </summary>
        /// <returns>The current minute after midnight from the native or fallback clock.</returns>
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

        /// <summary>
        /// Record the current missed-check-in consequence and escalate supervision when required.
        /// </summary>
        /// <param name="player">Parolee who missed the appointment.</param>
        /// <param name="requirement">Expired requirement used to describe the missed window.</param>
        /// <remarks>
        /// The first missed check-in lowers rapport, resets the high-compliance streak, and
        /// raises LSI without adding a formal violation. The second and later misses add a
        /// formal violation and issue an agent warrant through the parole system.
        /// </remarks>
        private void HandleMissedDailyCheckIn(Player player, DailyCheckInRequirement requirement)
        {
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            if (rapSheet?.CurrentParoleRecord == null)
            {
                return;
            }

            var paroleRecord = rapSheet.CurrentParoleRecord;
            paroleRecord.RecordMissedCheckIn();
            paroleRecord.AdjustRapport(-10f);
            paroleRecord.ResetHighComplianceDays();

            int missedCheckIns = paroleRecord.GetMissedCheckIns();
            if (missedCheckIns <= 1)
            {
                LSILevel escalatedLsi = EscalateLSILevel(rapSheet.LSILevel);
                rapSheet.LSILevel = escalatedLsi;
                Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

                string message =
                    $"{GetPlayerDisplayName(player)}, you missed your required check-in window ({FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} to {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}). " +
                    $"First offense recorded: compliance score decreased and supervision level increased to {escalatedLsi}.";
                SendSupervisingOfficerText(player, message);

                ModLogger.Warn($"ParoleManager: First missed daily check-in recorded for {player.name}");
                return;
            }

            var violation = new ViolationRecord(
                ViolationType.MissedCheckIn,
                $"Missed scheduled daily check-in window {FormatMinuteOfDay(requirement.WindowStartMinuteOfDay)} - {FormatMinuteOfDay(requirement.WindowEndMinuteOfDay)}",
                2.5f);

            rapSheet.AddParoleViolation(violation);
            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
            IssueAgentWarrant(player, ViolationType.MissedCheckIn);

            string violationMessage =
                $"{GetPlayerDisplayName(player)}, this is your second missed check-in. You are now in parole violation. " +
                "Agent warrant issued. You will remain wanted until your next arrest.";
            SendSupervisingOfficerText(player, violationMessage);

            ModLogger.Warn($"ParoleManager: Escalated missed check-in violation for {player.name}");
        }

        /// <summary>
        /// Move an LSI level up one step, clamping at severe supervision.
        /// </summary>
        /// <param name="currentLevel">Current supervision level.</param>
        /// <returns>The next level, or severe when already at the maximum.</returns>
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

        /// <summary>
        /// Issue a warrant through the manager-owned parole seam.
        /// </summary>
        public void IssueAgentWarrant(Player player, ViolationType cause = ViolationType.Other)
        {
            ParoleSystem.IssueAgentWarrantInternal(player, cause);
        }

        /// <summary>
        /// Routes curfew enforcement through the owned parole system so all sources
        /// share persisted warnings, formal violations, and warrant escalation.
        /// </summary>
        public void ReportCurfewViolation(Player player, RapSheet rapSheet, string source)
        {
            ParoleSystem.ReportCurfewViolation(player, rapSheet, source);
        }
    }
}
