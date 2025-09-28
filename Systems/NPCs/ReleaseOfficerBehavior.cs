using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
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
            EscortingToStorage,        // Escorting to inventory pickup
            WaitingAtStorage,          // Supervising inventory pickup
            EscortingToExitScanner,    // NEW: Escorting to exit scanner station
            WaitingForExitScan,        // NEW: Waiting for exit scan completion
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


        // Door management
        private HashSet<string> triggeredDoorOperations = new HashSet<string>();
        private bool isSecurityDoorActive = false;
        private float lastDoorOperationTime = 0f;

        // Destination tracking to prevent duplicate events
        private Dictionary<string, bool> stationDestinationProcessed = new Dictionary<string, bool>();

        // Continuous rotation system
        private object continuousLookingCoroutine;

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
            // EnsureSecurityDoorComponent(); // DISABLED - SecurityDoor causes infinite loops

            // Register with ReleaseManager
            if (ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.RegisterOfficer(this);
            }

            // DISABLED SecurityDoor events - using direct door control only
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Using direct door control (SecurityDoor disabled)");

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

                dialogueController.AddStateDialogue("AtStorage", "Collect your belongings.",
                    new[] { "Get your items.", "Pick up your things.", "Retrieve your possessions.", "Tell me when you're done." });

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
                case ReleaseState.EscortingToStorage:
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Arrived at storage, enabling pickup station");
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
            // Check state timeout, but ignore timeout for Idle state (guards can wait indefinitely)
            if (currentReleaseState != ReleaseState.Idle && Time.time - stateStartTime > STATE_TIMEOUT)
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: State {currentReleaseState} timed out, returning to post");
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Check for door triggers during escort phases
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
                    // Cell opening will be handled in CheckForDoorTriggers during EscortingToStorage
                    // Immediately proceed to storage escort
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Starting storage escort with sequential door handling");
                    ChangeReleaseState(ReleaseState.EscortingToStorage);
                    break;

                case ReleaseState.EscortingToStorage:
                    PlayVoiceCommand("Follow me to storage.", "Escorting");
                    NavigateToStorage();
                    break;

                case ReleaseState.WaitingAtStorage:
                    PlayVoiceCommand("Collect your belongings from the storage station.", "AtStorage");
                    EnableInventoryPickupStation();
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.EscortingToExitScanner:
                    PlayVoiceCommand("Time to leave. Follow me to the exit scanner.", "Escorting");
                    NavigateToExitScanner();
                    break;

                case ReleaseState.WaitingForExitScan:
                    PlayVoiceCommand("Complete your exit scan to be released.", "AtExit");
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.ProcessingExit:
                    PlayVoiceCommand("You're free to go.", "AtExit");
                    StartContinuousPlayerLooking();
                    break;

                case ReleaseState.ReturningToPost:
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
            if (currentReleasee == null)
            {
                ChangeReleaseState(ReleaseState.ReturningToPost);
                return;
            }

            // Check if we've reached the player
            float distanceToPlayer = Vector3.Distance(transform.position, currentReleasee.transform.position);
            if (distanceToPlayer <= DESTINATION_TOLERANCE)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Reached player, transitioning to OpeningCell");
                ChangeReleaseState(ReleaseState.OpeningCell);
            }
        }

        private void HandleOpeningCellState()
        {
            // Wait for cell door opening to complete, then transition to escort
            // This is handled by OpenPlayerCellIfNeeded() in OnStateEnter
            if (Time.time - stateStartTime > 5f) // Give time for door operations
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Cell opening complete, starting escort to storage");
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

                // Check if we're stuck
                if (Time.time - stateStartTime > 30f) // 30 second timeout per destination
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Movement timeout in state {currentReleaseState}");
                    HandleDestinationReached(currentDestination);
                }
            }
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

                // Check if we're stuck
                if (Time.time - stateStartTime > 30f) // 30 second timeout per destination
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Movement timeout to exit scanner");
                    ChangeReleaseState(ReleaseState.WaitingForExitScan);
                }
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

            // Timeout after 3 minutes
            if (Time.time - stateStartTime > 180f)
            {
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Exit scan timeout, proceeding to final processing");
                ChangeReleaseState(ReleaseState.ProcessingExit);
            }
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
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: NavigateToPlayer() called");

            if (currentReleasee == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Cannot navigate to player - currentReleasee is null");
                return;
            }

            Vector3 playerPos = currentReleasee.transform.position;
            lastKnownPlayerPosition = playerPos;
            destinationPosition = playerPos;

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Navigating to player at {playerPos}");
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Officer current position: {transform.position}");

            bool moveSuccess = MoveTo(playerPos);
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: MoveTo result: {moveSuccess}");

            if (!moveSuccess)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: MoveTo failed - checking navAgent status");
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
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Attempting to enable inventory pickup station for {currentReleasee.name}");

                // Use the interactive InventoryPickupStation for release item retrieval
                var jailController = Core.JailController;
                if (jailController?.storage?.inventoryDropOff != null)
                {
                    var dropOffTransform = jailController.storage.inventoryDropOff;
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Found InventoryDropOff at {dropOffTransform.position}, enabling InventoryPickupStation");

                    // Ensure the GameObject is active
                    dropOffTransform.gameObject.SetActive(true);
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Activated InventoryDropOff GameObject");

                    // Check if it already has InventoryPickupStation component
                    var inventoryPickupStation = dropOffTransform.GetComponent<InventoryPickupStation>();
                    if (inventoryPickupStation == null)
                    {
                        // Add InventoryPickupStation component to the InventoryDropOff GameObject
                        inventoryPickupStation = dropOffTransform.gameObject.AddComponent<InventoryPickupStation>();
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Added InventoryPickupStation component to InventoryDropOff");
                    }
                    else
                    {
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: InventoryDropOff already has InventoryPickupStation component");
                    }

                    // Enable the inventory pickup station
                    inventoryPickupStation.enabled = true;
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Enabled InventoryPickupStation component");

                    // Enable the station for release
                    try
                    {
                        inventoryPickupStation.EnableForRelease(currentReleasee);
                        ModLogger.Info($"ReleaseOfficer {badgeNumber}: Called EnableForRelease on InventoryPickupStation");
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Could not call EnableForRelease: {ex.Message}");
                    }

                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Successfully activated InventoryPickupStation for {currentReleasee.name}");
                }
                else
                {
                    ModLogger.Error($"ReleaseOfficer {badgeNumber}: No InventoryDropOff found in JailController.storage");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: Error enabling inventory pickup station: {ex.Message}");
            }
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

            ModLogger.Info($"ReleaseOfficer {badgeNumber}: OnInventoryPickupComplete called - current state: {currentReleaseState}");

            // SIMPLIFIED: Just proceed directly to exit scanner after short delay
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Inventory pickup complete, proceeding to exit scanner");
            MelonCoroutines.Start(DelayedExitScanner(3f)); // 3 second delay for realism
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
        /// Called when player confirms they are ready to leave (SIMPLIFIED)
        /// </summary>
        public void OnPlayerConfirmedReady()
        {
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Player confirmed ready, proceeding to exit scanner");
            PlayVoiceCommand("Good. Time to leave. Follow me to the exit scanner.", "Escorting");

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
            // Wait for guard to move away from door area
            yield return new WaitForSeconds(1.5f);
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Navigation resuming after door clearance delay");
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

        private void TriggerPrisonEntryDoorIfNeeded()
        {
            // Check if we've already triggered the prison entry door operation
            if (triggeredDoorOperations.Contains("PrisonEntryDoor"))
            {
                // Check if SecurityDoor is stuck - if it's been active for too long, reset it
                if (isSecurityDoorActive && Time.time - lastDoorOperationTime > 5f)
                {
                    ModLogger.Warn($"ReleaseOfficer {badgeNumber}: SecurityDoor stuck for 5+ seconds, forcing reset and direct door control");
                    isSecurityDoorActive = false;
                    triggeredDoorOperations.Remove("PrisonEntryDoor"); // Allow re-triggering
                    // Try direct door control as emergency fallback
                    FallbackDirectDoorControl("PrisonEntryDoor");
                }
                else
                {
                    ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Prison entry door already triggered, SecurityDoor active: {isSecurityDoorActive}");
                }
                return;
            }

            var securityDoor = GetSecurityDoor();
            if (securityDoor == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: No SecurityDoor component - fallback to direct control");
                FallbackDirectDoorControl("PrisonEntryDoor");
                return;
            }

            // Trigger SecurityDoor operation for prison entry door (FROM prison TO hall)
            string triggerName = "PrisonDoorTrigger_FromPrison"; // Guard moving from prison area to hall
            ModLogger.Info($"ReleaseOfficer {badgeNumber}: Attempting to trigger SecurityDoor with '{triggerName}' for player {currentReleasee?.name}");
            bool triggered = securityDoor.HandleDoorTrigger(triggerName, true, currentReleasee);

            if (triggered)
            {
                triggeredDoorOperations.Add("PrisonEntryDoor");
                isSecurityDoorActive = true;
                lastDoorOperationTime = Time.time; // Track when operation started
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: ✅ SecurityDoor operation triggered for prison entry door");
            }
            else
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: ❌ Failed to trigger SecurityDoor for prison entry door - trying fallback");
                FallbackDirectDoorControl("PrisonEntryDoor");
            }
        }

        private void TriggerBookingInnerDoorIfNeeded()
        {
            // Check if we've already triggered the booking inner door operation
            if (triggeredDoorOperations.Contains("BookingInnerDoor")) return;

            var securityDoor = GetSecurityDoor();
            if (securityDoor == null)
            {
                ModLogger.Error($"ReleaseOfficer {badgeNumber}: No SecurityDoor component - fallback to direct control");
                FallbackDirectDoorControl("BookingInnerDoor");
                return;
            }

            // Trigger SecurityDoor operation for booking inner door (FROM hall TO booking)
            string triggerName = "BookingDoorTrigger_FromHall"; // Guard moving from hall to booking area
            bool triggered = securityDoor.HandleDoorTrigger(triggerName, true, currentReleasee);

            if (triggered)
            {
                triggeredDoorOperations.Add("BookingInnerDoor");
                isSecurityDoorActive = true;
                ModLogger.Info($"ReleaseOfficer {badgeNumber}: SecurityDoor operation triggered for booking inner door");
            }
            else
            {
                ModLogger.Warn($"ReleaseOfficer {badgeNumber}: Failed to trigger SecurityDoor for booking inner door");
            }
        }

        private void CheckForDoorTriggers()
        {
            // Only check door triggers every 2 seconds to reduce spam
            if (Time.time % 2f >= Time.deltaTime) return;

            // SEQUENTIAL door control like intake process - no door slamming!
            if (currentReleaseState == ReleaseState.EscortingToStorage)
            {
                // Step 1: Open cell door when starting escort (if not already done)
                if (!triggeredDoorOperations.Contains("CellDoor"))
                {
                    int cellNumber = GetPlayerCellNumber(currentReleasee);
                    if (cellNumber >= 0)
                    {
                        var jailController = Core.JailController;
                        if (jailController?.doorController != null)
                        {
                            bool cellOpened = jailController.doorController.OpenJailCellDoor(cellNumber);
                            if (cellOpened)
                            {
                                triggeredDoorOperations.Add("CellDoor");
                                ModLogger.Info($"ReleaseOfficer {badgeNumber}: Opened cell {cellNumber} door for release");
                            }
                        }
                    }
                }

                // Step 2: Open prison entry door ONLY when approaching it
                if (triggeredDoorOperations.Contains("CellDoor") && !triggeredDoorOperations.Contains("PrisonEntryDoor") && IsNearPrisonDoor())
                {
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Opening prison entry door - approaching with player");
                    FallbackDirectDoorControl("PrisonEntryDoor");
                }
            }
            else if (currentReleaseState == ReleaseState.WaitingAtStorage)
            {
                // Step 3: Close the prison door ONLY after both are safely in storage area
                if (triggeredDoorOperations.Contains("PrisonEntryDoor") && !triggeredDoorOperations.Contains("PrisonEntryDoor_Close") && ArePlayerAndOfficerInStorageArea())
                {
                    var jailController = Core.JailController;
                    if (jailController?.doorController != null)
                    {
                        bool doorClosed = jailController.doorController.ClosePrisonEntryDoor();
                        if (doorClosed)
                        {
                            triggeredDoorOperations.Add("PrisonEntryDoor_Close");
                            ModLogger.Info($"ReleaseOfficer {badgeNumber}: CLOSED prison entry door - both are safely in storage area");
                        }
                    }
                }
            }
            else if (currentReleaseState == ReleaseState.EscortingToExitScanner)
            {
                // Step 4: Open booking door ONLY when approaching exit scanner
                if (!triggeredDoorOperations.Contains("BookingInnerDoor") && IsNearBookingDoor())
                {
                    ModLogger.Info($"ReleaseOfficer {badgeNumber}: Opening booking inner door - approaching exit scanner");
                    FallbackDirectDoorControl("BookingInnerDoor");
                }
            }
            else if (currentReleaseState == ReleaseState.WaitingForExitScan)
            {
                // Step 5: Close booking door ONLY after both are at exit scanner
                if (triggeredDoorOperations.Contains("BookingInnerDoor") && !triggeredDoorOperations.Contains("BookingInnerDoor_Close") && ArePlayerAndOfficerAtExitScanner())
                {
                    var jailController = Core.JailController;
                    if (jailController?.doorController != null)
                    {
                        bool doorClosed = jailController.doorController.CloseBookingInnerDoor();
                        if (doorClosed)
                        {
                            triggeredDoorOperations.Add("BookingInnerDoor_Close");
                            ModLogger.Info($"ReleaseOfficer {badgeNumber}: CLOSED booking inner door - both are safely at exit scanner");
                        }
                    }
                }
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
            ModLogger.Debug($"ReleaseOfficer {badgeNumber}: Door tracking reset for new release");
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

            // SecurityDoor events disabled - using direct door control only

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