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

        /// <summary>
        /// Detailed intake workflow state. This state is authoritative for booking progression; the
        /// inherited <see cref="BaseJailNPC.NPCState"/> is retained only for shared movement plumbing.
        /// </summary>
        public enum IntakeState
        {
            /// <summary>Officer is available at the booking post.</summary>
            Idle,                    // At Booking/GuardPoint[0]
            /// <summary>Officer is waiting for the booking process to announce a prisoner.</summary>
            WaitingForBooking,       // Monitoring for booking event
            /// <summary>Officer waits the configured short delay before fetching the prisoner.</summary>
            DelayBeforeFetch,        // 5-10 second random delay
            /// <summary>Officer navigates to the tracked holding cell.</summary>
            EscortToHolding,         // Walk to holding cell
            /// <summary>Officer is requesting the tracked holding-cell door to open.</summary>
            OpeningHoldingDoor,      // Open holding cell door
            /// <summary>Officer waits until the prisoner clears the holding-cell doorway.</summary>
            WaitingForPlayerExit,    // Check holding cell bounds
            /// <summary>Officer is securing the holding-cell door before the next station.</summary>
            ClosingHoldingDoor,      // Close holding cell door
            /// <summary>Officer navigates to the mugshot station.</summary>
            EscortToMugshot,         // Navigate to mugshot station
            /// <summary>Officer waits for the booking process to complete the mugshot.</summary>
            WaitingForMugshot,       // BookingProcess.mugshotComplete
            /// <summary>Officer navigates to the fingerprint scanner.</summary>
            EscortToScanner,         // Navigate to scanner station
            /// <summary>Officer waits for the booking process to complete fingerprinting.</summary>
            WaitingForScan,          // BookingProcess.fingerprintComplete
            /// <summary>Officer navigates to the storage area.</summary>
            EscortToStorage,         // Navigate to storage area
            /// <summary>Officer waits for prison-gear pickup to complete.</summary>
            WaitingForStorage,       // BookingProcess.inventoryDropOffComplete
            /// <summary>Officer navigates to the assigned cell.</summary>
            EscortToCell,            // Navigate to assigned cell
            /// <summary>Officer is requesting the assigned cell door to open.</summary>
            OpeningCellDoor,         // Open jail cell door
            /// <summary>Officer waits until the prisoner enters the assigned cell bounds.</summary>
            WaitingForCellEntry,     // Check cell bounds
            /// <summary>Officer is securing the assigned cell door.</summary>
            ClosingCellDoor,         // Close jail cell door
            /// <summary>Officer returns to the booking/guard post after the workflow finishes.</summary>
            ReturningToPost          // Back to guard point
        }

        [System.Serializable]
        public class IntakeStation
        {
            /// <summary>Stable key used to select this station from the intake state machine.</summary>
            public string stationName;
            /// <summary>Name used to resolve the station's door/navigation point.</summary>
            public string doorPointName;
            /// <summary>Instruction sent before the officer waits or escorts at this station.</summary>
            public string guardMessage;
            /// <summary>World-space dialogue display duration, in seconds.</summary>
            public float messageDuration = 3f;
#if MONO
            /// <summary>
            /// Optional MONO-only completion predicate. IL2CPP intentionally does not expose this delegate
            /// surface; IL2CPP completion is driven by the native booking flags handled by this class.
            /// </summary>
            public System.Func<bool> completionCheck;
#endif
        }

        #endregion

        #region Component References

        /// <summary>Native booking process whose completion flags and events drive intake progression.</summary>
        private BookingProcess bookingProcess;

        #endregion

        #region State Variables

#if MONO
        [SerializeField]
#endif
        /// <summary>
        /// Authoritative detailed intake state. The <c>new</c> declaration intentionally shadows the base
        /// coarse state; <see cref="Start"/> saves/restores it around base initialization.
        /// </summary>
        private new IntakeState currentState = IntakeState.Idle;
        /// <summary>The prisoner currently owned by this intake workflow, or null while idle.</summary>
        private Player currentPrisoner;
        /// <summary>Resolved booking/guard post used when returning the officer after intake.</summary>
        private Transform guardPostTransform;
        /// <summary>Assigned jail-cell number for the current prisoner, or -1 until assignment succeeds.</summary>
        private int assignedCellNumber = -1;
        /// <summary>Runtime holding-cell index containing the current prisoner, or -1 when none is tracked.</summary>
        private int currentHoldingCellIndex = -1;  // Which holding cell contains the current prisoner
        /// <summary>Stable holding-cell name retained for logs and disciplinary resume decisions.</summary>
        private string currentHoldingCellName = "";
        /// <summary>Prisoner reserved for a disciplinary repeat intake from a specific holding cell.</summary>
        private Player requiredHoldingCellPrisoner;
        /// <summary>Holding-cell name required by the pending disciplinary repeat intake.</summary>
        private string requiredHoldingCellName = "";
        /// <summary>True while resuming a prior booking and preserving completed station flags.</summary>
        private bool resumingDisciplinaryIntake;
        // A punishment-cell repeat starts on the booking side of the inner corridor door.
        // A direct cell escort must therefore traverse that door before the prison-entry door.
        /// <summary>
        /// Indicates that a disciplinary repeat must cross the booking inner door before the prison-entry
        /// door. This route-specific flag is cleared when the repeat checkpoint is resumed.
        /// </summary>
        private bool requiresBookingInnerDoorBeforeCellEscort;

        /// <summary>Ordered secured-door legs used after a completed cell escort.</summary>
        private enum ReturnTransitStage
        {
            None,
            PrisonToHall,
            HallToBooking,
            MovingToPost
        }

        /// <summary>Current leg of the officer-only return route through the intake corridor.</summary>
        private ReturnTransitStage returnTransitStage = ReturnTransitStage.None;
        /// <summary>True after the prisoner reaches a cell and the officer must traverse both corridor doors.</summary>
        private bool requiresSecuredReturnTransit;

        /// <summary>Prevents repeated holding-cell exit handling before the door is secured.</summary>
        private bool playerExitDetected = false;
        /// <summary>Tracks whether the current holding/cell door close request has already begun.</summary>
        private bool doorCloseInitiated = false;
        /// <summary>Unity time at which the prisoner first achieved full doorway clearance.</summary>
        private float holdingExitConfirmationStart = -1f;

        /// <summary>Unity-time start of the detailed intake state; shadows the base state timestamp.</summary>
        private new float stateStartTime;
        /// <summary>Randomized delay used by <see cref="IntakeState.DelayBeforeFetch"/>.</summary>
        private float delayDuration;
        /// <summary>Next Unity-time retry for cell assignment after booking data is not ready.</summary>
        private float nextCellAssignmentRetryTime;
        /// <summary>Minimum interval between cell-assignment retries, in Unity seconds.</summary>
        private const float CellAssignmentRetryInterval = 2f;
        /// <summary>Continuous clearance required before the holding-cell door may close.</summary>
        private const float HoldingExitConfirmationSeconds = 0.5f;

        /// <summary>Station definitions keyed by the names used by navigation and dialogue mapping.</summary>
        private Dictionary<string, IntakeStation> intakeStations;
        /// <summary>Current station key used for destination and UI command de-duplication.</summary>
        private string currentTargetStation = "";

        /// <summary>Optional dialogue surface initialized after the native NPC graph is ready.</summary>
        private JailNPCDialogueController dialogueController;
        /// <summary>Legacy escort marker retained for current state/UI distance decisions.</summary>
        private bool isEscorting = false;
        /// <summary>Last station/cell destination used by escort distance checks.</summary>
        private Vector3 destinationPosition;

        /// <summary>Opaque handle for the global continuous-looking coroutine.</summary>
        private object continuousLookingCoroutine;
        /// <summary>Opaque handle for the delayed player-facing command coroutine.</summary>
        private object playerFacingCommandCoroutine;
        /// <summary>Opaque handle for the mugshot announcement/escort coroutine.</summary>
        private object mugshotEscortCoroutine;
        /// <summary>Opaque handle reserved for delayed door closure work.</summary>
        private object delayedDoorCloseCoroutine;
        /// <summary>Opaque handle for resuming navigation after a native door operation.</summary>
        private object delayedNavigationResumeCoroutine;
        /// <summary>Opaque handle for a deferred retry of the booking event.</summary>
        private object retryIntakeCoroutine;
        /// <summary>Opaque handle for fallback direct-door closure after escort clearance.</summary>
        private object fallbackDoorCloseCoroutine;

        /// <summary>Tracks which station destinations have already fired their arrival transition.</summary>
        private Dictionary<string, bool> stationDestinationProcessed = new Dictionary<string, bool>();

        #endregion

        #region Events

