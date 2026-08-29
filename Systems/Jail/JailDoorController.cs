using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BehindBars.Areas;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
#endif

namespace Behind_Bars.Systems.Jail
{
#if MONO
    public sealed class JailDoorController : MonoBehaviour
#else
    public sealed class JailDoorController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Door System")]
#endif
        public GameObject jailDoorPrefab;
        public GameObject steelDoorPrefab;

#if MONO
        [Header("Control Keys")]
#endif
        public KeyCode modifierKey = KeyCode.LeftAlt;

#if MONO
        [Header("Test Door References")]
#endif
        public Transform holdingCellDoor0;
        public Transform holdingCellDoor1;

        // References to other systems
        private List<CellDetail> cells = new List<CellDetail>();
        private List<CellDetail> holdingCells = new List<CellDetail>();
        private BookingArea booking = new BookingArea();
        private JailController jailController;
        private AudioSource doorAudioSource;
        private AudioClip cellDoorCloseClip;
        private AudioClip securityDoorUnlockClip;
        private float nextCellDoorCloseCueTime;

        void Update()
        {
            UpdateDoorAnimations();
            HandleDoorKeyboardShortcuts();
        }

        public void Initialize(List<CellDetail> prisonCells, List<CellDetail> holdingCells, BookingArea bookingArea, JailController controller = null, bool setupDoorsImmediately = true)
        {
            this.cells = prisonCells;
            this.holdingCells = holdingCells;
            this.booking = bookingArea;
            this.jailController = controller;

            if (setupDoorsImmediately)
            {
                SetupDoors();
            }
            InitializeHoldingCellDoorReferences();
            EnsureDoorAudio();
        }

        void HandleDoorKeyboardShortcuts()
        {
            if (!Core.EnableDeveloperShortcuts)
            {
                return;
            }

            if (Input.GetKey(modifierKey) && Input.GetKeyDown(KeyCode.Alpha1))
            {
                ToggleBookingDoor(booking.prisonEntryDoor, "Prison Enter Door");
            }

            if (Input.GetKey(modifierKey) && Input.GetKeyDown(KeyCode.Alpha2))
            {
                ToggleBookingDoor(booking.bookingInnerDoor, "Booking Inner Door");
            }

            if (Input.GetKey(modifierKey) && Input.GetKeyDown(KeyCode.Alpha3))
            {
                ToggleBookingDoor(booking.guardDoor, "Guard Door");
            }

            if (Input.GetKey(modifierKey) && Input.GetKeyDown(KeyCode.Alpha4))
            {
                ToggleDoor(holdingCellDoor0, "Holding Cell Door 0");
            }

            if (Input.GetKey(modifierKey) && Input.GetKeyDown(KeyCode.Alpha5))
            {
                ToggleDoor(holdingCellDoor1, "Holding Cell Door 1");
            }
        }

        void ToggleBookingDoor(JailDoor jailDoor, string doorName)
        {
            if (jailDoor == null)
            {
                ModLogger.Warn($"{doorName} not found in booking area - cannot toggle");
                return;
            }

            if (!jailDoor.IsInstantiated())
            {
                ModLogger.Warn($"{doorName} not instantiated - cannot toggle");
                return;
            }

            if (jailDoor.IsOpen())
            {
                jailDoor.CloseDoor();
                ModLogger.Info($"Closed {doorName}");
            }
            else
            {
                jailDoor.OpenDoor();
                ModLogger.Info($"Opened {doorName}");
            }
        }

        void ToggleDoor(Transform doorTransform, string doorName)
        {
            if (doorTransform == null)
            {
                ModLogger.Warn($"{doorName} not assigned - cannot toggle");
                return;
            }

            JailDoor jailDoor = FindJailDoor(doorTransform);
            if (jailDoor != null)
            {
                if (jailDoor.IsOpen())
                {
                    jailDoor.CloseDoor();
                    ModLogger.Info($"Closed {doorName} via JailDoor");
                }
                else
                {
                    jailDoor.OpenDoor();
                    ModLogger.Info($"Opened {doorName} via JailDoor");
                }
                return;
            }

            ModLogger.Warn($"{doorName} not found in door collections - logging position for manual assignment");
            ModLogger.Info($"{doorName} located at: {doorTransform.position} (Path: {GetGameObjectPath(doorTransform)})");
        }

