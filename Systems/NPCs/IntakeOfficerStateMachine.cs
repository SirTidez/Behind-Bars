using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Dedicated state machine for intake officer behavior during prisoner processing
    /// Integrates with existing SecurityDoorBehavior, BookingProcess, and guard systems
    /// </summary>
    public class IntakeOfficerStateMachine : BaseJailNPC
    {
#if !MONO
        public IntakeOfficerStateMachine(System.IntPtr ptr) : base(ptr) { }
#endif

        #region State Machine Definition

        public enum IntakeState
        {
            Idle,                    // At Booking/GuardPoint[0]
            WaitingForBooking,       // Monitoring for booking event
            DelayBeforeFetch,        // 5-10 second random delay
            EscortToHolding,         // Walk to holding cell
            OpeningHoldingDoor,      // Open holding cell door
            WaitingForPlayerExit,    // Check holding cell bounds
            ClosingHoldingDoor,      // Close holding cell door
            EscortToMugshot,         // Navigate to mugshot station
            WaitingForMugshot,       // BookingProcess.mugshotComplete
            EscortToScanner,         // Navigate to scanner station
            WaitingForScan,          // BookingProcess.fingerprintComplete
            EscortToStorage,         // Navigate to storage area
            WaitingForStorage,       // BookingProcess.inventoryDropOffComplete
            EscortToCell,            // Navigate to assigned cell
            OpeningCellDoor,         // Open jail cell door
            WaitingForCellEntry,     // Check cell bounds
            ClosingCellDoor,         // Close jail cell door
            ReturningToPost          // Back to guard point
        }

        [System.Serializable]
        public class IntakeStation
        {
            public string stationName;
            public string doorPointName;
            public string guardMessage;
            public float messageDuration = 3f;
            public System.Func<bool> completionCheck;
        }

        #endregion

        #region Component References

        private GuardBehavior guardBehavior;
        private BookingProcess bookingProcess;

        #endregion

        #region State Variables

        [SerializeField] private new IntakeState currentState = IntakeState.Idle;
        private Player currentPrisoner;
        private Transform guardPostTransform;
        private int assignedCellNumber = -1;
        private int currentHoldingCellIndex = -1;  // Which holding cell contains the current prisoner

        // State tracking to prevent spam
        private bool playerExitDetected = false;
        private bool doorCloseInitiated = false;

        // Timing variables
        private new float stateStartTime;
        private float delayDuration;

        // Station definitions
        private Dictionary<string, IntakeStation> intakeStations;
        private string currentTargetStation = "";

        #endregion

        #region Events

        public new System.Action<IntakeState> OnStateChanged;
        public System.Action<Player> OnIntakeStarted;
        public System.Action<Player> OnIntakeCompleted;
        public System.Action<string> OnStationReached;

        #endregion

        #region Initialization

        protected override void Awake()
        {
            base.Awake(); // Initialize BaseJailNPC
            guardBehavior = GetComponent<GuardBehavior>();
            // SecurityDoor will be retrieved from JailController when needed
        }

        protected override void Start()
        {
            // Save current intake state before base initialization
            var savedState = currentState;

            base.Start(); // Initialize BaseJailNPC

            // Restore intake state after base initialization
            currentState = savedState;
            ModLogger.Info($"IntakeOfficer: Restored state to {currentState} after base initialization");

            InitializeStations();
            FindGuardPost();
            SubscribeToEvents();

            ModLogger.Info($"IntakeOfficerStateMachine initialized for {gameObject.name}");
        }

        protected override void InitializeNPC()
        {
            // Ensure SecurityDoorBehavior component is attached
            EnsureSecurityDoorComponent();

            // IntakeOfficer-specific initialization
            ChangeIntakeState(IntakeState.Idle);
        }

        private void EnsureSecurityDoorComponent()
        {
            // Check if SecurityDoorBehavior is already attached
            var existingSecurityDoor = GetComponent<SecurityDoorBehavior>();
            if (existingSecurityDoor == null)
            {
                // Add SecurityDoorBehavior component to this IntakeOfficer
                var securityDoor = gameObject.AddComponent<SecurityDoorBehavior>();
                ModLogger.Info("IntakeOfficer: Added SecurityDoorBehavior component for automated door operations");
            }
            else
            {
                ModLogger.Info("IntakeOfficer: SecurityDoorBehavior component already attached");
            }
        }

        private void InitializeStations()
        {
            intakeStations = new Dictionary<string, IntakeStation>
            {
                ["HoldingCell"] = new IntakeStation
                {
                    stationName = "HoldingCell",
                    doorPointName = "HoldingCell",
                    guardMessage = "Time to process you.",
                    messageDuration = 3f
                },
                ["MugshotStation"] = new IntakeStation
                {
                    stationName = "MugshotStation",
                    doorPointName = "MugshotStation",
                    guardMessage = "Go take your mugshot!",
                    messageDuration = 3f
                },
                ["ScannerStation"] = new IntakeStation
                {
                    stationName = "ScannerStation",
                    doorPointName = "ScannerStation",
                    guardMessage = "Scan in.",
                    messageDuration = 2f
                },
                ["Storage"] = new IntakeStation
                {
                    stationName = "Storage",
                    doorPointName = "Storage",
                    guardMessage = "Drop your belongings and pick up prison items.",
                    messageDuration = 4f
                }
            };
        }

        private void FindGuardPost()
        {
            // Find the guard's assigned post (Booking/GuardPoint[0])
            var jailController = Core.JailController;
            if (jailController?.booking?.guardSpawns != null && jailController.booking.guardSpawns.Count > 0)
            {
                guardPostTransform = jailController.booking.guardSpawns[0];
                ModLogger.Info($"Found guard post at {guardPostTransform.position}");
            }
            else
            {
                ModLogger.Error("Could not find guard post for intake officer");
            }
        }

        private void SubscribeToEvents()
        {
            // Subscribe to booking process events
            if (BookingProcess.Instance != null)
            {
                bookingProcess = BookingProcess.Instance;
                bookingProcess.OnBookingStarted += HandleBookingStarted;
                bookingProcess.OnMugshotCompleted += HandleMugshotCompleted;
                bookingProcess.OnFingerprintCompleted += HandleFingerprintCompleted;
                bookingProcess.OnInventoryDropOffCompleted += HandleInventoryCompleted;
            }

            // Subscribe to SecurityDoor events
            var securityDoor = GetSecurityDoor();
            if (securityDoor != null)
            {
                securityDoor.OnDoorOperationComplete += HandleSecurityDoorOperationComplete;
                securityDoor.OnDoorOperationFailed += HandleSecurityDoorOperationFailed;
                ModLogger.Info("IntakeOfficer: Subscribed to SecurityDoor events");
            }
            else
            {
                ModLogger.Warn("IntakeOfficer: No SecurityDoor component found - will use fallback direct door control");
            }

            // Subscribe to movement completion events
            OnDestinationReached += HandleDestinationReached;
        }

        #endregion

        #region State Machine Core

        protected override void Update()
        {
            if (!isInitialized) return;

            // Allow base class to handle movement, but prevent it from interfering with our states
            base.Update();

            // Handle our own state machine
            UpdateStateMachine();

            // Check for door triggers during escort states
            if (IsEscortState(currentState) && currentPrisoner != null)
            {
                CheckForDoorTriggers();
            }
        }

        private void UpdateStateMachine()
        {
            switch (currentState)
            {
                case IntakeState.Idle:
                    HandleIdleState();
                    break;

                case IntakeState.DelayBeforeFetch:
                    HandleDelayState();
                    break;

                case IntakeState.EscortToHolding:
                case IntakeState.EscortToMugshot:
                case IntakeState.EscortToScanner:
                case IntakeState.EscortToStorage:
                case IntakeState.EscortToCell:
                case IntakeState.ReturningToPost:
                    HandleEscortState();
                    break;

                case IntakeState.OpeningHoldingDoor:
                    HandleOpeningHoldingDoorState();
                    break;

                case IntakeState.WaitingForPlayerExit:
                    HandleWaitingForPlayerExitState();
                    break;

                case IntakeState.ClosingHoldingDoor:
                    HandleClosingHoldingDoorState();
                    break;

                case IntakeState.WaitingForMugshot:
                    HandleWaitingForMugshotState();
                    break;

                case IntakeState.WaitingForScan:
                    HandleWaitingForScanState();
                    break;

                case IntakeState.WaitingForStorage:
                    HandleWaitingForStorageState();
                    break;

                case IntakeState.OpeningCellDoor:
                    HandleOpeningCellDoorState();
                    break;

                case IntakeState.WaitingForCellEntry:
                    HandleWaitingForCellEntryState();
                    break;

                case IntakeState.ClosingCellDoor:
                    HandleClosingCellDoorState();
                    break;
            }

        }

        private void ChangeIntakeState(IntakeState newState)
        {
            if (currentState == newState) return;

            IntakeState oldState = currentState;
            currentState = newState;
            stateStartTime = Time.time;

            OnStateChanged?.Invoke(newState);
            ModLogger.Info($"IntakeOfficer: {oldState} → {newState}");

            // Handle state entry logic
            OnStateEnter(newState);
        }

        private void OnStateEnter(IntakeState state)
        {
            switch (state)
            {
                case IntakeState.DelayBeforeFetch:
                    delayDuration = UnityEngine.Random.Range(5f, 10f);
                    ModLogger.Info($"IntakeOfficer: Waiting {delayDuration:F1} seconds before fetching prisoner");
                    break;

                case IntakeState.EscortToHolding:
                    NavigateToStation("HoldingCell");
                    break;

                case IntakeState.OpeningHoldingDoor:
                    OpenHoldingCellDoor();
                    break;

                case IntakeState.EscortToMugshot:
                    NavigateToStation("MugshotStation");
                    break;

                case IntakeState.EscortToScanner:
                    NavigateToStation("ScannerStation");
                    break;

                case IntakeState.EscortToStorage:
                    NavigateToStation("Storage");
                    break;

                case IntakeState.EscortToCell:
                    NavigateToAssignedCell();
                    break;

                case IntakeState.OpeningCellDoor:
                    OpenJailCellDoor();
                    break;

                case IntakeState.ReturningToPost:
                    ReturnToGuardPost();
                    break;
            }
        }

        #endregion

        #region State Handlers

        private new void HandleIdleState()
        {
            // Stay at guard post and monitor for booking events
            if (guardPostTransform != null)
            {
                float distanceToPost = Vector3.Distance(transform.position, guardPostTransform.position);
                if (distanceToPost > 2f)
                {
                    MoveTo(guardPostTransform.position);
                }
            }
        }

        private void HandleDelayState()
        {
            if (Time.time - stateStartTime >= delayDuration)
            {
                ModLogger.Info($"IntakeOfficer: Delay completed ({delayDuration:F1}s), transitioning to EscortToHolding");
                ChangeIntakeState(IntakeState.EscortToHolding);
            }
        }

        private void HandleWaitingForPlayerExitState()
        {
            if (currentPrisoner == null)
            {
                ChangeIntakeState(IntakeState.Idle);
                return;
            }

            // Only check once to prevent spam
            if (!playerExitDetected)
            {
                var jailController = Core.JailController;
                if (jailController != null && currentHoldingCellIndex >= 0)
                {
                    if (jailController.HasPlayerExitedHoldingCell(currentPrisoner, currentHoldingCellIndex))
                    {
                        playerExitDetected = true;
                        ModLogger.Info($"IntakeOfficer: Player has exited holding cell {currentHoldingCellIndex}");
                        // Add a 2-second delay before closing door to ensure player is fully clear
                        MelonCoroutines.Start(DelayedDoorClose());
                    }
                }
            }
        }

        private void HandleWaitingForMugshotState()
        {
            if (bookingProcess != null && bookingProcess.mugshotComplete)
            {
                ChangeIntakeState(IntakeState.EscortToScanner);
            }
        }

        private void HandleWaitingForScanState()
        {
            if (bookingProcess != null && bookingProcess.fingerprintComplete)
            {
                ChangeIntakeState(IntakeState.EscortToStorage);
            }
        }

        private void HandleWaitingForStorageState()
        {
            if (bookingProcess != null && bookingProcess.inventoryDropOffComplete)
            {
                // Assign cell before escorting
                AssignPrisonerCell();
                ChangeIntakeState(IntakeState.EscortToCell);
            }
        }

        private void HandleWaitingForCellEntryState()
        {
            if (currentPrisoner == null)
            {
                ChangeIntakeState(IntakeState.ReturningToPost);
                return;
            }

            // Check if player has entered assigned cell bounds using centralized method
            var jailController = Core.JailController;
            if (jailController != null && assignedCellNumber >= 0)
            {
                if (jailController.IsPlayerInJailCellBounds(currentPrisoner, assignedCellNumber))
                {
                    ModLogger.Info($"IntakeOfficer: Player has entered jail cell {assignedCellNumber}!");
                    ChangeIntakeState(IntakeState.ClosingCellDoor);
                }
            }
        }

        private void HandleOpeningHoldingDoorState()
        {
            // Use the stored holding cell index from when intake started
            if (currentHoldingCellIndex == -1)
            {
                ModLogger.Error("IntakeOfficer: No holding cell index stored");
                ChangeIntakeState(IntakeState.ReturningToPost);
                return;
            }

            var jailController = Core.JailController;
            if (jailController?.doorController != null)
            {
                bool doorOpened = jailController.doorController.UnlockAndOpenHoldingCellDoor(currentHoldingCellIndex);
                if (doorOpened)
                {
                    ModLogger.Info($"IntakeOfficer: Holding cell {currentHoldingCellIndex} door opened successfully");
                    SendGuardMessage("Come with me.", 3f);
                    ChangeIntakeState(IntakeState.WaitingForPlayerExit);
                }
                else
                {
                    ModLogger.Error($"IntakeOfficer: Failed to open holding cell {currentHoldingCellIndex} door");
                    ChangeIntakeState(IntakeState.ReturningToPost);
                }
            }
            else
            {
                ModLogger.Error("IntakeOfficer: No door controller available");
                ChangeIntakeState(IntakeState.WaitingForPlayerExit);
            }
        }

        private void HandleClosingHoldingDoorState()
        {
            // Use the stored holding cell index from when intake started
            if (currentHoldingCellIndex != -1)
            {
                var jailController = Core.JailController;
                if (jailController?.doorController != null)
                {
                    bool doorClosed = jailController.doorController.CloseHoldingCellDoor(currentHoldingCellIndex);
                    if (doorClosed)
                    {
                        ModLogger.Info($"IntakeOfficer: Holding cell {currentHoldingCellIndex} door closed successfully");
                    }
                    else
                    {
                        ModLogger.Error($"IntakeOfficer: Failed to close holding cell {currentHoldingCellIndex} door");
                    }
                }
            }

            // Proceed to mugshot regardless
            SendGuardMessage("Follow me.", 3f);
            ChangeIntakeState(IntakeState.EscortToMugshot);
        }

        private void HandleOpeningCellDoorState()
        {
            // Door opening should complete quickly, then wait for player entry
            SendGuardMessage("Get in.", 2f);
            ChangeIntakeState(IntakeState.WaitingForCellEntry);
        }

        private void HandleClosingCellDoorState()
        {
            // Door closing should complete quickly, then return to post
            SendGuardMessage("Processing complete.", 3f);
            CloseCellDoor();
            ChangeIntakeState(IntakeState.ReturningToPost);
        }

        private void HandleEscortState()
        {
            // Monitor movement progress during escort states
            if (currentDestination != Vector3.zero)
            {
                float distance = Vector3.Distance(transform.position, currentDestination);

                // Check if we've reached destination manually (Unity precision issues)
                if (distance < 2.0f || (navAgent != null && !navAgent.pathPending && navAgent.remainingDistance < 2.0f))
                {
                    ModLogger.Info($"IntakeOfficer: Reached destination during {currentState} (distance: {distance:F2}m)");
                    OnDestinationReached?.Invoke(currentDestination);
                    return;
                }

                // Check if we're stuck
                if (Time.time - stateStartTime > 30f) // 30 second timeout
                {
                    ModLogger.Warn($"IntakeOfficer: {currentState} timeout after 30s - forcing destination reached");
                    OnDestinationReached?.Invoke(currentDestination);
                }
            }
        }

        #endregion

        #region Navigation and Escort

        private void NavigateToStation(string stationName)
        {
            if (!intakeStations.ContainsKey(stationName))
            {
                ModLogger.Error($"Unknown station: {stationName}");
                return;
            }

            var station = intakeStations[stationName];
            currentTargetStation = stationName;

            // Find door point for station
            Transform doorPoint = FindDoorPoint(station.doorPointName);
            if (doorPoint == null)
            {
                ModLogger.Error($"Could not find door point: {station.doorPointName}");
                return;
            }

            // Navigate to station
            MoveTo(doorPoint.position);

            // Send guard message
            SendGuardMessage(station.guardMessage, station.messageDuration);

            OnStationReached?.Invoke(stationName);
            ModLogger.Info($"IntakeOfficer: Navigating to {stationName}");
        }

        private void NavigateToAssignedCell()
        {
            if (assignedCellNumber < 0)
            {
                ModLogger.Error("No cell assigned for prisoner");
                ChangeIntakeState(IntakeState.ReturningToPost);
                return;
            }

            // Use JailController's cell system with doorPoint property
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("JailController not available for cell navigation");
                ChangeIntakeState(IntakeState.ReturningToPost);
                return;
            }

            var cell = jailController.GetCellByIndex(assignedCellNumber);
            if (cell?.cellDoor?.doorPoint == null)
            {
                ModLogger.Error($"Cell {assignedCellNumber} door point not available - checking if door needs to be unlocked");

                // Try to unlock and open the cell door first
                bool doorOpened = jailController.doorController?.OpenJailCellDoor(assignedCellNumber) ?? false;
                if (!doorOpened)
                {
                    ModLogger.Error($"Failed to open jail cell {assignedCellNumber} door");
                    ChangeIntakeState(IntakeState.ReturningToPost);
                    return;
                }

                // Retry getting the door point after opening
                cell = jailController.GetCellByIndex(assignedCellNumber);
                if (cell?.cellDoor?.doorPoint == null)
                {
                    ModLogger.Error($"Cell {assignedCellNumber} door point still not available after opening door");
                    ChangeIntakeState(IntakeState.ReturningToPost);
                    return;
                }
            }

            MoveTo(cell.cellDoor.doorPoint.position);
            SendGuardMessage("Follow me to your cell.", 3f);

            ModLogger.Info($"IntakeOfficer: Escorting to cell {assignedCellNumber} via doorPoint at {cell.cellDoor.doorPoint.position}");
        }

        private void ReturnToGuardPost()
        {
            if (guardPostTransform != null)
            {
                MoveTo(guardPostTransform.position);
                ModLogger.Info("IntakeOfficer: Returning to guard post");
            }

            // Close all doors that were opened during intake process
            CloseAllIntakeDoors();

            // Complete intake process
            CompleteIntakeProcess();
        }

        #endregion

        #region Door Timing

        private IEnumerator DelayedDoorClose()
        {
            if (doorCloseInitiated) yield break; // Prevent multiple coroutines
            doorCloseInitiated = true;

            yield return new WaitForSeconds(2f); // Give player time to fully exit
            ChangeIntakeState(IntakeState.ClosingHoldingDoor);
        }

        #endregion

        #region Door Management

        /// <summary>
        /// Get the centralized SecurityDoor system from JailController
        /// </summary>
        private SecurityDoorBehavior GetSecurityDoor()
        {
            // Try to get SecurityDoor component from this GameObject first
            var securityDoor = GetComponent<SecurityDoorBehavior>();
            if (securityDoor != null) return securityDoor;

            // Fallback to JailController (centralized SecurityDoor)
            return Core.JailController?.GetComponent<SecurityDoorBehavior>();
        }


        private void CloseAllIntakeDoors()
        {
            var jailController = Core.JailController;
            if (jailController?.doorController == null)
            {
                ModLogger.Warn("IntakeOfficer: No door controller available for closing doors");
                return;
            }

            ModLogger.Info("IntakeOfficer: Closing all doors opened during intake process");

            // Close storage access doors
            jailController.doorController.CloseBookingInnerDoor();
            jailController.doorController.ClosePrisonEntryDoor();

            // Close and lock the holding cell door if it was opened
            if (currentHoldingCellIndex >= 0)
            {
                jailController.doorController.CloseHoldingCellDoor(currentHoldingCellIndex);
            }

            // Close and lock the jail cell door if one was assigned
            if (assignedCellNumber >= 0)
            {
                jailController.doorController.CloseJailCellDoor(assignedCellNumber);
            }

            ModLogger.Info("IntakeOfficer: All intake doors secured");
        }

        #endregion

        #region Door Integration

        private void CheckForDoorTriggers()
        {
            // SecurityDoor integration - trigger appropriate door operations based on escort state
            // SecurityDoor will handle movement to door points, security delays, and door operations

            if (currentState == IntakeState.EscortToStorage)
            {
                TriggerBookingInnerDoorIfNeeded();
            }
            else if (currentState == IntakeState.EscortToCell)
            {
                TriggerPrisonEntryDoorIfNeeded();
            }
        }

        public void HandleDoorTrigger(string triggerName)
        {
            // Note: Direct door trigger handling will be implemented in future version
            ModLogger.Debug($"IntakeOfficer: Door trigger received: {triggerName}");
        }

        private void HandleDoorOperationComplete(string doorName)
        {
            ModLogger.Debug($"IntakeOfficer: Door operation complete for {doorName}");
            // Continue with current objective after door operation
        }

        private void HandleSecurityDoorOperationComplete(string doorName)
        {
            ModLogger.Info($"IntakeOfficer: SecurityDoor operation completed for {doorName}");

            // SecurityDoor has completed its operation - clear the active flag
            isSecurityDoorActive = false;

            // IMPORTANT: Give guard time to move away from door before resuming navigation
            // SecurityDoor finishes but guard needs to clear the door area first
            ModLogger.Info("IntakeOfficer: Waiting for guard to clear door area before resuming navigation");
            MelonCoroutines.Start(DelayedNavigationResume());
        }

        private IEnumerator DelayedNavigationResume()
        {
            // Wait for guard to move away from door area
            yield return new WaitForSeconds(1.5f);

            // Now safely resume navigation to the original target
            if (currentState == IntakeState.EscortToStorage || currentState == IntakeState.WaitingForStorage)
            {
                ModLogger.Info("IntakeOfficer: Resuming navigation to Storage after door clearance delay");
                NavigateToStation("Storage");
            }
            else if (currentState == IntakeState.EscortToCell || currentState == IntakeState.OpeningCellDoor)
            {
                ModLogger.Info("IntakeOfficer: Resuming navigation to Cell after door clearance delay");
                NavigateToAssignedCell();
            }

            ModLogger.Info($"IntakeOfficer: Navigation resumed for state: {currentState}");
        }

        private void HandleSecurityDoorOperationFailed(string doorName)
        {
            ModLogger.Error($"IntakeOfficer: SecurityDoor operation FAILED for {doorName} - attempting fallback");

            // If SecurityDoor fails, try fallback direct door control
            if (doorName.Contains("Booking") || doorName.Contains("Inner"))
            {
                FallbackDirectDoorControl("BookingInnerDoor");
            }
            else if (doorName.Contains("Prison") || doorName.Contains("Enter"))
            {
                FallbackDirectDoorControl("PrisonEntryDoor");
            }
        }

        // Track which SecurityDoor operations have been triggered to prevent re-triggering
        private HashSet<string> triggeredDoorOperations = new HashSet<string>();

        // Track when SecurityDoor is active to pause destination checking
        private bool isSecurityDoorActive = false;

        private void TriggerBookingInnerDoorIfNeeded()
        {
            // Check if we've already triggered the booking inner door operation
            if (triggeredDoorOperations.Contains("BookingInnerDoor")) return;

            var securityDoor = GetSecurityDoor();
            if (securityDoor == null)
            {
                ModLogger.Error("IntakeOfficer: No SecurityDoor component available - falling back to direct door control");
                FallbackDirectDoorControl("BookingInnerDoor");
                return;
            }

            // Trigger SecurityDoor operation for booking inner door
            // SecurityDoor will handle: movement to door point → security delay → unlock → open
            string triggerName = "BookingDoorTrigger_FromBooking"; // Guard moving from booking area to hall
            bool triggered = securityDoor.HandleDoorTrigger(triggerName, true, currentPrisoner);

            if (triggered)
            {
                triggeredDoorOperations.Add("BookingInnerDoor");
                isSecurityDoorActive = true;
                ModLogger.Info("IntakeOfficer: SecurityDoor operation triggered for booking inner door");
            }
            else
            {
                ModLogger.Warn("IntakeOfficer: Failed to trigger SecurityDoor for booking inner door");
            }
        }

        private void TriggerPrisonEntryDoorIfNeeded()
        {
            // Check if we've already triggered the prison entry door operation
            if (triggeredDoorOperations.Contains("PrisonEntryDoor")) return;

            var securityDoor = GetSecurityDoor();
            if (securityDoor == null)
            {
                ModLogger.Error("IntakeOfficer: No SecurityDoor component available - falling back to direct door control");
                FallbackDirectDoorControl("PrisonEntryDoor");
                return;
            }

            // Trigger SecurityDoor operation for prison entry door
            // SecurityDoor will handle: movement to door point → security delay → unlock → open
            string triggerName = "PrisonDoorTrigger_FromHall"; // Guard moving from hall to prison area
            bool triggered = securityDoor.HandleDoorTrigger(triggerName, true, currentPrisoner);

            if (triggered)
            {
                triggeredDoorOperations.Add("PrisonEntryDoor");
                isSecurityDoorActive = true;
                ModLogger.Info("IntakeOfficer: SecurityDoor operation triggered for prison entry door");
            }
            else
            {
                ModLogger.Warn("IntakeOfficer: Failed to trigger SecurityDoor for prison entry door");
            }
        }

        private void FallbackDirectDoorControl(string doorType)
        {
            // Fallback to direct door control if SecurityDoor is not available
            var jailController = Core.JailController;
            if (jailController?.doorController == null) return;

            if (doorType == "BookingInnerDoor")
            {
                bool opened = jailController.doorController.UnlockAndOpenBookingInnerDoor();
                if (opened)
                {
                    triggeredDoorOperations.Add("BookingInnerDoor");
                    ModLogger.Info("IntakeOfficer: Booking inner door opened via fallback direct control");
                }
            }
            else if (doorType == "PrisonEntryDoor")
            {
                bool opened = jailController.doorController.OpenPrisonEntryDoor();
                if (opened)
                {
                    triggeredDoorOperations.Add("PrisonEntryDoor");
                    ModLogger.Info("IntakeOfficer: Prison entry door opened via fallback direct control");
                }
            }
        }

        private void ResetDoorTracking()
        {
            // Clear triggered door operations when starting new intake process
            triggeredDoorOperations.Clear();
            ModLogger.Debug("IntakeOfficer: Door operation tracking reset for new intake process");
        }

        private void HandleDestinationReached(Vector3 destination)
        {
            // Ignore destination events when SecurityDoor is actively controlling the guard
            if (isSecurityDoorActive)
            {
                return; // No logging - SecurityDoor is handling movement
            }

            ModLogger.Info($"IntakeOfficer: *** DESTINATION REACHED EVENT FIRED *** at {destination} during state {currentState}");

            // Handle state transitions based on current state
            switch (currentState)
            {
                case IntakeState.EscortToHolding:
                    ModLogger.Info("IntakeOfficer: Transitioning from EscortToHolding to OpeningHoldingDoor");
                    ChangeIntakeState(IntakeState.OpeningHoldingDoor);
                    break;

                case IntakeState.EscortToMugshot:
                    ChangeIntakeState(IntakeState.WaitingForMugshot);
                    break;

                case IntakeState.EscortToScanner:
                    ChangeIntakeState(IntakeState.WaitingForScan);
                    break;

                case IntakeState.EscortToStorage:
                    ChangeIntakeState(IntakeState.WaitingForStorage);
                    break;

                case IntakeState.EscortToCell:
                    ChangeIntakeState(IntakeState.OpeningCellDoor);
                    break;

                case IntakeState.ReturningToPost:
                    ChangeIntakeState(IntakeState.Idle);
                    break;

                default:
                    ModLogger.Warn($"IntakeOfficer: HandleDestinationReached called during unexpected state: {currentState}");
                    break;
            }
        }

        // Override base class movement handling to prevent NPCState.Idle interference
        protected override void HandleMovingState()
        {
            // Instead of calling base.HandleMovingState(), handle movement ourselves
            if (HasReachedDestination())
            {
                ModLogger.Info($"IntakeOfficer: Movement destination reached during state {currentState}");
                // Trigger our own destination reached handler instead of base class
                OnDestinationReached?.Invoke(currentDestination);
                // DON'T call ChangeState(NPCState.Idle) like the base class does
            }
        }

        // Override base class ChangeState to prevent interference with our intake state machine
        public override void ChangeState(NPCState newState)
        {
            // Completely ignore base class state changes - we manage our own state
            // (Removed spammy logging)
        }

        #endregion

        #region Event Handlers

        private void HandleBookingStarted(Player player)
        {
            if (currentState != IntakeState.Idle)
            {
                ModLogger.Warn("IntakeOfficer: Already processing another intake");
                return;
            }

            currentPrisoner = player;

            // Reset state tracking flags for new intake
            playerExitDetected = false;
            doorCloseInitiated = false;

            // Reset door tracking for new intake process
            ResetDoorTracking();

            // Reset SecurityDoor state
            isSecurityDoorActive = false;

            // Determine which holding cell contains this player using JailController's centralized method
            var jailController = Core.JailController;
            currentHoldingCellIndex = jailController?.FindPlayerHoldingCell(player) ?? -1;
            if (currentHoldingCellIndex == -1)
            {
                ModLogger.Error($"IntakeOfficer: Could not find player {player.name} in any holding cell");
                return;
            }

            ModLogger.Info($"IntakeOfficer: Player {player.name} found in holding cell {currentHoldingCellIndex}");
            OnIntakeStarted?.Invoke(player);

            ChangeIntakeState(IntakeState.DelayBeforeFetch);
            ModLogger.Info($"IntakeOfficer: Starting intake process for {player.name}");
        }

        private void HandleMugshotCompleted(Player player)
        {
            if (currentState == IntakeState.WaitingForMugshot)
            {
                ModLogger.Info("IntakeOfficer: Mugshot completed, proceeding to scanner");
            }
        }

        private void HandleFingerprintCompleted(Player player)
        {
            if (currentState == IntakeState.WaitingForScan)
            {
                ModLogger.Info("IntakeOfficer: Fingerprint scan completed, proceeding to storage");
            }
        }

        private void HandleInventoryCompleted(Player player)
        {
            if (currentState == IntakeState.WaitingForStorage)
            {
                ModLogger.Info("IntakeOfficer: Inventory processing completed, proceeding to cell");
            }
        }

        #endregion

        #region Utility Methods

        private bool IsEscortState(IntakeState state)
        {
            return state == IntakeState.EscortToHolding ||
                   state == IntakeState.EscortToMugshot ||
                   state == IntakeState.EscortToScanner ||
                   state == IntakeState.EscortToStorage ||
                   state == IntakeState.EscortToCell;
        }


        private Transform FindDoorPoint(string stationName)
        {
            var jailController = Core.JailController;
            if (jailController == null) return null;

            // Search by name patterns in the hierarchy
            Transform[] allTransforms = jailController.GetComponentsInChildren<Transform>();

            switch (stationName)
            {
                case "HoldingCell":
                    // Look for HoldingCell_00/HoldingDoorHolder[0]/DoorPoint
                    foreach (Transform t in allTransforms)
                    {
                        if (t.name == "DoorPoint" &&
                            t.parent?.name.Contains("HoldingDoor") == true &&
                            t.parent?.parent?.name.Contains("HoldingCell") == true)
                        {
                            ModLogger.Info($"Found holding cell door point: {t.name} under {t.parent.parent.name}");
                            return t;
                        }
                    }
                    break;

                case "MugshotStation":
                    // Look for MugshotStation/GuardPoint
                    foreach (Transform t in allTransforms)
                    {
                        if ((t.name == "GuardPoint" || t.name == "DoorPoint") &&
                            t.parent?.name.Contains("Mugshot") == true)
                        {
                            ModLogger.Info($"Found mugshot station point: {t.name} under {t.parent.name}");
                            return t;
                        }
                    }
                    break;

                case "ScannerStation":
                    // Look for ScannerStation/GuardPoint
                    foreach (Transform t in allTransforms)
                    {
                        if ((t.name == "GuardPoint" || t.name == "DoorPoint") &&
                            t.parent?.name.Contains("Scanner") == true)
                        {
                            ModLogger.Info($"Found scanner station point: {t.name} under {t.parent.name}");
                            return t;
                        }
                    }
                    break;

                case "Storage":
                    // Look for Storage/GuardPoint
                    foreach (Transform t in allTransforms)
                    {
                        if ((t.name == "GuardPoint" || t.name == "DoorPoint") &&
                            t.parent?.name.Contains("Storage") == true)
                        {
                            ModLogger.Info($"Found storage point: {t.name} under {t.parent.name}");
                            return t;
                        }
                    }
                    break;

                default:
                    // For jail cells, look for Cell_XX/DoorPoint
                    if (stationName.StartsWith("JailCell_"))
                    {
                        string cellNumStr = stationName.Replace("JailCell_", "").Replace("/DoorPoint", "");
                        foreach (Transform t in allTransforms)
                        {
                            if (t.name == "DoorPoint" &&
                                t.parent?.name.Contains($"Cell_{cellNumStr}") == true)
                            {
                                ModLogger.Info($"Found jail cell door point: {t.name} under {t.parent.name}");
                                return t;
                            }
                        }
                    }
                    break;
            }

            ModLogger.Warn($"Could not find door point for station: {stationName}");
            return null;
        }

        private void AssignPrisonerCell()
        {
            if (currentPrisoner == null) return;

            var cellManager = CellAssignmentManager.Instance;
            if (cellManager != null)
            {
                assignedCellNumber = cellManager.AssignPlayerToCell(currentPrisoner);
                if (assignedCellNumber >= 0)
                {
                    ModLogger.Info($"Assigned prisoner to cell {assignedCellNumber}");
                }
                else
                {
                    ModLogger.Error("Failed to assign cell to prisoner");
                    assignedCellNumber = 0; // Default to cell 0
                }
            }
            else
            {
                ModLogger.Error("CellAssignmentManager not available");
                assignedCellNumber = 0; // Default to cell 0
            }
        }

        private void CloseCellDoor()
        {
            if (assignedCellNumber < 0) return;

            var jailController = Core.JailController;
            if (jailController?.doorController != null)
            {
                bool doorClosed = jailController.doorController.CloseJailCellDoor(assignedCellNumber);
                if (doorClosed)
                {
                    ModLogger.Info($"IntakeOfficer: Jail cell {assignedCellNumber} door closed successfully via JailDoorController");
                }
                else
                {
                    ModLogger.Error($"IntakeOfficer: Failed to close jail cell {assignedCellNumber} door via JailDoorController");
                }
            }
            else
            {
                ModLogger.Error("IntakeOfficer: No door controller available for closing jail cell door");
            }
        }

        private void OpenHoldingCellDoor()
        {
            var jailController = Core.JailController;
            if (jailController?.holdingCells?.Count > 0)
            {
                var holdingCell = jailController.holdingCells[0];
                if (holdingCell.cellDoor != null)
                {
                    if (holdingCell.cellDoor.IsClosed())
                    {
                        holdingCell.cellDoor.OpenDoor();
                        ModLogger.Info("Opened holding cell door");
                    }
                }
                else
                {
                    ModLogger.Warn("No door found on holding cell");
                }
            }
        }

        private void OpenJailCellDoor()
        {
            if (assignedCellNumber < 0) return;

            var jailController = Core.JailController;
            if (jailController?.doorController != null)
            {
                bool doorOpened = jailController.doorController.OpenJailCellDoor(assignedCellNumber);
                if (doorOpened)
                {
                    ModLogger.Info($"IntakeOfficer: Jail cell {assignedCellNumber} door opened successfully via JailDoorController");
                }
                else
                {
                    ModLogger.Error($"IntakeOfficer: Failed to open jail cell {assignedCellNumber} door via JailDoorController");
                }
            }
            else
            {
                ModLogger.Error("IntakeOfficer: No door controller available for opening jail cell door");
            }
        }

        private void SendGuardMessage(string message, float duration)
        {
            TrySendNPCMessage(message, duration);
        }


        private void CompleteIntakeProcess()
        {
            OnIntakeCompleted?.Invoke(currentPrisoner);

            // Reset state
            currentPrisoner = null;
            assignedCellNumber = -1;
            currentTargetStation = "";
            currentHoldingCellIndex = -1;

            // Reset state tracking flags
            playerExitDetected = false;
            doorCloseInitiated = false;

            ChangeIntakeState(IntakeState.Idle);

            ModLogger.Info("IntakeOfficer: Intake process completed");
        }

        #endregion

        #region Public Interface

        public new IntakeState GetCurrentState() => currentState;
        public Player GetCurrentPrisoner() => currentPrisoner;
        public bool IsProcessingIntake() => currentState != IntakeState.Idle;
        public string GetCurrentTargetStation() => currentTargetStation;

        /// <summary>
        /// Force start intake process (for testing)
        /// </summary>
        public void ForceStartIntake(Player player)
        {
            if (player != null)
            {
                HandleBookingStarted(player);
            }
        }

        /// <summary>
        /// Emergency stop intake process
        /// </summary>
        public void StopIntakeProcess()
        {
            ModLogger.Info("IntakeOfficer: Emergency stop of intake process");
            ChangeIntakeState(IntakeState.ReturningToPost);
        }

        #endregion

        #region Cleanup

        new void OnDestroy()
        {
            // Unsubscribe from events
            if (bookingProcess != null)
            {
                bookingProcess.OnBookingStarted -= HandleBookingStarted;
                bookingProcess.OnMugshotCompleted -= HandleMugshotCompleted;
                bookingProcess.OnFingerprintCompleted -= HandleFingerprintCompleted;
                bookingProcess.OnInventoryDropOffCompleted -= HandleInventoryCompleted;
            }

            // Unsubscribe from SecurityDoor events
            var securityDoor = GetSecurityDoor();
            if (securityDoor != null)
            {
                securityDoor.OnDoorOperationComplete -= HandleSecurityDoorOperationComplete;
                securityDoor.OnDoorOperationFailed -= HandleSecurityDoorOperationFailed;
            }

            OnDestinationReached -= HandleDestinationReached;
        }

        #endregion

    }
}