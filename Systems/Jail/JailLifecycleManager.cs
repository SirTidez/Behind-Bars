using System;
using System.Collections;
using System.Collections.Generic;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Utils;
using MelonLoader;
using UnityEngine;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.GameTime;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Owns the jail's local daily recreation cycle.  It is deliberately
    /// scene-local: cell ownership persists through CellAssignmentManager,
    /// while open doors, audio, and inmate navigation are rebuilt each load.
    /// </summary>
    public sealed class JailLifecycleManager : MonoBehaviour
    {
        // Inmates can be placed on the far side of either dayroom tier. Give
        // them enough of the two-hour block to complete a real NavMesh return
        // before their doors are secured.
        private const int WarningMinutesBeforeClose = 30;
        // Navigation remains the normal return path, but autonomous pathing is not
        // allowed to hold the institution past a tier boundary. Any inmate still out
        // five game minutes before close is recovered by their canonical behavior.
        private const int NpcRecoveryMinutesBeforeClose = 5;
        private const float LocalAudioMaxDistance = 55f;
        private const float StragglerRepathSeconds = 5f;

        // Schedule state is scene-local and rebuilt from the native clock. The status
        // clock fields below deliberately preserve fractional real-time progress while
        // the native game minute remains unchanged, so the player-facing countdown can
        // move in real seconds without becoming a second schedule authority.
        private JailRecreationTier activeTier = JailRecreationTier.Unknown;
        private int lastWarningScheduleMinute = -1;
        private int lastObservedNativeMinute = -1;
        private bool loggedNativeTimeFallback;
        private int statusClockMinute = -1;
        private float statusClockMinuteProgress;
        private float statusClockLastRealtime;
        private float statusSecondsPerGameMinute = 1f;
        private bool scheduleHoldActive;
        private int heldScheduleMinute = -1;
        private float holdStartedNativeMinute;
        private float accumulatedScheduleDelayMinutes;
        private Coroutine lowerTierReturnCoroutine;
        private Coroutine upperTierReturnCoroutine;
        private Coroutine initialScheduleCoroutine;

        private AudioSource signalAudioSource;
        private AudioSource lockdownAudioSource;
        private AudioClip doorBuzzerClip;
        private AudioClip warningChimeClip;
        private AudioClip lockdownSirenClip;

#if !MONO
        public JailLifecycleManager(IntPtr ptr) : base(ptr) { }
#endif

        private void Start()
        {
            EnsureAudioSources();
            RefreshScheduleFromNativeTime(force: true);
            // The prison manager spawns one inmate roughly every half second. A
            // single fixed startup delay can therefore finish before the roster
            // exists. The bounded refresh covers legacy spawn paths; the
            // canonical manager also notifies us as each behavior is attached.
            initialScheduleCoroutine = MelonCoroutines.Start(RefreshDuringInitialInmateSpawn()) as Coroutine;
        }

        private void OnDestroy()
        {
            StopTierReturnMonitor(JailRecreationTier.Lower);
            StopTierReturnMonitor(JailRecreationTier.Upper);
            if (initialScheduleCoroutine != null)
            {
                MelonCoroutines.Stop(initialScheduleCoroutine);
                initialScheduleCoroutine = null;
            }
            if (lockdownAudioSource != null)
            {
                lockdownAudioSource.Stop();
            }
        }

        private void Update()
        {
            RefreshScheduleFromNativeTime(force: false);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Reconcile only on native-minute changes (or an explicit force) so schedule
        // transitions are single-shot while the status snapshot can still tick often.
        private void RefreshScheduleFromNativeTime(bool force)
        {
            if (!TryGetCurrentScheduleMinute(out int currentMinute))
            {
                return;
            }

            if (scheduleHoldActive)
            {
                return;
            }

            if (!force && currentMinute == lastObservedNativeMinute)
            {
                return;
            }

            lastObservedNativeMinute = currentMinute;
            if (force)
            {
                JailRecreationTier initialTier = JailRecreationSchedule.GetScheduledTier(currentMinute);
                ModLogger.Info($"[JAIL LIFECYCLE] Native Schedule I clock resolved to {currentMinute / 60:00}:{currentMinute % 60:00}; initial recreation state is {initialTier}");
            }
            ApplySchedule(currentMinute, force);

            if (activeTier == JailRecreationTier.None || activeTier == JailRecreationTier.Unknown)
            {
                return;
            }

            int endScheduleMinute = JailRecreationSchedule.GetActiveBlockEndMinute(currentMinute);
            int minutesRemaining = endScheduleMinute - currentMinute;
            if (minutesRemaining <= NpcRecoveryMinutesBeforeClose)
            {
                RecoverTierStragglers(activeTier, GetActiveInmateBehaviors(), "final pre-close deadline");
            }

            if (minutesRemaining != WarningMinutesBeforeClose || lastWarningScheduleMinute == currentMinute)
            {
                return;
            }

            lastWarningScheduleMinute = currentMinute;
            // The approved warning sample is intentionally distinct from the
            // door and lockdown cues, but it should sit under normal jail
            // ambience.  Reduce only this cue by 35%.
            PlayOneShot(warningChimeClip, "30-minute recreation warning", 0.65f);
            // This is not just a player-facing warning.  NPCs must start their
            // return while their tier doors are still open, otherwise a busy
            // NavMesh route can leave them in the dayroom after lockup begins.
            CommandTierReturn(activeTier, GetActiveInmateBehaviors());
            ModLogger.Info($"[JAIL LIFECYCLE] Thirty-minute warning issued for {activeTier} recreation tier; inmates recalled before doors close");
        }

        /// <summary>
        /// Applies a native schedule transition: recalls the previous tier, opens the
        /// new tier or bedtime state, and enforces the local player's assigned cell.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void ApplySchedule(int currentMinute, bool force)
        {
            if (Core.JailController == null)
            {
                return;
            }

            JailRecreationTier desiredTier = JailRecreationSchedule.GetScheduledTier(currentMinute);
            if (!force && desiredTier == activeTier)
            {
                return;
            }

            List<InmateBehavior> inmates = GetActiveInmateBehaviors();
            JailRecreationTier previousTier = activeTier;
            if (TryBeginTransitionLockdown(currentMinute, previousTier, desiredTier, inmates))
            {
                return;
            }

            UpdatePlayerSegregationForTransition(previousTier, desiredTier);
            activeTier = desiredTier;
            lastWarningScheduleMinute = -1;
            if (previousTier == JailRecreationTier.Lower || previousTier == JailRecreationTier.Upper)
            {
                // Reissue at the transition as a recovery guard for an inmate
                // that spawned after the warning or briefly lost its route.
                CommandTierReturn(previousTier, inmates);
                StartTierReturnMonitor(previousTier);
            }

            if (desiredTier == JailRecreationTier.None)
            {
                CommandAllInmatesHome(inmates);
                StartTierReturnMonitor(JailRecreationTier.Lower);
                StartTierReturnMonitor(JailRecreationTier.Upper);
                ModLogger.Info("[JAIL LIFECYCLE] Bedtime count started; all recreation is closed until 07:00");
            }
            else
            {
                StopTierReturnMonitor(desiredTier);
                OpenTierForRecreation(desiredTier);
                CommandScheduledRecreation(desiredTier, inmates);
                ModLogger.Info($"[JAIL LIFECYCLE] {desiredTier} tier recreation opened on schedule; outgoing cells secure individually as inmates arrive");
            }

        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// At a real tier boundary, autonomous NPC pathing is recovered immediately so it
        /// cannot block intake or shorten the incoming tier. A late local player remains a
        /// custody incident and holds the schedule until responding officers secure them.
        /// </summary>
        private bool TryBeginTransitionLockdown(
            int currentMinute,
            JailRecreationTier previousTier,
            JailRecreationTier desiredTier,
            List<InmateBehavior> inmates)
        {
            if ((previousTier != JailRecreationTier.Lower && previousTier != JailRecreationTier.Upper) ||
                desiredTier == previousTier || GuardAssaultLockdownManager.IsLockdownActive)
            {
                return false;
            }

            var lateInmates = new List<InmateBehavior>();
            foreach (InmateBehavior inmate in inmates)
            {
                if (inmate != null && GetTierForCell(inmate.GetAssignedCellNumber()) == previousTier &&
                    !inmate.IsConfinedToAssignedCell())
                {
                    lateInmates.Add(inmate);
                }
            }

            Player latePlayer = null;
            Player player = GetLocalPlayer();
            JailTimeTracker tracker = Core.ResolveJailTimeTracker();
            CellAssignmentManager assignments = Core.ResolveCellAssignmentManager();
            if (player != null && tracker != null && assignments != null && tracker.IsTracking(player))
            {
                int cellIndex = assignments.GetPlayerCellNumber(player);
                if (cellIndex >= 0 && GetTierForCell(cellIndex) == previousTier &&
                    Core.JailController?.IsPlayerInJailCellBounds(player, cellIndex) != true)
                {
                    latePlayer = player;
                }
            }

            if (lateInmates.Count > 0)
            {
                RecoverTierStragglers(previousTier, lateInmates, "tier-boundary safety net");
            }

            if (latePlayer == null)
            {
                return false;
            }

            BeginEmergencyScheduleHold(currentMinute);
            if (GuardAssaultLockdownManager.TryBeginScheduleViolation(latePlayer))
            {
                ModLogger.Warn(
                    $"[JAIL LIFECYCLE] Held {previousTier}-to-{desiredTier} boundary at " +
                    $"{currentMinute / 60:00}:{currentMinute % 60:00} for the late local player");
                return true;
            }

            CancelEmergencyScheduleHold();
            ModLogger.Error("[JAIL LIFECYCLE] Could not start officer response for the late player; schedule hold was canceled without transferring the player");
            return false;
        }

        /// <summary>
        /// Resolves the native Schedule I clock into minutes of day. The legacy
        /// GameTimeManager path is only a safe construction fallback and is not preferred
        /// once the native clock is available.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool TryGetCurrentScheduleMinute(out int minuteOfDay)
        {
            if (scheduleHoldActive)
            {
                minuteOfDay = heldScheduleMinute;
                return minuteOfDay >= 0;
            }

            if (!TryGetNativeScheduleMinute(out int nativeMinute))
            {
                minuteOfDay = 0;
                return false;
            }

            minuteOfDay = NormalizeScheduleMinute(
                Mathf.FloorToInt(GetPreciseNativeScheduleMinute() - accumulatedScheduleDelayMinutes));
            return true;
        }

        /// <summary>Reads the unmodified Schedule I time-of-day minute.</summary>
        private bool TryGetNativeScheduleMinute(out int minuteOfDay)
        {
            try
            {
                TimeManager nativeTimeManager = TimeManager.Instance;
                if (nativeTimeManager != null)
                {
                    int rawTime = nativeTimeManager.CurrentTime;
                    int hour = Mathf.Clamp(rawTime / 100, 0, 23);
                    int minute = Mathf.Clamp(rawTime % 100, 0, 59);
                    minuteOfDay = (hour * 60) + minute;
                    return true;
                }
            }
            catch (Exception exception)
            {
                if (!loggedNativeTimeFallback)
                {
                    loggedNativeTimeFallback = true;
                    ModLogger.Warn($"[JAIL LIFECYCLE] Native game clock unavailable; using legacy fallback until it becomes available: {exception.Message}");
                }
            }

            // The fallback keeps early scene construction safe, but is no
            // longer the schedule source once Schedule I's native clock exists.
            GameTimeManager fallback = GameTimeManager.Instance;
            minuteOfDay = (Mathf.Clamp(fallback.GetCurrentGameHour(), 0, 23) * 60) +
                          Mathf.Clamp(fallback.GetCurrentGameMinute(), 0, 59);
            return true;
        }

        /// <summary>Begins an idempotent jail-local clock hold at the current effective schedule minute.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void BeginEmergencyScheduleHold()
        {
            if (TryGetCurrentScheduleMinute(out int currentMinute))
            {
                BeginEmergencyScheduleHold(currentMinute);
            }
        }

        /// <summary>Begins a jail-local clock hold without modifying the global Schedule I clock.</summary>
        private void BeginEmergencyScheduleHold(int currentMinute)
        {
            if (scheduleHoldActive)
            {
                return;
            }

            heldScheduleMinute = NormalizeScheduleMinute(currentMinute);
            holdStartedNativeMinute = GetPreciseNativeScheduleMinute();
            scheduleHoldActive = true;
            statusClockLastRealtime = Time.realtimeSinceStartup;
            BeginLockdownAudio();
            ModLogger.Warn($"[JAIL LIFECYCLE] Jail recreation clock held at {heldScheduleMinute / 60:00}:{heldScheduleMinute % 60:00}");
        }

        /// <summary>
        /// Releases the jail-local clock hold, shifts future recreation boundaries by the
        /// elapsed native game minutes, and reapplies the held boundary as a fresh transition.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void EndEmergencyScheduleHold()
        {
            if (!scheduleHoldActive)
            {
                return;
            }

            float elapsedMinutes = ForwardMinuteDelta(
                holdStartedNativeMinute,
                GetPreciseNativeScheduleMinute());
            accumulatedScheduleDelayMinutes = Mathf.Repeat(
                accumulatedScheduleDelayMinutes + elapsedMinutes,
                1440f);
            scheduleHoldActive = false;
            heldScheduleMinute = -1;
            lastObservedNativeMinute = -1;
            statusClockMinute = -1;
            EndLockdownAudio();
            ModLogger.Info($"[JAIL LIFECYCLE] Jail recreation clock resumed after {elapsedMinutes:F1} held game minute(s)");
            RefreshScheduleFromNativeTime(force: true);
        }

        /// <summary>Releases a hold that failed before an emergency response took ownership.</summary>
        private void CancelEmergencyScheduleHold()
        {
            scheduleHoldActive = false;
            heldScheduleMinute = -1;
            EndLockdownAudio();
        }

        private static int NormalizeScheduleMinute(int minute)
        {
            int normalized = minute % 1440;
            return normalized < 0 ? normalized + 1440 : normalized;
        }

        private static float ForwardMinuteDelta(float startMinute, float endMinute)
        {
            float delta = endMinute - startMinute;
            return delta < 0f ? delta + 1440f : delta;
        }

        /// <summary>Reads fractional native time-of-day for exact hold-duration accounting.</summary>
        private static float GetPreciseNativeScheduleMinute()
        {
            try
            {
                TimeManager manager = TimeManager.Instance;
                if (manager != null)
                {
                    int rawTime = manager.CurrentTime;
                    int wholeMinute = (Mathf.Clamp(rawTime / 100, 0, 23) * 60) +
                                      Mathf.Clamp(rawTime % 100, 0, 59);
                    float normalizedMinute = manager.NormalizedTimeOfDay * 1440f;
                    float fraction = Mathf.Clamp(normalizedMinute - manager.DailyMinSum, 0f, 0.999f);
                    return wholeMinute + fraction;
                }
            }
            catch
            {
                // Integer fallback remains sufficient to keep the schedule monotonic.
            }

            GameTimeManager fallback = GameTimeManager.Instance;
            return (Mathf.Clamp(fallback.GetCurrentGameHour(), 0, 23) * 60) +
                   Mathf.Clamp(fallback.GetCurrentGameMinute(), 0, 59);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator RefreshDuringInitialInmateSpawn()
        {
            // PrisonNPCManager begins spawning on its own Start cycle and creates
            // inmate behaviours over several seconds. Reapply commands without
            // reopening doors or replaying signals while that roster settles.
            const int refreshAttempts = 12;
            for (int attempt = 0; attempt < refreshAttempts; attempt++)
            {
                yield return new WaitForSecondsRealtime(1f);
                ApplyCurrentTierToInmates();
            }

            initialScheduleCoroutine = null;
        }

        /// <summary>
        /// Called by the canonical prison NPC manager after it attaches an
        /// <see cref="InmateBehavior"/>. This avoids relying on a race-prone
        /// startup delay to put late-spawned inmates into the current schedule.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void NotifyInmateRosterChanged()
        {
            ApplyCurrentTierToInmates();
            ModLogger.Debug($"[JAIL LIFECYCLE] Applied {activeTier} schedule to the updated inmate roster");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Reapply the already-selected tier to late-spawned inmates without replaying
        // door/audio transition effects; the canonical manager also calls this directly.
        private void ApplyCurrentTierToInmates()
        {
            if (activeTier == JailRecreationTier.Unknown || scheduleHoldActive)
            {
                return;
            }

            List<InmateBehavior> inmates = GetActiveInmateBehaviors();
            if (activeTier == JailRecreationTier.None)
            {
                CommandAllInmatesHome(inmates);
                return;
            }

            CommandScheduledRecreation(activeTier, inmates);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Opening is limited to the cell indices belonging to the selected tier; the
        // actual schedule decision is made by ApplySchedule, not by the door controller.
        private void OpenTierForRecreation(JailRecreationTier tier)
        {
            foreach (int cellIndex in GetCellIndicesForTier(tier))
            {
                if (IsCellRestrictedBySegregation(cellIndex))
                {
                    Core.JailController?.doorController?.SecureJailCellDoor(cellIndex);
                    ModLogger.Info($"[SEGREGATION] Kept assigned cell {cellIndex} secured while {tier} tier recreation opened");
                    continue;
                }

                Core.JailController?.doorController?.OpenJailCellDoor(cellIndex);
            }
            PlayOneShot(doorBuzzerClip, $"{tier} recreation door buzzer");
        }

        /// <summary>
        /// Starts and completes persisted segregation only at real assigned-tier boundaries.
        /// A punishment imposed during a partial recreation block therefore cannot consume
        /// that block; its first counted cycle starts at the player's next tier opening.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void UpdatePlayerSegregationForTransition(
            JailRecreationTier previousTier,
            JailRecreationTier desiredTier)
        {
            if (previousTier == JailRecreationTier.Unknown || previousTier == desiredTier)
            {
                return;
            }

            Player player = GetLocalPlayer();
            CellAssignmentManager assignments = Core.ResolveCellAssignmentManager();
            RapSheet rapSheet = Core.GetRapSheet(player);
            if (player == null || assignments == null || rapSheet == null || !rapSheet.HasActiveSegregation)
            {
                return;
            }

            int cellIndex = assignments.GetPlayerCellNumber(player);
            JailRecreationTier assignedTier = GetTierForCell(cellIndex);
            if (previousTier == assignedTier && rapSheet.IsSegregationCycleActive &&
                rapSheet.TryCompleteSegregationCycle())
            {
                Core.MarkRapSheetChanged(player);
                ModLogger.Info(
                    $"[SEGREGATION] {player.name} completed one full recreation cycle; " +
                    $"{rapSheet.SegregationCyclesRemaining} cycle(s) remain");
            }

            if (desiredTier == assignedTier && rapSheet.HasActiveSegregation &&
                !rapSheet.IsSegregationCycleActive && rapSheet.TryBeginSegregationCycle())
            {
                Core.MarkRapSheetChanged(player);
                ModLogger.Warn(
                    $"[SEGREGATION] {player.name} began a full {assignedTier} tier segregation cycle; " +
                    $"{rapSheet.SegregationCyclesRemaining} cycle(s) remain including this block");
            }
        }

        /// <summary>Whether an assigned player cell must stay locked during its tier's recreation.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool IsCellRestrictedBySegregation(int cellIndex)
        {
            Player player = GetLocalPlayer();
            CellAssignmentManager assignments = Core.ResolveCellAssignmentManager();
            return player != null && assignments != null &&
                   assignments.GetPlayerCellNumber(player) == cellIndex &&
                   Core.GetRapSheet(player)?.HasActiveSegregation == true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Assign only inmates whose authored cell tier is active; every other inmate is
        // recalled even if the roster arrived after the schedule transition.
        private void CommandScheduledRecreation(JailRecreationTier tier, List<InmateBehavior> inmates)
        {
            List<Transform> anchors = GetRecreationAnchors(tier);
            foreach (InmateBehavior inmate in inmates)
            {
                if (inmate == null)
                {
                    continue;
                }

                if (GetTierForCell(inmate.GetAssignedCellNumber()) == tier)
                {
                    inmate.BeginScheduledRecreation(anchors);
                }
                else
                {
                    inmate.ReturnToAssignedCell();
                }
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void CommandTierReturn(JailRecreationTier tier, List<InmateBehavior> inmates)
        {
            foreach (InmateBehavior inmate in inmates)
            {
                if (inmate != null && GetTierForCell(inmate.GetAssignedCellNumber()) == tier)
                {
                    inmate.ReturnToAssignedCell();
                }
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void CommandAllInmatesHome(List<InmateBehavior> inmates)
        {
            foreach (InmateBehavior inmate in inmates)
            {
                inmate?.ReturnToAssignedCell();
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void StartTierReturnMonitor(JailRecreationTier tier)
        {
            if (tier == JailRecreationTier.Lower)
            {
                if (lowerTierReturnCoroutine == null)
                {
                    lowerTierReturnCoroutine = MelonCoroutines.Start(SecureTierAsInmatesArrive(tier)) as Coroutine;
                }
            }
            else if (tier == JailRecreationTier.Upper && upperTierReturnCoroutine == null)
            {
                upperTierReturnCoroutine = MelonCoroutines.Start(SecureTierAsInmatesArrive(tier)) as Coroutine;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void RecoverTierStragglers(
            JailRecreationTier tier,
            List<InmateBehavior> inmates,
            string reason)
        {
            int recovered = 0;
            int failed = 0;
            foreach (InmateBehavior inmate in inmates)
            {
                if (inmate == null || GetTierForCell(inmate.GetAssignedCellNumber()) != tier ||
                    inmate.IsConfinedToAssignedCell())
                {
                    continue;
                }

                if (inmate.SecureInAssignedCellForScheduleRecovery(reason))
                {
                    recovered++;
                }
                else
                {
                    failed++;
                }
            }

            if (recovered > 0 || failed > 0)
            {
                ModLogger.Warn(
                    $"[JAIL LIFECYCLE] {tier} tier NPC schedule recovery ({reason}): " +
                    $"recovered={recovered}, failed={failed}. NPC pathing will not hold the jail clock.");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void StopTierReturnMonitor(JailRecreationTier tier)
        {
            Coroutine monitor = tier == JailRecreationTier.Lower
                ? lowerTierReturnCoroutine
                : upperTierReturnCoroutine;
            if (monitor != null)
            {
                MelonCoroutines.Stop(monitor);
            }

            if (tier == JailRecreationTier.Lower)
            {
                lowerTierReturnCoroutine = null;
            }
            else if (tier == JailRecreationTier.Upper)
            {
                upperTierReturnCoroutine = null;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Each inmate owns their canonical NavMesh return. Cells lock independently as
        // their occupants arrive, so a straggler cannot shorten the other tier's block.
        private IEnumerator SecureTierAsInmatesArrive(JailRecreationTier tier)
        {
            var individuallySecuredCells = new HashSet<int>();
            float nextRepathTime = Time.realtimeSinceStartup + StragglerRepathSeconds;
            while (true)
            {
                bool pending = false;
                foreach (InmateBehavior inmate in GetActiveInmateBehaviors())
                {
                    if (GetTierForCell(inmate.GetAssignedCellNumber()) != tier)
                    {
                        continue;
                    }

                    if (inmate.IsConfinedToAssignedCell())
                    {
                        int cellIndex = inmate.GetAssignedCellNumber();
                        if (individuallySecuredCells.Add(cellIndex))
                        {
                            Core.JailController?.doorController?.SecureJailCellDoor(cellIndex);
                        }
                    }
                    else
                    {
                        pending = true;
                        if (Time.realtimeSinceStartup >= nextRepathTime)
                        {
                            // Idempotent while a valid route is active; otherwise this
                            // recovers a route lost to a door/NavMesh state change.
                            inmate.ReturnToAssignedCell();
                        }
                    }
                }

                if (!pending)
                {
                    SecureTierDoors(tier);
                    if (tier == JailRecreationTier.Lower)
                    {
                        lowerTierReturnCoroutine = null;
                    }
                    else
                    {
                        upperTierReturnCoroutine = null;
                    }
                    ModLogger.Info($"[JAIL LIFECYCLE] {tier} tier return complete; all cells secured");
                    yield break;
                }

                if (Time.realtimeSinceStartup >= nextRepathTime)
                {
                    nextRepathTime = Time.realtimeSinceStartup + StragglerRepathSeconds;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SecureTierDoors(JailRecreationTier tier)
        {
            foreach (int cellIndex in GetCellIndicesForTier(tier))
            {
                Core.JailController?.doorController?.SecureJailCellDoor(cellIndex);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Uses CellDetail.cellIndex (authored index), never the compact list position,
        // because non-cell children may exist under the Cells parent.
        private List<int> GetCellIndicesForTier(JailRecreationTier tier)
        {
            var results = new List<int>();
            var cells = Core.JailController?.cells;
            if (cells == null)
            {
                return results;
            }

            foreach (CellDetail cell in cells)
            {
                if (cell != null && GetTierForCell(cell.cellIndex) == tier)
                {
                    // Door and inmate systems use the authored child index,
                    // not this compacted list position.  The Cells parent can
                    // contain non-cell children, so using a list offset here
                    // quietly opened the wrong tier.
                    results.Add(cell.cellIndex);
                }
            }
            return results;
        }

        /// <summary>
        /// Classifies a cell by its authored physical height. Bounds/collider height is
        /// preferred over the shared cell-root transform and returns None when no usable
        /// cell geometry is available.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private JailRecreationTier GetTierForCell(int cellIndex)
        {
            var cellManager = Core.JailController?.cellManager;
            var cells = cellManager?.cells;
            CellDetail targetCell = cellManager?.GetCellByIndex(cellIndex);
            if (cells == null || targetCell == null)
            {
                return JailRecreationTier.None;
            }

            float minY = float.MaxValue;
            float maxY = float.MinValue;
            foreach (CellDetail cell in cells)
            {
                if (!TryGetCellTierHeight(cell, out float cellHeight))
                {
                    continue;
                }
                minY = Mathf.Min(minY, cellHeight);
                maxY = Mathf.Max(maxY, cellHeight);
            }

            if (minY == float.MaxValue)
            {
                return JailRecreationTier.None;
            }

            float divider = (minY + maxY) * 0.5f;
            if (!TryGetCellTierHeight(targetCell, out float targetHeight))
            {
                return JailRecreationTier.None;
            }

            return targetHeight > divider
                ? JailRecreationTier.Upper
                : JailRecreationTier.Lower;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static bool TryGetCellTierHeight(CellDetail cell, out float height)
        {
            // The Cell root is a shared authoring-level transform in the jail
            // prefab, so its world Y is not a reliable floor discriminator.
            // CellBounds is authored at the actual physical cell on either tier;
            // its collider center also accounts for a non-zero local center.
            Transform levelAnchor = cell?.cellBounds ?? cell?.cellTransform;
            if (levelAnchor == null)
            {
                height = 0f;
                return false;
            }

            BoxCollider boundsCollider = levelAnchor.GetComponent<BoxCollider>();
            height = boundsCollider != null ? boundsCollider.bounds.center.y : levelAnchor.position.y;
            return true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Recreation anchors use a name contract: names containing "Upper" belong to
        // the upper tier; all other patrol anchors are treated as lower-tier anchors.
        private List<Transform> GetRecreationAnchors(JailRecreationTier tier)
        {
            var anchors = new List<Transform>();
            List<Transform> allAnchors = Core.JailController?.GetPatrolPoints();
            if (allAnchors == null)
            {
                return anchors;
            }

            foreach (Transform anchor in allAnchors)
            {
                if (anchor == null)
                {
                    continue;
                }

                bool upperName = anchor.name.IndexOf("Upper", StringComparison.OrdinalIgnoreCase) >= 0;
                if ((tier == JailRecreationTier.Upper && upperName) ||
                    (tier == JailRecreationTier.Lower && !upperName))
                {
                    anchors.Add(anchor);
                }
            }

            return anchors;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Resolve canonical inmate behaviors through the safe component helper and
        // return a fresh snapshot so callers can iterate without owning the manager list.
        private static List<InmateBehavior> GetActiveInmateBehaviors()
        {
            var behaviors = new List<InmateBehavior>();
            PrisonNPCManager manager = Core.ResolvePrisonNpcManager();
            if (manager == null)
            {
                return behaviors;
            }

            foreach (PrisonInmate inmate in manager.GetActiveInmates())
            {
                if (inmate == null)
                {
                    continue;
                }

                InmateBehavior behavior = BBHelpers.GetComponentSafe<InmateBehavior>(inmate.gameObject);
                if (behavior != null)
                {
                    behaviors.Add(behavior);
                }
            }

            return behaviors;
        }

        /// <summary>
        /// Returns whether the supplied cell belongs to the tier that is
        /// currently in recreation. Used by the booking flow so a newly
        /// assigned prisoner is not locked into an active out-time tier.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public bool IsCellInActiveRecreation(int cellIndex)
        {
            return !scheduleHoldActive && (activeTier == JailRecreationTier.Lower || activeTier == JailRecreationTier.Upper)
                ? GetTierForCell(cellIndex) == activeTier && !IsCellRestrictedBySegregation(cellIndex)
                : false;
        }

        /// <summary>
        /// Returns true when the local jailed player's assigned tier is presently scheduled
        /// for recreation. Segregated escapees still qualify so additional assaults escalate.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public bool IsPlayerOnActiveRecreation(Player player)
        {
            if (player == null || player != GetLocalPlayer() || scheduleHoldActive ||
                !Core.ResolveJailTimeTracker().IsInJail(player))
            {
                return false;
            }

            CellAssignmentManager assignments = Core.ResolveCellAssignmentManager();
            int cellIndex = assignments?.GetPlayerCellNumber(player) ?? -1;
            return cellIndex >= 0 && GetTierForCell(cellIndex) == activeTier;
        }

        /// <summary>
        /// Produces the player-facing recreation snapshot without duplicating schedule
        /// ownership in the UI. The countdown is expressed in real seconds so the
        /// default two-game-hour block displays as two real minutes.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool TryGetLocalPlayerRecreationStatus(out JailRecreationStatus status)
        {
            status = null;

            Player player = GetLocalPlayer();
            CellAssignmentManager assignments = Core.ResolveCellAssignmentManager();
            JailTimeTracker tracker = Core.ResolveJailTimeTracker();
            if (player == null || assignments == null || tracker == null || !tracker.IsInJail(player))
            {
                return false;
            }

            int assignedCell = assignments.GetPlayerCellNumber(player);
            if (assignedCell < 0 || !TryGetCurrentScheduleMinute(out int currentMinute))
            {
                return false;
            }

            TimeManager nativeTimeManager = null;
            try
            {
                nativeTimeManager = TimeManager.Instance;
            }
            catch (Exception)
            {
                // The legacy one-real-second fallback below keeps early scene setup safe.
            }

            UpdateStatusClock(nativeTimeManager, currentMinute);

            JailRecreationTier assignedTier = GetTierForCell(assignedCell);
            if (assignedTier != JailRecreationTier.Lower && assignedTier != JailRecreationTier.Upper)
            {
                return false;
            }

            JailRecreationTier scheduledTier = JailRecreationSchedule.GetScheduledTier(currentMinute);
            RapSheet rapSheet = Core.GetRapSheet(player);
            bool isSegregated = rapSheet?.HasActiveSegregation == true;
            bool segregationCycleActive = rapSheet?.IsSegregationCycleActive == true;
            int segregationCyclesRemaining = rapSheet?.SegregationCyclesRemaining ?? 0;
            bool assignedTierScheduled = scheduledTier == assignedTier;
            bool assignedTierActive = assignedTierScheduled && !isSegregated;
            int targetMinute = segregationCycleActive
                ? JailRecreationSchedule.GetActiveBlockEndMinute(currentMinute)
                : assignedTierActive
                ? JailRecreationSchedule.GetActiveBlockEndMinute(currentMinute)
                : JailRecreationSchedule.GetNextTierStartMinute(currentMinute, assignedTier);
            int phaseStartMinute = JailRecreationSchedule.GetActiveBlockStartMinute(currentMinute);
            float currentScheduleMinute = currentMinute + statusClockMinuteProgress;

            status = new JailRecreationStatus
            {
                AssignedCellNumber = assignedCell,
                AssignedTier = assignedTier,
                ActiveTier = scheduleHoldActive ? JailRecreationTier.None : scheduledTier,
                IsAssignedTierActive = !scheduleHoldActive && assignedTierActive,
                IsInSegregation = isSegregated,
                IsSegregationCycleActive = segregationCycleActive,
                SegregationCyclesRemaining = segregationCyclesRemaining,
                RemainingRealSeconds = JailRecreationSchedule.GetRemainingRealSeconds(
                    currentMinute,
                    statusClockMinuteProgress,
                    targetMinute,
                    statusSecondsPerGameMinute),
                PhaseProgress = JailRecreationSchedule.GetPhaseProgress(
                    currentScheduleMinute,
                    phaseStartMinute,
                    targetMinute)
            };
            return true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Advance fractional schedule progress from wall-clock time while the native
        // minute is stable. Progress is capped below 1 so the next native tick owns the
        // actual boundary transition.
        private void UpdateStatusClock(TimeManager nativeTimeManager, int currentMinute)
        {
            float realtimeNow = Time.realtimeSinceStartup;
            if (scheduleHoldActive)
            {
                statusClockLastRealtime = realtimeNow;
                return;
            }
            float secondsPerGameMinute = ResolveSecondsPerGameMinute(nativeTimeManager, currentMinute);

            if (statusClockMinute != currentMinute)
            {
                statusClockMinute = currentMinute;
                statusClockMinuteProgress = ResolveEffectiveMinuteProgress(currentMinute);
                statusClockLastRealtime = realtimeNow;
                return;
            }

            float elapsedRealSeconds = Mathf.Max(0f, realtimeNow - statusClockLastRealtime);
            statusClockLastRealtime = realtimeNow;

            if (IsNativeClockAdvancing(nativeTimeManager, currentMinute) && secondsPerGameMinute > 0f)
            {
                statusClockMinuteProgress = Mathf.Min(
                    0.999f,
                    statusClockMinuteProgress + (elapsedRealSeconds / secondsPerGameMinute));
            }
        }

        /// <summary>
        /// Computes real seconds represented by one game minute from the native time
        /// manager. When the native clock is paused, the last valid conversion is kept so
        /// the UI countdown does not advance behind the game's clock.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private float ResolveSecondsPerGameMinute(TimeManager nativeTimeManager, int currentMinute)
        {
            if (!IsNativeClockAdvancing(nativeTimeManager, currentMinute))
            {
                return statusSecondsPerGameMinute;
            }

            float minuteDuration = nativeTimeManager != null ? TimeManager.MinuteDuration : 1f;
            float speedMultiplier = nativeTimeManager != null
                ? Mathf.Max(nativeTimeManager.TimeSpeedMultiplier, 0.0001f)
                : 1f;
            statusSecondsPerGameMinute = minuteDuration /
                                         (speedMultiplier * Mathf.Max(Time.timeScale, 0.0001f));
            return statusSecondsPerGameMinute;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Game time is intentionally frozen during the overnight 04:00-06:00 window
        // and whenever Unity/native time is paused; callers then retain the last progress.
        private static bool IsNativeClockAdvancing(TimeManager nativeTimeManager, int currentMinute)
        {
            if (Time.timeScale <= 0f || (currentMinute >= 4 * 60 && currentMinute < 6 * 60))
            {
                return false;
            }

            return nativeTimeManager == null || nativeTimeManager.TimeSpeedMultiplier > 0f;
        }

        /// <summary>Resolves fractional progress after applying any accumulated jail-local delay.</summary>
        private float ResolveEffectiveMinuteProgress(int currentMinute)
        {
            float effectiveMinute = Mathf.Repeat(
                GetPreciseNativeScheduleMinute() - accumulatedScheduleDelayMinutes,
                1440f);
            return Mathf.Clamp(effectiveMinute - currentMinute, 0f, 0.999f);
        }

        private static Player GetLocalPlayer()
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Called once from Start for this scene; it creates separate one-shot and looping
        // sources because the warning/door cues must not interrupt the lockdown siren.
        private void EnsureAudioSources()
        {
            signalAudioSource = gameObject.AddComponent<AudioSource>();
            lockdownAudioSource = gameObject.AddComponent<AudioSource>();
            ConfigureLocalAudioSource(signalAudioSource, loop: false);
            ConfigureLocalAudioSource(lockdownAudioSource, loop: true);

            const string bundleName = "Behind_Bars.behind_bars_jail_lifecycle_audio";
            doorBuzzerClip = AssetBundleUtils.LoadAudioClipFromBundle(bundleName, "assets/behindbars/sounds/jail_recreation_buzzer.wav");
            warningChimeClip = AssetBundleUtils.LoadAudioClipFromBundle(bundleName, "assets/behindbars/sounds/jail_recreation_warning.mp3");
            lockdownSirenClip = AssetBundleUtils.LoadAudioClipFromBundle(bundleName, "assets/behindbars/sounds/jail_lockdown_siren.wav");

            ModLogger.Info(
                $"[JAIL LIFECYCLE] Audio ready: buzzer={doorBuzzerClip != null}, " +
                $"warning={warningChimeClip != null}, siren={lockdownSirenClip != null}");
        }

        private static void ConfigureLocalAudioSource(AudioSource source, bool loop)
        {
            // The jail root is an injected runtime object, not a reliable
            // physical loudspeaker. Use a 2D source for consistent playback,
            // then enforce locality explicitly before each cue is played.
            source.spatialBlend = 0f;
            source.volume = 0.82f;
            source.loop = loop;
            source.playOnAwake = false;
        }

        private void PlayOneShot(AudioClip clip, string purpose, float volumeScale = 1f)
        {
            if (signalAudioSource == null || clip == null)
            {
                ModLogger.Warn($"[JAIL LIFECYCLE] Local audio clip unavailable for {purpose}");
                return;
            }

            if (!IsLocalPlayerWithinJailAudioRange())
            {
                ModLogger.Debug($"[JAIL LIFECYCLE] Suppressed {purpose} cue because the player is outside the jail audio range");
                return;
            }

            signalAudioSource.PlayOneShot(clip, Mathf.Clamp01(volumeScale));
        }

        private void BeginLockdownAudio()
        {
            if (lockdownAudioSource == null || lockdownSirenClip == null || lockdownAudioSource.isPlaying)
            {
                return;
            }

            if (IsLocalPlayerWithinJailAudioRange())
            {
                lockdownAudioSource.clip = lockdownSirenClip;
                lockdownAudioSource.Play();
            }
        }

        private void EndLockdownAudio()
        {
            if (lockdownAudioSource != null && lockdownAudioSource.isPlaying)
            {
                lockdownAudioSource.Stop();
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool IsLocalPlayerWithinJailAudioRange()
        {
            Player player = GetLocalPlayer();
            if (player == null)
            {
                // Do not make an unavailable player surface suppress a
                // scene-local cue during early initialization.
                return true;
            }

            Vector3 listenerPosition = player.transform.position;
            Vector3 jailPosition = transform.position;
            return (listenerPosition - jailPosition).sqrMagnitude <= LocalAudioMaxDistance * LocalAudioMaxDistance;
        }
    }
}