        JailDoor FindJailDoor(Transform doorTransform)
        {
            foreach (var cell in cells)
            {
                if (cell.cellDoor?.doorHolder == doorTransform ||
                    cell.cellDoor?.doorInstance?.transform == doorTransform)
                {
                    return cell.cellDoor;
                }
            }

            foreach (var holdingCell in holdingCells)
            {
                if (holdingCell.cellDoor?.doorHolder == doorTransform ||
                    holdingCell.cellDoor?.doorInstance?.transform == doorTransform)
                {
                    return holdingCell.cellDoor;
                }
            }

            var allAreaDoors = new List<JailDoor>();
            if (booking.doors != null) allAreaDoors.AddRange(booking.doors);

            foreach (var door in allAreaDoors)
            {
                if (door?.doorHolder == doorTransform ||
                    door?.doorInstance?.transform == doorTransform)
                {
                    return door;
                }
            }

            return null;
        }

        string GetGameObjectPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        void InitializeHoldingCellDoorReferences()
        {
            Transform holdingCellsParent = transform.Find("HoldingCells");
            if (holdingCellsParent != null)
            {
                Transform holdingCell0 = holdingCellsParent.Find("HoldingCell_00");
                if (holdingCell0 != null)
                    holdingCellDoor0 = holdingCell0.Find("HoldingDoorHolder[0]");

                Transform holdingCell1 = holdingCellsParent.Find("HoldingCell_01");
                if (holdingCell1 != null)
                    holdingCellDoor1 = holdingCell1.Find("HoldingDoorHolder[0]");
            }

            ModLogger.Debug($"Holding Cell Door 0: {(holdingCellDoor0 != null ? "✓ Found" : "✗ Missing")}");
            ModLogger.Debug($"Holding Cell Door 1: {(holdingCellDoor1 != null ? "✓ Found" : "✗ Missing")}");
        }

        void UpdateDoorAnimations()
        {
            float deltaTime = Time.deltaTime;

            foreach (var cell in cells)
            {
                if (cell.cellDoor.IsInstantiated())
                {
                    cell.cellDoor.UpdateDoorAnimation(deltaTime);
                }
            }

            foreach (var holdingCell in holdingCells)
            {
                if (holdingCell.cellDoor.IsInstantiated())
                {
                    holdingCell.cellDoor.UpdateDoorAnimation(deltaTime);
                }
            }

            if (booking.prisonEntryDoor != null && booking.prisonEntryDoor.IsInstantiated())
            {
                booking.prisonEntryDoor.UpdateDoorAnimation(deltaTime);
            }

            if (booking.guardDoor != null && booking.guardDoor.IsInstantiated())
            {
                booking.guardDoor.UpdateDoorAnimation(deltaTime);
            }

            foreach (var door in booking.doors)
            {
                if (door.IsInstantiated())
                {
                    door.UpdateDoorAnimation(deltaTime);
                }
            }

            // Update exit door animation (MISSING!)
            if (jailController?.exitScanner?.exitDoor != null && jailController.exitScanner.exitDoor.IsInstantiated())
            {
                jailController.exitScanner.exitDoor.UpdateDoorAnimation(deltaTime);
            }
        }

        public void SetupDoors()
        {
            SetupCellDoors();
            SetupHoldingCellDoors();
            SetupAreaDoors();
        }

        void SetupCellDoors()
        {
            foreach (var cell in cells)
            {
                if (cell.cellDoor.doorHolder != null)
                {
                    InstantiateDoor(cell.cellDoor, jailDoorPrefab);
                }
            }
        }