#if MONO
        /// <summary>Raised after the detailed intake state changes; MONO-only delegate surface.</summary>
        public new System.Action<IntakeState> OnStateChanged;
        /// <summary>Raised when a prisoner enters the intake workflow; MONO-only.</summary>
        public System.Action<Player> OnIntakeStarted;
        /// <summary>Raised after the prisoner is secured and intake state is reset; MONO-only.</summary>
        public System.Action<Player> OnIntakeCompleted;
        /// <summary>Raised when the officer confirms arrival at a named station; MONO-only.</summary>
        public System.Action<string> OnStationReached;
#endif

        #endregion

        #region Initialization

        /// <summary>
        /// Runs the base component discovery. Security-door lookup is intentionally deferred until the
        /// JailController is available, so startup does not cache a stale scene reference.
        /// </summary>
        protected override void Awake()
        {
            base.Awake(); // Initialize BaseJailNPC
            // SecurityDoor will be retrieved from JailController when needed
        }

        /// <summary>
        /// Completes intake initialization after the base class has resolved shared components. The detailed
        /// intake state is saved around <see cref="BaseJailNPC.Start"/> because this class intentionally
        /// shadows the base coarse state field.
        /// </summary>
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

        /// <summary>
        /// Installs the security-door surface and enters the authoritative detailed idle state after base
        /// component/avatar initialization has completed.
        /// </summary>
        protected override void InitializeNPC()
        {
            // Ensure SecurityDoorBehavior component is attached
            EnsureSecurityDoorComponent();

            // IntakeOfficer-specific initialization
            ChangeIntakeState(IntakeState.Idle);
        }

        /// <summary>
        /// Ensures exactly one safe <see cref="SecurityDoorBehavior"/> is attached. Door references remain
        /// resolved by the JailController when operations are requested.
        /// </summary>
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

        /// <summary>
        /// Builds the canonical station table used by escort navigation, guard messages, and state-to-UI
        /// command mapping. Station keys are workflow identifiers, not scene object names by themselves.
        /// </summary>
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

        /// <summary>Resolves the first booking guard spawn as the officer's return post.</summary>
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

        /// <summary>
        /// Subscribes once to booking and security-door completion/failure events. Matching removal occurs in
        /// <see cref="OnDestroy"/>; movement completion is delivered by the base destination hook.
        /// </summary>
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

        /// <summary>Starts bounded retry initialization for the optional dialogue controller.</summary>
        private void InitializeDialogueSystem()
        {
            // Use a coroutine to retry getting the dialogue controller
            MelonLoader.MelonCoroutines.Start(WaitForDialogueController());
        }

