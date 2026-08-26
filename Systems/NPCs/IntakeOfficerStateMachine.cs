using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using Behind_Bars.UI;
using BBHelpers = Behind_Bars.Helpers.Helpers;

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
#if MONO
            public System.Func<bool> completionCheck;
#endif
        }

        #endregion

        #region Component References

        private BookingProcess bookingProcess;

        #endregion

        #region State Variables

#if MONO
        [SerializeField]
#endif
        private new IntakeState currentState = IntakeState.Idle;
        private Player currentPrisoner;
        private Transform guardPostTransform;
        private int assignedCellNumber = -1;
        private int currentHoldingCellIndex = -1;  // Which holding cell contains the current prisoner
        private string currentHoldingCellName = "";
        private Player requiredHoldingCellPrisoner;
        private string requiredHoldingCellName = "";
        private bool resumingDisciplinaryIntake;
        // A punishment-cell repeat starts on the booking side of the inner corridor door.
        // A direct cell escort must therefore traverse that door before the prison-entry door.
        private bool requiresBookingInnerDoorBeforeCellEscort;

        // State tracking to prevent spam
        private bool playerExitDetected = false;
        private bool doorCloseInitiated = false;

        // Timing variables
        private new float stateStartTime;
        private float delayDuration;
        private float nextCellAssignmentRetryTime;
        private const float CellAssignmentRetryInterval = 2f;

        // Station definitions
        private Dictionary<string, IntakeStation> intakeStations;
        private string currentTargetStation = "";

        // Dialogue system
        private JailNPCDialogueController dialogueController;
        private bool isEscorting = false;
        private Vector3 destinationPosition;

        // Continuous rotation system
        private object continuousLookingCoroutine;
        private object playerFacingCommandCoroutine;
        private object mugshotEscortCoroutine;
        private object delayedDoorCloseCoroutine;
        private object delayedNavigationResumeCoroutine;
        private object retryIntakeCoroutine;

        // Destination tracking to prevent duplicate events
        private Dictionary<string, bool> stationDestinationProcessed = new Dictionary<string, bool>();
        private float lastDoorOperationTime = 0f;

        #endregion

        #region Events

#if MONO
        public new System.Action<IntakeState> OnStateChanged;
        public System.Action<Player> OnIntakeStarted;
        public System.Action<Player> OnIntakeCompleted;
        public System.Action<string> OnStationReached;