        void SetupHoldingCellDoors()
        {
            foreach (var holdingCell in holdingCells)
            {
                if (holdingCell.cellDoor.doorHolder != null)
                {
                    InstantiateDoor(holdingCell.cellDoor, jailDoorPrefab);
                }
            }
        }

        void SetupAreaDoors()
        {
            // Use BookingArea's own InstantiateDoors method which correctly uses steelDoorPrefab
            if (booking.isInitialized)
            {
                booking.InstantiateDoors(steelDoorPrefab ?? jailDoorPrefab);
                ModLogger.Debug("✓ Setup booking area doors using steelDoorPrefab");
            }
            else
            {
                ModLogger.Warn("Booking area not initialized - cannot setup doors");
            }

            // MISSING: Setup ExitScannerArea doors the same way!
            if (jailController?.exitScanner?.isInitialized == true)
            {
                jailController.exitScanner.InstantiateDoors(steelDoorPrefab ?? jailDoorPrefab);
                ModLogger.Debug("✓ Setup exit scanner area doors using steelDoorPrefab");
            }
            else
            {
                ModLogger.Warn("Exit scanner area not initialized - cannot setup doors");
            }
        }

        void InstantiateDoor(JailDoor door, GameObject doorPrefab)
        {
            if (door.doorInstance != null)
            {
                ModLogger.Debug($"Door {door.doorName} already instantiated");
                return;
            }

            if (doorPrefab == null)
            {
                ModLogger.Error($"No door prefab available for {door.doorName}");
                return;
            }

            door.doorInstance = Instantiate(doorPrefab, door.doorHolder);
            door.doorInstance.name = door.doorName;

            door.doorHinge = FindDoorHinge(door.doorInstance);

            if (door.doorHinge == null)
            {
                ModLogger.Warn($"No door hinge found for {door.doorName} - door will not animate");
            }

            ModLogger.Debug($"✓ Instantiated door: {door.doorName}");
        }

        Transform FindDoorHinge(GameObject doorInstance)
        {
            Transform[] children = doorInstance.GetComponentsInChildren<Transform>();
            foreach (Transform child in children)
            {
                if (child.name.ToLower().Contains("hinge") ||
                    child.name.ToLower().Contains("pivot"))
                {
                    return child;
                }
            }

            if (doorInstance.transform.childCount > 0)
            {
                return doorInstance.transform.GetChild(0);
            }

            return doorInstance.transform;
        }

        public void EmergencyLockdown()
        {
            booking.LockAllDoors();

            foreach (var cell in cells)
            {
                bool wasOpen = cell?.cellDoor != null && !cell.cellDoor.IsClosed();
                cell.LockCell(true);
                if (wasOpen)
                {
                    PlayCellDoorCloseCue(cell.cellDoor, "jail cell during lockdown");
                }
            }

            foreach (var holdingCell in holdingCells)
            {
                bool wasOpen = holdingCell?.cellDoor != null && !holdingCell.cellDoor.IsClosed();
                holdingCell.LockCell(true);
                if (wasOpen)
                {
                    PlayCellDoorCloseCue(holdingCell.cellDoor, "holding cell during lockdown");
                }
            }

            // Activate emergency lighting if we have access to the jail controller
            if (jailController != null)
            {
                jailController.SetJailLighting(JailLightingController.LightingState.Emergency);
                ModLogger.Info("🔒 EMERGENCY LOCKDOWN ACTIVATED! All doors locked and emergency lighting enabled.");
            }
            else
            {
                ModLogger.Info("🔒 EMERGENCY LOCKDOWN ACTIVATED! All doors locked.");
            }
        }

        public void UnlockAll()
        {
            booking.UnlockAllDoors();

            foreach (var cell in cells)
            {
                cell.LockCell(false);
            }

            foreach (var holdingCell in holdingCells)
            {
                holdingCell.LockCell(false);
            }

            ModLogger.Info("🔓 All doors unlocked!");
        }

