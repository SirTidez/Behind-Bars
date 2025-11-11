using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using Behind_Bars.UI;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Interaction;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Interaction;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Specialized guard behavior for escorting players during release process
    /// Handles cell → storage → exit escort sequence with voice commands
    /// </summary>
    public class ReleaseOfficerBehavior : BaseJailNPC
    {
#if !MONO
        public ReleaseOfficerBehavior(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Officer State

        public enum ReleaseState
        {
            Idle,                      // At guard post
            MovingToPlayer,            // Moving to player's current location
            OpeningCell,               // Opening cell door if needed
            WaitingForPlayerExitCell,  // Waiting for player to exit cell
            EscortingToStorage,        // Escorting to inventory pickup
            WaitingAtStorage,          // Supervising inventory pickup
            EscortingToExitScanner,    // Escorting to exit scanner station
            WaitingForExitScan,        // Waiting for exit scan completion
            ProcessingExit,            // Final exit procedures
            ReturningToPost            // Returning to guard post
        }

        private ReleaseState currentReleaseState = ReleaseState.Idle;
        private Player currentReleasee;
        private Transform guardPost;
        private string badgeNumber;
        private bool isAvailable = true;

        // State machine timing
        private new float stateStartTime;
        private const float STATE_TIMEOUT = 300f; // 5 minutes max per state
        private const float PLAYER_CHECK_INTERVAL = 2f; // How often to update player position
        private float lastPlayerPositionCheck = 0f;

        // Movement and tracking
        private Vector3 destinationPosition;
        private Vector3 lastKnownPlayerPosition;
        private bool isEscorting = false;
        private const float ESCORT_FOLLOW_DISTANCE = 5f;
        private const float DESTINATION_TOLERANCE = 2.0f;

        // Audio and dialogue
        private JailNPCAudioController audioController;
        private JailNPCDialogueController dialogueController;

        // Guard interaction for ready confirmation
        private InteractableObject guardInteraction;
        private bool readyInteractionEnabled = false;

        // Door management
        private HashSet<string> triggeredDoorOperations = new HashSet<string>();
        private bool isSecurityDoorActive = false;
        private float lastDoorOperationTime = 0f;

        // Destination tracking to prevent duplicate events
        private Dictionary<string, bool> stationDestinationProcessed = new Dictionary<string, bool>();

        // Continuous rotation system
        private object continuousLookingCoroutine;

        // Door clearance tracking (like IntakeOfficer)
        private bool playerDoorClearDetected = false;
        private bool doorCloseInitiated = false;

        // Proactive release timing - learn from first escort cycle
        private float estimatedTravelTimeToPost = 0f; // Time it takes to get from cell to guard post
        private bool hasLearnedTravelTime = false;
        private float returnJourneyStartTime = 0f;

        #endregion

        #region Events

        public new System.Action<ReleaseState> OnStateChanged;
        public System.Action<Player> OnEscortStarted;
        public System.Action<Player> OnEscortCompleted;
        public System.Action<Player, string> OnEscortFailed;
        public System.Action<Player, ReleaseState> OnStatusUpdate;

        #endregion

        #region Initialization

        protected override void InitializeNPC()
        {
            // Don't call base.InitializeNPC() as it's abstract

            badgeNumber = GenerateBadgeNumber();
            FindGuardPost();
            InitializeAudioComponents();
            EnsureSecurityDoorComponent(); // RE-ENABLED - using correct door triggers now
            SetupGuardInteraction(); // NEW: Add ready confirmation interaction

            // Register with ReleaseManager
            if (ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.RegisterOfficer(this);
            }

            // Subscribe to SecurityDoor events (like IntakeOfficer)
            var securityDoor = GetSecurityDoor();
            if (securityDoor != null)
            {
                securityDoor.OnDoorOperationComplete += HandleSecurityDoorOperationComplete;
                securityDoor.OnDoorOperationFailed += HandleSecurityDoorOperationFailed;
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Subscribed to SecurityDoor events");
            }
            else
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: No SecurityDoor component - will use fallback direct control");
            }

            // Subscribe to movement completion events
            OnDestinationReached += HandleDestinationReached;

            // CRITICAL FIX: Only set to Idle if not already in an active release
            if (currentReleasee == null && currentReleaseState == ReleaseState.Idle)
            {
                ChangeReleaseState(ReleaseState.Idle);
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Set to Idle state during initialization");
            }
            else
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Preserving active state {currentReleaseState} during initialization");
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber} initialized and registered");
        }

        private void EnsureSecurityDoorComponent()
        {
            // Check if SecurityDoorBehavior is already attached
            var existingSecurityDoor = GetComponent<SecurityDoorBehavior>();
            if (existingSecurityDoor == null)
            {
                // Add SecurityDoorBehavior component to this ReleaseOfficer
                var securityDoor = gameObject.AddComponent<SecurityDoorBehavior>();
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Added SecurityDoorBehavior component for door operations");
            }
            else
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: SecurityDoorBehavior component already attached");
            }
        }

        private void FindGuardPost()
        {
            // Look for a guard post or use the booking area - use guardSpawns[1] for release officers
            var jailController = Core.JailController;
            if (jailController?.booking?.guardSpawns != null && jailController.booking.guardSpawns.Count > 1)
            {
                guardPost = jailController.booking.guardSpawns[1]; // Use second guard spawn for release officers
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Found guard post at {guardPost.position}");
            }
            else
            {
                // Create a default post position
                var defaultPost = new GameObject($"ReleaseOfficerPost_{badgeNumber}");
                defaultPost.transform.position = transform.position;
                guardPost = defaultPost.transform;
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Created default guard post");
            }
        }

        private void InitializeAudioComponents()
        {
            try
            {
                audioController = GetComponent<JailNPCAudioController>();
                if (audioController == null)
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: No JailNPCAudioController found");
                }

                dialogueController = GetComponent<JailNPCDialogueController>();
                if (dialogueController == null)
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: No JailNPCDialogueController found");
                }
                else
                {
                    SetupReleaseDialogue();
                }

                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Audio components initialized");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing audio components for ReleaseOfficer {badgeNumber}: {e.Message}");
            }
        }

        private void SetupReleaseDialogue()
        {
            if (dialogueController == null) return;

            try
            {
                // Set up release-specific dialogue states
                dialogueController.AddStateDialogue("Idle", "Ready for release duties.",
                    new[] { "Waiting for release orders.", "Standing by.", "Post secured." });

                dialogueController.AddStateDialogue("Fetching", "Going to fetch the prisoner.",
                    new[] { "On my way to the cell.", "Retrieving prisoner.", "Moving to collect prisoner." });

                dialogueController.AddStateDialogue("Escorting", "Follow me for release processing.",
                    new[] { "This way.", "Stay close.", "Follow me.", "Keep moving." });

                dialogueController.AddStateDialogue("AtStorage", "Collect your personal items.",
                    new[] { "Get your items.", "Pick up your belongings.", "Retrieve your possessions.", "Tell me when you're ready." });

                dialogueController.AddStateDialogue("AtExit", "You're free to go.",
                    new[] { "You're released.", "Freedom awaits.", "Your time is served." });

                dialogueController.UpdateGreetingForState("Idle");
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Dialogue system configured");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting up dialogue for ReleaseOfficer {badgeNumber}: {e.Message}");
            }
        }

        private string GenerateBadgeNumber()
        {
            return $"R{UnityEngine.Random.Range(1000, 9999)}";
        }

        private void SetupGuardInteraction()
        {
            // COMPLETELY REMOVE the base NPC dialog interaction
            var baseInteractions = GetComponents<InteractableObject>();
            foreach (var interaction in baseInteractions)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Destroying base InteractableObject component");
                Destroy(interaction);
            }

            // Also disable DialogueController if it exists (causes the "Hi" dialog)
            var dialogueControllers = GetComponents<MonoBehaviour>();
            foreach (var component in dialogueControllers)
            {
                if (component.GetType().Name.Contains("DialogueController") && !component.GetType().Name.Contains("JailNPC"))
                {
                    component.enabled = false;
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Disabled base DialogueController: {component.GetType().Name}");
                }
            }

            // Create a new child GameObject for the interaction prompt (positioned at chest/head level)
            var interactionPoint = new GameObject("ReleaseOfficerInteraction");
            interactionPoint.transform.SetParent(transform);
            interactionPoint.transform.localPosition = new Vector3(0f, 1.5f, 0f); // Position at chest height

            // Add a trigger collider for the interaction to work
            var interactionCollider = interactionPoint.AddComponent<SphereCollider>();
            interactionCollider.radius = 1.5f; // Interaction radius
            interactionCollider.isTrigger = true;

            // Add InteractableObject to the child GameObject
            guardInteraction = interactionPoint.AddComponent<InteractableObject>();
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Added InteractableObject at proper height with collider");

            // Configure interaction (disabled by default)
            guardInteraction.SetMessage("Tell guard you're ready to leave");
            guardInteraction.SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            guardInteraction.SetInteractableState(InteractableObject.EInteractableState.Invalid); // Disabled initially

