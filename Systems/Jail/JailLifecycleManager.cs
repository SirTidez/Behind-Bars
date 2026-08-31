using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Behind_Bars.Helpers;
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
        private const float LocalAudioMaxDistance = 55f;
        private const float InmateReturnGraceSeconds = 10f;

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
        private bool playerReturnInProgress;
        private Coroutine returnGraceCoroutine;
        private Coroutine playerReturnCoroutine;
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
            if (returnGraceCoroutine != null)
            {
                MelonCoroutines.Stop(returnGraceCoroutine);
                returnGraceCoroutine = null;
            }
            if (playerReturnCoroutine != null)
            {
                MelonCoroutines.Stop(playerReturnCoroutine);
                playerReturnCoroutine = null;
            }
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
            if (endScheduleMinute - currentMinute != WarningMinutesBeforeClose ||
                lastWarningScheduleMinute == currentMinute)
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

            JailRecreationTier previousTier = activeTier;
            activeTier = desiredTier;
            lastWarningScheduleMinute = -1;

            if (returnGraceCoroutine != null)
            {
                MelonCoroutines.Stop(returnGraceCoroutine);
                returnGraceCoroutine = null;
            }

            List<InmateBehavior> inmates = GetActiveInmateBehaviors();
            if (previousTier == JailRecreationTier.Lower || previousTier == JailRecreationTier.Upper)
            {
                // Reissue at the transition as a recovery guard for an inmate
                // that spawned after the warning or briefly lost its route.
                CommandTierReturn(previousTier, inmates);
                returnGraceCoroutine = MelonCoroutines.Start(SecureTierAfterReturn(previousTier)) as Coroutine;
            }

            if (desiredTier == JailRecreationTier.None)
            {
                CommandAllInmatesHome(inmates);
                if (returnGraceCoroutine == null)
                {
                    returnGraceCoroutine = MelonCoroutines.Start(SecureAllAfterReturn()) as Coroutine;
                }
                ModLogger.Info("[JAIL LIFECYCLE] Bedtime count started; all recreation is closed until 07:00");
            }
            else
            {
                OpenTierForRecreation(desiredTier);
                CommandScheduledRecreation(desiredTier, inmates);
                ModLogger.Info($"[JAIL LIFECYCLE] {desiredTier} tier recreation opened");
            }

            EnforcePlayerAtTransition(desiredTier);
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
            if (activeTier == JailRecreationTier.Unknown)
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
                Core.JailController?.doorController?.OpenJailCellDoor(cellIndex);
            }
            PlayOneShot(doorBuzzerClip, $"{tier} recreation door buzzer");
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
        // The grace period is real time so NPC return/door security still completes when
        // the game clock is paused or running at a different speed.
        private IEnumerator SecureTierAfterReturn(JailRecreationTier tier)
        {
            float deadline = Time.realtimeSinceStartup + InmateReturnGraceSeconds;
            while (Time.realtimeSinceStartup < deadline)
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
                        Core.JailController?.doorController?.SecureJailCellDoor(inmate.GetAssignedCellNumber());
                    }
                    else
                    {
                        pending = true;
                    }
                }

                if (!pending)
                {
                    SecureTierDoors(tier);
                    returnGraceCoroutine = null;
                    yield break;
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            SecureTierDoors(tier);
            returnGraceCoroutine = null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator SecureAllAfterReturn()
        {
            yield return SecureTierAfterReturn(JailRecreationTier.Lower);
            yield return SecureTierAfterReturn(JailRecreationTier.Upper);
            returnGraceCoroutine = null;
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        // A transition only returns a tracked player whose assigned tier is not active;
        // an already-confined player is left alone to avoid a disruptive false lockdown.
        private void EnforcePlayerAtTransition(JailRecreationTier tier)
        {
            if (playerReturnInProgress)
            {
                return;
            }

            Player player = GetLocalPlayer();
            JailTimeTracker tracker = Core.ResolveJailTimeTracker();
            CellAssignmentManager assignments = Core.ResolveCellAssignmentManager();
            if (player == null || tracker == null || assignments == null || !tracker.IsTracking(player))
            {
                return;
            }

            int cellIndex = assignments.GetPlayerCellNumber(player);
            if (cellIndex < 0)
            {
                return;
            }

            // A tier transition is not itself an escape. The player may have
            // obeyed the recall and already be inside their own cell; in that
            // case the normal tier-door securing path is sufficient and a
            // blackout/teleport would be both disruptive and misleading.
            if (Core.JailController?.IsPlayerInJailCellBounds(player, cellIndex) == true)
            {
                ModLogger.Debug($"[JAIL LIFECYCLE] Player is already confined in cell {cellIndex}; no schedule-return teleport required");
                return;
            }

            JailRecreationTier playerTier = GetTierForCell(cellIndex);
            if (tier == playerTier)
            {
                return;
            }

            playerReturnCoroutine = MelonCoroutines.Start(ForcePlayerToAssignedCell(player, cellIndex)) as Coroutine;
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
            return activeTier == JailRecreationTier.Lower || activeTier == JailRecreationTier.Upper
                ? GetTierForCell(cellIndex) == activeTier
                : false;
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
            bool assignedTierActive = scheduledTier == assignedTier;
            int targetMinute = assignedTierActive
                ? JailRecreationSchedule.GetActiveBlockEndMinute(currentMinute)
                : JailRecreationSchedule.GetNextTierStartMinute(currentMinute, assignedTier);
            int phaseStartMinute = JailRecreationSchedule.GetActiveBlockStartMinute(currentMinute);
            float currentScheduleMinute = currentMinute + statusClockMinuteProgress;

            status = new JailRecreationStatus
            {
                AssignedCellNumber = assignedCell,
                AssignedTier = assignedTier,
                ActiveTier = scheduledTier,
                IsAssignedTierActive = assignedTierActive,
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
            float secondsPerGameMinute = ResolveSecondsPerGameMinute(nativeTimeManager, currentMinute);

            if (statusClockMinute != currentMinute)
            {
                statusClockMinute = currentMinute;
                statusClockMinuteProgress = ResolveNativeMinuteProgress(nativeTimeManager);
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        // NormalizedTimeOfDay supplies fractional day progress; DailyMinSum anchors it
        // to the current native minute without exposing native time types to the UI.
        private static float ResolveNativeMinuteProgress(TimeManager nativeTimeManager)
        {
            if (nativeTimeManager == null)
            {
                return 0f;
            }

            float preciseMinute = nativeTimeManager.NormalizedTimeOfDay * 1440f;
            return Mathf.Clamp(preciseMinute - nativeTimeManager.DailyMinSum, 0f, 0.999f);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        // Schedule recall uses a brief real-time blackout, then the same authored spawn
        // destination/orientation as custody transfer, and finally restores normal audio
        // and lighting. Failure is logged rather than treated as a successful return.
        private IEnumerator ForcePlayerToAssignedCell(Player player, int cellIndex)
        {
            playerReturnInProgress = true;
            try
            {
                Singleton<BlackOverlay>.Instance.Open(0.15f);
            }
            catch (Exception exception)
            {
                ModLogger.Warn($"[JAIL LIFECYCLE] Could not open schedule-lockdown blackout: {exception.Message}");
            }

            BeginLockdownAudio();
            Core.JailController?.SetJailLighting(JailLightingController.LightingState.Emergency);
            yield return new WaitForSecondsRealtime(0.2f);

            var assignments = Core.ResolveCellAssignmentManager();
            CellDetail cell = Core.JailController?.GetCellByIndex(cellIndex);
            Transform destination = assignments?.GetCellSpawnPoints(cellIndex).FirstOrDefault(point => point != null)
                ?? cell?.cellBounds
                ?? cell?.cellTransform;
            if (destination != null && player != null)
            {
                player.transform.SetPositionAndRotation(destination.position, Quaternion.LookRotation(Vector3.left, Vector3.up));
                Core.JailController?.doorController?.SecureJailCellDoor(cellIndex);
                ModLogger.Info($"[JAIL LIFECYCLE] Returned prisoner to assigned cell {cellIndex} for schedule lockdown");
            }
            else
            {
                ModLogger.Error($"[JAIL LIFECYCLE] Could not return prisoner to assigned cell {cellIndex}: no usable spawn destination");
            }

            yield return new WaitForSecondsRealtime(0.25f);
            Core.JailController?.SetJailLighting(JailLightingController.LightingState.Normal);
            EndLockdownAudio();
            try
            {
                Singleton<BlackOverlay>.Instance.Close(0.25f);
            }
            catch (Exception exception)
            {
                ModLogger.Warn($"[JAIL LIFECYCLE] Could not close schedule-lockdown blackout: {exception.Message}");
            }

            playerReturnInProgress = false;
            playerReturnCoroutine = null;
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