        /// <summary>
        /// Clears only the locks introduced by the emergency response on shared booking and
        /// release routes. Cell and holding-cell locks are normal custody state and must not
        /// be opened merely because the emergency has ended.
        /// </summary>
        public void ClearEmergencyRouteLockdown()
        {
            booking.UnlockAllDoors();
            ModLogger.Info("[LOCKDOWN] Cleared emergency locks from shared booking and release routes; custody cells remain secured.");
        }

        public void OpenAllCells()
        {
            foreach (var cell in cells)
            {
                if (!cell.cellDoor.isLocked)
                    cell.OpenCell();
            }

            ModLogger.Info("Opened all unlocked cells");
        }

        public void CloseAllCells()
        {
            foreach (var cell in cells)
            {
                if (cell?.cellDoor != null && !cell.cellDoor.IsClosed())
                {
                    CloseJailCellDoor(cell.cellIndex);
                }
            }

            ModLogger.Info("Closed all cells");
        }

        // Programmatic door opening methods for IntakeOfficer escort system
        public bool UnlockAndOpenHoldingCellDoor(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= holdingCells.Count)
            {
                ModLogger.Error($"Invalid holding cell index: {cellIndex}");
                return false;
            }

            var holdingCell = holdingCells[cellIndex];
            if (holdingCell?.cellDoor != null && holdingCell.cellDoor.IsInstantiated())
            {
                // First unlock the door
                bool wasLocked = holdingCell.cellDoor.isLocked;
                holdingCell.LockCell(false);
                if (wasLocked)
                {
                    PlayGuardUnlockCue(holdingCell.cellDoor, $"holding cell {cellIndex}");
                }
                ModLogger.Info($"Unlocked holding cell {cellIndex} door");

                // Then open it
                holdingCell.cellDoor.OpenDoor();
                ModLogger.Info($"Opened holding cell {cellIndex} door");
                return true;
            }

