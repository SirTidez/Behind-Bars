using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.Dialogue;
using Behind_Bars.UI;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Dialogue;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Interaction;
using ScheduleOne.Dialogue;
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
            MovingToPrisonDoor,        // Moving to prison entry door for transition
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
        private const float PLAYER_CHECK_INTERVAL = 1f; // How often to update player position (faster for responsive door handling)
        private const float PRISON_DOOR_WAIT_LOG_INTERVAL = 2f; // Log wait status every 2 seconds
        private const float PRISON_DOOR_PROXIMITY = 11f; // Distance to start waiting for door
        private const float DOOR_CLOSE_RETRY_DELAY = 2f;
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

        // Dialogue system for ready-to-leave interaction
        private DialogueHandler dialogueHandler;
        private DialogueController baseDialogueController;
        private NPCDialogueWrapper npcDialogueWrapper;
        private const string READY_TO_LEAVE_CONTAINER_NAME = "ReleaseOfficer_ReadyToLeave";
        private bool dialogueContainerRegistered = false;
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
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Subscribed to SecurityDoor events");
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
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Set to Idle state during initialization");
            }
            else
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Preserving active state {currentReleaseState} during initialization");
            }

            ModLogger.Debug($"ReleaseOfficer {badgeNumber} initialized and registered");
        }

        private void EnsureSecurityDoorComponent()
        {
            // Check if SecurityDoorBehavior is already attached
            var existingSecurityDoor = GetComponent<SecurityDoorBehavior>();
            if (existingSecurityDoor == null)
            {
                // Add SecurityDoorBehavior component to this ReleaseOfficer
                var securityDoor = gameObject.AddComponent<SecurityDoorBehavior>();
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Added SecurityDoorBehavior component for door operations");
            }
            else
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: SecurityDoorBehavior component already attached");
            }
        }

        private void FindGuardPost()
        {
            // Look for a guard post or use the booking area - use guardSpawns[1] for release officers
            var jailController = Core.JailController;
            if (jailController?.booking?.guardSpawns != null && jailController.booking.guardSpawns.Count > 1)
            {
                guardPost = jailController.booking.guardSpawns[1]; // Use second guard spawn for release officers
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Found guard post at {guardPost.position}");
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
                
                // NEW: Set up dialogue container for ready-to-leave interaction
                SetupReadyToLeaveDialogue();
                
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Dialogue system configured");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting up dialogue for ReleaseOfficer {badgeNumber}: {e.Message}");
            }
        }

        /// <summary>
        /// Sets up the dialogue container for the "ready to leave" interaction
        /// Returns true if setup was successful, false otherwise
        /// </summary>
        private bool SetupReadyToLeaveDialogue()
        {
            try
            {
                // CRITICAL: ReleaseOfficerBehavior might be on a child GameObject
                // We need to find the root NPC GameObject first, then search for dialogue components
                GameObject rootNPCGameObject = GetRootNPCGameObject();
                
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: ReleaseOfficerBehavior is on GameObject: {gameObject.name}");
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Root NPC GameObject: {rootNPCGameObject.name}");
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Are they the same? {gameObject == rootNPCGameObject}");
                
                // Get DialogueHandler and DialogueController components from root NPC GameObject
                // Use GetComponentInChildren to match S1API - components might be on child objects (like Avatar)
                dialogueHandler = rootNPCGameObject.GetComponentInChildren<DialogueHandler>(true);
                baseDialogueController = rootNPCGameObject.GetComponentInChildren<DialogueController>(true);
                
                if (dialogueHandler != null)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Found DialogueHandler on: {dialogueHandler.gameObject.name} (parent: {dialogueHandler.transform.parent?.name ?? "root"})");
                }
                if (baseDialogueController != null)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Found DialogueController on: {baseDialogueController.gameObject.name} (parent: {baseDialogueController.transform.parent?.name ?? "root"})");
                }
                
                // Initialize NPCDialogueWrapper for easier dialogue management - use root NPC GameObject
                npcDialogueWrapper = new NPCDialogueWrapper(rootNPCGameObject);
                
                // If components don't exist, try to add them to root NPC GameObject (for guards created without DirectNPCBuilder)
                if (dialogueHandler == null)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: DialogueHandler not found, attempting to add component to root NPC GameObject");
                    try
                    {
#if !MONO
                        dialogueHandler = rootNPCGameObject.AddComponent<Il2CppScheduleOne.Dialogue.DialogueHandler>();
#else
                        dialogueHandler = rootNPCGameObject.AddComponent<ScheduleOne.Dialogue.DialogueHandler>();
#endif
                        if (dialogueHandler == null)
                        {
                            ModLogger.Error($"ReleaseOfficer {badgeNumber}: AddComponent returned null for DialogueHandler");
                            return false;
                        }
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ Added DialogueHandler component to root NPC GameObject");
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: Exception adding DialogueHandler: {ex.Message}\n{ex.StackTrace}");
                        return false;
                    }
                }
                else
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: DialogueHandler already exists");
                }
                
                if (baseDialogueController == null)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: DialogueController not found, attempting to add component to root NPC GameObject");
                    try
                    {
#if !MONO
                        baseDialogueController = rootNPCGameObject.AddComponent<Il2CppScheduleOne.Dialogue.DialogueController>();
#else
                        baseDialogueController = rootNPCGameObject.AddComponent<ScheduleOne.Dialogue.DialogueController>();
#endif
                        if (baseDialogueController == null)
                        {
                            ModLogger.Error($"ReleaseOfficer {badgeNumber}: AddComponent returned null for DialogueController");
                            return false;
                        }
                        baseDialogueController.DialogueEnabled = true;
                        baseDialogueController.UseDialogueBehaviour = true;
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ Added DialogueController component");
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: Exception adding DialogueController: {ex.Message}\n{ex.StackTrace}");
                        return false;
                    }
                }
                else
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: DialogueController already exists");
                }

                // Build the dialogue container
                // NOTE: S1API examples use "ENTRY" as the start node label - the dialogue system may expect this
                var builder = new DialogueContainerBuilder();
                
                builder.AddNode("ENTRY", "Collect your personal items. Tell me when you're ready to leave.", choices =>
                {
                    choices.Add("ready", "I'm ready to leave.", "confirm");
                    choices.Add("not_ready", "Not yet.", "end");
                });
                
                builder.AddNode("confirm", "Good. Time to leave. Follow me.", choices =>
                {
                    choices.Add("acknowledge", "Let's go.", "end");
                });
                
                builder.AddNode("end", "", null); // End node with no choices
                
                builder.SetAllowExit(true);
                
                // Build and register the container
                var container = builder.Build(READY_TO_LEAVE_CONTAINER_NAME);
                
                // Register with DialogueHandler
                if (dialogueHandler.dialogueContainers == null)
                {
#if !MONO
                    dialogueHandler.dialogueContainers = new Il2CppSystem.Collections.Generic.List<DialogueContainer>();
#else
                    dialogueHandler.dialogueContainers = new System.Collections.Generic.List<DialogueContainer>();
#endif
                }
                
                dialogueHandler.dialogueContainers.Add(container);
                dialogueContainerRegistered = true;
                
                // Register choice listener for "acknowledge" choice ("Let's go") - this triggers movement to exit scanner
                DialogueChoiceListener.Register(dialogueHandler, "acknowledge", OnPlayerAcknowledgeDialogueChoice);
                
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Ready-to-leave dialogue container registered successfully");
                return true;
            }
            catch (Exception e)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error setting up ready-to-leave dialogue: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Called when player selects "Let's go" (acknowledge) in dialogue
        /// THIS IS THE ONLY PLACE that triggers movement to exit scanner
        /// The release officer will NOT move until this choice is selected
        /// 
        /// Dialogue flow:
        ///   1. Player selects "I'm ready to leave" -> advances to confirm node (no action needed)
        ///   2. Player selects "Let's go" -> THIS method is called -> triggers movement to exit scanner
        /// </summary>
        private void OnPlayerAcknowledgeDialogueChoice()
        {
            if (currentReleaseState != ReleaseState.WaitingAtStorage)
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Acknowledge dialogue choice triggered but not in WaitingAtStorage state");
                return;
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player selected 'Let's go' - proceeding to exit scanner");

            // CRITICAL: End the dialogue FIRST before proceeding with any logic
            // This prevents the blank dialogue screen from freezing the officer
            try
            {
                if (npcDialogueWrapper != null)
                {
                    npcDialogueWrapper.End();
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Dialogue ended via NPCDialogueWrapper");
                }
                else if (dialogueHandler != null)
                {
                    dialogueHandler.EndDialogue();
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Dialogue ended via DialogueHandler");
                }
                else
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Cannot end dialogue - both npcDialogueWrapper and dialogueHandler are null");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error ending dialogue: {ex.Message}");
            }
            // Finalize inventory processing now that player is done with storage
            FinalizeInventoryPickup();

            // Disable the dialogue override
            DisableReadyToLeaveDialogue();

            // CRITICAL: This is the ONLY place that triggers movement to exit scanner
            // The release officer will wait at storage until player selects "Let's go"
            OnPlayerConfirmedReady();
        }

        private string GenerateBadgeNumber()
        {
            return $"R{UnityEngine.Random.Range(1000, 9999)}";
        }

        /// <summary>
        /// Gets the root NPC GameObject - handles case where ReleaseOfficerBehavior might be on a child object
        /// </summary>
        private GameObject GetRootNPCGameObject()
        {
            // First, check if we're on the root (has NPC component)
            if (npcComponent != null && npcComponent.gameObject == gameObject)
            {
                return gameObject;
            }
            
            // If npcComponent is null, try to find it
            if (npcComponent == null)
            {
                // Try to get from this GameObject first
                npcComponent = GetComponent<NPC>();
                if (npcComponent != null)
                {
                    return gameObject;
                }
                
                // Try parent
                npcComponent = GetComponentInParent<NPC>(true);
                if (npcComponent != null)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Found NPC component on parent: {npcComponent.gameObject.name}");
                    return npcComponent.gameObject;
                }
            }
            else
            {
                // npcComponent exists but might be on parent
                if (npcComponent.gameObject != gameObject)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: NPC component is on different GameObject: {npcComponent.gameObject.name}");
                    return npcComponent.gameObject;
                }
            }
            
            // Fallback: return this GameObject (shouldn't happen, but safety check)
            ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Could not find root NPC GameObject, using current GameObject");
            return gameObject;
        }

        private void SetupGuardInteraction()
        {
            // Get root NPC GameObject first
            GameObject rootNPCGameObject = GetRootNPCGameObject();
            
            // S1API pattern: Ensure InteractableObject exists and is linked to NPC.intObj
            // Use GetComponentInChildren to match S1API - InteractableObject might be on child object
            var interactable = rootNPCGameObject.GetComponentInChildren<InteractableObject>(true);
            if (interactable == null)
            {
                interactable = rootNPCGameObject.AddComponent<InteractableObject>();
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Added InteractableObject component to root NPC GameObject");
            }
            
            // Link InteractableObject to base NPC component if available (S1API pattern)
            // Ensure we have npcComponent reference
            if (npcComponent == null)
            {
                npcComponent = rootNPCGameObject.GetComponent<NPC>();
            }
            
            if (npcComponent != null && interactable != null)
            {
                try
                {
#if !MONO
                    npcComponent.intObj = interactable;
#else
                    // Use reflection for Mono
                    var intObjField = typeof(NPC).GetField("intObj", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    intObjField?.SetValue(npcComponent, interactable);
#endif
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Linked InteractableObject to NPC.intObj");
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Could not link InteractableObject to NPC.intObj: {ex.Message}");
                }
            }

            // Keep DialogueController enabled - we need it for dialogue system
            // Use GetComponentInChildren to match S1API - might be on child object
            // Search from root NPC GameObject
            var dialogueControllers = rootNPCGameObject.GetComponentsInChildren<DialogueController>(true);
            foreach (var controller in dialogueControllers)
            {
                if (controller != null && !controller.GetType().Name.Contains("JailNPC"))
                {
                    controller.DialogueEnabled = true;
                    controller.UseDialogueBehaviour = true;
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Configured DialogueController for custom dialogue on: {controller.gameObject.name}");
                }
            }

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Guard interaction setup complete - using dialogue system");
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
            
            // Continuously ensure custom dialogue container stays active and greetings stay disabled
            if (readyInteractionEnabled && baseDialogueController != null)
            {
                // Ensure all greeting overrides stay disabled
                if (baseDialogueController.GreetingOverrides != null)
                {
                    foreach (var greetingOverride in baseDialogueController.GreetingOverrides)
                    {
                        if (greetingOverride.ShouldShow)
                        {
                            greetingOverride.ShouldShow = false;
                            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Disabled re-enabled greeting override in Update()");
                        }
                    }
                }
                
                // Ensure container is still first in the list
                if (dialogueHandler != null && dialogueHandler.dialogueContainers != null && dialogueHandler.dialogueContainers.Count > 0)
                {
                    var targetContainer = dialogueHandler.dialogueContainers.Find(c => c != null && c.name == READY_TO_LEAVE_CONTAINER_NAME);
                    if (targetContainer != null && dialogueHandler.dialogueContainers[0] != targetContainer)
                    {
                        dialogueHandler.dialogueContainers.Remove(targetContainer);
                        dialogueHandler.dialogueContainers.Insert(0, targetContainer);
                        ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Re-ensured container is first in Update()");
                    }
                }
            }
            
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

            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Destination reached at {destination} during state {currentReleaseState}");

            // Handle state transitions based on current state
            switch (currentReleaseState)
            {
                case ReleaseState.MovingToPlayer:
                    // We've reached the cell door, now open it
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Arrived at cell door, opening cell");
                    ChangeReleaseState(ReleaseState.OpeningCell);
                    break;

                case ReleaseState.EscortingToStorage:
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Arrived at storage area");
                    ChangeReleaseState(ReleaseState.WaitingAtStorage);
                    break;

                case ReleaseState.EscortingToExitScanner:
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Arrived at exit scanner");
                    ChangeReleaseState(ReleaseState.WaitingForExitScan);
                    break;

                case ReleaseState.ReturningToPost:
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Returned to guard post");
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
            // Also exclude MovingToPrisonDoor as SecurityDoorBehavior handles timing
            bool isWaitingState = currentReleaseState == ReleaseState.Idle ||
                                  currentReleaseState == ReleaseState.WaitingForPlayerExitCell ||
                                  currentReleaseState == ReleaseState.MovingToPrisonDoor ||
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

                case ReleaseState.MovingToPrisonDoor:
                    HandleMovingToPrisonDoorState();
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
            return state == ReleaseState.MovingToPrisonDoor ||
                   state == ReleaseState.EscortingToStorage ||
                   state == ReleaseState.EscortingToExitScanner ||
                   state == ReleaseState.MovingToPlayer;
        }

        private void CheckAndUpdatePlayerPosition()
        {
            if (currentReleasee == null || Time.time - lastPlayerPositionCheck < PLAYER_CHECK_INTERVAL)
                return;

            lastPlayerPositionCheck = Time.time;

            Vector3 currentPlayerPos = currentReleasee.transform.position;
            float distanceToPlayer = Vector3.Distance(transform.position, currentPlayerPos);

            // Update last known position tracking
            bool playerMovedSignificantly = Vector3.Distance(currentPlayerPos, lastKnownPlayerPosition) > 2f;
            if (playerMovedSignificantly)
            {
                lastKnownPlayerPosition = currentPlayerPos;
            }

            // If we're moving to player, update destination when player moves
            if (currentReleaseState == ReleaseState.MovingToPlayer && playerMovedSignificantly)
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Player moved, updating navigation to {currentPlayerPos}");
                NavigateToPlayer();
            }
            else if (currentReleaseState == ReleaseState.EscortingToStorage || currentReleaseState == ReleaseState.EscortingToExitScanner)
            {
                // During escort, check if player is falling behind
                if (distanceToPlayer > ESCORT_FOLLOW_DISTANCE)
                {
                    // Only remind occasionally (voice command has its own throttling)
                    PlayVoiceCommand("Stay close! Follow me.", "Escorting");
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
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: {oldState} → {newState}");

            // Notify ReleaseManager of status updates for key states
            if (currentReleasee != null && ShouldNotifyStatusUpdate(newState))
            {
                OnStatusUpdate?.Invoke(currentReleasee, newState);
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Notified ReleaseManager of status update: {newState}");
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

            // CRITICAL: Skip ALL greeting updates when override container is active
            // The dialogue system may prioritize greeting overrides over containers, so we must prevent any greeting updates
            if (readyInteractionEnabled)
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Skipping greeting update - override container is active (readyInteractionEnabled=true)");
                
                // Also ensure greeting overrides stay cleared
                if (baseDialogueController != null && baseDialogueController.GreetingOverrides != null)
                {
                    foreach (var greetingOverride in baseDialogueController.GreetingOverrides)
                    {
                        greetingOverride.ShouldShow = false;
                    }
                }
                
                return;
            }

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
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: OnStateEnter({state})");

            switch (state)
            {
                case ReleaseState.Idle:
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Entering Idle state");
                    ReturnToPost();
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.MovingToPlayer:
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Entering MovingToPlayer state");
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

                case ReleaseState.MovingToPrisonDoor:
                    PlayVoiceCommand("Follow me through the door.", "Escorting");
                    NavigateToPrisonDoor();
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
                        ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Opened cell {cellNumber} door for {currentReleasee.name}");
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
                // Player is not in cell - skip door opening, proceed to prison door
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Player not in cell, proceeding to prison door");
                ChangeReleaseState(ReleaseState.MovingToPrisonDoor);
            }
        }

        private void HandleMovingToPrisonDoorState()
        {
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Check if SecurityDoorBehavior is already handling the door operation
            var securityDoor = GetSecurityDoor();
            if (securityDoor != null && securityDoor.IsBusy())
            {
                // SecurityDoorBehavior is in control - wait for it to complete
                return;
            }

            // Check if we've reached the door entry point
            var jailController = Core.JailController;
            var prisonDoor = jailController?.booking?.prisonEntryDoor;
            if (prisonDoor?.doorPoint == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Could not find prison door entry point");
                // Fallback: proceed directly to storage escort
                ChangeReleaseState(ReleaseState.EscortingToStorage);
                return;
            }

            Vector3 doorEntryPoint = prisonDoor.doorPoint.position;
            float distanceToDoor = Vector3.Distance(transform.position, doorEntryPoint);

            // If we're close enough to the door, trigger SecurityDoorBehavior
            if (distanceToDoor < DESTINATION_TOLERANCE || (navAgent != null && !navAgent.pathPending && navAgent.remainingDistance < DESTINATION_TOLERANCE))
            {
                // We've reached the door - trigger SecurityDoorBehavior to handle the operation
                if (securityDoor != null)
                {
                    string triggerName = "PrisonDoorTrigger_FromPrison"; // Guard moving from prison to hall
                    bool triggered = securityDoor.HandleDoorTrigger(triggerName, true, currentReleasee);

                    if (triggered)
                    {
                        isSecurityDoorActive = true;
                        triggeredDoorOperations.Add("PrisonEntryDoor");
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: SecurityDoor operation triggered for prison entry door - waiting for completion");
                    }
                    else
                    {
                        ModLogger.Warn($"ReleaseOfficer {badgeNumber}: SecurityDoor trigger failed - proceeding directly to storage");
                        ChangeReleaseState(ReleaseState.EscortingToStorage);
                    }
                }
                else
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: No SecurityDoor component - proceeding directly to storage");
                    ChangeReleaseState(ReleaseState.EscortingToStorage);
                }
            }
            // Otherwise, continue navigating to door (handled by OnStateEnter navigation)
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
                            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Player has exited cell {cellNumber}");
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
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Player has been teleported - completing release process");
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
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Arrived at exit scanner");
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

        private void NavigateToPrisonDoor()
        {
            var jailController = Core.JailController;
            var prisonDoor = jailController?.booking?.prisonEntryDoor;
            if (prisonDoor?.doorPoint == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Could not find prison door entry point - proceeding directly to storage");
                ChangeReleaseState(ReleaseState.EscortingToStorage);
                return;
            }

            Vector3 doorEntryPoint = prisonDoor.doorPoint.position;
            destinationPosition = doorEntryPoint;
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Navigating to prison door entry point at {doorEntryPoint}");
            MoveTo(doorEntryPoint);
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
            if (readyInteractionEnabled) return;
            
            readyInteractionEnabled = true;
            
            // Enable dialogue container override instead of InteractableObject
            EnableReadyToLeaveDialogue();
            
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Ready-to-leave dialogue enabled");
        }

        /// <summary>
        /// Enables the ready-to-leave dialogue container override
        /// Attempts lazy initialization if components weren't ready during setup
        /// </summary>
        private void EnableReadyToLeaveDialogue()
        {
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: EnableReadyToLeaveDialogue called - containerRegistered={dialogueContainerRegistered}");
            
            // Try lazy initialization if dialogue container wasn't registered yet
            if (!dialogueContainerRegistered)
            {
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Dialogue container not registered, attempting lazy initialization");
                if (!SetupReadyToLeaveDialogue())
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: Lazy initialization failed - dialogue will not be available");
                    // Don't return - try to continue anyway in case components exist but container registration failed
                }
            }
            
            // Ensure components are still valid (re-check even if we just set them up)
                if (baseDialogueController == null)
                {
                    // Get root NPC GameObject and search from there
                    GameObject rootNPCGameObject = GetRootNPCGameObject();
                    baseDialogueController = rootNPCGameObject.GetComponentInChildren<DialogueController>(true);
                    if (baseDialogueController == null)
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cannot enable dialogue - DialogueController not found on root NPC GameObject after setup attempt");
                        return;
                    }
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Found DialogueController on root NPC GameObject: {baseDialogueController.gameObject.name}");
                }
            
            if (dialogueHandler == null)
            {
                // Use GetComponentInChildren to match S1API - DialogueHandler might be on child object
                dialogueHandler = GetComponentInChildren<DialogueHandler>(true);
                if (dialogueHandler == null)
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cannot enable dialogue - DialogueHandler not found after setup attempt");
                    return;
                }
            }
            
            // Verify container exists
            if (dialogueHandler.dialogueContainers == null || dialogueHandler.dialogueContainers.Count == 0)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: DialogueHandler has no containers registered - attempting to rebuild");
                if (!SetupReadyToLeaveDialogue())
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: Failed to rebuild dialogue container");
                    return;
                }
            }
            
            try
            {
                // Find the container in the handler
                DialogueContainer targetContainer = null;
                if (dialogueHandler.dialogueContainers != null)
                {
                    foreach (var container in dialogueHandler.dialogueContainers)
                    {
                        if (container != null && container.name == READY_TO_LEAVE_CONTAINER_NAME)
                        {
                            targetContainer = container;
                            break;
                        }
                    }
                }
                
                if (targetContainer == null)
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Container '{READY_TO_LEAVE_CONTAINER_NAME}' not found. Available containers: {GetContainerNames()}");
                    // Try one more time to set up
                    if (SetupReadyToLeaveDialogue())
                    {
                        // Try to find it again
                        if (dialogueHandler.dialogueContainers != null)
                        {
                            foreach (var container in dialogueHandler.dialogueContainers)
                            {
                                if (container != null && container.name == READY_TO_LEAVE_CONTAINER_NAME)
                                {
                                    targetContainer = container;
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (targetContainer == null)
                    {
                        ModLogger.Error($"ReleaseOfficer {badgeNumber}: Failed to find or create dialogue container after retry");
                        return;
                    }
                }
                
                // Ensure container is properly registered and FIRST in DialogueHandler's list
                // The dialogue system may check dialogueContainers list and use the first available container
                if (dialogueHandler.dialogueContainers != null)
                {
                    // Remove our container if it exists elsewhere in the list
                    dialogueHandler.dialogueContainers.Remove(targetContainer);
                    
                    // Add it as the FIRST container (index 0) - this ensures it's selected first
                    dialogueHandler.dialogueContainers.Insert(0, targetContainer);
                    
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Container '{READY_TO_LEAVE_CONTAINER_NAME}' set as FIRST in dialogueContainers list (index 0)");
                    
                    // Verify container structure
                    if (targetContainer.DialogueNodeData != null)
                    {
                        ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Container has {targetContainer.DialogueNodeData.Count} nodes");
                        foreach (var node in targetContainer.DialogueNodeData)
                        {
                            if (node != null)
                            {
                                ModLogger.Debug($"ReleaseOfficer {badgeNumber}:   - Node '{node.DialogueNodeLabel}': '{node.DialogueText}' ({node.choices?.Length ?? 0} choices)");
                            }
                        }
                    }
                    
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: dialogueContainers list now has {dialogueHandler.dialogueContainers.Count} container(s). First: {(dialogueHandler.dialogueContainers.Count > 0 && dialogueHandler.dialogueContainers[0] != null ? dialogueHandler.dialogueContainers[0].name : "null")}");
                }
                
                // CRITICAL: Disable ALL greeting overrides - greetings take precedence over containers!
                // The dialogue system checks greetings first, so we must disable them all
                if (baseDialogueController.GreetingOverrides != null)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Disabling {baseDialogueController.GreetingOverrides.Count} greeting overrides");
                    foreach (var greetingOverride in baseDialogueController.GreetingOverrides)
                    {
                        greetingOverride.ShouldShow = false;
                    }
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: All greeting overrides disabled");
                }
                
                // Ensure DialogueController is properly configured to use containers
                baseDialogueController.DialogueEnabled = true;
                baseDialogueController.UseDialogueBehaviour = true;
                
                // Use NPCDialogueWrapper.UseContainerOnInteract() - this is the correct S1API way!
                bool success = npcDialogueWrapper.UseContainerOnInteract(READY_TO_LEAVE_CONTAINER_NAME);
                if (success)
                {
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ UseContainerOnInteract succeeded - container '{READY_TO_LEAVE_CONTAINER_NAME}' set as override");
                }
                else
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: ⚠️ UseContainerOnInteract failed - falling back to manual SetOverrideContainer");
                    // Fallback to manual method
                    baseDialogueController.SetOverrideContainer(targetContainer);
                }
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: DialogueController - DialogueEnabled={baseDialogueController.DialogueEnabled}, UseDialogueBehaviour={baseDialogueController.UseDialogueBehaviour}");
                
                // Verify override was set using reflection
                try
                {
                    var overrideField = typeof(DialogueController).GetField("overrideContainer", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    var overrideProperty = overrideField == null ? typeof(DialogueController).GetProperty("OverrideContainer", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public) : null;
                    
                    DialogueContainer verifiedOverride = null;
                    if (overrideField != null)
                    {
                        verifiedOverride = overrideField.GetValue(baseDialogueController) as DialogueContainer;
                    }
                    else if (overrideProperty != null)
                    {
                        verifiedOverride = overrideProperty.GetValue(baseDialogueController) as DialogueContainer;
                    }
                    
                    if (verifiedOverride != null && verifiedOverride.name == READY_TO_LEAVE_CONTAINER_NAME)
                    {
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ Enabled ready-to-leave dialogue override successfully (verified override container: {verifiedOverride.name})");
                    }
                    else
                    {
                        ModLogger.Warn($"ReleaseOfficer {badgeNumber}: ⚠️ SetOverrideContainer called but verification shows: {(verifiedOverride != null ? verifiedOverride.name : "null")}");
                    }
                }
                catch (Exception verifyEx)
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Could not verify override container: {verifyEx.Message}");
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ Enabled ready-to-leave dialogue override successfully");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error enabling ready-to-leave dialogue: {e.Message}\n{e.StackTrace}");
            }
        }
        
        /// <summary>
        /// Helper method to get container names for debugging
        /// </summary>
        private string GetContainerNames()
        {
            if (dialogueHandler?.dialogueContainers == null)
                return "null";
            
            var names = new System.Collections.Generic.List<string>();
            foreach (var container in dialogueHandler.dialogueContainers)
            {
                if (container != null)
                    names.Add(container.name);
                else
                    names.Add("null");
            }
            return string.Join(", ", names);
        }

        /// <summary>
        /// Disables the ready-to-leave dialogue container override
        /// </summary>
        private void DisableReadyToLeaveDialogue()
        {
            if (baseDialogueController == null) return;
            
            try
            {
                baseDialogueController.SetOverrideContainer(null);
                ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Disabled ready-to-leave dialogue override");
            }
            catch (Exception e)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error disabling ready-to-leave dialogue: {e.Message}");
            }
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
            if (!readyInteractionEnabled) return;
            
            readyInteractionEnabled = false;
            
            // Disable dialogue container override
            DisableReadyToLeaveDialogue();
            
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Ready-to-leave dialogue disabled");
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

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: OnInventoryPickupComplete called - waiting for player to select 'Let's go' in dialogue");

            // CRITICAL: Don't auto-proceed - wait for player to interact with guard and select "Let's go"
            // The dialogue interaction was already enabled in EnableGuardReadyInteraction()
            // Player must:
            //   1. Walk up to guard and press E to open dialogue
            //   2. Select "I'm ready to leave" (moves to confirm node - no movement triggered)
            //   3. Select "Let's go" (THIS triggers movement to exit scanner via OnPlayerAcknowledgeDialogueChoice)
            // Movement will NOT happen until step 3 is completed
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
                        ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Player {player.name} assigned to cell {assignedCell}");
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

            // If we're in MovingToPrisonDoor state, transition to EscortingToStorage
            if (currentReleaseState == ReleaseState.MovingToPrisonDoor)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Prison door operation complete - transitioning to EscortingToStorage");
                ChangeReleaseState(ReleaseState.EscortingToStorage);
                return;
            }

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
            // Door handling is now done via dedicated MovingToPrisonDoor state
            // which uses SecurityDoorBehavior to properly handle the door sequence
            // NOTE: NO door needed for EscortingToExitScanner!
            // Storage and ExitScanner are both in the Booking/Hallway area already
            // The booking door is only needed when coming FROM outside (intake direction)
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
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Door tracking reset for new release");
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

            // Proceed to prison door first, then storage
            ChangeReleaseState(ReleaseState.MovingToPrisonDoor);
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

            // Close prison entry door if it was opened (SecurityDoorBehavior should have closed it, but ensure it's closed)
            if (triggeredDoorOperations.Contains("PrisonEntryDoor"))
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