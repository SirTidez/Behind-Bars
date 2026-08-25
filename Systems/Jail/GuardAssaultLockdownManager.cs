using System;
using System.Collections;
using System.Linq;
using Behind_Bars.Harmony;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.NPCs;
using MelonLoader;
using UnityEngine;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.PlayerScripts.Health;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.PlayerScripts;
using ScheduleOne.PlayerScripts.Health;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Owns the jail-only response to an assault on staff.  It intentionally keeps this
    /// transient state scene-local: the crime is persisted by CrimeDetectionSystem, but
    /// guards and the current pursuit target are never serialized.
    /// </summary>
    public sealed class GuardAssaultLockdownManager : MonoBehaviour
    {
        private const string DisciplinaryHoldingCellName = "HoldingCell_01";
        private const float PursuitUpdateSeconds = 0.35f;
        private const float SubdualDistance = 2.2f;
        private const float NonLethalSubdualDamage = 1f;
        private const float DisciplinaryHoldSeconds = 60f;

        private static GuardAssaultLockdownManager _instance;
        private bool lockdownActive;
        private Player incidentPlayer;
        private GuardBehavior initiatingGuard;
        private bool resumeInterruptedBooking;
        private Coroutine incidentCoroutine;

#if !MONO
        public GuardAssaultLockdownManager(IntPtr ptr) : base(ptr) { }
#endif

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            CancelForSceneExit();

            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// Starts a jail-only assault response. Returns false when this is not a valid local
        /// custody incident, allowing ordinary street-police handling to continue unchanged.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public static bool TryBeginJailStaffAssault(GuardBehavior guard, Player player, CrimeDetectionSystem crimeDetection)
        {
            if (guard == null || player == null || crimeDetection == null || Core.JailController == null)
            {
                return false;
            }

            if (player != Player.Local || !Core.ResolveJailTimeTracker().IsInJail(player))
            {
                return false;
            }

            var manager = GetOrCreate();
            if (manager == null)
            {
                ModLogger.Error("Cannot start guard-assault lockdown: manager could not be resolved");
                return false;
            }

            if (manager.lockdownActive)
            {
                ModLogger.Debug("Guard-assault lockdown is already active; ignored duplicate staff-damage callback");
                return true;
            }

            var nativeNpc = guard.GetNativeNpc();
            if (nativeNpc == null)
            {
                ModLogger.Error("Cannot start guard-assault lockdown: guard has no native NPC component");
                return false;
            }

            crimeDetection.ProcessOfficerAssault(
                nativeNpc,
                player,
                applyWantedLevel: false,
                persistToRapSheet: true);
            Core.Instance?.JailSystem?.RefreshCustodyChargeDisplay(player);
            manager.BeginLockdown(guard, player);
            return true;
        }

        private static GuardAssaultLockdownManager GetOrCreate()
        {
            if (_instance != null)
            {
                return _instance;
            }

            var jail = Core.JailController;
            if (jail == null)
            {
                return null;
            }

            _instance = BBHelpers.GetComponentSafe<GuardAssaultLockdownManager>(jail.gameObject);
            if (_instance == null)
            {
                _instance = BBHelpers.AddComponentSafe<GuardAssaultLockdownManager>(jail.gameObject);
            }

            return _instance;
        }

        private void BeginLockdown(GuardBehavior guard, Player player)
        {
            lockdownActive = true;
            initiatingGuard = guard;
            incidentPlayer = player;

            resumeInterruptedBooking = SuspendInterruptedIntake(player);

            Core.JailController.EmergencyLockdown();
            foreach (var registeredGuard in Core.Instance?.NpcManager?.GetRegisteredGuards() ?? Enumerable.Empty<GuardBehavior>())
            {
                if (registeredGuard != null)
                {
                    registeredGuard.EnterEmergencyLockdown(registeredGuard == guard);
                }
            }

            ModLogger.Warn($"[LOCKDOWN] {player.name} assaulted jail staff. Emergency response engaged; wanted level intentionally unchanged.");
            incidentCoroutine = MelonCoroutines.Start(ResolveIncident()) as Coroutine;
        }

        private static bool SuspendInterruptedIntake(Player player)
        {
            SecureInterruptedHoldingCellDoor(player);

            bool suspendedBooking = false;
            var booking = Core.ResolveBookingProcess();
            if (booking != null && booking.SuspendForDisciplinaryHold(player))
            {
                suspendedBooking = true;
                ModLogger.Info("[LOCKDOWN] Suspended active booking before disciplinary transfer");
            }

            var intakeGuard = Core.Instance?.NpcManager?.GetIntakeOfficer();
            var intakeStateMachine = intakeGuard == null
                ? null
                : BBHelpers.GetComponentSafe<IntakeOfficerStateMachine>(intakeGuard.gameObject);
            if (intakeStateMachine != null && intakeStateMachine.GetCurrentPrisoner() == player)
            {
                intakeStateMachine.CancelIntake();
                ModLogger.Info("[LOCKDOWN] Canceled the interrupted intake officer state before disciplinary transfer");
            }

            return suspendedBooking;
        }

        /// <summary>
        /// Intake can be interrupted just after the officer has opened the holding-cell
        /// door. Close and lock that exact occupied cell before clearing the booking
        /// state so the former intake cell cannot remain unsecured after transfer.
        /// </summary>
        private static void SecureInterruptedHoldingCellDoor(Player player)
        {
            var jail = Core.JailController;
            int holdingCellIndex = jail?.FindPlayerHoldingCell(player) ?? -1;
            if (holdingCellIndex < 0 || jail == null || holdingCellIndex >= jail.holdingCells.Count)
            {
                return;
            }

            var holdingCell = jail.holdingCells[holdingCellIndex];
            bool doorClosed = jail.doorController?.CloseHoldingCellDoor(holdingCellIndex) ?? false;
            holdingCell?.CloseCell();
            holdingCell?.LockCell(true);
            ModLogger.Info($"[LOCKDOWN] Secured interrupted holding cell {holdingCellIndex}: doorClosed={doorClosed}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ResolveIncident()
        {
            GuardBehavior responder = initiatingGuard;
            while (lockdownActive && incidentPlayer != null && responder != null)
            {
                if (Vector3.Distance(responder.transform.position, incidentPlayer.transform.position) <= SubdualDistance)
                {
                    responder.PerformLockdownSubdual();
                    ApplyNonLethalGuardStrike(incidentPlayer);
                    yield return new WaitForSecondsRealtime(0.35f);
                    yield return SecurePlayerAfterSubdual();
                    yield break;
                }

                responder.MoveTo(incidentPlayer.transform.position, 1.1f);
                yield return new WaitForSecondsRealtime(PursuitUpdateSeconds);

                // Re-select only if the initiating guard was destroyed while the incident ran.
                if (responder == null)
                {
                    responder = Core.Instance?.NpcManager?.GetRegisteredGuards()
                        .Where(guard => guard != null)
                        .OrderBy(guard => Vector3.Distance(guard.transform.position, incidentPlayer.transform.position))
                        .FirstOrDefault();
                }
            }

            EndLockdownWithoutTransfer("response guard or incident player became unavailable");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator SecurePlayerAfterSubdual()
        {
            SetSubdualControls(true);
            TryOpenBlackOverlay();
            yield return new WaitForSecondsRealtime(0.2f);

            bool secured;
            if (resumeInterruptedBooking)
            {
                // An unfinished booking always serves the disciplinary minute in the
                // punishment holding cell. The booking checkpoint determines whether the
                // officer resumes at scanner, storage, or the final cell escort afterward.
                secured = SecureInDisciplinaryHoldingCell(incidentPlayer);
            }
            else
            {
                var cellAssignments = Core.ResolveCellAssignmentManager();
                bool hasAssignedCell = cellAssignments != null && cellAssignments.GetPlayerCellNumber(incidentPlayer) >= 0;
                secured = hasAssignedCell
                    ? SecureInAssignedCell(incidentPlayer)
                    : SecureInDisciplinaryHoldingCell(incidentPlayer);
            }

            if (!secured)
            {
                ModLogger.Error("[LOCKDOWN] Could not secure player after staff assault; lockdown remains active for safety");
                yield break;
            }

            RestoreJailAfterSecure();
            yield return new WaitForSecondsRealtime(0.25f);
            TryCloseBlackOverlay();
            SetSubdualControls(false);

            if (resumeInterruptedBooking)
            {
                yield return new WaitForSecondsRealtime(DisciplinaryHoldSeconds);
                ResumeIntakeAfterDisciplinaryHold(incidentPlayer);
                resumeInterruptedBooking = false;
            }
        }

        private bool SecureInAssignedCell(Player player, bool applyTrackedPenalty = true)
        {
            var cellAssignments = Core.ResolveCellAssignmentManager();
            if (cellAssignments == null)
            {
                ModLogger.Error("[LOCKDOWN] CellAssignmentManager was unavailable while securing the player");
                return false;
            }

            int assignedCell = cellAssignments.GetPlayerCellNumber(player);
            var cell = Core.JailController?.GetCellByIndex(assignedCell);
            var destination = cellAssignments.GetCellSpawnPoints(assignedCell).FirstOrDefault(point => point != null)
                ?? cell?.cellBounds
                ?? cell?.cellTransform;
            if (destination == null || cell == null)
            {
                ModLogger.Error($"[LOCKDOWN] Assigned cell {assignedCell} had no usable secure destination");
                return false;
            }

            PlacePlayerInSecureCell(player, destination, $"assigned cell {assignedCell}");
            cell.CloseCell();
            cell.LockCell(true);

            if (applyTrackedPenalty)
            {
                float penalty = SentenceConfigManager.Instance.GetSentenceLength("AssaultOnOfficer");
                if (!Core.ResolveJailTimeTracker().AddPenaltyTime(player, penalty, "Assault on an LEO"))
                {
                    ModLogger.Warn("[LOCKDOWN] Active sentence was not yet tracking; the assault penalty will be applied by the booking path if required");
                }
            }

            ModLogger.Info($"[LOCKDOWN] Secured {player.name} in assigned cell {assignedCell} after guard subdual");
            return true;
        }

        private bool SecureInDisciplinaryHoldingCell(Player player)
        {
            var jail = Core.JailController;
            Transform authoredHoldingCell = jail?.transform.Find($"HoldingCells/{DisciplinaryHoldingCellName}");
            var holdingCell = jail?.GetHoldingCellByName(DisciplinaryHoldingCellName);
            if (authoredHoldingCell == null || holdingCell == null || holdingCell.cellTransform != authoredHoldingCell)
            {
                ModLogger.Error($"[LOCKDOWN] Reserved holding target did not resolve to the authored path HoldingCells/{DisciplinaryHoldingCellName}");
                return false;
            }

            // The prisoner was already reserved in their original intake cell. Clear that
            // reservation first so no residual occupancy or officer state can point at it.
            jail.ReleasePlayerFromHoldingCell(player);
            var destination = jail.AssignPlayerToHoldingCellByName(player, DisciplinaryHoldingCellName);
            if (destination == null || holdingCell == null)
            {
                ModLogger.Error($"[LOCKDOWN] Reserved disciplinary holding cell '{DisciplinaryHoldingCellName}' was unavailable");
                return false;
            }

            PlacePlayerInSecureCell(player, destination, DisciplinaryHoldingCellName);
            holdingCell.CloseCell();
            holdingCell.LockCell(true);
            Core.ResolveUIManager().ShowNotification(
                "Intake delayed for one real minute due to your assault on a correctional officer.",
                NotificationType.Warning);
            ModLogger.Info($"[LOCKDOWN] Secured {player.name} in authored {DisciplinaryHoldingCellName} at {destination.position}; disciplinary re-intake starts in one real minute");
            return true;
        }

        private void ResumeIntakeAfterDisciplinaryHold(Player player)
        {
            if (player == null)
            {
                return;
            }

            float penalty = SentenceConfigManager.Instance.GetSentenceLength("AssaultOnOfficer");
            var booking = Core.ResolveBookingProcess();
            if (booking == null || !booking.ResumeAfterDisciplinaryHold(player, penalty, DisciplinaryHoldingCellName))
            {
                ModLogger.Error("[LOCKDOWN] Disciplinary hold completed but the booking process could not resume its checkpoint");
                return;
            }

            Core.ResolveUIManager().ShowNotification("Disciplinary hold complete. Intake is resuming from your previous step.", NotificationType.Progress);
            ModLogger.Info($"[LOCKDOWN] Resumed intake for {player.name} after disciplinary hold; Assault on an LEO added {penalty:F0} game minutes");
        }

        /// <summary>
        /// Uses the authored marker for position only. Holding-cell markers are nested in
        /// rotated prefab geometry, so copying their complete rotation pitches the player's
        /// camera upward. Match the normal holding-cell spawn: upright and facing west.
        /// </summary>
        private static void PlacePlayerInSecureCell(Player player, Transform destination, string destinationName)
        {
            player.transform.SetPositionAndRotation(
                destination.position,
                Quaternion.LookRotation(Vector3.left, Vector3.up));
            ModLogger.Debug($"[LOCKDOWN] Placed {player.name} upright and facing west in {destinationName}");
        }

        private void RestoreJailAfterSecure()
        {
            RestoreNormalCustodyState();
            ModLogger.Info("[LOCKDOWN] Fully cleared after the player was secured; guards, shared routes, and lighting restored to normal custody state.");
        }

        private void EndLockdownWithoutTransfer(string reason)
        {
            RestoreNormalCustodyState();
            ModLogger.Error($"[LOCKDOWN] Emergency response ended without securing the player; guards, shared routes, and lighting were restored: {reason}");
        }

        /// <summary>
        /// Cancels a scene-local incident without allowing its pursuit, disciplinary delay,
        /// or stale guard state to leak into a later Main-scene session.
        /// </summary>
        public void CancelForSceneExit()
        {
            if (incidentCoroutine != null)
            {
                MelonCoroutines.Stop(incidentCoroutine);
                incidentCoroutine = null;
            }

            SetSubdualControls(false);
            TryCloseBlackOverlay();
            RestoreNormalCustodyState();
            incidentPlayer = null;
            initiatingGuard = null;
            resumeInterruptedBooking = false;
        }

        private void RestoreNormalCustodyState()
        {
            foreach (var registeredGuard in Core.Instance?.NpcManager?.GetRegisteredGuards() ?? Enumerable.Empty<GuardBehavior>())
            {
                registeredGuard?.ExitEmergencyLockdown();
            }

            Core.JailController?.doorController?.ClearEmergencyRouteLockdown();
            Core.JailController?.SetJailLighting(JailLightingController.LightingState.Normal);
            lockdownActive = false;
            incidentCoroutine = null;
        }

        private static void SetSubdualControls(bool locked)
        {
            HarmonyPatches.SetGuardLockdownInputLocked(locked);
            try
            {
                PlayerSingleton<PlayerMovement>.Instance.CanMove = !locked;
                PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(!locked);
                PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(!locked);
                PlayerSingleton<PlayerCamera>.Instance.SetCanLook(!locked);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LOCKDOWN] Could not fully apply subdual controls: {ex.Message}");
            }
        }

        private static void ApplyNonLethalGuardStrike(Player player)
        {
            try
            {
                var health = player.GetComponent<PlayerHealth>();
                if (health == null || !health.CanTakeDamage)
                {
                    ModLogger.Warn("[LOCKDOWN] Guard reached the prisoner, but native PlayerHealth was unavailable for the non-lethal strike");
                    return;
                }

                // This is intentionally one point of damage: the native damage path supplies
                // the hit reaction, while custody blackout replaces ordinary player death.
                health.TakeDamage(NonLethalSubdualDamage, false, false);
                ModLogger.Info("[LOCKDOWN] Applied native non-lethal guard strike before custody blackout");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LOCKDOWN] Native non-lethal guard strike failed: {ex.Message}");
            }
        }

        private static void TryOpenBlackOverlay()
        {
            try
            {
                Singleton<BlackOverlay>.Instance.Open(0.15f);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LOCKDOWN] Could not open blackout overlay: {ex.Message}");
            }
        }

        private static void TryCloseBlackOverlay()
        {
            try
            {
                Singleton<BlackOverlay>.Instance.Close(0.25f);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LOCKDOWN] Could not close blackout overlay: {ex.Message}");
            }
        }
    }
}
