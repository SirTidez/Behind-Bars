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

        // Dialogue system
        private JailNPCDialogueController dialogueController;
        private bool isEscorting = false;
        private Vector3 destinationPosition;

        // Continuous rotation system
        private object continuousLookingCoroutine;

        // Destination tracking to prevent duplicate events
        private Dictionary<string, bool> stationDestinationProcessed = new Dictionary<string, bool>();
        private float lastDoorOperationTime = 0f;

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
            InitializeDialogueSystem();

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
                    guardMessage = "Follow me to storage.",
                    messageDuration = 3f
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

        private void InitializeDialogueSystem()
        {
            // Use a coroutine to retry getting the dialogue controller
            MelonLoader.MelonCoroutines.Start(WaitForDialogueController());
        }

        private System.Collections.IEnumerator WaitForDialogueController()
        {
            int retryCount = 0;
            const int maxRetries = 10;

            while (retryCount < maxRetries)
            {
                // Try to get the dialogue controller that should have been added by PrisonNPCManager
                dialogueController = GetComponent<JailNPCDialogueController>();

                if (dialogueController != null)
                {
                    // Set up intake-specific dialogue states
                    dialogueController.AddStateDialogue("Idle", "I'm here to process inmates.",
                        new[] { "Waiting for the next intake.", "Everything's running smoothly.", "Ready for processing." });

                    dialogueController.AddStateDialogue("Processing", "Time to process you.",
                        new[] { "Follow me.", "This way.", "Stay close." });

                    // Escort states - show "Follow me" during movement
                    dialogueController.AddStateDialogue("EscortToHolding", "Follow me.",
                        new[] { "This way.", "Keep moving.", "Stay close." });

                    dialogueController.AddStateDialogue("EscortToMugshot", "Follow me.",
                        new[] { "This way.", "Keep moving.", "Stay close." });

                    dialogueController.AddStateDialogue("EscortToScanner", "Follow me.",
                        new[] { "This way.", "Keep moving.", "Stay close." });

                    dialogueController.AddStateDialogue("EscortToStorage", "Follow me.",
                        new[] { "This way.", "Keep moving.", "Stay close." });

                    dialogueController.AddStateDialogue("EscortToCell", "Follow me.",
                        new[] { "This way.", "Keep moving.", "Stay close." });

                    // Action states - show specific instructions when at destination
                    dialogueController.AddStateDialogue("AtMugshot", "Go take your mugshot!",
                        new[] { "Stand in front of the camera.", "Look straight ahead.", "Don't move." });

                    dialogueController.AddStateDialogue("AtScanner", "Place your hand on the scanner.",
                        new[] { "Scan in.", "Press your palm down.", "Hold still." });

                    dialogueController.AddStateDialogue("AtStorage", "Drop your belongings and pick up prison items.",
                        new[] { "Put your things in the box.", "Take the prison uniform.", "Change quickly." });

                    dialogueController.AddStateDialogue("AtCell", "This is your cell.",
                        new[] { "Get in.", "This is where you'll be staying.", "Inside." });

                    dialogueController.AddStateDialogue("AtHolding", "Go through the door.",
                        new[] { "Step inside.", "Move in.", "Enter the holding area." });

                    // Start with idle state
                    dialogueController.UpdateGreetingForState("Idle");

                    ModLogger.Info("IntakeOfficer: Dialogue system initialized with custom states");
                    yield break; // Success - exit the coroutine
                }
                else
                {
                    retryCount++;
                    ModLogger.Debug($"IntakeOfficer: JailNPCDialogueController not found yet, retry {retryCount}/{maxRetries}");
                    yield return new UnityEngine.WaitForSeconds(0.1f); // Wait 100ms before retrying
                }
            }

            ModLogger.Error("IntakeOfficer: Failed to find JailNPCDialogueController component after maximum retries - dialogue system not initialized");
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

            // Update dialogue state
            ModLogger.Debug($"IntakeOfficer: Calling UpdateDialogueForState({newState}) - dialogueController is {(dialogueController != null ? "available" : "null")}");
            UpdateDialogueForState(newState);

            // Handle state entry logic
            OnStateEnter(newState);
        }

        private void UpdateDialogueForState(IntakeState state)
        {
            if (dialogueController == null)
            {
                ModLogger.Debug($"IntakeOfficer: UpdateDialogueForState called but dialogueController is null");
                return;
            }

            // Check if we're currently escorting and far from destination
            bool showEscortDialog = IsCurrentlyEscorting();

            string dialogueState;

            if (showEscortDialog)
            {
                // If we're escorting and far from destination, always show "Follow me"
                dialogueState = "EscortToHolding"; // Use any escort state - they all show "Follow me"
            }
            else
            {
                // Use state-specific dialog when close to destination or not escorting
                dialogueState = state switch
                {
                    IntakeState.Idle => "Idle",
                    IntakeState.WaitingForBooking => "Idle",
                    IntakeState.DelayBeforeFetch => "Processing",

                    // During escort - show "Follow me"
                    IntakeState.EscortToHolding => "EscortToHolding",
                    IntakeState.EscortToMugshot => "EscortToMugshot",
                    IntakeState.EscortToScanner => "EscortToScanner",
                    IntakeState.EscortToStorage => "EscortToStorage",
                    IntakeState.EscortToCell => "EscortToCell",

                    // At destination - show specific action instructions
                    IntakeState.OpeningHoldingDoor => "AtHolding",
                    IntakeState.WaitingForPlayerExit => "AtHolding",
                    IntakeState.ClosingHoldingDoor => "AtHolding",
                    IntakeState.WaitingForMugshot => "AtMugshot",
                    IntakeState.WaitingForScan => "AtScanner",
                    IntakeState.WaitingForStorage => "AtStorage",
                    IntakeState.OpeningCellDoor => "AtCell",
                    IntakeState.WaitingForCellEntry => "AtCell",
                    IntakeState.ClosingCellDoor => "AtCell",

                    IntakeState.ReturningToPost => "Processing",
                    _ => "Idle"
                };
            }

            ModLogger.Debug($"IntakeOfficer: UpdateDialogueForState - setting dialogue state to '{dialogueState}' for intake state {state}");
            dialogueController.UpdateGreetingForState(dialogueState);
        }

        private bool IsCurrentlyEscorting()
        {
            // Check if we're in an escort state
            bool isInEscortState = currentState == IntakeState.EscortToHolding ||
                                   currentState == IntakeState.EscortToMugshot ||
                                   currentState == IntakeState.EscortToScanner ||
                                   currentState == IntakeState.EscortToStorage ||
                                   currentState == IntakeState.EscortToCell ||
                                   currentState == IntakeState.WaitingForMugshot ||
                                   currentState == IntakeState.WaitingForScan ||
                                   currentState == IntakeState.WaitingForStorage;

            if (!isInEscortState) return false;

            // Check distance to destination - if we're far away, show escort dialog
            float distanceToDestination = Vector3.Distance(transform.position, destinationPosition);
            return distanceToDestination > 3f; // If more than 3 units away, show "Follow me"
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
                if (distanceToPost > 2f && navAgent != null && (!navAgent.hasPath || navAgent.remainingDistance < 0.5f))
                {
                    // Only move to guard post if not already moving there
                    MoveTo(guardPostTransform.position);
                    ModLogger.Debug($"IntakeOfficer: Moving to guard post from distance {distanceToPost:F2}m");
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
            if (bookingProcess != null)
            {
                if (bookingProcess.prisonGearPickupComplete)
                {
                    ModLogger.Info("IntakeOfficer: Prison gear pickup detected as complete, proceeding to cell assignment");
                    // Assign cell before escorting
                    AssignPrisonerCell();
                    ChangeIntakeState(IntakeState.EscortToCell);
                }
                else
                {
                    // Add periodic logging to see what's happening
                    if (Time.time % 5f < Time.deltaTime) // Every 5 seconds
                    {
                        ModLogger.Debug($"IntakeOfficer: Still waiting for prison gear pickup - prisonGearPickupComplete: {bookingProcess.prisonGearPickupComplete}");
                    }
                }
            }
            else
            {
                ModLogger.Error("IntakeOfficer: BookingProcess is null in HandleWaitingForStorageState");
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
                    OnDestinationReached?.Invoke(currentDestination);
                    return;
                }

                // Check if we're stuck
                if (Time.time - stateStartTime > 30f) // 30 second timeout
                {
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

            // Set destination for dialog distance checking
            destinationPosition = doorPoint.position;

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
                    ModLogger.Warn($"Cell {assignedCellNumber} door point still not available - trying alternative positioning");

                    // Try to use cell transform position as fallback
                    if (cell?.cellTransform != null)
                    {
                        ModLogger.Info($"Using cell transform position for cell {assignedCellNumber}: {cell.cellTransform.position}");
                        destinationPosition = cell.cellTransform.position;
                        MoveTo(cell.cellTransform.position);
                        SendGuardMessage("Follow me to your cell.", 3f);
                        ModLogger.Info($"IntakeOfficer: Escorting to cell {assignedCellNumber} via cellTransform at {cell.cellTransform.position}");
                        return;
                    }
                    else
                    {
                        ModLogger.Error($"Cell {assignedCellNumber} has no cellTransform either - cannot escort to cell");
                        ChangeIntakeState(IntakeState.ReturningToPost);
                        return;
                    }
                }
            }

            // Set destination for dialog distance checking
            destinationPosition = cell.cellDoor.doorPoint.position;

            MoveTo(cell.cellDoor.doorPoint.position);
            SendGuardMessage("Follow me to your cell.", 3f);

            ModLogger.Info($"IntakeOfficer: Escorting to cell {assignedCellNumber} via doorPoint at {cell.cellDoor.doorPoint.position}");
            ModLogger.Info($"IntakeOfficer: Current position: {transform.position}, Target position: {cell.cellDoor.doorPoint.position}");
            ModLogger.Info($"IntakeOfficer: Distance to cell: {Vector3.Distance(transform.position, cell.cellDoor.doorPoint.position):F2}m");
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

            // Record the time of door operation completion to prevent premature destination events
            lastDoorOperationTime = Time.time;

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
            else if (currentState == IntakeState.EscortToCell)
            {
                ModLogger.Info("IntakeOfficer: Resuming navigation to Cell after door clearance delay");
                NavigateToAssignedCell();
            }
            else if (currentState == IntakeState.OpeningCellDoor)
            {
                ModLogger.Info("IntakeOfficer: Already at cell, continuing with door opening");
                // Don't re-navigate if we're already at the cell and opening the door
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
            stationDestinationProcessed.Clear();
            ModLogger.Debug("IntakeOfficer: Door operation and destination tracking reset for new intake process");
        }

        private void HandleDestinationReached(Vector3 destination)
        {
            // Ignore destination events when SecurityDoor is actively controlling the guard
            if (isSecurityDoorActive)
            {
                return; // No logging - SecurityDoor is handling movement
            }

            // IMPORTANT: Ignore all destination events during door clearance delay period
            // This prevents premature destination triggers when guard is temporarily at wrong location
            float timeSinceLastDoorOperation = Time.time - lastDoorOperationTime;
            if (timeSinceLastDoorOperation < 3.0f) // Within 3 seconds of door operation
            {
                ModLogger.Debug($"IntakeOfficer: Ignoring destination reached - within door clearance delay period ({timeSinceLastDoorOperation:F1}s ago)");
                return;
            }

            // Ignore if we're already in a waiting state (already processed this destination)
            if (currentState == IntakeState.WaitingForMugshot ||
                currentState == IntakeState.WaitingForScan ||
                currentState == IntakeState.WaitingForStorage ||
                currentState == IntakeState.WaitingForCellEntry ||
                currentState == IntakeState.WaitingForPlayerExit)
            {
                ModLogger.Debug($"IntakeOfficer: Ignoring destination reached - already in waiting state {currentState}");
                return;
            }

            // Also ignore if we're not at the correct target for our current state
            if (!IsAtCorrectDestinationForState(destination))
            {
                ModLogger.Debug($"IntakeOfficer: Ignoring destination reached at {destination} - not the correct target for state {currentState}");
                return;
            }

            ModLogger.Info($"IntakeOfficer: *** DESTINATION REACHED EVENT FIRED *** at {destination} during state {currentState}");

            // Handle state transitions based on current state
            switch (currentState)
            {
                case IntakeState.EscortToHolding:
                    ModLogger.Info("IntakeOfficer: Transitioning from EscortToHolding to OpeningHoldingDoor");
                    // Rotate to face the holding cell door
                    RotateToFaceStationTarget("HoldingCell");
                    ChangeIntakeState(IntakeState.OpeningHoldingDoor);
                    break;

                case IntakeState.EscortToMugshot:
                    // Rotate to face the mugshot station
                    RotateToFaceStationTarget("MugshotStation");
                    ChangeIntakeState(IntakeState.WaitingForMugshot);
                    break;

                case IntakeState.EscortToScanner:
                    // Rotate to face the scanner station
                    RotateToFaceStationTarget("ScannerStation");
                    ChangeIntakeState(IntakeState.WaitingForScan);
                    break;

                case IntakeState.EscortToStorage:
                    // Rotate to face the storage station and send arrival message
                    RotateToFaceStationTarget("Storage");
                    SendGuardMessage("Pick up your prison gear.", 3f);
                    ChangeIntakeState(IntakeState.WaitingForStorage);
                    break;

                case IntakeState.EscortToCell:
                    ModLogger.Info($"IntakeOfficer: Destination reached for EscortToCell at {destination}");
                    ModLogger.Info($"IntakeOfficer: Current position: {transform.position}");
                    ModLogger.Info($"IntakeOfficer: Target cell door position should be: {(Core.JailController?.GetCellByIndex(assignedCellNumber)?.cellDoor?.doorPoint?.position.ToString() ?? "UNKNOWN")}");

                    // Check if we're actually at the cell door
                    var targetCell = Core.JailController?.GetCellByIndex(assignedCellNumber);
                    if (targetCell?.cellDoor?.doorPoint != null)
                    {
                        float distanceToActualCell = Vector3.Distance(transform.position, targetCell.cellDoor.doorPoint.position);
                        ModLogger.Info($"IntakeOfficer: Distance to actual cell door: {distanceToActualCell:F2}m");

                        if (distanceToActualCell > 5.0f)
                        {
                            ModLogger.Warn($"IntakeOfficer: Guard stopped too far from cell door! Re-navigating to cell...");
                            NavigateToAssignedCell(); // Try again
                            return;
                        }
                    }

                    ChangeIntakeState(IntakeState.OpeningCellDoor);
                    break;

                case IntakeState.ReturningToPost:
                    // Start continuous rotation when back at post
                    StartContinuousPlayerLooking();
                    ChangeIntakeState(IntakeState.Idle);
                    break;

                default:
                    ModLogger.Warn($"IntakeOfficer: HandleDestinationReached called during unexpected state: {currentState}");
                    break;
            }
        }

        private bool IsAtCorrectDestinationForState(Vector3 destination)
        {
            var jailController = Core.JailController;
            if (jailController == null) return true; // Allow if no controller

            float tolerance = 1.5f; // Tighter tolerance to prevent early rotation

            // Get station name for current state
            string stationName = GetStationNameForState(currentState);
            if (!string.IsNullOrEmpty(stationName))
            {
                // Check if we've already processed this station destination
                if (stationDestinationProcessed.ContainsKey(stationName) && stationDestinationProcessed[stationName])
                {
                    ModLogger.Debug($"IntakeOfficer: Already processed destination for {stationName} - ignoring duplicate");
                    return false;
                }

                // Check if we're actually at the correct location
                bool isAtCorrectLocation = IsNearDoorPoint(stationName, destination, tolerance);
                if (isAtCorrectLocation)
                {
                    // Mark this station as processed
                    stationDestinationProcessed[stationName] = true;
                    ModLogger.Debug($"IntakeOfficer: Marking {stationName} destination as processed");
                }
                return isAtCorrectLocation;
            }

            // Handle cell state separately
            if (currentState == IntakeState.EscortToCell)
            {
                if (assignedCellNumber >= 0)
                {
                    var cell = jailController.GetCellByIndex(assignedCellNumber);
                    if (cell?.cellDoor?.doorPoint != null)
                    {
                        float distance = Vector3.Distance(destination, cell.cellDoor.doorPoint.position);
                        return distance <= tolerance;
                    }
                }
                return false;
            }

            return true; // Allow for other states
        }

        private string GetStationNameForState(IntakeState state)
        {
            switch (state)
            {
                case IntakeState.EscortToHolding: return "HoldingCell";
                case IntakeState.EscortToMugshot: return "MugshotStation";
                case IntakeState.EscortToScanner: return "ScannerStation";
                case IntakeState.EscortToStorage: return "Storage";
                default: return null;
            }
        }

        private bool IsNearDoorPoint(string stationName, Vector3 destination, float tolerance)
        {
            var doorPoint = FindDoorPoint(stationName);
            if (doorPoint == null) return true; // Allow if door point not found

            float distance = Vector3.Distance(destination, doorPoint.position);
            ModLogger.Debug($"IntakeOfficer: Checking distance to {stationName}: {distance:F2}m (tolerance: {tolerance:F2}m)");
            return distance <= tolerance;
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

            // Check for officer coordination conflicts
            if (!OfficerCoordinator.Instance.RegisterEscort(this, OfficerCoordinator.EscortType.Intake, player))
            {
                ModLogger.Info($"IntakeOfficer: Intake delayed due to coordination conflict - will retry");
                // Retry after a short delay
                MelonCoroutines.Start(RetryIntakeAfterDelay(player, 5f));
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator RetryIntakeAfterDelay(Player player, float delay)
        {
            yield return new WaitForSeconds(delay);

            // Try again if still idle and player is still valid
            if (currentState == IntakeState.Idle && player != null)
            {
                ModLogger.Info($"IntakeOfficer: Retrying intake for {player?.name} after coordination delay");
                HandleBookingStarted(player);
            }
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
            ModLogger.Info($"HandleInventoryCompleted called for {player?.name} while in state {currentState}");

            if (currentState == IntakeState.WaitingForStorage)
            {
                ModLogger.Info("IntakeOfficer: Inventory processing completed, proceeding to cell");
                // Assign cell before escorting
                AssignPrisonerCell();
                // Actually transition to escorting to cell
                ChangeIntakeState(IntakeState.EscortToCell);
            }
            else
            {
                ModLogger.Warn($"IntakeOfficer: HandleInventoryCompleted called but guard is in {currentState} state, not WaitingForStorage");
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
                    //jailController.holdingCell00GuardPoint;

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
                    // Use JailController's statically assigned guard point
                    if (jailController != null)
                    {
                        var guardPoint = jailController.GetGuardPoint("MugshotStation");
                        if (guardPoint != null)
                        {
                            ModLogger.Info($"Using JailController assigned MugshotStation guard point");
                            return guardPoint;
                        }
                    }
                    ModLogger.Warn("MugshotStation guard point not found in JailController");
                    break;

                case "ScannerStation":
                    // Use JailController's statically assigned guard point
                    if (jailController != null)
                    {
                        var guardPoint = jailController.GetGuardPoint("ScannerStation");
                        if (guardPoint != null)
                        {
                            ModLogger.Info($"Using JailController assigned ScannerStation guard point");
                            return guardPoint;
                        }
                    }
                    ModLogger.Warn("ScannerStation guard point not found in JailController");
                    break;

                case "Storage":
                    // Use JailController's statically assigned guard point
                    if (jailController != null)
                    {
                        var guardPoint = jailController.GetGuardPoint("Storage");
                        if (guardPoint != null)
                        {
                            ModLogger.Info($"Using JailController assigned Storage guard point");
                            return guardPoint;
                        }
                    }
                    ModLogger.Warn("Storage guard point not found in JailController");
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
                // Start continuous rotation while at the cell
                StartContinuousPlayerLooking();
                ModLogger.Info($"IntakeOfficer: Started continuous player looking before opening cell {assignedCellNumber} door");

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
            // Use the enhanced message system that supports native dialog
            TrySendNPCMessage(message, duration);

            // Also trigger contextual dialogue if available (for when player interacts with NPC)
            if (dialogueController != null)
            {
                dialogueController.SendContextualMessage("interaction");
            }
        }


        private void CompleteIntakeProcess()
        {
            OnIntakeCompleted?.Invoke(currentPrisoner);

            // Unregister from officer coordination
            OfficerCoordinator.Instance.UnregisterEscort(this);

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

        /// <summary>
        /// Override base attack handling to interrupt intake process
        /// TEMPORARILY DISABLED FOR TESTING
        /// </summary>
        public override void OnAttackedByPlayer(Player attacker)
        {
            // DISABLED FOR TESTING - no more annoying arrest on accidental punch
            ModLogger.Debug($"IntakeOfficer: Attack by {attacker?.name} ignored during testing");
            return;

            /*
            base.OnAttackedByPlayer(attacker);

            if (attacker == null) return;

            ModLogger.Warn($"IntakeOfficer: Attacked by {attacker.name} during {currentState}");

            // Check if the attacker is our current prisoner
            if (currentPrisoner != null && currentPrisoner == attacker)
            {
                // Prisoner attacked during intake - serious violation
                TrySendNPCMessage("You just attacked a correctional officer! This is a serious offense!", 4f);

                // Stop the intake process immediately
                StopIntakeProcess();

                // The GuardBehavior will handle the arrest
                ModLogger.Error($"IntakeOfficer: Prisoner {attacker.name} attacked during intake process");
            }
            else if (attacker != currentPrisoner)
            {
                // Someone else attacked during intake
                TrySendNPCMessage("Security breach! Intake process suspended!", 3f);
                StopIntakeProcess();

                ModLogger.Error($"IntakeOfficer: Attacked by non-prisoner {attacker.name} during intake");
            }
            */
        }

        #endregion

        #region Utility Methods


        private void StartContinuousPlayerLooking()
        {
            // Stop any existing continuous looking
            StopContinuousPlayerLooking();

            // Start new continuous looking coroutine
            continuousLookingCoroutine = MelonCoroutines.Start(ContinuousPlayerLookingCoroutine());
            ModLogger.Debug("IntakeOfficer: Started continuous player looking");
        }

        private void StopContinuousPlayerLooking()
        {
            if (continuousLookingCoroutine != null)
            {
                MelonCoroutines.Stop(continuousLookingCoroutine);
                continuousLookingCoroutine = null;

                // Re-enable NavMeshAgent rotation when stopping
                if (navAgent != null && navAgent.enabled)
                {
                    navAgent.updateRotation = true;
                }

                ModLogger.Debug("IntakeOfficer: Stopped continuous player looking");
            }
        }

        private System.Collections.IEnumerator ContinuousPlayerLookingCoroutine()
        {
            while (true)
            {
                // Apply rotation immediately, then wait
                ApplyInstantPlayerRotation();

                // Wait 2 seconds before reapplying
                yield return new WaitForSeconds(2f);
            }
        }

        private void ApplyInstantPlayerRotation()
        {
            try
            {
                // Get the current player from the booking process
                var jailController = Core.JailController;
                var currentPlayer = jailController?.BookingProcessController?.GetCurrentPlayer();
                if (currentPlayer == null)
                {
                    return; // Silently skip if no player
                }

                Vector3 playerPosition = currentPlayer.transform.position;
                Vector3 currentPos = transform.position;

                // Calculate the look direction
                Vector3 lookDirection = (playerPosition - currentPos).normalized;
                lookDirection.y = 0; // Keep on horizontal plane

                if (lookDirection != Vector3.zero)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(lookDirection);

                    // Start smooth rotation coroutine instead of instant
                    MelonCoroutines.Start(SmoothRotateToTarget(targetRotation, 0.3f));

                    ModLogger.Debug($"IntakeOfficer: Started smooth rotation to face player at {playerPosition}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"IntakeOfficer: Error in continuous rotation: {ex.Message}");
            }
        }

        private System.Collections.IEnumerator SmoothRotateToTarget(Quaternion targetRotation, float duration)
        {
            Quaternion startRotation = transform.rotation;
            float elapsed = 0f;

            // Disable NavMeshAgent rotation during smooth rotation
            if (navAgent != null && navAgent.enabled)
            {
                navAgent.updateRotation = false;
            }

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;

                // Smooth lerp with easing
                t = Mathf.SmoothStep(0f, 1f, t);

                Quaternion currentRotation = Quaternion.Lerp(startRotation, targetRotation, t);
                transform.rotation = currentRotation;

                if (navAgent != null && navAgent.enabled)
                {
                    navAgent.transform.rotation = currentRotation;
                }

                yield return null;
            }

            // Ensure final rotation is exact
            transform.rotation = targetRotation;
            if (navAgent != null && navAgent.enabled)
            {
                navAgent.transform.rotation = targetRotation;
            }
        }

        /// <summary>
        /// Override MoveTo to add debug logging
        /// </summary>
        /// <param name="destination">Target position</param>
        /// <param name="tolerance">Distance tolerance</param>
        /// <returns>True if navigation started successfully</returns>
        public override bool MoveTo(Vector3 destination, float tolerance = -1f)
        {
            // Stop continuous looking when starting to move
            StopContinuousPlayerLooking();

            // Call base MoveTo with original destination
            bool success = base.MoveTo(destination, tolerance);

            if (success)
            {
                ModLogger.Debug($"IntakeOfficer: Navigation started to {destination}, stopped continuous looking");
            }

            return success;
        }

        /// <summary>
        /// Rotates to face the guard point for a specific station using JailController direct references
        /// </summary>
        /// <param name="stationName">Name of the station to face</param>
        private void RotateToFaceStationTarget(string stationName)
        {
            try
            {
                var jailController = Core.JailController;
                if (jailController == null)
                {
                    ModLogger.Warn($"IntakeOfficer: No jail controller available for rotation to {stationName}");
                    return;
                }

                // Start continuous rotation while at the station
                StartContinuousPlayerLooking();
                ModLogger.Info($"IntakeOfficer: Started continuous player looking at {stationName} station");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"IntakeOfficer: Error rotating to face station {stationName}: {ex.Message}");
            }
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