#endif

        #endregion

        #region Initialization

        protected override void Awake()
        {
            base.Awake(); // Initialize BaseJailNPC
            // SecurityDoor will be retrieved from JailController when needed
        }

        protected override void Start()
        {
            // Save current intake state before base initialization
            var savedState = currentState;

            base.Start(); // Initialize BaseJailNPC

            // Restore intake state after base initialization
            currentState = savedState;
            ModLogger.Debug($"IntakeOfficer: Restored state to {currentState} after base initialization");

            InitializeStations();
            FindGuardPost();
            SubscribeToEvents();
            InitializeDialogueSystem();

            ModLogger.Debug($"IntakeOfficerStateMachine initialized for {gameObject.name}");
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
            var existingSecurityDoor = BBHelpers.GetComponentSafe<SecurityDoorBehavior>(gameObject);
            if (existingSecurityDoor == null)
            {
                // Add SecurityDoorBehavior component to this IntakeOfficer
                var securityDoor = BBHelpers.AddComponentSafe<SecurityDoorBehavior>(gameObject);
                ModLogger.Debug("IntakeOfficer: Added SecurityDoorBehavior component for automated door operations");
            }
            else
            {
                ModLogger.Debug("IntakeOfficer: SecurityDoorBehavior component already attached");
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
                    guardMessage = "Walk over and take your mugshot.",
                    messageDuration = 3f
                },
                ["ScannerStation"] = new IntakeStation
                {
                    stationName = "ScannerStation",
                    doorPointName = "ScannerStation",
                    guardMessage = "Now come here and let us take your fingerprints.",
                    messageDuration = 2f
                },
                ["Storage"] = new IntakeStation
                {
                    stationName = "Storage",
                    doorPointName = "Storage",
                    guardMessage = "Walk over to storage and collect your prison gear.",
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
            var resolvedBookingProcess = Core.ResolveBookingProcess();
            if (resolvedBookingProcess != null)
            {
                bookingProcess = resolvedBookingProcess;
                bookingProcess.OnBookingStarted += HandleBookingStarted;
                bookingProcess.OnMugshotCompleted += HandleMugshotCompleted;
                bookingProcess.OnFingerprintCompleted += HandleFingerprintCompleted;
                bookingProcess.OnInventoryDropOffCompleted += HandleInventoryCompleted;
            }

            // Subscribe to SecurityDoor events
            var securityDoor = GetSecurityDoor();
            if (securityDoor != null)
            {
                securityDoor.AddDoorOperationCompleteListener(HandleSecurityDoorOperationComplete);
                securityDoor.AddDoorOperationFailedListener(HandleSecurityDoorOperationFailed);
                ModLogger.Info("IntakeOfficer: Subscribed to SecurityDoor events");
            }
            else
            {
                ModLogger.Warn("IntakeOfficer: No SecurityDoor component found - will use fallback direct door control");
            }

            // Movement completion is handled via BaseJailNPC.NotifyDestinationReached override.
        }

        private void InitializeDialogueSystem()
        {
            // Use a coroutine to retry getting the dialogue controller
            MelonLoader.MelonCoroutines.Start(WaitForDialogueController());
        }

#if MONO
        private System.Collections.IEnumerator WaitForDialogueController()
#else
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForDialogueController()
#endif
        {
            int retryCount = 0;
            const int maxRetries = 10;

            while (retryCount < maxRetries)
            {
                // Try to get the dialogue controller that should have been added by PrisonNPCManager
                dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);

                if (dialogueController != null)
                {
                    // Set up intake-specific dialogue states
                    dialogueController.AddStateDialogue("Idle", "I'm here to process inmates.",
                        new[] { "Waiting for the next intake.", "Everything's running smoothly.", "Ready for processing." });

                    dialogueController.AddStateDialogue("Processing", "Time to process you.",
                        new[] { "Time to process you." });

                    // Every interactive greeting mirrors the active intake task. Do not
                    // attach generic/random responses here: they can be spoken after the
                    // officer has already advanced to a different station.
                    dialogueController.AddStateDialogue("EscortToHolding", "Come with me to processing.",
                        new[] { "Come with me to processing." });

                    dialogueController.AddStateDialogue("EscortToMugshot", "Walk over and take your mugshot.",
                        new[] { "Walk over and take your mugshot." });

                    dialogueController.AddStateDialogue("EscortToScanner", "Now come here and let us take your fingerprints.",
                        new[] { "Now come here and let us take your fingerprints." });

                    dialogueController.AddStateDialogue("EscortToStorage", "Walk over to storage and collect your prison gear.",
                        new[] { "Walk over to storage and collect your prison gear." });

                    dialogueController.AddStateDialogue("EscortToCell", "Follow me to your cell.",
                        new[] { "Follow me to your cell." });

                    // Action states - show specific instructions when at destination
                    dialogueController.AddStateDialogue("AtMugshot", "Go take your mugshot!",
                        new[] { "Go take your mugshot!" });

                    dialogueController.AddStateDialogue("AtScanner", "Place your hand on the scanner.",
                        new[] { "Place your hand on the scanner." });

                    dialogueController.AddStateDialogue("AtStorage", "Drop your belongings and pick up prison items.",
                        new[] { "Drop your belongings and pick up prison items." });

                    dialogueController.AddStateDialogue("AtCell", "Step inside your cell.",
                        new[] { "Step inside your cell." });

                    dialogueController.AddStateDialogue("AtHolding", "Step out of the cell.",
                        new[] { "Step out of the cell." });

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

        /// <summary>
        /// Performance: Override OnEnable to use custom state update handler
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            AbortPendingIntakeActions();
            base.OnDisable();
        }

        /// <summary>
        /// Custom state update handler that includes intake state machine logic
        /// </summary>
        protected override void OnStateUpdateTick(float currentTime)
        {
            base.OnStateUpdateTick(currentTime);

            // Handle intake state machine
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

#if MONO
            OnStateChanged?.Invoke(newState);
#endif
            ModLogger.Info($"IntakeOfficer: {oldState} → {newState}");

            // Update dialogue state
            ModLogger.Debug($"IntakeOfficer: Calling UpdateDialogueForState({newState}) - dialogueController is {(dialogueController != null ? "available" : "null")}");
            UpdateDialogueForState(newState);

            // Update officer command notification
            UpdateOfficerCommandNotification(newState);

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

            // The visible/interactable greeting must stay aligned with the exact state;
            // replacing it with a generic escort line caused out-of-order instructions.
            string dialogueState = state switch
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

        /// <summary>
        /// Update officer command notification based on current state
        /// </summary>
        private void UpdateOfficerCommandNotification(IntakeState state)
        {
            // Check if we should show a command notification for this state
            if (!ShouldShowCommandNotification(state))
            {
                return;
            }

            try
            {
                var commandData = GetCommandDataForState(state);
                if (commandData != null)
                {
                    Core.ResolveUIManager().UpdateOfficerCommand(commandData);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"IntakeOfficer: Error updating command notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Determine if this state should display a command notification
        /// </summary>
        private bool ShouldShowCommandNotification(IntakeState state)
        {
            return state switch
            {
                IntakeState.WaitingForPlayerExit => true,
                IntakeState.EscortToMugshot => true,
                IntakeState.WaitingForMugshot => true,
                IntakeState.EscortToScanner => true,
                IntakeState.WaitingForScan => true,
                IntakeState.EscortToStorage => true,
                IntakeState.WaitingForStorage => true,
                IntakeState.EscortToCell => true,
                IntakeState.WaitingForCellEntry => true,
                _ => false
            };
        }

        /// <summary>
        /// Get command data for the current state
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private OfficerCommandData? GetCommandDataForState(IntakeState state)
        {
            bool isEscorting = IsCurrentlyEscorting();

            return state switch
            {
                IntakeState.WaitingForPlayerExit => new OfficerCommandData(
                    "INTAKE OFFICER",
                    "Step out of the cell",
                    1, 5, false),

                IntakeState.EscortToMugshot => new OfficerCommandData(
                    "INTAKE OFFICER",
                    "Follow me to the mugshot station",
                    2, 5, true),

                IntakeState.WaitingForMugshot => new OfficerCommandData(
                    "INTAKE OFFICER",
                    isEscorting ? "Follow me to the mugshot station" : "Go take your mugshot!",
                    2, 5, isEscorting),

                IntakeState.EscortToScanner => new OfficerCommandData(
                    "INTAKE OFFICER",
                    "Follow me to the fingerprint scanner",
                    3, 5, true),

                IntakeState.WaitingForScan => new OfficerCommandData(
                    "INTAKE OFFICER",
                    isEscorting ? "Follow me to the scanner" : "Place your hand on the scanner",
                    3, 5, isEscorting),

                IntakeState.EscortToStorage => new OfficerCommandData(
                    "INTAKE OFFICER",
                    "Follow me to storage",
                    4, 5, true),

                IntakeState.WaitingForStorage => new OfficerCommandData(
                    "INTAKE OFFICER",
                    isEscorting ? "Follow me to storage" : "Drop your belongings and pick up prison items",
                    4, 5, isEscorting),

                IntakeState.EscortToCell => new OfficerCommandData(
                    "INTAKE OFFICER",
                    "Follow me to your cell",
                    5, 5, true),

                IntakeState.WaitingForCellEntry => new OfficerCommandData(
                    "INTAKE OFFICER",
                    "Enter your cell",
                    5, 5, false),

                _ => null
            };
        }

        /// <summary>
        /// Hide officer command notification
        /// </summary>
        private void HideOfficerCommandNotification()
        {
            try
            {
                Core.ResolveUIManager().HideOfficerCommand();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"IntakeOfficer: Error hiding command notification: {ex.Message}");
            }
        }

        private void OnStateEnter(IntakeState state)
        {
            switch (state)
            {
                case IntakeState.DelayBeforeFetch:
                    delayDuration = UnityEngine.Random.Range(2f, 4f); // Reduced from 5-10 seconds to 2-4 seconds
                    ModLogger.Info($"IntakeOfficer: Waiting {delayDuration:F1} seconds before fetching prisoner");
                    break;

                case IntakeState.EscortToHolding:
                    NavigateToStation("HoldingCell");
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
                    HideOfficerCommandNotification();
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
                        StopPendingDelayedDoorClose();
                        delayedDoorCloseCoroutine = MelonCoroutines.Start(DelayedDoorClose());
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
                    BeginCellEscortAfterAssignment();
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
                    SendGuardMessage("Step out of the cell.", 3f);
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
            if (currentHoldingCellIndex < 0)
            {
                ModLogger.Error("IntakeOfficer: Cannot continue intake because no holding cell is tracked");
                ChangeIntakeState(IntakeState.ReturningToPost);
                return;
            }

            var jailController = Core.JailController;
            // The disciplinary resume must never begin the next escort while the player is
            // still in the punishment cell. Revalidate here because this runs after the
            // delayed exit check and immediately before the storage route can be selected.
            if (resumingDisciplinaryIntake &&
                jailController != null &&
                jailController.IsPlayerInHoldingCellBounds(currentPrisoner, currentHoldingCellIndex))
            {
                playerExitDetected = false;
                doorCloseInitiated = false;
                ModLogger.Warn($"IntakeOfficer: Disciplinary prisoner is still inside holding cell {currentHoldingCellIndex}; keeping the holding door open before resuming booking");
                ChangeIntakeState(IntakeState.WaitingForPlayerExit);
                return;
            }

            bool doorClosed = jailController?.doorController?.CloseHoldingCellDoor(currentHoldingCellIndex) ?? false;
            if (!doorClosed)
            {
                ModLogger.Error($"IntakeOfficer: Holding cell {currentHoldingCellIndex} must be closed before escorting to mugshot; retrying");
                return;
            }

            if (resumingDisciplinaryIntake)
            {
                ContinueDisciplinaryIntakeFromCheckpoint();
                return;
            }

            ModLogger.Info($"IntakeOfficer: Holding cell {currentHoldingCellIndex} secured; continuing to mugshot");
            ChangeIntakeState(IntakeState.EscortToMugshot);
        }

        /// <summary>
        /// Continues a booking that was interrupted by a staff-assault hold. Completion
        /// flags are retained by BookingProcess, so we select only the first unfinished
        /// station and never replay property or clothing work that already succeeded.
        /// </summary>
        private void ContinueDisciplinaryIntakeFromCheckpoint()
        {
            resumingDisciplinaryIntake = false;
            if (bookingProcess == null)
            {
                ModLogger.Error("IntakeOfficer: Cannot resume disciplinary intake because BookingProcess is unavailable");
                ChangeIntakeState(IntakeState.ReturningToPost);
                return;
            }

            if (!bookingProcess.mugshotComplete)
            {
                ModLogger.Info("IntakeOfficer: Resuming disciplinary intake at mugshot");
                ChangeIntakeState(IntakeState.EscortToMugshot);
                return;
            }

            if (!bookingProcess.fingerprintComplete)
            {
                ModLogger.Info("IntakeOfficer: Resuming disciplinary intake at fingerprint scanner");
                ChangeIntakeState(IntakeState.EscortToScanner);
                return;
            }

            if (!bookingProcess.prisonGearPickupComplete)
            {
                ModLogger.Info("IntakeOfficer: Resuming disciplinary intake at storage");
                ChangeIntakeState(IntakeState.EscortToStorage);
                return;
            }

            ModLogger.Info("IntakeOfficer: All booking stations were complete before disciplinary hold; resuming at cell escort");
            BeginCellEscortAfterAssignment();
        }

        private void HandleOpeningCellDoorState()
        {
            // Door opening should complete quickly, then wait for player entry
            SendGuardMessage("Step inside your cell.", 2f);
            ChangeIntakeState(IntakeState.WaitingForCellEntry);
        }

        private void HandleClosingCellDoorState()
        {
            // Door closing should complete quickly, then return to post
            SendGuardMessage("Processing complete.", 3f);
            CloseCellDoor();

            // A disciplinary repeat can resume directly at the cell escort after all
            // stations are complete. That route intentionally bypasses the legacy escort
            // monitor, so finalize the booking only after the prisoner is actually secured.
            bookingProcess?.FinishBookingAfterCellEscort(currentPrisoner);
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
                    HandleDestinationReached(currentDestination);
                    return;
                }

                // NO TIMEOUT - let officer take as long as needed from far cells
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

            // The mugshot order is the first transition the player has to read
            // and act on.  Keep the officer posted long enough for its command
            // to be seen before starting the escort walk.
            if (stationName == "MugshotStation")
            {
                StopPendingMugshotEscort();
                mugshotEscortCoroutine = MelonCoroutines.Start(
                    AnnounceMugshotThenStartEscort(doorPoint, station.guardMessage, station.messageDuration));
                ModLogger.Info("IntakeOfficer: Holding position for the mugshot instruction");
                return;
            }

            // Navigate to station
            MoveTo(doorPoint.position);

            // Send guard message
            SendGuardMessage(station.guardMessage, station.messageDuration);

#if MONO
            OnStationReached?.Invoke(stationName);
#endif
            ModLogger.Info($"IntakeOfficer: Navigating to {stationName}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator AnnounceMugshotThenStartEscort(Transform doorPoint, string message, float duration)
        {
            yield return WaitForPlayerFacingThenSendGuardMessage(message, duration);
            yield return new WaitForSeconds(3.5f);

            if (currentState != IntakeState.EscortToMugshot || doorPoint == null)
            {
                mugshotEscortCoroutine = null;
                yield break;
            }

            MoveTo(doorPoint.position);
#if MONO
            OnStationReached?.Invoke("MugshotStation");
#endif
            ModLogger.Info("IntakeOfficer: Beginning escort to MugshotStation after instruction hold");
            mugshotEscortCoroutine = null;
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

                // Keep the intake active until the officer is physically back at the post.
                // A release cannot safely begin while this officer still owns the cell-return
                // portion of the booking flow.
                CloseAllIntakeDoors();
                return;
            }

            ModLogger.Warn("IntakeOfficer: Guard post was unavailable; completing intake without a return walk");
            CloseAllIntakeDoors();
            CompleteIntakeProcess();
        }

        #endregion

        #region Door Timing

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        private IEnumerator DelayedDoorClose()
        {
            if (doorCloseInitiated) yield break; // Prevent multiple coroutines
            doorCloseInitiated = true;

            yield return new WaitForSeconds(2f); // Give player time to fully exit
            delayedDoorCloseCoroutine = null;
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
            var securityDoor = BBHelpers.GetComponentSafe<SecurityDoorBehavior>(gameObject);
            if (securityDoor != null) return securityDoor;

            // Fallback to JailController (centralized SecurityDoor)
            return Core.JailController != null
                ? BBHelpers.GetComponentSafe<SecurityDoorBehavior>(Core.JailController.gameObject)
                : null;
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
                // Normal booking reaches storage through this door. A disciplinary direct
                // cell escort starts back in booking, so open the booking door first.
                if (requiresBookingInnerDoorBeforeCellEscort && !triggeredDoorOperations.Contains("BookingInnerDoor"))
                {
                    TriggerBookingInnerDoorIfNeeded();
                    return;
                }

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
            StopPendingDelayedNavigationResume();
            delayedNavigationResumeCoroutine = MelonCoroutines.Start(DelayedNavigationResume());
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
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
            delayedNavigationResumeCoroutine = null;
        }

        private void HandleSecurityDoorOperationFailed(string doorName)
        {
            ModLogger.Error($"IntakeOfficer: SecurityDoor operation FAILED for {doorName} - attempting fallback");

            // SecurityDoor owns the NavMeshAgent while crossing the threshold.  A
            // failed operation previously left this flag true after the direct-door
            // fallback opened the door, so the intake state machine ignored every
            // subsequent destination update and the officer remained at the entry
            // point.  Return ownership before resuming the canonical escort route.
            isSecurityDoorActive = false;
            lastDoorOperationTime = Time.time;

            // If SecurityDoor fails, try fallback direct door control
            if (doorName.Contains("Booking") || doorName.Contains("Inner"))
            {
                FallbackDirectDoorControl("BookingInnerDoor");
            }
            else if (doorName.Contains("Prison") || doorName.Contains("Enter"))
            {
                FallbackDirectDoorControl("PrisonEntryDoor");
            }

            // The fallback only operates the door; it deliberately does not move
            // the guard.  Resume the state-owned destination after the door has had
            // a frame to apply its unlocked/open state to the NavMesh route.
            StopPendingDelayedNavigationResume();
            delayedNavigationResumeCoroutine = MelonCoroutines.Start(DelayedNavigationResume());
            ModLogger.Info($"IntakeOfficer: SecurityDoor fallback complete for {doorName}; scheduling escort route resume");
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

            if (securityDoor.IsBusy())
            {
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

            if (securityDoor.IsBusy())
            {
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
                    SendGuardMessage("Store your belongings and collect your prison gear.", 3f);
                    ChangeIntakeState(IntakeState.WaitingForStorage);
                    break;

                case IntakeState.EscortToCell:
                    // Just proceed - NavAgent destination reached means we're close enough
                    ChangeIntakeState(IntakeState.OpeningCellDoor);
                    break;

                case IntakeState.ReturningToPost:
                    // Start continuous rotation when back at post
                    StartContinuousPlayerLooking();
                    CompleteIntakeProcess();
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
                HandleDestinationReached(currentDestination);
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
                StopPendingRetryIntake();
                retryIntakeCoroutine = MelonCoroutines.Start(RetryIntakeAfterDelay(player, 5f));
                return;
            }

            currentPrisoner = player;
            assignedCellNumber = -1;
            nextCellAssignmentRetryTime = 0f;

            // Reset state tracking flags for new intake
            playerExitDetected = false;
            doorCloseInitiated = false;

            // Reset door tracking for new intake process
            ResetDoorTracking();

            // Reset SecurityDoor state
            isSecurityDoorActive = false;

            // Determine which holding cell contains this player using JailController's centralized method.
            // A disciplinary repeat intake supplies a named cell so this is never redirected by
            // prefab traversal order or a stale previous holding-cell cache.
            var jailController = Core.JailController;
            if (requiredHoldingCellPrisoner == player && !string.IsNullOrEmpty(requiredHoldingCellName))
            {
                currentHoldingCellIndex = jailController?.GetHoldingCellRuntimeIndexByName(requiredHoldingCellName) ?? -1;
                if (currentHoldingCellIndex < 0 || !jailController.IsPlayerInHoldingCellBounds(player, currentHoldingCellIndex))
                {
                    ModLogger.Error($"IntakeOfficer: Disciplinary repeat intake requires {requiredHoldingCellName}, but {player.name} is not inside that holding cell");
                    ClearRequiredHoldingCell();
                    OfficerCoordinator.Instance.UnregisterEscort(this);
                    currentPrisoner = null;
                    return;
                }

                currentHoldingCellName = requiredHoldingCellName;
                ModLogger.Info($"IntakeOfficer: Using required disciplinary holding cell {currentHoldingCellName} (runtime index {currentHoldingCellIndex}) for {player.name}");
                ClearRequiredHoldingCell();
            }
            else
            {
                currentHoldingCellIndex = jailController?.FindPlayerHoldingCell(player) ?? -1;
                currentHoldingCellName = currentHoldingCellIndex >= 0 && jailController != null && currentHoldingCellIndex < jailController.holdingCells.Count
                    ? jailController.holdingCells[currentHoldingCellIndex].cellTransform?.name ?? ""
                    : "";
            }

            if (currentHoldingCellIndex == -1)
            {
                ModLogger.Error($"IntakeOfficer: Could not find player {player.name} in any holding cell");
                OfficerCoordinator.Instance.UnregisterEscort(this);
                currentPrisoner = null;
                return;
            }

            ModLogger.Info($"IntakeOfficer: Player {player.name} found in holding cell {currentHoldingCellIndex} ({currentHoldingCellName})");
#if MONO
            OnIntakeStarted?.Invoke(player);
#endif

            ChangeIntakeState(IntakeState.DelayBeforeFetch);
            ModLogger.Info($"IntakeOfficer: Starting intake process for {player.name}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator RetryIntakeAfterDelay(Player player, float delay)
        {
            yield return new WaitForSeconds(delay);

            retryIntakeCoroutine = null;

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
                BeginCellEscortAfterAssignment();
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
                    if (!string.IsNullOrEmpty(currentHoldingCellName))
                    {
                        var guardPoint = jailController.GetGuardPoint(currentHoldingCellName);
                        if (guardPoint != null)
                        {
                            ModLogger.Info($"Using assigned holding-cell guard point for {currentHoldingCellName}");
                            return guardPoint;
                        }

                        if (currentHoldingCellIndex >= 0 && currentHoldingCellIndex < jailController.holdingCells.Count)
                        {
                            var holdingCellDoorPoint = jailController.holdingCells[currentHoldingCellIndex].cellDoor?.doorPoint;
                            if (holdingCellDoorPoint != null)
                            {
                                ModLogger.Info($"Using holding-cell door point from {currentHoldingCellName}");
                                return holdingCellDoorPoint;
                            }
                        }
                    }

                    ModLogger.Error("IntakeOfficer: No targeted holding-cell guard point was available");
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

        private void BeginCellEscortAfterAssignment()
        {
            if (Time.time < nextCellAssignmentRetryTime)
            {
                return;
            }

            if (TryAssignPrisonerCell())
            {
                ChangeIntakeState(IntakeState.EscortToCell);
                return;
            }

            nextCellAssignmentRetryTime = Time.time + CellAssignmentRetryInterval;
            SendGuardMessage("Remain here while I assign your cell.", CellAssignmentRetryInterval);
            ModLogger.Warn($"IntakeOfficer: Cell assignment unavailable; retaining {currentPrisoner?.name} at storage and retrying in {CellAssignmentRetryInterval:F0}s");
        }

        private bool TryAssignPrisonerCell()
        {
            if (currentPrisoner == null)
            {
                return false;
            }

            var cellManager = Core.ResolveCellAssignmentManager();
            if (cellManager != null)
            {
                assignedCellNumber = cellManager.AssignPlayerToCell(currentPrisoner);
                if (assignedCellNumber >= 0)
                {
                    ModLogger.Debug($"Assigned prisoner to cell {assignedCellNumber}");
                    return true;
                }

                ModLogger.Error("Failed to assign cell to prisoner; no fallback cell will be used");
            }
            else
            {
                ModLogger.Error("CellAssignmentManager not available");
            }

            assignedCellNumber = -1;
            nextCellAssignmentRetryTime = 0f;
            return false;
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
            // A command must be emitted once. The previous follow-up contextual
            // message selected a random interaction from the state and could
            // contradict the command that just advanced the intake process.
            // Do not snap the player toward the officer.  The command is held
            // until a short, gentle turn has completed, unless the player is
            // already facing the officer.  Mugshot positioning remains owned
            // by MugshotStation and is intentionally not involved here.
            StopPendingPlayerFacingCommand();

            playerFacingCommandCoroutine = MelonCoroutines.Start(
                WaitForPlayerFacingThenSendGuardMessage(message, duration));
        }

        private void StopPendingPlayerFacingCommand()
        {
            if (playerFacingCommandCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(playerFacingCommandCoroutine);
            playerFacingCommandCoroutine = null;
        }

        private void StopPendingMugshotEscort()
        {
            if (mugshotEscortCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(mugshotEscortCoroutine);
            mugshotEscortCoroutine = null;
        }

        private void StopPendingDelayedDoorClose()
        {
            if (delayedDoorCloseCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(delayedDoorCloseCoroutine);
            delayedDoorCloseCoroutine = null;
        }

        private void StopPendingDelayedNavigationResume()
        {
            if (delayedNavigationResumeCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(delayedNavigationResumeCoroutine);
            delayedNavigationResumeCoroutine = null;
        }

        private void StopPendingRetryIntake()
        {
            if (retryIntakeCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(retryIntakeCoroutine);
            retryIntakeCoroutine = null;
        }

        /// <summary>
        /// Tears down all actions owned by the interrupted intake session. State fields alone
        /// are not enough: Melon coroutines and an active NavMesh path can continue to issue
        /// the prior prisoner's command after the officer appears to have returned to post.
        /// </summary>
        private void AbortPendingIntakeActions()
        {
            StopPendingPlayerFacingCommand();
            StopPendingMugshotEscort();
            StopPendingDelayedDoorClose();
            StopPendingDelayedNavigationResume();
            StopPendingRetryIntake();
            StopContinuousPlayerLooking();

            // SecurityDoor owns a separate escort coroutine.  Merely clearing the local
            // tracking flag leaves that coroutine alive, where it can continue to prompt
            // the prisoner to go through the old corridor door during a disciplinary hold.
            var securityDoor = GetSecurityDoor();
            if (securityDoor != null && securityDoor.IsBusy())
            {
                securityDoor.StopDoorOperation();
                ModLogger.Info("IntakeOfficer: Stopped the active security-door escort while cancelling intake");
            }

            isSecurityDoorActive = false;
            destinationPosition = transform.position;
            StopMovement();
            HideOfficerCommandNotification();
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForPlayerFacingThenSendGuardMessage(string message, float duration)
        {
            if (currentPrisoner != null)
            {
                Vector3 directionToOfficer = transform.position - currentPrisoner.transform.position;
                directionToOfficer.y = 0f;

                if (directionToOfficer.sqrMagnitude >= 0.001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(directionToOfficer.normalized, Vector3.up);
                    float initialAngle = Quaternion.Angle(currentPrisoner.transform.rotation, targetRotation);

                    // Avoid an unnecessary camera movement when the player is
                    // already looking at the officer.
                    if (initialAngle > 4f)
                    {
                        float turnDuration = Mathf.Clamp(initialAngle / 220f, 0.18f, 0.65f);
                        Quaternion startingRotation = currentPrisoner.transform.rotation;
                        float elapsed = 0f;

                        while (elapsed < turnDuration && currentPrisoner != null)
                        {
                            elapsed += Time.deltaTime;
                            float progress = Mathf.Clamp01(elapsed / turnDuration);
                            progress = progress * progress * (3f - 2f * progress);
                            currentPrisoner.transform.rotation = Quaternion.Slerp(
                                startingRotation,
                                targetRotation,
                                progress);
                            yield return null;
                        }

                        if (currentPrisoner != null)
                        {
                            currentPrisoner.transform.rotation = targetRotation;
                        }
                    }
                }
            }

            EmitGuardMessage(message, duration);
            playerFacingCommandCoroutine = null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void EmitGuardMessage(string message, float duration)
        {

            if (dialogueController != null)
            {
                dialogueController.SendGuardCommand(
                    JailNPCAudioController.GuardCommandType.Move,
                    message,
                    useRadio: false);
                return;
            }

            TrySendNPCMessage(message, duration);
        }


        private void CompleteIntakeProcess()
        {
#if MONO
            OnIntakeCompleted?.Invoke(currentPrisoner);
#endif

            // Unregister from officer coordination
            OfficerCoordinator.Instance.UnregisterEscort(this);

            // Reset state
            currentPrisoner = null;
            assignedCellNumber = -1;
            nextCellAssignmentRetryTime = 0f;
            currentTargetStation = "";
            currentHoldingCellIndex = -1;
            currentHoldingCellName = "";
            ClearRequiredHoldingCell();
            requiresBookingInnerDoorBeforeCellEscort = false;

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
        /// Requires the next booking event for this prisoner to start from the supplied holding
        /// cell. Used after a disciplinary hold so the canonical intake state machine fetches
        /// the player from the actual punishment cell rather than the first holding-cell door.
        /// </summary>
        public bool PrepareDisciplinaryRepeatIntake(Player player, string holdingCellName)
        {
            var jailController = Core.JailController;
            int holdingCellIndex = jailController?.GetHoldingCellRuntimeIndexByName(holdingCellName) ?? -1;
            if (player == null || currentState != IntakeState.Idle || holdingCellIndex < 0 ||
                !jailController.IsPlayerInHoldingCellBounds(player, holdingCellIndex))
            {
                ModLogger.Error($"IntakeOfficer: Cannot prepare disciplinary repeat intake from {holdingCellName}; officer idle={currentState == IntakeState.Idle}, holding index={holdingCellIndex}");
                return false;
            }

            requiredHoldingCellPrisoner = player;
            requiredHoldingCellName = holdingCellName;
            resumingDisciplinaryIntake = true;
            requiresBookingInnerDoorBeforeCellEscort = true;
            ModLogger.Info($"IntakeOfficer: Prepared repeat intake for {player.name} from {holdingCellName} (runtime index {holdingCellIndex})");
            return true;
        }

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
        /// Cancel active intake process for new arrest (clears state completely)
        /// </summary>
        public void CancelIntake()
        {
            ModLogger.Info($"IntakeOfficer: Canceling active intake for {currentPrisoner?.name}");

            // Unregister from officer coordination
            OfficerCoordinator.Instance.UnregisterEscort(this);

            // Stop every pending command/escort before releasing the prisoner reference.
            // This is required when disciplinary lockdown interrupts the officer between
            // its mugshot instruction and the delayed walk to that station.
            AbortPendingIntakeActions();

            // AbortPendingIntakeActions stops the moving escort, but the route may already
            // have opened a holding or shared booking door. Secure the exact tracked route
            // before its indices are reset so an interrupted intake never leaves an open
            // cell or corridor behind.
            CloseAllIntakeDoors();

            // Reset all state
            currentPrisoner = null;
            assignedCellNumber = -1;
            nextCellAssignmentRetryTime = 0f;
            currentTargetStation = "";
            currentHoldingCellIndex = -1;
            currentHoldingCellName = "";
            ClearRequiredHoldingCell();
            resumingDisciplinaryIntake = false;
            requiresBookingInnerDoorBeforeCellEscort = false;
            playerExitDetected = false;
            doorCloseInitiated = false;
            ResetDoorTracking();

            // Return to idle immediately
            ChangeIntakeState(IntakeState.Idle);

            ModLogger.Info("IntakeOfficer: Intake canceled - now available for new prisoner");
        }

        private void ClearRequiredHoldingCell()
        {
            requiredHoldingCellPrisoner = null;
            requiredHoldingCellName = "";
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

#if MONO
        private System.Collections.IEnumerator ContinuousPlayerLookingCoroutine()
#else
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ContinuousPlayerLookingCoroutine()
#endif
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

#if MONO
        private System.Collections.IEnumerator SmoothRotateToTarget(Quaternion targetRotation, float duration)
#else
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator SmoothRotateToTarget(Quaternion targetRotation, float duration)
#endif
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
            AbortPendingIntakeActions();

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
                securityDoor.RemoveDoorOperationCompleteListener(HandleSecurityDoorOperationComplete);
                securityDoor.RemoveDoorOperationFailedListener(HandleSecurityDoorOperationFailed);
            }

            // Movement completion is handled via BaseJailNPC.NotifyDestinationReached override.
        }

        protected override void NotifyDestinationReached(Vector3 destination)
        {
            base.NotifyDestinationReached(destination);
            HandleDestinationReached(destination);
        }

        #endregion

    }
}