            ModLogger.Error($"Could not unlock and open holding cell {cellIndex} door - not instantiated");
            return false;
        }

        public bool OpenHoldingCellDoor(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= holdingCells.Count)
            {
                ModLogger.Error($"Invalid holding cell index: {cellIndex}");
                return false;
            }

            var holdingCell = holdingCells[cellIndex];
            if (holdingCell?.cellDoor != null && holdingCell.cellDoor.IsInstantiated())
            {
                holdingCell.cellDoor.OpenDoor();
                ModLogger.Info($"Opened holding cell {cellIndex} door");
                return true;
            }

            ModLogger.Error($"Could not open holding cell {cellIndex} door - not instantiated");
            return false;
        }

        public bool CloseHoldingCellDoor(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= holdingCells.Count)
            {
                ModLogger.Error($"Invalid holding cell index: {cellIndex}");
                return false;
            }

            var holdingCell = holdingCells[cellIndex];
            if (holdingCell?.cellDoor != null && holdingCell.cellDoor.IsInstantiated())
            {
                bool wasOpen = !holdingCell.cellDoor.IsClosed();
                holdingCell.cellDoor.CloseDoor();
                if (wasOpen)
                {
                    PlayCellDoorCloseCue(holdingCell.cellDoor, $"holding cell {cellIndex}");
                }
                ModLogger.Info($"Closed holding cell {cellIndex} door");
                return true;
            }

            ModLogger.Error($"Could not close holding cell {cellIndex} door - not instantiated");
            return false;
        }

        public bool UnlockAndOpenBookingInnerDoor()
        {
            if (booking.bookingInnerDoor != null && booking.bookingInnerDoor.IsInstantiated())
            {
                // Unlock then open
                bool wasLocked = booking.bookingInnerDoor.isLocked;
                booking.UnlockAllDoors();
                if (wasLocked)
                {
                    PlayGuardUnlockCue(booking.bookingInnerDoor, "booking inner door");
                }
                booking.bookingInnerDoor.OpenDoor();
                ModLogger.Info("Unlocked and opened booking inner door");
                return true;
            }

            ModLogger.Error("Could not unlock and open booking inner door - not instantiated");
            return false;
        }

        public bool OpenBookingInnerDoor()
        {
            if (booking.bookingInnerDoor != null && booking.bookingInnerDoor.IsInstantiated())
            {
                booking.bookingInnerDoor.OpenDoor();
                ModLogger.Info("Opened booking inner door");
                return true;
            }

            ModLogger.Error("Could not open booking inner door - not instantiated");
            return false;
        }

        public bool CloseBookingInnerDoor()
        {
            if (booking.bookingInnerDoor != null && booking.bookingInnerDoor.IsInstantiated())
            {
                booking.bookingInnerDoor.CloseDoor();
                ModLogger.Info("Closed booking inner door");
                return true;
            }

            ModLogger.Error("Could not close booking inner door - not instantiated");
            return false;
        }

        public bool OpenPrisonEntryDoor()
        {
            if (booking.prisonEntryDoor != null && booking.prisonEntryDoor.IsInstantiated())
            {
                // A completed emergency lockdown intentionally leaves the secured cell
                // locked, but the release route must restore this shared transit door.
                bool wasLocked = booking.prisonEntryDoor.isLocked;
                booking.prisonEntryDoor.UnlockDoor();
                if (wasLocked)
                {
                    PlayGuardUnlockCue(booking.prisonEntryDoor, "prison entry door");
                }
                booking.prisonEntryDoor.OpenDoor();
                ModLogger.Info("Opened prison entry door");
                return true;
            }

            ModLogger.Error("Could not open prison entry door - not instantiated");
            return false;
        }

        public bool ClosePrisonEntryDoor()
        {
            if (booking.prisonEntryDoor != null && booking.prisonEntryDoor.IsInstantiated())
            {
                booking.prisonEntryDoor.CloseDoor();
                ModLogger.Info("Closed prison entry door");
                return true;
            }

            ModLogger.Error("Could not close prison entry door - not instantiated");
            return false;
        }

        public bool OpenJailCellDoor(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= cells.Count)
            {
                ModLogger.Error($"Invalid jail cell index: {cellIndex}");
                return false;
            }

            var cell = cells[cellIndex];
            if (cell?.cellDoor != null && cell.cellDoor.IsInstantiated())
            {
                // First unlock the door
                bool wasLocked = cell.cellDoor.isLocked;
                cell.LockCell(false);
                if (wasLocked)
                {
                    PlayGuardUnlockCue(cell.cellDoor, $"jail cell {cellIndex}");
                }
                ModLogger.Info($"Unlocked jail cell {cellIndex} door");

                // Then open it
                cell.cellDoor.OpenDoor();
                ModLogger.Info($"Opened jail cell {cellIndex} door");
                return true;
            }

            ModLogger.Error($"Could not open jail cell {cellIndex} door - not instantiated");
            return false;
        }

        public bool CloseJailCellDoor(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= cells.Count)
            {
                ModLogger.Error($"Invalid jail cell index: {cellIndex}");
                return false;
            }

            var cell = cells[cellIndex];
            if (cell?.cellDoor != null && cell.cellDoor.IsInstantiated())
            {
                bool wasOpen = !cell.cellDoor.IsClosed();
                cell.cellDoor.CloseDoor();
                if (wasOpen)
                {
                    PlayCellDoorCloseCue(cell.cellDoor, $"jail cell {cellIndex}");
                }
                ModLogger.Info($"Closed jail cell {cellIndex} door");
                return true;
            }

            ModLogger.Error($"Could not close jail cell {cellIndex} door - not instantiated");
            return false;
        }

        /// <summary>
        /// Closes and locks an individual cell without changing any intake or
        /// release-route doors.  The daily recreation controller uses this to
        /// secure only the tier that is off recreation.
        /// </summary>
        public bool SecureJailCellDoor(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= cells.Count)
            {
                ModLogger.Error($"Invalid jail cell index: {cellIndex}");
                return false;
            }

            var cell = cells[cellIndex];
            if (cell?.cellDoor != null && cell.cellDoor.IsInstantiated())
            {
                bool wasOpen = !cell.cellDoor.IsClosed();
                cell.cellDoor.CloseDoor();
                if (wasOpen)
                {
                    PlayCellDoorCloseCue(cell.cellDoor, $"jail cell {cellIndex}");
                }
                cell.LockCell(true);
                ModLogger.Info($"Secured jail cell {cellIndex} door");
                return true;
            }

            ModLogger.Error($"Could not secure jail cell {cellIndex} door - not instantiated");
            return false;
        }

        public bool OpenExitDoor()
        {
            if (jailController?.exitScanner?.exitDoor != null && jailController.exitScanner.exitDoor.IsInstantiated())
            {
                bool wasLocked = jailController.exitScanner.exitDoor.isLocked;
                jailController.exitScanner.exitDoor.UnlockDoor();
                if (wasLocked)
                {
                    PlayGuardUnlockCue(jailController.exitScanner.exitDoor, "exit door");
                }
                jailController.exitScanner.exitDoor.OpenDoor();
                ModLogger.Info("Opened exit door via JailDoorController");
                return true;
            }

            ModLogger.Error("Could not open exit door - not instantiated or not found");
            return false;
        }

        public bool CloseExitDoor()
        {
            if (jailController?.exitScanner?.exitDoor != null && jailController.exitScanner.exitDoor.IsInstantiated())
            {
                jailController.exitScanner.exitDoor.CloseDoor();
                jailController.exitScanner.exitDoor.LockDoor();
                ModLogger.Info("Closed and locked exit door via JailDoorController");
                return true;
            }

            ModLogger.Error("Could not close exit door - not instantiated or not found");
            return false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void EnsureDoorAudio()
        {
            if (doorAudioSource == null)
            {
                doorAudioSource = gameObject.GetComponent<AudioSource>();
                if (doorAudioSource == null)
                {
                    doorAudioSource = gameObject.AddComponent<AudioSource>();
                }

                doorAudioSource.spatialBlend = 0f;
                doorAudioSource.volume = 0.82f;
                doorAudioSource.playOnAwake = false;
            }

            const string bundleName = "Behind_Bars.behind_bars_jail_lifecycle_audio";
            cellDoorCloseClip ??= AssetBundleUtils.LoadAudioClipFromBundle(
                bundleName, "assets/behindbars/sounds/jail_cell_door_close.wav");
            securityDoorUnlockClip ??= AssetBundleUtils.LoadAudioClipFromBundle(
                bundleName, "assets/behindbars/sounds/jail_security_door_unlock.wav");

            ModLogger.Info(
                $"[JAIL DOORS] Audio ready: cellClose={cellDoorCloseClip != null}, " +
                $"guardUnlock={securityDoorUnlockClip != null}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void PlayGuardUnlockCue(JailDoor door, string doorDescription)
        {
            PlayDoorCue(securityDoorUnlockClip, $"unlocking {doorDescription}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void PlayCellDoorCloseCue(JailDoor door, string doorDescription)
        {
            // A lifecycle transition can secure dozens of cells in one frame.
            // Preserve the audible cell-door feedback without stacking an
            // indistinguishable wall of one-shots at the listener.
            if (Time.unscaledTime < nextCellDoorCloseCueTime)
            {
                return;
            }

            nextCellDoorCloseCueTime = Time.unscaledTime + 0.12f;
            PlayDoorCue(cellDoorCloseClip, $"closing {doorDescription}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void PlayDoorCue(AudioClip clip, string purpose)
        {
            EnsureDoorAudio();
            if (doorAudioSource == null || clip == null)
            {
                ModLogger.Warn($"[JAIL DOORS] Audio clip unavailable for {purpose}");
                return;
            }

            Camera listenerCamera = Camera.main;
            if (listenerCamera != null &&
                (listenerCamera.transform.position - transform.position).sqrMagnitude > 6400f)
            {
                ModLogger.Debug($"[JAIL DOORS] Suppressed {purpose} cue because the player is outside the jail audio range");
                return;
            }

            doorAudioSource.PlayOneShot(clip);
        }
    }
}
