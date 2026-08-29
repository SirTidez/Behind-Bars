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
        private enum RecreationTier
        {
            Unknown = -1,
            None = 0,
            Lower = 1,
            Upper = 2
        }

        private const int RecreationStartHour = 7;
        private const int BedtimeHour = 22;
        // Inmates can be placed on the far side of either dayroom tier. Give
        // them enough of the two-hour block to complete a real NavMesh return
        // before their doors are secured.
        private const int WarningMinutesBeforeClose = 30;
        private const float LocalAudioMaxDistance = 55f;
        private const float InmateReturnGraceSeconds = 10f;

        private RecreationTier activeTier = RecreationTier.Unknown;
        private int lastWarningScheduleMinute = -1;
        private int lastObservedNativeMinute = -1;
        private bool loggedNativeTimeFallback;
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
                RecreationTier initialTier = GetScheduledTier(currentMinute / 60);
                ModLogger.Info($"[JAIL LIFECYCLE] Native Schedule I clock resolved to {currentMinute / 60:00}:{currentMinute % 60:00}; initial recreation state is {initialTier}");
            }
            ApplySchedule(currentMinute, force);

            if (activeTier == RecreationTier.None || activeTier == RecreationTier.Unknown)
            {
                return;
            }

            int hour = currentMinute / 60;
            int endScheduleMinute = GetActiveBlockEndMinute(hour);
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void ApplySchedule(int currentMinute, bool force)
        {
            if (Core.JailController == null)
            {
                return;
            }

            int hour = currentMinute / 60;
            RecreationTier desiredTier = GetScheduledTier(hour);
            if (!force && desiredTier == activeTier)
            {
                return;
            }

            RecreationTier previousTier = activeTier;
            activeTier = desiredTier;
            lastWarningScheduleMinute = -1;

            if (returnGraceCoroutine != null)
            {
                MelonCoroutines.Stop(returnGraceCoroutine);
                returnGraceCoroutine = null;
            }

            List<InmateBehavior> inmates = GetActiveInmateBehaviors();
            if (previousTier == RecreationTier.Lower || previousTier == RecreationTier.Upper)
            {
                // Reissue at the transition as a recovery guard for an inmate
                // that spawned after the warning or briefly lost its route.
                CommandTierReturn(previousTier, inmates);
                returnGraceCoroutine = MelonCoroutines.Start(SecureTierAfterReturn(previousTier)) as Coroutine;
            }

            if (desiredTier == RecreationTier.None)
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
        private static RecreationTier GetScheduledTier(int hour)
        {
            if (hour < RecreationStartHour || hour >= BedtimeHour)
            {
                return RecreationTier.None;
            }

            // Lower and upper blocks alternate in two-hour windows.  The final
            // 21:00-22:00 upper-tier window is intentionally shortened by bedtime.
            return ((hour - RecreationStartHour) / 2) % 2 == 0
                ? RecreationTier.Lower
                : RecreationTier.Upper;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static int GetActiveBlockEndMinute(int currentHour)
        {
            int blockStartHour = RecreationStartHour + (((currentHour - RecreationStartHour) / 2) * 2);
            return Math.Min(BedtimeHour, blockStartHour + 2) * 60;
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
        private void ApplyCurrentTierToInmates()
        {
            if (activeTier == RecreationTier.Unknown)
            {
                return;
            }

            List<InmateBehavior> inmates = GetActiveInmateBehaviors();
            if (activeTier == RecreationTier.None)
            {
                CommandAllInmatesHome(inmates);
                return;
            }

            CommandScheduledRecreation(activeTier, inmates);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void OpenTierForRecreation(RecreationTier tier)
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
        private void CommandScheduledRecreation(RecreationTier tier, List<InmateBehavior> inmates)
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
        private void CommandTierReturn(RecreationTier tier, List<InmateBehavior> inmates)
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
        private IEnumerator SecureTierAfterReturn(RecreationTier tier)
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
            yield return SecureTierAfterReturn(RecreationTier.Lower);
            yield return SecureTierAfterReturn(RecreationTier.Upper);
            returnGraceCoroutine = null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SecureTierDoors(RecreationTier tier)
        {
            foreach (int cellIndex in GetCellIndicesForTier(tier))
            {
                Core.JailController?.doorController?.SecureJailCellDoor(cellIndex);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private List<int> GetCellIndicesForTier(RecreationTier tier)
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        private RecreationTier GetTierForCell(int cellIndex)
        {
            var cellManager = Core.JailController?.cellManager;
            var cells = cellManager?.cells;
            CellDetail targetCell = cellManager?.GetCellByIndex(cellIndex);
            if (cells == null || targetCell == null)
            {
                return RecreationTier.None;
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
                return RecreationTier.None;
            }

            float divider = (minY + maxY) * 0.5f;
            if (!TryGetCellTierHeight(targetCell, out float targetHeight))
            {
                return RecreationTier.None;
            }

            return targetHeight > divider
                ? RecreationTier.Upper
                : RecreationTier.Lower;
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
        private List<Transform> GetRecreationAnchors(RecreationTier tier)
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
                if ((tier == RecreationTier.Upper && upperName) ||
                    (tier == RecreationTier.Lower && !upperName))
                {
                    anchors.Add(anchor);
                }
            }

            return anchors;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
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
        private void EnforcePlayerAtTransition(RecreationTier tier)
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

            RecreationTier playerTier = GetTierForCell(cellIndex);
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
            return activeTier == RecreationTier.Lower || activeTier == RecreationTier.Upper
                ? GetTierForCell(cellIndex) == activeTier
                : false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
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