#if MONO
        /// <summary>
        /// Waits briefly for <see cref="JailNPCDialogueController"/>, then installs intake-specific dialogue
        /// states. The retry is bounded so a missing controller cannot leave an untracked global coroutine.
        /// </summary>
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
        /// Re-registers the intake officer with the base throttled update manager. Intake-specific work is
        /// appended by <see cref="OnStateUpdateTick"/> after the base movement/state tick.
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
        }

        /// <summary>
        /// Stops pending intake commands and then unregisters from the base update manager. This prevents a
        /// disabled officer from retaining door, escort, or dialogue coroutine work.
        /// </summary>
        protected override void OnDisable()
        {
            AbortPendingIntakeActions();
            base.OnDisable();
        }

        /// <summary>
        /// Runs the base throttled tick, then the authoritative detailed intake state machine and escort-door
        /// trigger polling. The detailed state—not the inherited coarse state—determines booking progression.
        /// </summary>
        /// <param name="currentTime">Unity-time value supplied by the base update manager.</param>
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

        /// <summary>
        /// Dispatches each detailed intake state to its handler. Escort states share movement completion,
        /// while waiting/door states advance only from their corresponding native booking or door condition.
        /// </summary>
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

        /// <summary>
        /// Performs an intake-state transition in the fixed order: assign timestamp, notify MONO listeners,
        /// update dialogue/UI, then execute state-entry side effects. Re-entering the same state is ignored.
        /// </summary>
        /// <param name="newState">Detailed workflow state to enter.</param>
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

        /// <summary>
        /// Maps the detailed intake state to the exact dialogue state shown to the player. Escort and action
        /// labels are intentionally state-specific so an old generic instruction cannot outlive a transition.
        /// </summary>
        /// <param name="state">Detailed intake state whose dialogue should be selected.</param>
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

        /// <summary>
        /// Reports whether the officer is in an escort-adjacent state and still more than three world units
        /// from its tracked destination. This feeds command wording, not workflow progression.
        /// </summary>
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
        /// Updates the officer-command surface for states that require a player instruction. This surface is
        /// the authoritative higher-priority instruction channel; the tier-status UI must yield while it is
        /// visible rather than replacing these state-specific commands.
        /// </summary>
        /// <param name="state">Detailed intake state to render.</param>
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

        /// <summary>Returns whether the supplied intake state has a player-facing officer command.</summary>
        /// <param name="state">Detailed intake state to test.</param>
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
        /// Creates command data for the supplied state, including whether the player is still being escorted.
        /// This helper is hidden from IL2CPP because its nullable/value-object surface is consumed internally.
        /// </summary>
        /// <param name="state">Detailed intake state to translate.</param>
        /// <returns>Command data for instruction-bearing states; null for states without an instruction.</returns>
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

        /// <summary>Hides the higher-priority officer-command surface when intake returns to its post.</summary>
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

        /// <summary>
        /// Performs state-entry side effects such as station navigation, cell-door opening, return-to-post,
        /// and command cleanup. Waiting states intentionally do not start new navigation here.
        /// </summary>
        /// <param name="state">Detailed state just entered.</param>
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

                case IntakeState.WaitingForPlayerExit:
                    holdingExitConfirmationStart = -1f;
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

        /// <summary>Holds the officer at the booking post while the booking event subscription remains active.</summary>
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

        /// <summary>Advances from the short fetch delay to the holding-cell escort when its Unity-time window expires.</summary>
        private void HandleDelayState()
        {
            if (Time.time - stateStartTime >= delayDuration)
            {
                ModLogger.Info($"IntakeOfficer: Delay completed ({delayDuration:F1}s), transitioning to EscortToHolding");
                ChangeIntakeState(IntakeState.EscortToHolding);
            }
        }

        /// <summary>
        /// Waits for the current prisoner to clear the tracked holding-cell doorway, then permits the
        /// holding door to close exactly once. A missing prisoner returns the officer to idle.
        /// </summary>
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
                    bool hasClearedDoorway = jailController.HasPlayerExitedHoldingCell(
                        currentPrisoner,
                        currentHoldingCellIndex);
                    if (!hasClearedDoorway)
                    {
                        // Stepping back into the doorway cancels the pending close rather than
                        // allowing a stale timer to secure the door around the player.
                        holdingExitConfirmationStart = -1f;
                        return;
                    }

                    if (holdingExitConfirmationStart < 0f)
                    {
                        holdingExitConfirmationStart = Time.time;
                        ModLogger.Debug(
                            $"IntakeOfficer: Player fully cleared holding cell {currentHoldingCellIndex}; " +
                            $"confirming exit for {HoldingExitConfirmationSeconds:F2}s");
                        return;
                    }

                    if (Time.time - holdingExitConfirmationStart >= HoldingExitConfirmationSeconds)
                    {
                        playerExitDetected = true;
                        doorCloseInitiated = true;
                        holdingExitConfirmationStart = -1f;
                        ModLogger.Info($"IntakeOfficer: Player cleared holding cell {currentHoldingCellIndex} doorway");

                        // Full-body clearance plus a continuous confirmation window prevents
                        // a hesitant player from being caught by the closing door.
                        ChangeIntakeState(IntakeState.ClosingHoldingDoor);
                    }
                }
            }
        }

        /// <summary>Waits for the native booking process to set its mugshot-complete flag.</summary>
        private void HandleWaitingForMugshotState()
        {
            if (bookingProcess != null && bookingProcess.mugshotComplete)
            {
                ChangeIntakeState(IntakeState.EscortToScanner);
            }
        }

        /// <summary>Waits for the native booking process to set its fingerprint-complete flag.</summary>
        private void HandleWaitingForScanState()
        {
            if (bookingProcess != null && bookingProcess.fingerprintComplete)
            {
                ChangeIntakeState(IntakeState.EscortToStorage);
            }
        }

        /// <summary>Waits for prison-gear pickup, periodically logging the pending native completion condition.</summary>
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

        /// <summary>Waits for the prisoner to enter the assigned cell bounds before securing its door.</summary>
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

        /// <summary>Opens the tracked holding-cell door and transitions to the doorway-exit wait.</summary>
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

        /// <summary>
        /// Revalidates disciplinary-cell exit, closes the holding door, and continues at the first unfinished
        /// booking station or the normal mugshot route.
        /// </summary>
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

        /// <summary>Announces the cell-entry instruction and begins waiting for the prisoner to enter.</summary>
        private void HandleOpeningCellDoorState()
        {
            // Door opening should complete quickly, then wait for player entry
            SendGuardMessage("Step inside your cell.", 2f);
            ChangeIntakeState(IntakeState.WaitingForCellEntry);
        }

        /// <summary>
        /// Secures the assigned cell, finalizes native booking after the prisoner is actually inside, and
        /// returns the officer to the post workflow.
        /// </summary>
        private void HandleClosingCellDoorState()
        {
            // Door closing should complete quickly, then return to post
            SendGuardMessage("Processing complete.", 3f);
            CloseCellDoor();

            // A disciplinary repeat can resume directly at the cell escort after all
            // stations are complete. That route intentionally bypasses the legacy escort
            // monitor, so finalize the booking only after the prisoner is actually secured.
            bookingProcess?.FinishBookingAfterCellEscort(currentPrisoner);
            requiresSecuredReturnTransit = true;
            ChangeIntakeState(IntakeState.ReturningToPost);
        }

        /// <summary>
        /// Monitors escort movement using direct distance or NavMesh remaining distance. There is deliberately
        /// no timeout: long routes from distant cells must not be abandoned by this state machine.
        /// </summary>
        private void HandleEscortState()
        {
            // SecurityDoorBehavior owns the intermediate destinations while returning.
            // Suppress the stale cell destination during the one-frame handoff between doors.
            if (currentState == IntakeState.ReturningToPost &&
                returnTransitStage != ReturnTransitStage.MovingToPost)
            {
                return;
            }

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

        /// <summary>
        /// Resolves a named station door point, records it as the state-owned destination, and starts the
        /// escort. Mugshot navigation is delayed so its instruction remains visible before movement begins.
        /// </summary>
        /// <param name="stationName">Canonical key from <see cref="intakeStations"/>.</param>
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
        /// <summary>
        /// Holds the mugshot officer long enough to deliver the instruction, then starts movement only if the
        /// intake state and destination are still valid. The handle is cleared on both success and cancellation.
        /// </summary>
        /// <param name="doorPoint">Resolved mugshot navigation point.</param>
        /// <param name="message">Instruction to deliver before movement.</param>
        /// <param name="duration">Instruction display duration in seconds.</param>
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

        /// <summary>
        /// Resolves the assigned cell door point and starts the cell escort. If the door point is unavailable,
        /// it attempts native door opening and finally falls back to the cell transform as a position-only route.
        /// </summary>
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

        /// <summary>
        /// Starts the secured officer-only route back through the prison-entry and booking-inner doors before
        /// the final walk to the booking post. If no post exists, the workflow completes after door cleanup.
        /// </summary>
        private void ReturnToGuardPost()
        {
            if (guardPostTransform == null)
            {
                ModLogger.Warn("IntakeOfficer: Guard post was unavailable; completing intake without a return walk");
                CloseAllIntakeDoors();
                CompleteIntakeProcess();
                return;
            }

            // Only a successfully completed cell escort is known to end on the prison side
            // of both secured corridor doors. Earlier recovery exits retain the direct route.
            if (requiresSecuredReturnTransit)
            {
                returnTransitStage = ReturnTransitStage.PrisonToHall;
                if (TryStartReturnDoorTransit(
                        "PrisonDoorTrigger_FromPrison",
                        "prison entry door from prison to hall"))
                {
                    return;
                }

                BeginFallbackReturnToPost("prison-to-hall SecurityDoor transition was unavailable");
                return;
            }

            BeginFinalReturnToPost();
        }

        /// <summary>Starts one canonical officer-only SecurityDoor transit during the return route.</summary>
        private bool TryStartReturnDoorTransit(string triggerName, string description)
        {
            var securityDoor = GetSecurityDoor();
            if (securityDoor == null)
            {
                ModLogger.Error($"IntakeOfficer: No SecurityDoor component for return through {description}");
                return false;
            }

            if (securityDoor.IsBusy())
            {
                ModLogger.Warn($"IntakeOfficer: SecurityDoor was unexpectedly busy before return through {description}");
                return false;
            }

            if (!securityDoor.HandleDoorTrigger(triggerName, false, null))
            {
                ModLogger.Warn($"IntakeOfficer: SecurityDoor rejected return through {description}");
                return false;
            }

            isSecurityDoorActive = true;
            ModLogger.Info($"IntakeOfficer: Returning through {description}");
            return true;
        }

        /// <summary>Begins the final unobstructed leg from booking to the officer's post.</summary>
        private void BeginFinalReturnToPost()
        {
            returnTransitStage = ReturnTransitStage.MovingToPost;
            MoveTo(guardPostTransform.position);
            ModLogger.Info("IntakeOfficer: Returning to guard post");
        }

        /// <summary>
        /// Recovery-only route used when the canonical SecurityDoor sequence cannot start or complete. Both
        /// corridor doors are opened directly, then secured together once the officer reaches the post.
        /// </summary>
        private void BeginFallbackReturnToPost(string reason)
        {
            isSecurityDoorActive = false;
            returnTransitStage = ReturnTransitStage.MovingToPost;

            var doorController = Core.JailController?.doorController;
            bool prisonDoorOpened = doorController?.OpenPrisonEntryDoor() ?? false;
            bool bookingDoorOpened = doorController?.UnlockAndOpenBookingInnerDoor() ?? false;
            ModLogger.Warn(
                $"IntakeOfficer: Using direct-door fallback for return to post ({reason}); " +
                $"prisonOpen={prisonDoorOpened}, bookingOpen={bookingDoorOpened}");

            MoveTo(guardPostTransform.position);
        }

        #endregion

        #region Door Management

        /// <summary>
        /// Resolves the officer-local security-door component first, then the centralized JailController
        /// component. The result may be null during startup, in which case callers use their documented
        /// recovery path.
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


        /// <summary>
        /// Secures every door this workflow may have opened, including the tracked holding cell and the
        /// assigned cell when recreation is inactive. It must run before intake references are reset.
        /// </summary>
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

            // The daily lifecycle owns final cell-door state. In particular,
            // do not immediately undo an active recreation tier after the
            // player has just been assigned and escorted into one of its cells.
            CloseAssignedCellDoorIfRecreationIsInactive(jailController);

            ModLogger.Info("IntakeOfficer: All intake doors secured");
        }

        #endregion

        #region Door Integration

        /// <summary>
        /// Polls escort states for the next required SecurityDoor operation. A disciplinary direct-cell route
        /// crosses the booking inner door before the prison-entry door; duplicate triggers are suppressed.
        /// </summary>
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

        /// <summary>
        /// Receives a direct door-trigger notification but currently performs no door operation. The security
        /// door state machine remains the active owner; this public hook is retained for compatibility and its
        /// no-op status must not be mistaken for completed trigger handling.
        /// </summary>
        /// <param name="triggerName">Compatibility trigger name received from an external door surface.</param>
        public void HandleDoorTrigger(string triggerName)
        {
            // Direct trigger handling remains intentionally disabled; SecurityDoorBehavior owns operations.
            ModLogger.Debug($"IntakeOfficer: Door trigger received: {triggerName}");
        }

        /// <summary>
        /// Compatibility callback for a generic door completion notification. It currently records the event
        /// only; stateful continuation is handled by <see cref="HandleSecurityDoorOperationComplete"/>.
        /// </summary>
        /// <param name="doorName">Door identifier reported by the generic callback.</param>
        private void HandleDoorOperationComplete(string doorName)
        {
            ModLogger.Debug($"IntakeOfficer: Door operation complete for {doorName}");
        }

        /// <summary>
        /// Returns navigation ownership to the intake state machine after the centralized security door has
        /// physically completed. Resumption is deferred one frame to avoid re-entering from the event stack.
        /// </summary>
        /// <param name="doorName">Door identifier reported by the native security-door system.</param>
        private void HandleSecurityDoorOperationComplete(string doorName)
        {
            ModLogger.Info($"IntakeOfficer: SecurityDoor operation completed for {doorName}");

            // SecurityDoor has completed its operation - clear the active flag
            isSecurityDoorActive = false;

            // SecurityDoor only raises completion once this officer is already at
            // the exit point and the door has physically closed. Resume on the
            // next frame so the event stack can unwind, rather than holding the
            // escort on a fixed clearance delay.
            ModLogger.Info("IntakeOfficer: Door closed; scheduling next-frame escort resume");
            StopPendingDelayedNavigationResume();
            delayedNavigationResumeCoroutine = MelonCoroutines.Start(DelayedNavigationResume());
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        /// <summary>
        /// Performs the one-frame handoff from SecurityDoorBehavior back to the state-owned escort route.
        /// The coroutine is intentionally frame-based, not a gameplay delay, and clears its opaque handle.
        /// </summary>
        private IEnumerator DelayedNavigationResume()
        {
            // Let the completed-door event return before transferring NavMesh
            // ownership back to the intake state machine. This is a frame-order
            // handoff, not a timed gameplay delay.
            yield return null;

            // Resume navigation to the state-owned destination.
            if (currentState == IntakeState.EscortToStorage || currentState == IntakeState.WaitingForStorage)
            {
                ModLogger.Info("IntakeOfficer: Resuming navigation to Storage after door completion");
                NavigateToStation("Storage");
            }
            else if (currentState == IntakeState.EscortToCell)
            {
                ModLogger.Info("IntakeOfficer: Resuming navigation to Cell after door completion");
                NavigateToAssignedCell();
            }
            else if (currentState == IntakeState.OpeningCellDoor)
            {
                ModLogger.Info("IntakeOfficer: Already at cell, continuing with door opening");
                // Don't re-navigate if we're already at the cell and opening the door
            }
            else if (currentState == IntakeState.ReturningToPost)
            {
                if (returnTransitStage == ReturnTransitStage.PrisonToHall)
                {
                    returnTransitStage = ReturnTransitStage.HallToBooking;
                    if (!TryStartReturnDoorTransit(
                            "BookingDoorTrigger_FromHall",
                            "booking inner door from hall to booking"))
                    {
                        BeginFallbackReturnToPost("hall-to-booking SecurityDoor transition was unavailable");
                    }
                }
                else if (returnTransitStage == ReturnTransitStage.HallToBooking)
                {
                    BeginFinalReturnToPost();
                }
            }

            ModLogger.Info($"IntakeOfficer: Navigation resumed for state: {currentState}");
            delayedNavigationResumeCoroutine = null;
        }

        /// <summary>
        /// Clears centralized-door ownership, attempts a recovery-only direct-door operation, and schedules
        /// the normal escort route to resume. Direct fallback does not replace canonical SecurityDoor behavior.
        /// </summary>
        /// <param name="doorName">Door identifier reported by the native security-door system.</param>
        private void HandleSecurityDoorOperationFailed(string doorName)
        {
            ModLogger.Error($"IntakeOfficer: SecurityDoor operation FAILED for {doorName} - attempting fallback");

            // SecurityDoor owns the NavMeshAgent while crossing the threshold.  A
            // failed operation previously left this flag true after the direct-door
            // fallback opened the door, so the intake state machine ignored every
            // subsequent destination update and the officer remained at the entry
            // point.  Return ownership before resuming the canonical escort route.
            isSecurityDoorActive = false;

            if (currentState == IntakeState.ReturningToPost)
            {
                BeginFallbackReturnToPost($"SecurityDoor operation failed for {doorName}");
                return;
            }

            // If SecurityDoor fails, try fallback direct door control
            string fallbackDoorType = null;
            if (doorName.Contains("Booking") || doorName.Contains("Inner"))
            {
                fallbackDoorType = "BookingInnerDoor";
            }
            else if (doorName.Contains("Prison") || doorName.Contains("Enter"))
            {
                fallbackDoorType = "PrisonEntryDoor";
            }

            if (fallbackDoorType != null && FallbackDirectDoorControl(fallbackDoorType))
            {
                // This is a recovery-only path.  The canonical SecurityDoor path
                // closes through the door's event; a direct fallback has no such
                // callback, so secure it once both members of the escort clear it.
                ScheduleFallbackDoorClosure(fallbackDoorType);
            }

            // The fallback only operates the door; it deliberately does not move
            // the guard.  Resume the state-owned destination after the door has had
            // a frame to apply its unlocked/open state to the NavMesh route.
            StopPendingDelayedNavigationResume();
            delayedNavigationResumeCoroutine = MelonCoroutines.Start(DelayedNavigationResume());
            ModLogger.Info($"IntakeOfficer: SecurityDoor fallback complete for {doorName}; scheduling escort route resume");
        }

        /// <summary>Door operation keys already issued for the current escort route.</summary>
        private HashSet<string> triggeredDoorOperations = new HashSet<string>();

        /// <summary>True while SecurityDoorBehavior owns the agent at a door threshold.</summary>
        private bool isSecurityDoorActive = false;

        /// <summary>Requests the booking inner-door operation once for storage or disciplinary cell routes.</summary>
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

        /// <summary>Requests the prison-entry door once for a cell escort after its prerequisite route is clear.</summary>
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

        /// <summary>
        /// Performs a recovery-only direct door operation when SecurityDoorBehavior is unavailable or fails.
        /// This path opens the named door but does not move the officer or provide native completion events.
        /// </summary>
        /// <param name="doorType">Canonical fallback door key.</param>
        /// <returns>True when the direct door operation was accepted.</returns>
        private bool FallbackDirectDoorControl(string doorType)
        {
            // Fallback to direct door control if SecurityDoor is not available
            var jailController = Core.JailController;
            if (jailController?.doorController == null) return false;

            if (doorType == "BookingInnerDoor")
            {
                bool opened = jailController.doorController.UnlockAndOpenBookingInnerDoor();
                if (opened)
                {
                    triggeredDoorOperations.Add("BookingInnerDoor");
                    ModLogger.Info("IntakeOfficer: Booking inner door opened via fallback direct control");
                }
                return opened;
            }
            else if (doorType == "PrisonEntryDoor")
            {
                bool opened = jailController.doorController.OpenPrisonEntryDoor();
                if (opened)
                {
                    triggeredDoorOperations.Add("PrisonEntryDoor");
                    ModLogger.Info("IntakeOfficer: Prison entry door opened via fallback direct control");
                }
                return opened;
            }

            return false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Starts the recovery-only closure coroutine for a directly opened door after the escort clears its
        /// threshold. The stored position is used to detect clearance without a SecurityDoor callback.
        /// </summary>
        /// <param name="doorType">Canonical fallback door key.</param>
        private void ScheduleFallbackDoorClosure(string doorType)
        {
            StopPendingFallbackDoorClosure();
            fallbackDoorCloseCoroutine = MelonCoroutines.Start(CloseFallbackDoorAfterEscortClears(doorType, transform.position));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Waits for both escort participants to clear a fallback door threshold, then closes the direct door.
        /// The coroutine is a recovery path only and must not be treated as canonical door synchronization.
        /// </summary>
        /// <param name="doorType">Canonical fallback door key.</param>
        /// <param name="fallbackStartPosition">Officer position captured when fallback opened the door.</param>
        private IEnumerator CloseFallbackDoorAfterEscortClears(string doorType, Vector3 fallbackStartPosition)
        {
            const float clearanceMeters = 1.15f;
            const float timeoutSeconds = 8f;
            float deadline = Time.time + timeoutSeconds;

            try
            {
                while (Time.time < deadline)
                {
                    bool officerClear = Vector3.Distance(transform.position, fallbackStartPosition) >= clearanceMeters;
                    bool prisonerClear = currentPrisoner == null ||
                                         Vector3.Distance(currentPrisoner.transform.position, fallbackStartPosition) >= clearanceMeters;
                    if (officerClear && prisonerClear)
                    {
                        break;
                    }

                    yield return null;
                }

                var doorController = Core.JailController?.doorController;
                bool closed = doorType == "BookingInnerDoor"
                    ? doorController?.CloseBookingInnerDoor() ?? false
                    : doorType == "PrisonEntryDoor" && (doorController?.ClosePrisonEntryDoor() ?? false);
                ModLogger.Info($"IntakeOfficer: Fallback {doorType} secured after escort clearance (closed={closed})");
            }
            finally
            {
                fallbackDoorCloseCoroutine = null;
            }
        }
        /// <summary>Clears per-intake door operation keys and ownership flags before a new workflow begins.</summary>
        private void ResetDoorTracking()
        {
            // Clear triggered door operations when starting new intake process
            triggeredDoorOperations.Clear();
            stationDestinationProcessed.Clear();
            ModLogger.Debug("IntakeOfficer: Door operation and destination tracking reset for new intake process");
        }

        /// <summary>
        /// Converts a validated navigation arrival into the next detailed intake state. Arrivals are ignored
        /// while SecurityDoorBehavior owns movement, while a waiting state is already active, or when the
        /// destination does not match the state-owned station/cell target.
        /// </summary>
        /// <param name="destination">World-space destination reported by base navigation.</param>
        private void HandleDestinationReached(Vector3 destination)
        {
            // Ignore destination events when SecurityDoor is actively controlling the guard
            if (isSecurityDoorActive)
            {
                return; // No logging - SecurityDoor is handling movement
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
                    CloseAllIntakeDoors();
                    CompleteIntakeProcess();
                    break;

                default:
                    ModLogger.Warn($"IntakeOfficer: HandleDestinationReached called during unexpected state: {currentState}");
                    break;
            }
        }

        /// <summary>
        /// Validates an arrival against the station/cell target for the current escort state and suppresses
        /// duplicate station events. Missing JailController/door-point data is treated as permissive only where
        /// the existing recovery path explicitly allows it.
        /// </summary>
        /// <param name="destination">World-space destination reported by base navigation.</param>
        /// <returns>True when the arrival may advance the detailed intake state.</returns>
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

        /// <summary>Maps station escort states to their canonical JailController guard-point keys.</summary>
        /// <param name="state">Detailed intake state to map.</param>
        /// <returns>Station key, or null for non-station states.</returns>
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

        /// <summary>Checks whether an arrival is inside the requested station door-point tolerance.</summary>
        /// <param name="stationName">JailController station key.</param>
        /// <param name="destination">Reported world-space arrival.</param>
        /// <param name="tolerance">Maximum acceptable distance in world units.</param>
        private bool IsNearDoorPoint(string stationName, Vector3 destination, float tolerance)
        {
            var doorPoint = FindDoorPoint(stationName);
            if (doorPoint == null) return true; // Allow if door point not found

            float distance = Vector3.Distance(destination, doorPoint.position);
            ModLogger.Debug($"IntakeOfficer: Checking distance to {stationName}: {distance:F2}m (tolerance: {tolerance:F2}m)");
            return distance <= tolerance;
        }

        /// <summary>
        /// Handles base movement completion without transitioning the inherited coarse state to idle. The
        /// detailed intake state machine owns the next transition and must not be interrupted by the base hook.
        /// </summary>
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

        /// <summary>
        /// Intentionally ignores coarse <see cref="BaseJailNPC.NPCState"/> changes. Intake progression is
        /// owned by <see cref="ChangeIntakeState"/>; this no-op prevents base navigation completion from
        /// collapsing an escort into idle. It is not a substitute for detailed state transitions.
        /// </summary>
        /// <param name="newState">Ignored coarse state requested by base/other callers.</param>
        public override void ChangeState(NPCState newState)
        {
            // Intentionally empty: the detailed intake state machine owns progression.
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Claims an idle officer for a booking event, resolves the prisoner's actual holding cell, resets
        /// per-intake tracking, and starts the delayed fetch state. Coordination failure schedules one bounded
        /// retry instead of allowing two officers to escort the same prisoner.
        /// </summary>
        /// <param name="player">Prisoner announced by the booking process.</param>
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
            requiresSecuredReturnTransit = false;
            returnTransitStage = ReturnTransitStage.None;

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
        /// <summary>
        /// Retries a coordination-blocked booking only if the officer is still idle and the prisoner remains
        /// valid. The opaque coroutine handle is cleared before retrying.
        /// </summary>
        /// <param name="player">Prisoner whose booking should be retried.</param>
        /// <param name="delay">Retry delay in Unity seconds.</param>
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

        /// <summary>Records a mugshot completion event; the state tick performs the actual transition.</summary>
        /// <param name="player">Player associated with the booking event.</param>
        private void HandleMugshotCompleted(Player player)
        {
            if (currentState == IntakeState.WaitingForMugshot)
            {
                ModLogger.Info("IntakeOfficer: Mugshot completed, proceeding to scanner");
            }
        }

        /// <summary>Records a fingerprint completion event; the state tick performs the actual transition.</summary>
        /// <param name="player">Player associated with the booking event.</param>
        private void HandleFingerprintCompleted(Player player)
        {
            if (currentState == IntakeState.WaitingForScan)
            {
                ModLogger.Info("IntakeOfficer: Fingerprint scan completed, proceeding to storage");
            }
        }

        /// <summary>
        /// Advances storage completion to cell assignment only when the officer is waiting at storage; events
        /// from another booking phase are logged and ignored.
        /// </summary>
        /// <param name="player">Player associated with the booking event.</param>
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

        /// <summary>Returns whether a detailed state represents active station/cell escort movement.</summary>
        /// <param name="state">State to test.</param>
        private bool IsEscortState(IntakeState state)
        {
            return state == IntakeState.EscortToHolding ||
                   state == IntakeState.EscortToMugshot ||
                   state == IntakeState.EscortToScanner ||
                   state == IntakeState.EscortToStorage ||
                   state == IntakeState.EscortToCell;
        }


        /// <summary>
        /// Resolves a JailController guard point, targeted holding-cell point, or cell hierarchy door point.
        /// Holding-cell lookup is intentionally keyed by the tracked runtime cell rather than traversal order.
        /// </summary>
        /// <param name="stationName">Canonical station or jail-cell key.</param>
        /// <returns>Resolved navigation point, or null when the scene graph has no matching point.</returns>
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

        /// <summary>
        /// Attempts assignment after storage completion and enters cell escort only when a canonical cell is
        /// returned. Failed assignment leaves the prisoner at storage and retries at the configured interval.
        /// </summary>
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

        /// <summary>
        /// Requests a cell from the central assignment manager. No fallback cell is selected when the manager
        /// is unavailable or returns failure; the caller owns the retry schedule.
        /// </summary>
        /// <returns>True when an assigned cell number is available.</returns>
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

        /// <summary>
        /// Closes the assigned cell unless its tier is currently in active recreation. Recreation ownership
        /// belongs to the jail lifecycle manager, so intake must preserve an intentionally open recreation cell.
        /// </summary>
        private void CloseCellDoor()
        {
            if (assignedCellNumber < 0) return;

            var jailController = Core.JailController;
            if (jailController?.doorController != null)
            {
                if (IsAssignedCellInActiveRecreation(jailController))
                {
                    ModLogger.Info($"IntakeOfficer: Leaving jail cell {assignedCellNumber} open because its tier currently has recreation");
                    return;
                }

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

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Closes the assigned cell during generic intake cleanup only when recreation is inactive. This
        /// helper is hidden from IL2CPP because it is an internal lifecycle bridge, not an injected API.
        /// </summary>
        /// <param name="jailController">Current jail controller used for lifecycle and door lookup.</param>
        private void CloseAssignedCellDoorIfRecreationIsInactive(JailController jailController)
        {
            if (assignedCellNumber < 0)
            {
                return;
            }

            if (IsAssignedCellInActiveRecreation(jailController))
            {
                ModLogger.Info($"IntakeOfficer: Preserved recreation access for assigned cell {assignedCellNumber}");
                return;
            }

            jailController?.doorController?.CloseJailCellDoor(assignedCellNumber);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Checks the jail lifecycle manager before intake changes the assigned cell door.</summary>
        /// <param name="jailController">Current jail controller, if available.</param>
        /// <returns>True when the assigned cell's tier is in active recreation.</returns>
        private bool IsAssignedCellInActiveRecreation(JailController jailController)
        {
            if (jailController == null || assignedCellNumber < 0)
            {
                return false;
            }

            JailLifecycleManager lifecycle = BBHelpers.GetComponentSafe<JailLifecycleManager>(jailController.gameObject);
            return lifecycle != null && lifecycle.IsCellInActiveRecreation(assignedCellNumber);
        }

        /// <summary>
        /// Starts continuous player-facing rotation and requests the assigned cell door to open. The next
        /// state waits for cell-boundary entry; this method does not complete booking by itself.
        /// </summary>
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

        /// <summary>
        /// Queues one explicit guard instruction after a short optional player-facing turn. Existing pending
        /// command work is stopped first so an old station message cannot contradict the current state.
        /// </summary>
        /// <param name="message">Instruction text to emit.</param>
        /// <param name="duration">Native dialogue display duration in seconds.</param>
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

        /// <summary>Stops and clears the pending player-facing command handle, if any.</summary>
        private void StopPendingPlayerFacingCommand()
        {
            if (playerFacingCommandCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(playerFacingCommandCoroutine);
            playerFacingCommandCoroutine = null;
        }

        /// <summary>Stops and clears the delayed mugshot escort handle, if any.</summary>
        private void StopPendingMugshotEscort()
        {
            if (mugshotEscortCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(mugshotEscortCoroutine);
            mugshotEscortCoroutine = null;
        }

        /// <summary>Stops and clears the reserved delayed-door-close handle, if any.</summary>
        private void StopPendingDelayedDoorClose()
        {
            if (delayedDoorCloseCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(delayedDoorCloseCoroutine);
            delayedDoorCloseCoroutine = null;
        }

        /// <summary>Stops and clears the one-frame SecurityDoor navigation-resume handle, if any.</summary>
        private void StopPendingDelayedNavigationResume()
        {
            if (delayedNavigationResumeCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(delayedNavigationResumeCoroutine);
            delayedNavigationResumeCoroutine = null;
        }

        /// <summary>Stops and clears the coordination retry handle, if any.</summary>
        private void StopPendingRetryIntake()
        {
            if (retryIntakeCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(retryIntakeCoroutine);
            retryIntakeCoroutine = null;
        }

        /// <summary>Stops and clears the recovery-only fallback-door closure handle, if any.</summary>
        private void StopPendingFallbackDoorClosure()
        {
            if (fallbackDoorCloseCoroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(fallbackDoorCloseCoroutine);
            fallbackDoorCloseCoroutine = null;
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
            StopPendingFallbackDoorClosure();
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
        /// <summary>
        /// Gives the prisoner a brief horizontal turn toward the officer before emitting the explicit command.
        /// The command still emits when the prisoner reference disappears during the turn.
        /// </summary>
        /// <param name="message">Instruction text to emit.</param>
        /// <param name="duration">Native dialogue display duration in seconds.</param>
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
        /// <summary>
        /// Emits through the configured dialogue controller when available, otherwise uses the base native
        /// world-space message path. The fallback reports native availability rather than fabricating UI state.
        /// </summary>
        /// <param name="message">Instruction text to emit.</param>
        /// <param name="duration">Native dialogue display duration in seconds.</param>
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


        /// <summary>
        /// Completes the intake session after the prisoner is secured, unregisters the officer from
        /// coordination, clears all prisoner/cell/door flags, and returns the detailed state to idle.
        /// </summary>
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
            requiresSecuredReturnTransit = false;
            returnTransitStage = ReturnTransitStage.None;

            // Reset state tracking flags
            playerExitDetected = false;
            doorCloseInitiated = false;

            ChangeIntakeState(IntakeState.Idle);

            ModLogger.Info("IntakeOfficer: Intake process completed");
        }

        #endregion

        #region Public Interface

        /// <summary>Returns the authoritative detailed intake state.</summary>
        public new IntakeState GetCurrentState() => currentState;
        /// <summary>Returns the prisoner currently owned by the intake workflow, or null.</summary>
        public Player GetCurrentPrisoner() => currentPrisoner;
        /// <summary>Returns whether the detailed intake state is anything other than idle.</summary>
        public bool IsProcessingIntake() => currentState != IntakeState.Idle;
        /// <summary>Returns the current canonical station key, or an empty string when none is active.</summary>
        public string GetCurrentTargetStation() => currentTargetStation;

        /// <summary>
        /// Requires the next booking event for this prisoner to start from the supplied holding
        /// cell. Used after a disciplinary hold so the canonical intake state machine fetches
        /// the player from the actual punishment cell rather than the first holding-cell door.
        /// </summary>
        /// <param name="player">Prisoner who must remain in the named holding cell.</param>
        /// <param name="holdingCellName">Canonical punishment/holding cell name.</param>
        /// <returns>True when the officer is idle and the player is confirmed inside that cell.</returns>
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
        /// Invokes the normal booking-start path for testing. It does not bypass coordination, holding-cell
        /// resolution, or the detailed state transitions used by live booking.
        /// </summary>
        /// <param name="player">Prisoner to pass to the normal booking-start path.</param>
        public void ForceStartIntake(Player player)
        {
            if (player != null)
            {
                HandleBookingStarted(player);
            }
        }

        /// <summary>
        /// Requests a return-to-post transition for the current intake. This is not the same as
        /// <see cref="CancelIntake"/>: it does not immediately clear every field or stop every pending action.
        /// </summary>
        public void StopIntakeProcess()
        {
            ModLogger.Info("IntakeOfficer: Emergency stop of intake process");
            ChangeIntakeState(IntakeState.ReturningToPost);
        }

        /// <summary>
        /// Cancels the active intake, stops owned coroutines and SecurityDoor work, secures tracked doors, and
        /// clears prisoner/cell/route state so the officer can accept a new booking.
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
            requiresSecuredReturnTransit = false;
            returnTransitStage = ReturnTransitStage.None;
            playerExitDetected = false;
            doorCloseInitiated = false;
            ResetDoorTracking();

            // Return to idle immediately
            ChangeIntakeState(IntakeState.Idle);

            ModLogger.Info("IntakeOfficer: Intake canceled - now available for new prisoner");
        }

        /// <summary>Clears the pending disciplinary-repeat holding-cell reservation.</summary>
        private void ClearRequiredHoldingCell()
        {
            requiredHoldingCellPrisoner = null;
            requiredHoldingCellName = "";
        }

        /// <summary>
        /// Attack handling is currently disabled for testing: the event is logged and ignored, and no intake
        /// interruption or arrest is performed. The commented legacy branch is not an active parity path.
        /// </summary>
        /// <param name="attacker">Player whose attack was received.</param>
        public override void OnAttackedByPlayer(Player attacker)
        {
            // Intentionally disabled for testing; do not treat this as production assault handling.
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


        /// <summary>
        /// Replaces any existing continuous-looking coroutine with a new two-second polling loop. This is
        /// presentation support for waiting at stations/cells and is stopped whenever movement resumes.
        /// </summary>
        private void StartContinuousPlayerLooking()
        {
            // Stop any existing continuous looking
            StopContinuousPlayerLooking();

            // Start new continuous looking coroutine
            continuousLookingCoroutine = MelonCoroutines.Start(ContinuousPlayerLookingCoroutine());
            ModLogger.Debug("IntakeOfficer: Started continuous player looking");
        }

        /// <summary>
        /// Stops the continuous-looking coroutine and returns NavMeshAgent rotation ownership to navigation.
        /// </summary>
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
        /// <summary>
        /// Periodically rotates the officer toward the current booking player until the handle is stopped by
        /// movement, cancellation, disable, or destruction.
        /// </summary>
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

        /// <summary>Samples the booking player and starts a short smooth horizontal turn toward them.</summary>
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
        /// <summary>
        /// Temporarily disables NavMeshAgent rotation while interpolating to an exact target rotation, then
        /// leaves both the officer transform and agent transform aligned.
        /// </summary>
        /// <param name="targetRotation">Horizontal rotation to reach.</param>
        /// <param name="duration">Turn duration in Unity seconds.</param>
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
        /// Stops station-facing rotation before delegating to the base NavMesh route. Intake still owns the
        /// detailed destination transition after the base movement request succeeds.
        /// </summary>
        /// <param name="destination">Target position.</param>
        /// <param name="tolerance">Distance tolerance in world units.</param>
        /// <returns>True if navigation started successfully.</returns>
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
        /// Starts continuous player-facing rotation while the officer waits at a named station. The current
        /// implementation validates JailController presence but obtains the player from booking state.
        /// </summary>
        /// <param name="stationName">Name used for diagnostics.</param>
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

        /// <summary>
        /// Stops all intake-owned asynchronous work and unsubscribes booking/security-door listeners. This
        /// intentionally hides the base lifecycle method; pending actions are explicitly aborted here, but
        /// base cleanup is not invoked by the current implementation.
        /// </summary>
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

        /// <summary>
        /// Forwards base destination notification and then lets the detailed intake machine validate and
        /// transition the state-owned route.
        /// </summary>
        /// <param name="destination">World-space destination reported by base navigation.</param>
        protected override void NotifyDestinationReached(Vector3 destination)
        {
            base.NotifyDestinationReached(destination);
            HandleDestinationReached(destination);
        }

        #endregion

    }
}