#if !MONO
            // IL2CPP safe event subscription
            guardInteraction.onInteractStart.AddListener((System.Action)OnPlayerReadyInteraction);
#else
            // Mono event subscription
            guardInteraction.onInteractStart.AddListener(OnPlayerReadyInteraction);
#endif

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Guard interaction configured and disabled by default");
        }

        private void OnPlayerReadyInteraction()
        {
            if (!readyInteractionEnabled || currentReleaseState != ReleaseState.WaitingAtStorage)
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Ready interaction triggered but not enabled or wrong state");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player confirmed ready to leave storage");

            // Finalize inventory processing now that player is done with storage
            FinalizeInventoryPickup();

            // Disable the interaction
            DisableGuardReadyInteraction();

            // Proceed to exit scanner
            OnPlayerConfirmedReady();
        }

        /// <summary>
        /// Finalize the inventory pickup - clear remaining items and notify completion
        /// </summary>
        private void FinalizeInventoryPickup()
        {
            try
            {
                // Disable the InventoryPickupStation now that player is done
                var jailController = Core.JailController;
                if (jailController?.storage?.inventoryPickup != null)
                {
                    // Mark as disabled by officer to prevent reopening
                    var inventoryPickupStation = jailController.storage.inventoryPickup.GetComponent<InventoryPickupStation>();
                    if (inventoryPickupStation != null)
                    {
                        inventoryPickupStation.MarkDisabledByOfficer();
                        inventoryPickupStation.enabled = false;
                        ModLogger.Info("Disabled InventoryPickupStation and marked as officer-disabled");
                    }

                    // Disable the InteractableObject
                    var interactable = jailController.storage.inventoryPickup.GetComponent<InteractableObject>();
                    if (interactable != null)
                    {
                        interactable.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                        interactable.SetMessage("Storage closed");
                        ModLogger.Info("Disabled InventoryPickup InteractableObject");
                    }

                    // CRITICAL: Disable the entire GameObject to prevent any interaction
                    jailController.storage.inventoryPickup.gameObject.SetActive(false);
                    ModLogger.Info("Disabled InventoryPickup GameObject - player confirmed ready");
                }

                // DO NOT clear persistent storage here - player is still in jail and could get re-arrested!
                // Persistent storage is cleared in ExitScannerStation.TeleportPlayerToRelease() after successful exit

                // Notify ReleaseManager that inventory processing is complete
                if (ReleaseManager.Instance != null && currentReleasee != null)
                {
                    ReleaseManager.Instance.OnInventoryProcessingComplete(currentReleasee);
                    ModLogger.Info("Notified ReleaseManager that inventory processing is complete");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error finalizing inventory pickup: {ex.Message}");
            }
        }

        #endregion

        #region State Management

        protected override void Update()
        {
            if (!isInitialized) return;

            base.Update();
            UpdateReleaseStateMachine();
        }

        private void HandleDestinationReached(Vector3 destination)
        {
            // Ignore destination events when SecurityDoor is actively controlling the guard
            if (isSecurityDoorActive)
            {
                return; // No logging - SecurityDoor is handling movement
            }

            // IMPORTANT: Ignore all destination events during door clearance delay period
            float timeSinceLastDoorOperation = Time.time - lastDoorOperationTime;
            if (timeSinceLastDoorOperation < 3.0f) // Within 3 seconds of door operation
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Ignoring destination reached - within door clearance delay period ({timeSinceLastDoorOperation:F1}s ago)");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Destination reached at {destination} during state {currentReleaseState}");

            // Handle state transitions based on current state
            switch (currentReleaseState)
            {
                case ReleaseState.MovingToPlayer:
                    // We've reached the cell door, now open it
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Arrived at cell door, opening cell");
                    ChangeReleaseState(ReleaseState.OpeningCell);
                    break;

                case ReleaseState.EscortingToStorage:
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Arrived at storage area");
                    ChangeReleaseState(ReleaseState.WaitingAtStorage);
                    break;

                case ReleaseState.EscortingToExitScanner:
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Arrived at exit scanner");
                    ChangeReleaseState(ReleaseState.WaitingForExitScan);
                    break;

                case ReleaseState.ReturningToPost:
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Returned to guard post");
                    ChangeReleaseState(ReleaseState.Idle);
                    break;

                default:
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Destination reached during {currentReleaseState} - no action needed");
                    break;
            }
        }

        private void UpdateReleaseStateMachine()
        {
            // Check state timeout, but exclude waiting states (player can take as long as they want)
            bool isWaitingState = currentReleaseState == ReleaseState.Idle ||
                                  currentReleaseState == ReleaseState.WaitingForPlayerExitCell ||
                                  currentReleaseState == ReleaseState.WaitingAtStorage ||
                                  currentReleaseState == ReleaseState.WaitingForExitScan;

            if (!isWaitingState && Time.time - stateStartTime > STATE_TIMEOUT)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: State {currentReleaseState} timed out, returning to post");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Check for door triggers during escort phases (like IntakeOfficer)
            CheckForDoorTriggers();

            // Update player position during escort phases
            if (IsEscortState(currentReleaseState))
            {
                CheckAndUpdatePlayerPosition();
            }

            switch (currentReleaseState)
            {
                case ReleaseState.Idle:
                    HandleIdleState();
                    break;

                case ReleaseState.MovingToPlayer:
                    HandleMovingToPlayerState();
                    break;

                case ReleaseState.OpeningCell:
                    HandleOpeningCellState();
                    break;

                case ReleaseState.WaitingForPlayerExitCell:
                    HandleWaitingForPlayerExitCellState();
                    break;

                case ReleaseState.EscortingToStorage:
                    HandleEscortState();
                    break;

                case ReleaseState.WaitingAtStorage:
                    HandleWaitingAtStorageState();
                    break;

                case ReleaseState.EscortingToExitScanner:
                    HandleEscortingToExitScannerState();
                    break;

                case ReleaseState.WaitingForExitScan:
                    HandleWaitingForExitScanState();
                    break;

                case ReleaseState.ProcessingExit:
                    HandleProcessingExitState();
                    break;

                case ReleaseState.ReturningToPost:
                    HandleReturningToPostState();
                    break;
            }
        }

        private bool IsEscortState(ReleaseState state)
        {
            return state == ReleaseState.EscortingToStorage ||
                   state == ReleaseState.EscortingToExitScanner ||
                   state == ReleaseState.MovingToPlayer;
        }

        private void CheckAndUpdatePlayerPosition()
        {
            if (currentReleasee == null || Time.time - lastPlayerPositionCheck < PLAYER_CHECK_INTERVAL)
                return;

            lastPlayerPositionCheck = Time.time;

            Vector3 currentPlayerPos = currentReleasee.transform.position;

            // If player has moved significantly from last known position, update our navigation
            if (Vector3.Distance(currentPlayerPos, lastKnownPlayerPosition) > 3f)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player moved, updating navigation from {lastKnownPlayerPosition} to {currentPlayerPos}");
                lastKnownPlayerPosition = currentPlayerPos;

                // If we're moving to player, update destination
                if (currentReleaseState == ReleaseState.MovingToPlayer)
                {
                    NavigateToPlayer();
                }
                else if (currentReleaseState == ReleaseState.EscortingToStorage || currentReleaseState == ReleaseState.EscortingToExitScanner)
                {
                    // During escort, check if player is falling behind
                    float distance = Vector3.Distance(transform.position, currentPlayerPos);
                    if (distance > ESCORT_FOLLOW_DISTANCE)
                    {
                        PlayVoiceCommand("Stay close! Follow me.", "Escorting");
                    }
                }
            }
        }

        public void ChangeReleaseState(ReleaseState newState)
        {
            if (currentReleaseState == newState) return;

            ReleaseState oldState = currentReleaseState;
            currentReleaseState = newState;
            stateStartTime = Time.time;

            OnStateChanged?.Invoke(newState);
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: {oldState} → {newState}");

            // Notify ReleaseManager of status updates for key states
            if (currentReleasee != null && ShouldNotifyStatusUpdate(newState))
            {
                OnStatusUpdate?.Invoke(currentReleasee, newState);
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Notified ReleaseManager of status update: {newState}");
            }

            // Update dialogue state
            UpdateDialogueForState(newState);

            // Update officer command notification
            UpdateOfficerCommandNotification(newState);

            // Handle state entry logic
            OnStateEnter(newState);
        }

        private bool ShouldNotifyStatusUpdate(ReleaseState state)
        {
            // Only notify for states that correspond to ReleaseManager statuses
            return state == ReleaseState.EscortingToStorage ||
                   state == ReleaseState.WaitingAtStorage ||
                   state == ReleaseState.EscortingToExitScanner ||
                   state == ReleaseState.WaitingForExitScan;
        }

        private void UpdateDialogueForState(ReleaseState state)
        {
            if (dialogueController == null) return;

            string dialogueState = state switch
            {
                ReleaseState.Idle => "Idle",
                ReleaseState.MovingToPlayer => "Fetching",
                ReleaseState.OpeningCell => "Fetching",
                ReleaseState.EscortingToStorage => "Escorting",
                ReleaseState.WaitingAtStorage => "AtStorage",
                ReleaseState.EscortingToExitScanner => "Escorting",
                ReleaseState.WaitingForExitScan => "AtExit",
                ReleaseState.ProcessingExit => "AtExit",
                ReleaseState.ReturningToPost => "Idle",
                _ => "Idle"
            };

            dialogueController.UpdateGreetingForState(dialogueState);
        }

        /// <summary>
        /// Update officer command notification based on current state
        /// </summary>
        private void UpdateOfficerCommandNotification(ReleaseState state)
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
                    BehindBarsUIManager.Instance?.UpdateOfficerCommand(commandData);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error updating command notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Determine if this state should display a command notification
        /// </summary>
        private bool ShouldShowCommandNotification(ReleaseState state)
        {
            return state switch
            {
                ReleaseState.EscortingToStorage => true,
                ReleaseState.WaitingAtStorage => true,
                ReleaseState.EscortingToExitScanner => true,
                ReleaseState.WaitingForExitScan => true,
                _ => false
            };
        }

        /// <summary>
        /// Get command data for the current state
        /// </summary>
        private OfficerCommandData? GetCommandDataForState(ReleaseState state)
        {
            return state switch
            {
                ReleaseState.EscortingToStorage => new OfficerCommandData(
                    "RELEASE OFFICER",
                    "Follow me to storage",
                    1, 3, true),

                ReleaseState.WaitingAtStorage => new OfficerCommandData(
                    "RELEASE OFFICER",
                    "Collect your personal items",
                    2, 3, false),

                ReleaseState.EscortingToExitScanner => new OfficerCommandData(
                    "RELEASE OFFICER",
                    "Follow me to the exit scanner",
                    3, 3, true),

                ReleaseState.WaitingForExitScan => new OfficerCommandData(
                    "RELEASE OFFICER",
                    "Complete your exit scan to be released",
                    3, 3, false),

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
                BehindBarsUIManager.Instance?.HideOfficerCommand();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error hiding command notification: {ex.Message}");
            }
        }

        private void OnStateEnter(ReleaseState state)
        {
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: OnStateEnter({state})");

            switch (state)
            {
                case ReleaseState.Idle:
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Entering Idle state");
                    ReturnToPost();
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.MovingToPlayer:
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Entering MovingToPlayer state");
                    PlayVoiceCommand("On my way to collect the prisoner.", "Fetching");
                    NavigateToPlayer();
                    break;

                case ReleaseState.OpeningCell:
                    // OpeningCell state handler will handle the door
                    break;

                case ReleaseState.WaitingForPlayerExitCell:
                    // Reset door tracking flag for this door
                    playerDoorClearDetected = false;
                    break;

                case ReleaseState.EscortingToStorage:
                    NavigateToStorage();
                    break;

                case ReleaseState.WaitingAtStorage:
                    PlayVoiceCommand("Collect your personal items. Tell me when you're ready to leave.", "AtStorage");
                    EnableInventoryPickupStation();
                    EnableGuardReadyInteraction(); // NEW: Add interaction for player to confirm ready
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.EscortingToExitScanner:
                    PlayVoiceCommand("Time to leave. Follow me to the exit scanner.", "Escorting");
                    NavigateToExitScanner();
                    break;

                case ReleaseState.WaitingForExitScan:
                    PlayVoiceCommand("Complete your exit scan to be released.", "AtExit");
                    EnableExitScanner(); // Enable the exit scanner for interaction
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.ProcessingExit:
                    PlayVoiceCommand("You're free to go.", "AtExit");
                    HideOfficerCommandNotification();
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.ReturningToPost:
                    // Start tracking return journey time for proactive releases
                    returnJourneyStartTime = Time.time;
                    HideOfficerCommandNotification();
                    ReturnToPost();
                    break;
            }
        }

        #endregion

        #region State Handlers

        private new void HandleIdleState()
        {
            // Stay at guard post when idle
            if (guardPost != null)
            {
                float distanceToPost = Vector3.Distance(transform.position, guardPost.position);
                if (distanceToPost > 2f && (!navAgent.hasPath || navAgent.remainingDistance < 0.5f))
                {
                    MoveTo(guardPost.position);
                }
            }
        }

        private void HandleMovingToPlayerState()
        {
            // This state now navigates to the player's CELL DOOR, not the player themselves
            // The destination is set in NavigateToPlayerCell() and we wait for OnDestinationReached
            // Note: Actual transition to OpeningCell happens in HandleDestinationReached()

            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }
        }

        private void HandleOpeningCellState()
        {
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            var jailController = Core.JailController;
            int cellNumber = GetPlayerCellNumber(currentReleasee);

            // Check if player is actually in their cell
            bool isPlayerInCell = cellNumber >= 0 && jailController != null && jailController.IsPlayerInJailCellBounds(currentReleasee, cellNumber);

            if (isPlayerInCell)
            {
                // Player is in cell - open the door
                if (jailController?.doorController != null)
                {
                    bool doorOpened = jailController.doorController.OpenJailCellDoor(cellNumber);
                    if (doorOpened)
                    {
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Opened cell {cellNumber} door for {currentReleasee.name}");
                        PlayVoiceCommand("Your release has been processed. Come with me.", "Escorting");
                        ChangeReleaseState(ReleaseState.WaitingForPlayerExitCell);
                    }
                    else
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: Failed to open cell {cellNumber} door");
                        ChangeReleaseState(ReleaseState.ReturningToPost);
                    }
                }
                else
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: No door controller available");
                    ChangeReleaseState(ReleaseState.WaitingForPlayerExitCell);
                }
            }
            else
            {
                // Player is not in cell - skip door opening, proceed directly to escort
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player not in cell, proceeding directly to storage escort");
                PlayVoiceCommand("Come with me for release processing.", "Escorting");
                ChangeReleaseState(ReleaseState.EscortingToStorage);
            }
        }

        private void HandleEscortState()
        {
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Monitor movement progress during escort states
            if (currentDestination != Vector3.zero)
            {
                float distance = Vector3.Distance(transform.position, currentDestination);

                // Check if we've reached destination
                if (distance < DESTINATION_TOLERANCE || (navAgent != null && !navAgent.pathPending && navAgent.remainingDistance < DESTINATION_TOLERANCE))
                {
                    HandleDestinationReached(currentDestination);
                    return;
                }

                // NO TIMEOUT - let officer take as long as needed from far cells
            }
        }

        private void HandleWaitingForPlayerExitCellState()
        {
            // Wait indefinitely for player to exit cell - no timeout to avoid locking player in
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Only check once to prevent spam
            if (!playerDoorClearDetected)
            {
                // Check if player has moved out of the cell area
                int cellNumber = GetPlayerCellNumber(currentReleasee);
                if (cellNumber >= 0)
                {
                    var jailController = Core.JailController;
                    if (jailController != null)
                    {
                        // Check if player is no longer in cell bounds
                        bool isInCell = jailController.IsPlayerInJailCellBounds(currentReleasee, cellNumber);
                        if (!isInCell)
                        {
                            playerDoorClearDetected = true;
                            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player has exited cell {cellNumber}");
                            // Add a 2-second delay before proceeding to ensure player is fully clear
                            MelonCoroutines.Start(DelayedCellDoorClose());
                        }
                    }
                }
            }

            // NO TIMEOUT - wait indefinitely for player to avoid locking them in
        }

        private void HandleWaitingAtStorageState()
        {
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Check if player has been teleported out (emergency exit via scanner)
            if (Vector3.Distance(currentReleasee.transform.position, transform.position) > 100f)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player has been teleported - completing release process");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Wait for inventory processing to complete via OnInventoryPickupComplete callback
            // The DelayedExitScanner coroutine will handle the transition
        }

        // Removed old door state handlers - using simpler CheckForDoorTriggers approach like IntakeOfficer


        private void HandleEscortingToExitScannerState()
        {
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Monitor movement progress during escort to exit scanner
            if (currentDestination != Vector3.zero)
            {
                float distance = Vector3.Distance(transform.position, currentDestination);

                // Check if we've reached destination
                if (distance < DESTINATION_TOLERANCE || (navAgent != null && !navAgent.pathPending && navAgent.remainingDistance < DESTINATION_TOLERANCE))
                {
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Arrived at exit scanner");
                    ChangeReleaseState(ReleaseState.WaitingForExitScan);
                    return;
                }

                // NO TIMEOUT - let officer take as long as needed
            }
        }

        private void HandleWaitingForExitScanState()
        {
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // This state waits for ExitScannerStation completion
            // ExitScannerStation will call ReleaseManager when scan is complete
            if (Time.time % 20f < Time.deltaTime) // Every 20 seconds
            {
                PlayVoiceCommand("Complete your fingerprint scan to be released.", "AtExit");
            }

            // NO TIMEOUT - wait indefinitely for player to scan
        }

        private void HandleProcessingExitState()
        {
            // Final processing phase - wait a moment then complete
            if (Time.time - stateStartTime > 3f)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Exit processing complete");
                ChangeReleaseState(ReleaseState.ReturningToPost);
            }
        }

        private void HandleReturningToPostState()
        {
            // Monitor return to post
            if (guardPost != null)
            {
                float distanceToPost = Vector3.Distance(transform.position, guardPost.position);
                if (distanceToPost <= DESTINATION_TOLERANCE)
                {
                    // Learn travel time from first escort cycle for proactive future releases
                    if (!hasLearnedTravelTime && returnJourneyStartTime > 0)
                    {
                        estimatedTravelTimeToPost = Time.time - returnJourneyStartTime;
                        hasLearnedTravelTime = true;
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Learned travel time to post: {estimatedTravelTimeToPost:F1}s - will depart early for future releases");
                    }

                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Returned to post, completing release process");
                    CompleteReleaseProcess();
                    ChangeReleaseState(ReleaseState.Idle);
                }
            }
        }

        #endregion

        #region Navigation Methods

        /// <summary>
        /// Start release process (called by ReleaseManager) - now uses state machine
        /// </summary>
        public void StartReleaseEscort(Player player)
        {
            if (player == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: StartReleaseEscort - player is null");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: StartReleaseEscort called for {player.name}");

            if (!isInitialized)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: NPC not initialized yet - retrying in 1 second");
                // Retry after allowing time for initialization to complete
                MelonCoroutines.Start(RetryEscortAfterDelay(player, 1f));
                return;
            }

            if (navAgent == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cannot start escort - navAgent is null");
                return;
            }

            if (!navAgent.enabled)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cannot start escort - navAgent is disabled");
                return;
            }

            // Check for officer coordination conflicts
            if (!OfficerCoordinator.Instance.RegisterEscort(this, OfficerCoordinator.EscortType.Release, player))
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Escort delayed due to coordination conflict - will retry");
                // Retry after a short delay
                MelonCoroutines.Start(RetryEscortAfterDelay(player, 3f));
                return;
            }

            currentReleasee = player;
            isAvailable = false;
            lastKnownPlayerPosition = player.transform.position;

            ResetDoorTracking(); // Reset door operations for new release
            OnEscortStarted?.Invoke(player);

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Starting release escort for {player.name} at position {player.transform.position}");
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Officer position: {transform.position}");
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Distance to player: {Vector3.Distance(transform.position, player.transform.position):F2}m");

            ChangeReleaseState(ReleaseState.MovingToPlayer);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator RetryEscortAfterDelay(Player player, float delay)
        {
            yield return new WaitForSeconds(delay);

            // Try again
            if (isAvailable && currentReleasee == null)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Retrying escort for {player?.name} after coordination delay");
                StartReleaseEscort(player);
            }
        }

        private void NavigateToPlayer()
        {
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: NavigateToPlayer() called - determining player location");

            if (currentReleasee == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cannot navigate to player - currentReleasee is null");
                return;
            }

            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: No JailController - cannot navigate");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Check if player has an assigned cell
            int cellNumber = GetPlayerCellNumber(currentReleasee);

            // Check if player is actually IN their cell bounds
            bool isPlayerInCell = cellNumber >= 0 && jailController.IsPlayerInJailCellBounds(currentReleasee, cellNumber);

            if (isPlayerInCell)
            {
                // Player is in cell - use cell door navigation (existing logic)
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player is in cell {cellNumber} - navigating to cell door");
                NavigateToCellDoor(cellNumber);
            }
            else
            {
                // Player is NOT in cell - go directly to player's current position
                Vector3 playerPosition = currentReleasee.transform.position;
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player is NOT in cell - going directly to player at {playerPosition}");

                destinationPosition = playerPosition;
                bool moveSuccess = MoveTo(playerPosition);

                if (!moveSuccess)
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: MoveTo player failed - checking navAgent status");
                    if (navAgent == null)
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: navAgent is null");
                    }
                    else
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: navAgent enabled: {navAgent.enabled}, isOnNavMesh: {navAgent.isOnNavMesh}");
                    }
                }
            }
        }

        /// <summary>
        /// Navigate to a specific cell door point (used when player is in a cell)
        /// </summary>
        private void NavigateToCellDoor(int cellNumber)
        {
            var jailController = Core.JailController;

            if (jailController == null || jailController.cells == null || jailController.cells.Count == 0)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: No JailController or cells - cannot navigate");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            if (cellNumber >= jailController.cells.Count)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cell {cellNumber} out of range (max: {jailController.cells.Count})");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            var cell = jailController.cells[cellNumber];
            if (cell == null || cell.cellDoor == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cell {cellNumber} or door not found");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Use doorPoint (OUTSIDE position), not doorInstance position
            Vector3 cellDoorPointPos;
            if (cell.cellDoor.doorPoint != null)
            {
                cellDoorPointPos = cell.cellDoor.doorPoint.position;
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Using cell {cellNumber} doorPoint (OUTSIDE position) at {cellDoorPointPos}");
            }
            else if (cell.cellDoor.doorInstance != null)
            {
                cellDoorPointPos = cell.cellDoor.doorInstance.transform.position;
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: doorPoint not available, using doorInstance position at {cellDoorPointPos}");
            }
            else
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: No valid door position found for cell {cellNumber}");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            destinationPosition = cellDoorPointPos;
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Navigating to cell {cellNumber} door point at {cellDoorPointPos}");

            bool moveSuccess = MoveTo(cellDoorPointPos);
            if (!moveSuccess)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: MoveTo cell door failed");
            }
        }

        private void NavigateToStorage()
        {
            var storageLocation = GetStorageLocation();
            if (storageLocation == Vector3.zero)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Could not find storage location");
                OnEscortFailed?.Invoke(currentReleasee, "Could not find storage location");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            destinationPosition = storageLocation;
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Navigating to storage at {storageLocation}");
            MoveTo(storageLocation);
        }

        private void NavigateToExitScanner()
        {
            // Navigate to the exit scanner GUARD POINT, not the scanner itself!
            var jailController = Core.JailController;
            Vector3 guardPointLocation = Vector3.zero;

            // ALWAYS use the ExitScanner area GuardPoint - it exists 100%
            if (jailController?.exitScanner?.guardPoint != null)
            {
                guardPointLocation = jailController.exitScanner.guardPoint.position;
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Using ExitScanner area GuardPoint at {guardPointLocation}");
            }
            else
            {
                // If not registered, try to find and register it
                var exitScannerStation = FindObjectOfType<ExitScannerStation>();
                if (exitScannerStation != null)
                {
                    var guardPointTransform = exitScannerStation.transform.Find("GuardPoint");
                    if (guardPointTransform != null)
                    {
                        // Register the guard point in the exit scanner area
                        if (jailController?.exitScanner != null)
                        {
                            jailController.exitScanner.guardPoint = guardPointTransform;
                            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Registered ExitScanner GuardPoint at {guardPointTransform.position}");
                        }
                        guardPointLocation = guardPointTransform.position;
                    }
                    else
                    {
                        // Position guard to the side of scanner, not in front
                        guardPointLocation = exitScannerStation.transform.position + Vector3.right * 3f + Vector3.back * 2f;
                        ModLogger.Warn($"ReleaseOfficer {badgeNumber}: GuardPoint child not found, positioning to side of scanner at {guardPointLocation}");
                    }
                }
                else
                {
                    guardPointLocation = guardPost != null ? guardPost.position + Vector3.forward * 10f : transform.position + Vector3.forward * 10f;
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: No exit scanner found, using fallback position");
                }
            }

            destinationPosition = guardPointLocation;
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Navigating to exit scanner guard position at {guardPointLocation}");
            MoveTo(guardPointLocation);
        }

        private void OpenPlayerCellIfNeeded()
        {
            if (currentReleasee == null) return;

            // Find and open the player's cell if they're in one
            int cellNumber = GetPlayerCellNumber(currentReleasee);
            if (cellNumber >= 0)
            {
                var jailController = Core.JailController;
                if (jailController?.doorController != null)
                {
                    bool doorOpened = jailController.doorController.OpenJailCellDoor(cellNumber);
                    if (doorOpened)
                    {
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Opened cell {cellNumber} for {currentReleasee.name}");
                        PlayVoiceCommand("Your release has been processed. Come with me.", "Escorting");
                    }
                    else
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: Failed to open cell {cellNumber}");
                    }
                }
            }
        }

        private void EnableInventoryPickupStation()
        {
            if (currentReleasee == null) return;

            try
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Enabling interactive storage for release");

                var jailController = Core.JailController;
                if (jailController?.storage == null)
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: No storage area found");
                    return;
                }

                // Use InventoryPickup with interactive storage UI (as requested)
                Transform pickupTransform = jailController.storage.inventoryPickup;
                if (pickupTransform == null)
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: InventoryPickup GameObject not found");
                    return;
                }

                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Found InventoryPickup at {pickupTransform.position}");

                // CRITICAL: Re-enable the GameObject (it gets disabled when player confirms ready)
                pickupTransform.gameObject.SetActive(true);
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Re-enabled InventoryPickup GameObject for new release");

                // Disable other stations
                if (jailController.storage.inventoryDropOff != null)
                {
                    jailController.storage.inventoryDropOff.gameObject.SetActive(false);
                }
                Transform jailInventoryPickup = jailController.transform.Find("Storage/JailInventoryPickup");
                if (jailInventoryPickup != null)
                {
                    jailInventoryPickup.gameObject.SetActive(false);
                }

                // Get or add InventoryPickupStation component
                var inventoryPickupStation = pickupTransform.GetComponent<InventoryPickupStation>();
                if (inventoryPickupStation == null)
                {
                    inventoryPickupStation = pickupTransform.gameObject.AddComponent<InventoryPickupStation>();
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Added InventoryPickupStation component");
                }

                // Enable and prepare the station with fixed item ID capture
                inventoryPickupStation.enabled = true;
                inventoryPickupStation.PrepareStorageForPlayer(currentReleasee);

                ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ Interactive storage enabled with improved item ID capture");

                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Interact with storage to retrieve your belongings",
                        NotificationType.Instruction
                    );
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error enabling inventory pickup station: {ex.Message}");
            }
        }

        /// <summary>
        /// Enable the guard interaction for "ready to leave" confirmation
        /// </summary>
        private void EnableGuardReadyInteraction()
        {
            if (guardInteraction == null) return;

            readyInteractionEnabled = true;
            guardInteraction.SetMessage("Tell guard you're ready to leave");
            guardInteraction.SetInteractableState(InteractableObject.EInteractableState.Default);

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Enabled 'ready to leave' interaction");
        }

        /// <summary>
        /// Enable the exit scanner for release process
        /// </summary>
        private void EnableExitScanner()
        {
            try
            {
                // Find the ExitScannerStation
                var exitScannerStation = UnityEngine.Object.FindObjectOfType<ExitScannerStation>();
                if (exitScannerStation != null)
                {
                    exitScannerStation.EnableForRelease();
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Enabled exit scanner for release");

                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            "Scan your fingerprint to complete release",
                            NotificationType.Instruction
                        );
                    }
                }
                else
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Exit scanner station not found");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error enabling exit scanner: {ex.Message}");
            }
        }

        /// <summary>
        /// Disable the guard interaction
        /// </summary>
        private void DisableGuardReadyInteraction()
        {
            if (guardInteraction == null) return;

            readyInteractionEnabled = false;
            guardInteraction.SetInteractableState(InteractableObject.EInteractableState.Invalid);

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Disabled 'ready to leave' interaction");
        }

        /// <summary>
        /// Called by ReleaseManager when inventory processing is complete
        /// </summary>
        public void OnInventoryPickupComplete(Player player)
        {
            if (currentReleasee != player)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: OnInventoryPickupComplete called for wrong player - expected {currentReleasee?.name}, got {player?.name}");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: OnInventoryPickupComplete called - waiting for player confirmation");

            // NEW: Don't auto-proceed - wait for player to interact with guard
            // The interaction was already enabled in EnableGuardReadyInteraction()
            // Player must walk up to guard and press E to confirm "I'm ready to leave"
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator DelayedExitScanner(float delay)
        {
            PlayVoiceCommand("Good. Time to leave. Follow me.", "Escorting");
            yield return new WaitForSeconds(delay);

            if (currentReleaseState == ReleaseState.WaitingAtStorage)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Proceeding to exit scanner after {delay}s delay");
                ChangeReleaseState(ReleaseState.EscortingToExitScanner);
            }
        }

        /// <summary>
        /// Called when player confirms they are ready to leave via guard interaction
        /// </summary>
        public void OnPlayerConfirmedReady()
        {
            if (currentReleaseState != ReleaseState.WaitingAtStorage)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: OnPlayerConfirmedReady called but not in WaitingAtStorage state");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player confirmed ready, proceeding to exit scanner");
            PlayVoiceCommand("Good. Time to leave. Follow me to the exit scanner.", "Escorting");

            // Stop looking at player, start escorting
            StopContinuousPlayerLooking();

            // Transition to exit scanner escort
            ChangeReleaseState(ReleaseState.EscortingToExitScanner);
        }



        /// <summary>
        /// Called when exit scan is completed (NEW)
        /// </summary>
        public void OnExitScanCompleted()
        {
            if (currentReleaseState != ReleaseState.WaitingForExitScan)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: OnExitScanCompleted called but not in WaitingForExitScan state");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Exit scan completed, proceeding to final processing");
            ChangeReleaseState(ReleaseState.ProcessingExit);
        }

        #endregion

        #region Utility Methods

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForDestination(Vector3 destination, float timeout)
        {
            float startTime = Time.time;
            float currentDistance = Vector3.Distance(transform.position, destination);

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Waiting for destination - current distance: {currentDistance:F2}m to {destination}");

            // Always wait at least 2 seconds for escort to feel realistic
            yield return new WaitForSeconds(2f);

            while (Vector3.Distance(transform.position, destination) > 2f && Time.time - startTime < timeout)
            {
                yield return new WaitForSeconds(0.5f);
            }

            float finalDistance = Vector3.Distance(transform.position, destination);
            float totalTime = Time.time - startTime;

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Destination wait complete - final distance: {finalDistance:F2}m, time: {totalTime:F1}s");

            if (Time.time - startTime >= timeout)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Timeout waiting for destination {destination}");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator OpenPlayerCell(Player player)
        {
            var jailController = Core.JailController;
            if (jailController?.doorController != null)
            {
                // Find the player's cell
                int cellNumber = GetPlayerCellNumber(player);
                if (cellNumber >= 0)
                {
                    bool doorOpened = jailController.doorController.OpenJailCellDoor(cellNumber);
                    if (doorOpened)
                    {
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Opened cell {cellNumber} for {player.name}");
                        yield return new WaitForSeconds(2f); // Wait for door to fully open
                    }
                    else
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: Failed to open cell {cellNumber}");
                    }
                }
            }
            else
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: JailController or doorController not found");
            }
        }

        private Vector3 GetPlayerCellLocation(Player player)
        {
            try
            {
                // SIMPLIFIED: Just go to where the player actually is
                // If they're in a cell, the guard will find them there
                // If cell door is closed, we'll open it when we get there
                var playerPosition = player.transform.position;
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Going directly to player location: {playerPosition}");
                return playerPosition;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error getting player location: {ex.Message}");
                return Vector3.zero;
            }
        }

        private int GetPlayerCellNumber(Player player)
        {
            try
            {
                // Use the proper cell assignment system
                var cellAssignmentManager = CellAssignmentManager.Instance;
                if (cellAssignmentManager != null)
                {
                    int assignedCell = cellAssignmentManager.GetPlayerCellNumber(player);
                    if (assignedCell >= 0)
                    {
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player {player.name} assigned to cell {assignedCell}");
                        return assignedCell;
                    }
                }

                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: No cell assignment found for {player.name}");
                return -1;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error getting cell number: {ex.Message}");
                return -1;
            }
        }

        private Vector3 GetStorageLocation()
        {
            try
            {
                // Use the proper storage area from jail controller - repurpose inventoryDropOff for returns
                var jailController = Core.JailController;
                if (jailController?.storage?.inventoryDropOff != null)
                {
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Using inventoryDropOff for item returns at {jailController.storage.inventoryDropOff.position}");
                    return jailController.storage.inventoryDropOff.position;
                }

                // Fallback to inventory pickup station if it exists
                var pickupStation = FindObjectOfType<InventoryPickupStation>();
                if (pickupStation != null)
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Fallback to InventoryPickupStation at {pickupStation.transform.position}");
                    return pickupStation.transform.position;
                }

                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: No storage location found, using booking area");
                return guardPost?.position ?? transform.position;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error getting storage location: {ex.Message}");
                return transform.position;
            }
        }

        private void PlayVoiceCommand(string message, string context)
        {
            try
            {
                // Send message to player
                TrySendNPCMessage(message, 3f);

                // Update dialogue context
                if (dialogueController != null)
                {
                    dialogueController.SendContextualMessage(context);
                }

                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Voice command - {message}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error playing voice command: {ex.Message}");
            }
        }

        public void ReturnToPost()
        {
            if (guardPost != null)
            {
                MoveTo(guardPost.position);
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Returning to post");
            }
        }

        private SecurityDoorBehavior GetSecurityDoor()
        {
            // Try to get SecurityDoor component from this GameObject first
            var securityDoor = GetComponent<SecurityDoorBehavior>();
            if (securityDoor != null) return securityDoor;

            // Fallback to JailController (centralized SecurityDoor)
            return Core.JailController?.GetComponent<SecurityDoorBehavior>();
        }

        private void HandleSecurityDoorOperationComplete(string doorName)
        {
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ SecurityDoor operation completed for {doorName} during {currentReleaseState} state");

            // SecurityDoor has completed its operation - clear the active flag
            isSecurityDoorActive = false;

            // Record the time of door operation completion to prevent premature destination events
            lastDoorOperationTime = Time.time;

            // Give guard time to move away from door before resuming navigation
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Waiting for guard to clear door area before resuming navigation");
            MelonCoroutines.Start(DelayedNavigationResume());
        }

        private void HandleSecurityDoorOperationFailed(string doorName)
        {
            ModLogger.Error($"ReleaseOfficer {badgeNumber}: SecurityDoor operation FAILED for {doorName} - attempting fallback");

            // If SecurityDoor fails, try fallback direct door control
            if (doorName.Contains("Booking") || doorName.Contains("Inner"))
            {
                FallbackDirectDoorControl("BookingInnerDoor");
            }
            else if (doorName.Contains("Prison") || doorName.Contains("Entry"))
            {
                FallbackDirectDoorControl("PrisonEntryDoor");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator DelayedNavigationResume()
        {
            // Brief delay to clear door threshold without noticeable pause
            yield return new WaitForSeconds(0.3f);

            // Now safely resume navigation to the original target
            if (currentReleaseState == ReleaseState.EscortingToStorage || currentReleaseState == ReleaseState.WaitingAtStorage)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Resuming navigation to Storage after door clearance");
                NavigateToStorage();
            }
            else if (currentReleaseState == ReleaseState.EscortingToExitScanner)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Resuming navigation to ExitScanner after door clearance");
                NavigateToExitScanner();
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Navigation resumed for state: {currentReleaseState}");
        }

        private void FallbackDirectDoorControl(string doorType)
        {
            // Fallback to direct door control if SecurityDoor is not available
            var jailController = Core.JailController;
            if (jailController?.doorController == null) return;

            if (doorType == "PrisonEntryDoor")
            {
                bool opened = jailController.doorController.OpenPrisonEntryDoor();
                if (opened)
                {
                    triggeredDoorOperations.Add("PrisonEntryDoor");
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Prison entry door opened via fallback");
                }
            }
            else if (doorType == "BookingInnerDoor")
            {
                bool opened = jailController.doorController.OpenBookingInnerDoor();
                if (opened)
                {
                    triggeredDoorOperations.Add("BookingInnerDoor");
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Booking inner door opened via fallback");
                }
            }
        }

        private void CheckForDoorTriggers()
        {
            // Like IntakeOfficer - trigger doors during escort states
            // This makes the guard properly handle doors during navigation

            if (currentReleaseState == ReleaseState.EscortingToStorage)
            {
                // Open prison door when escorting FROM prison TO storage (through hallway)
                TriggerPrisonEntryDoorIfNeeded();

                // Close prison door after BOTH pass through
                CheckAndClosePrisonDoorBehind();
            }
            // NOTE: NO door needed for EscortingToExitScanner!
            // Storage and ExitScanner are both in the Booking/Hallway area already
            // The booking door is only needed when coming FROM outside (intake direction)
        }

        private void TriggerPrisonEntryDoorIfNeeded()
        {
            // Check if we've already triggered the prison entry door operation
            if (triggeredDoorOperations.Contains("PrisonEntryDoor")) return;

            var securityDoor = GetSecurityDoor();
            if (securityDoor == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: No SecurityDoor component - falling back to direct control");
                FallbackDirectDoorControl("PrisonEntryDoor");
                return;
            }

            // CRITICAL: Release direction is FROM Prison TO Hallway (opposite of intake)
            // Use PrisonDoorTrigger_FromPrison trigger (not FromHall)
            string triggerName = "PrisonDoorTrigger_FromPrison"; // Guard moving from prison to hall
            bool triggered = securityDoor.HandleDoorTrigger(triggerName, true, currentReleasee);

            if (triggered)
            {
                triggeredDoorOperations.Add("PrisonEntryDoor");
                isSecurityDoorActive = true;
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: SecurityDoor operation triggered for prison entry door (release direction)");
            }
            else
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: SecurityDoor trigger failed - using fallback");
                FallbackDirectDoorControl("PrisonEntryDoor");
            }
        }

        /// <summary>
        /// Check if officer and player are near the prison door (for opening before escort)
        /// </summary>
        private bool IsNearPrisonDoor()
        {
            try
            {
                if (currentReleasee == null) return false;

                var jailController = Core.JailController;
                if (jailController?.booking?.prisonEntryDoor?.doorInstance == null) return false;

                var doorPosition = jailController.booking.prisonEntryDoor.doorInstance.transform.position;
                var officerDistance = Vector3.Distance(transform.position, doorPosition);
                var playerDistance = Vector3.Distance(currentReleasee.transform.position, doorPosition);

                // Both should be within 8 meters of the door
                return officerDistance < 8f && playerDistance < 10f;
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error checking prison door proximity: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if officer and player are near the booking door (for opening before escort)
        /// </summary>
        private bool IsNearBookingDoor()
        {
            try
            {
                if (currentReleasee == null) return false;

                var jailController = Core.JailController;
                if (jailController?.booking?.bookingInnerDoor?.doorInstance == null) return false;

                var doorPosition = jailController.booking.bookingInnerDoor.doorInstance.transform.position;
                var officerDistance = Vector3.Distance(transform.position, doorPosition);
                var playerDistance = Vector3.Distance(currentReleasee.transform.position, doorPosition);

                // Both should be within 8 meters of the door
                return officerDistance < 8f && playerDistance < 10f;
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error checking booking door proximity: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if both player and officer are in the storage area (for closing doors behind them)
        /// </summary>
        private bool ArePlayerAndOfficerInStorageArea()
        {
            try
            {
                if (currentReleasee == null) return false;

                var storageLocation = GetStorageLocation();
                if (storageLocation == Vector3.zero) return false;

                var officerDistance = Vector3.Distance(transform.position, storageLocation);
                var playerDistance = Vector3.Distance(currentReleasee.transform.position, storageLocation);

                // Both should be within 6 meters of storage
                return officerDistance < 6f && playerDistance < 6f;
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error checking storage area position: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if both player and officer are at the exit scanner (for closing doors behind them)
        /// </summary>
        private bool ArePlayerAndOfficerAtExitScanner()
        {
            try
            {
                if (currentReleasee == null) return false;

                var jailController = Core.JailController;
                if (jailController?.exitScanner?.areaRoot == null) return false;

                var scannerPosition = jailController.exitScanner.areaRoot.position;
                var officerDistance = Vector3.Distance(transform.position, scannerPosition);
                var playerDistance = Vector3.Distance(currentReleasee.transform.position, scannerPosition);

                // Both should be within 6 meters of exit scanner
                return officerDistance < 6f && playerDistance < 6f;
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error checking exit scanner position: {ex.Message}");
                return false;
            }
        }

        private void ResetDoorTracking()
        {
            triggeredDoorOperations.Clear();
            isSecurityDoorActive = false;
            lastDoorOperationTime = 0f;
            stationDestinationProcessed.Clear();
            playerDoorClearDetected = false;
            doorCloseInitiated = false;
            prisonDoorClosed = false; // Reset door closed flag
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Door tracking reset for new release");
        }

        // Track if we've already closed the prison door
        private bool prisonDoorClosed = false;
        private float lastDoorCheckTime = 0f; // Throttle door checks

        private void CheckAndClosePrisonDoorBehind()
        {
            // Don't check if already closed
            if (prisonDoorClosed) return;

            // Check if we've opened the door first
            if (!triggeredDoorOperations.Contains("PrisonEntryDoor")) return;

            // Only check every half second to prevent spam (not every frame!)
            if (Time.time - lastDoorCheckTime < 0.5f) return;
            lastDoorCheckTime = Time.time;

            // Check if BOTH guard and player are on the STORAGE side of the door (past it)
            if (AreBothPastPrisonDoor())
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Both guard and player past prison door - closing behind them");

                var jailController = Core.JailController;
                if (jailController?.doorController != null)
                {
                    bool doorClosed = jailController.doorController.ClosePrisonEntryDoor();
                    if (doorClosed)
                    {
                        prisonDoorClosed = true;
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Prison entry door closed behind escort (secure)");
                    }
                }
            }
        }

        private bool AreBothPastPrisonDoor()
        {
            try
            {
                if (currentReleasee == null) return false;

                var jailController = Core.JailController;
                if (jailController?.booking?.prisonEntryDoor?.doorInstance == null) return false;

                var doorPosition = jailController.booking.prisonEntryDoor.doorInstance.transform.position;
                var doorForward = jailController.booking.prisonEntryDoor.doorInstance.transform.forward;

                // Calculate which side of door each person is on
                // Positive = storage side, negative = prison side
                float guardSide = Vector3.Dot(transform.position - doorPosition, doorForward);
                float playerSide = Vector3.Dot(currentReleasee.transform.position - doorPosition, doorForward);

                // Both should be on storage side (positive) and at least 3m past door
                bool guardPast = guardSide > 3f;
                bool playerPast = playerSide > 3f;

                // Only log when close to threshold to reduce spam
                if (!guardPast || !playerPast)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Prison door check - Guard side: {guardSide:F1}m, Player side: {playerSide:F1}m");
                }

                return guardPast && playerPast;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error checking prison door clearance: {ex.Message}");
                return false;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator DelayedCellDoorClose()
        {
            // Wait 2 seconds to ensure player has fully cleared, like IntakeOfficer
            yield return new WaitForSeconds(2f);

            // CRITICAL FIX: Verify guard is OUTSIDE cell before closing door
            int cellNumber = GetPlayerCellNumber(currentReleasee);
            if (cellNumber >= 0)
            {
                var jailController = Core.JailController;
                if (jailController != null)
                {
                    // Check if guard is still inside cell bounds - if so, move them out first
                    var cell = jailController.GetCellByIndex(cellNumber);
                    if (cell?.cellBounds != null)
                    {
                        // Get the BoxCollider from the cellBounds transform
                        var cellCollider = cell.cellBounds.GetComponent<BoxCollider>();
                        if (cellCollider != null)
                        {
                            bool guardInCell = cellCollider.bounds.Contains(transform.position);
                            if (guardInCell)
                            {
                                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Guard is inside cell - moving outside before closing door");

                                // Move guard to door point (OUTSIDE position)
                                if (cell.cellDoor?.doorPoint != null)
                                {
                                    transform.position = cell.cellDoor.doorPoint.position;
                                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Repositioned guard to door point (outside)");
                                }
                            }
                        }
                    }

                    // Now close the door
                    if (jailController.doorController != null)
                    {
                        bool doorClosed = jailController.doorController.CloseJailCellDoor(cellNumber);
                        if (doorClosed)
                        {
                            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Cell {cellNumber} door closed after delay (guard safe outside)");
                        }
                        else
                        {
                            ModLogger.Error($"ReleaseOfficer {badgeNumber}: Failed to close cell {cellNumber} door");
                        }
                    }
                }
            }

            // Proceed to escort to storage
            PlayVoiceCommand("Follow me to storage.", "Escorting");
            ChangeReleaseState(ReleaseState.EscortingToStorage);
        }

        private void StartContinuousPlayerLooking()
        {
            // Stop any existing continuous looking
            StopContinuousPlayerLooking();

            // Start new continuous looking coroutine
            continuousLookingCoroutine = MelonCoroutines.Start(ContinuousPlayerLookingCoroutine());
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Started continuous player looking");
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

                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Stopped continuous player looking");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ContinuousPlayerLookingCoroutine()
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
                if (currentReleasee == null) return;

                Vector3 playerPosition = currentReleasee.transform.position;
                Vector3 currentPos = transform.position;

                // Calculate the look direction
                Vector3 lookDirection = (playerPosition - currentPos).normalized;
                lookDirection.y = 0; // Keep on horizontal plane

                if (lookDirection != Vector3.zero)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(lookDirection);

                    // Start smooth rotation coroutine instead of instant
                    MelonCoroutines.Start(SmoothRotateToTarget(targetRotation, 0.3f));

                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Started smooth rotation to face player at {playerPosition}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error in continuous rotation: {ex.Message}");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator SmoothRotateToTarget(Quaternion targetRotation, float duration)
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

        private void CompleteReleaseProcess()
        {
            if (currentReleasee != null)
            {
                OnEscortCompleted?.Invoke(currentReleasee);
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Release process completed for {currentReleasee.name}");
            }

            // Close all doors that were opened during release process (like IntakeOfficer)
            CloseAllReleaseDoors();

            // Unregister from officer coordination
            OfficerCoordinator.Instance.UnregisterEscort(this);

            // Reset state
            currentReleasee = null;
            isAvailable = true;
            destinationPosition = Vector3.zero;
            lastKnownPlayerPosition = Vector3.zero;

            // Stop continuous looking
            StopContinuousPlayerLooking();

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Release officer now available for new assignments");
        }

        /// <summary>
        /// Close all doors that were opened during release escort - mirrors IntakeOfficer's CloseAllIntakeDoors
        /// </summary>
        private void CloseAllReleaseDoors()
        {
            var jailController = Core.JailController;
            if (jailController?.doorController == null)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: No door controller available for closing doors");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Closing all doors opened during release process");

            // Close and lock the cell door if one was opened
            int cellNumber = GetPlayerCellNumber(currentReleasee);
            if (cellNumber >= 0)
            {
                jailController.doorController.CloseJailCellDoor(cellNumber);
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Closed cell {cellNumber} door");
            }

            // Close prison entry door if it was opened
            if (triggeredDoorOperations.Contains("PrisonEntryDoor") && !prisonDoorClosed)
            {
                jailController.doorController.ClosePrisonEntryDoor();
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Closed prison entry door");
            }

            // NOTE: Booking door not used during release - Storage and ExitScanner both in Booking area

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: All release doors secured");
        }

        /// <summary>
        /// Override MoveTo to add debug logging and stop continuous looking
        /// </summary>
        public override bool MoveTo(Vector3 destination, float tolerance = -1f)
        {
            // Stop continuous looking when starting to move
            StopContinuousPlayerLooking();

            // Update officer coordinator with new destination
            OfficerCoordinator.Instance.UpdateEscortDestination(this, destination);

            // Call base MoveTo with original destination
            bool success = base.MoveTo(destination, tolerance);

            if (success)
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Navigation started to {destination}, stopped continuous looking");
            }

            return success;
        }

        #endregion

        #region Public Interface

        public bool IsAvailable()
        {
            return isAvailable && currentReleaseState == ReleaseState.Idle;
        }

        public void SetAvailable(bool available)
        {
            isAvailable = available;
            if (!available && currentReleaseState == ReleaseState.Idle)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Now busy");
            }
            else if (available)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Now available");
                ChangeReleaseState(ReleaseState.Idle);
            }
        }

        public string GetBadgeNumber()
        {
            return badgeNumber;
        }

        public new ReleaseState GetCurrentState()
        {
            return currentReleaseState;
        }

        public Player GetCurrentReleasee()
        {
            return currentReleasee;
        }

        /// <summary>
        /// Get estimated travel time from cell to guard post for proactive release scheduling
        /// Returns 0 if not yet learned, otherwise returns learned time in seconds
        /// </summary>
        public float GetEstimatedTravelTime()
        {
            return hasLearnedTravelTime ? estimatedTravelTimeToPost : 0f;
        }

        #endregion

        #region Cleanup

        protected override void OnDestroy()
        {
            // Stop any active continuous looking
            StopContinuousPlayerLooking();

            // Unregister from ReleaseManager
            if (ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.UnregisterOfficer(this);
            }

            // Unsubscribe from SecurityDoor events
            var securityDoor = GetSecurityDoor();
            if (securityDoor != null)
            {
                securityDoor.OnDoorOperationComplete -= HandleSecurityDoorOperationComplete;
                securityDoor.OnDoorOperationFailed -= HandleSecurityDoorOperationFailed;
            }

            // Unsubscribe from movement events
            OnDestinationReached -= HandleDestinationReached;

            // Clean up state
            currentReleasee = null;
            isAvailable = true;

            base.OnDestroy();
        }

        #endregion
    }
}