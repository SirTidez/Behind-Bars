using System;
using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
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
    /// State machine for supervising parole officer to process new parolees
    /// Handles parole intake: greeting, conditions review, documentation issuance
    /// </summary>
    public class ParoleIntakeStateMachine : BaseJailNPC
    {
#if !MONO
        public ParoleIntakeStateMachine(System.IntPtr ptr) : base(ptr) { }
#endif

        #region State Machine Definition

        /// <summary>
        /// Workflow phases used by the supervising officer while accepting a newly
        /// released parolee.  The machine deliberately keeps the officer at the
        /// station until the release-summary UI is dismissible, then escorts the
        /// player through the check-in explanation before returning to the post.
        /// </summary>
        public enum ParoleIntakeState
        {
            Idle,                    // Waiting at police station entrance
            DetectingParolee,        // Monitoring for new parolee arrival
            AwaitingReleaseSummary,  // Supervisor has reached the player while the release UI is visible
            AwaitingIntroductionDialogue, // Handler-owned initial supervision interview
            EscortingToCheckIn,      // Showing the player the check-in location
            ExplainingCheckInLocation,
            ExplainingCheckInSchedule,
            FinalizingIntake,
            ReturningToPost          // Back to entrance position
        }

        #endregion

        #region Component References

        private ParoleOfficerBehavior paroleOfficer;
        private StationaryBehavior stationaryBehavior;

        #endregion

        #region State Variables

#if MONO
        [SerializeField]
#endif
        // The state is the single source of truth for intake progress.  The
        // parole officer mirrors it for activity/notification purposes, while
        // this component owns the explanation freeze and release-summary gates.
        private ParoleIntakeState currentState = ParoleIntakeState.Idle;
        // The player currently owned by this intake session; null outside an
        // active intake.  Do not use the nearest player as a substitute here:
        // cleanup must release the exact player that was acquired.
        private Player currentParolee;
        // StationaryBehavior's post, or the authored courthouse fallback used
        // when the component is initialized before that helper is available.
        private Vector3 entrancePosition;
        // Uses real Unity time for state pacing; it is not parole/game-clock time.
        private float stateStartTime;
        // Delay used between explanation phases and state transitions.
        private float processingDelay = 2f; // Delay between states for processing
        private const float ParoleeGreetingDistance = 2.25f;
        private const float ApproachRepathInterval = 0.35f;
        private float nextApproachRepathTime;
        private bool loggedApproachFailure;
        private bool releaseSummaryAcknowledged;
        private bool hasPreparedReleaseMeeting;
        private Vector3 preparedReleaseMeetingPoint;
        private bool playerFrozenForExplanation;
        private float nextEscortRepathTime;
        private float nextEscortReminderTime;
        private const float EscortFollowDistance = 3f;
        private const float EscortFollowTolerance = 0.5f;
        private const float CheckInLocationTolerance = 1.5f;

        // Dialogue system
        private JailNPCDialogueController dialogueController;
        private ParoleCheckInSystem checkInSystem;
        private bool initialDialogueStarted;
        private bool locationDialogueStarted;

        #endregion

        #region Events

#if MONO
        /// <summary>Raised after the intake state changes on the Mono runtime.</summary>
        public System.Action<ParoleIntakeState> OnStateChanged;
        /// <summary>Raised when a player is accepted into the intake workflow.</summary>
        public System.Action<Player> OnIntakeStarted;
        /// <summary>Raised when the intake workflow completes normally.</summary>
        public System.Action<Player> OnIntakeCompleted;
#endif

        #endregion

        #region Initialization

        /// <summary>
        /// Caches the supervising-officer and stationary helpers used by the
        /// state machine.  The base NPC initialization is still responsible for
        /// registration and the common navigation components.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            paroleOfficer = BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(gameObject);
            stationaryBehavior = BBHelpers.GetComponentSafe<StationaryBehavior>(gameObject);
            checkInSystem = BBHelpers.GetComponentSafe<ParoleCheckInSystem>(gameObject);
        }

        /// <summary>
        /// Restores the serialized/current state after base initialization, then
        /// starts asynchronous dialogue-controller discovery and resolves the
        /// officer's return position.
        /// </summary>
        protected override void Start()
        {
            var savedState = currentState;
            base.Start();
            currentState = savedState;

            InitializeDialogueSystem();
            FindEntrancePosition();

            ModLogger.Debug($"ParoleIntakeStateMachine initialized for {gameObject.name}");
        }

        /// <summary>
        /// Establishes the idle intake state without starting a player session.
        /// </summary>
        protected override void InitializeNPC()
        {
            ChangeIntakeState(ParoleIntakeState.Idle);
        }

        /// <summary>
        /// Resolves the supervising officer's station from StationaryBehavior,
        /// falling back to the authored courthouse position when necessary.
        /// </summary>
        private void FindEntrancePosition()
        {
            if (stationaryBehavior != null)
            {
                entrancePosition = stationaryBehavior.GetStationaryPosition();
            }
            else
            {
                entrancePosition = PresetParoleOfficerRoutes.GetSupervisingOfficerStation();
            }
            ModLogger.Debug($"ParoleIntakeStateMachine: Entrance position set to {entrancePosition}");
        }

        /// <summary>
        /// Starts the retry coroutine that waits for the jail dialogue controller
        /// to become available after the native NPC hierarchy finishes loading.
        /// </summary>
        private void InitializeDialogueSystem()
        {
            MelonLoader.MelonCoroutines.Start(WaitForDialogueController());
        }

#if MONO
        /// <summary>
        /// Polls for the dialogue controller for up to ten half-second attempts.
        /// The coroutine is intentionally tolerant of native hierarchy load order;
        /// a failed lookup leaves intake available but without state greetings.
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
                dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
                if (dialogueController != null)
                {
                    ModLogger.Debug("ParoleIntakeStateMachine: Dialogue controller found");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
                retryCount++;
            }

            ModLogger.Warn("ParoleIntakeStateMachine: Dialogue controller not found after retries");
        }

        #endregion

        #region State Management

        /// <summary>
        /// Changes intake phase, stamps the transition with real Unity time, then
        /// updates dialogue and executes the new state's entry side effects.
        /// On Mono it also raises the state-changed event; it does not itself
        /// acquire or release coordinator ownership.
        /// </summary>
        private void ChangeIntakeState(ParoleIntakeState newState)
        {
            if (currentState == newState) return;

            ParoleIntakeState oldState = currentState;
            currentState = newState;
            stateStartTime = Time.time;

#if MONO
            OnStateChanged?.Invoke(newState);
#endif
            ModLogger.Info($"ParoleIntakeStateMachine: {oldState} → {newState}");

            UpdateDialogueForState(newState);
            OnStateEnter(newState);
        }

        /// <summary>
        /// Maps an intake phase to the dialogue-controller state.  Awaiting the
        /// release summary and returning to post intentionally reuse the idle
        /// greeting because neither phase has a dedicated greeting state.
        /// </summary>
        private void UpdateDialogueForState(ParoleIntakeState state)
        {
            if (dialogueController == null) return;

            string dialogueState = state switch
            {
                ParoleIntakeState.Idle => "Idle",
                ParoleIntakeState.DetectingParolee => "DetectingParolee",
                ParoleIntakeState.AwaitingReleaseSummary => "Idle",
                ParoleIntakeState.AwaitingIntroductionDialogue => "Idle",
                ParoleIntakeState.EscortingToCheckIn => "Escorting",
                ParoleIntakeState.ExplainingCheckInLocation => "ReviewingConditions",
                ParoleIntakeState.ExplainingCheckInSchedule => "ReviewingConditions",
                ParoleIntakeState.FinalizingIntake => "FinalizingIntake",
                ParoleIntakeState.ReturningToPost => "Idle",
                _ => "Idle"
            };

            dialogueController.UpdateGreetingForState(dialogueState);
        }

        /// <summary>
        /// Applies phase-entry side effects such as suspending stationary behavior,
        /// freezing the player for explanations, and restoring the officer to its
        /// post.  This method does not advance the state machine by itself.
        /// </summary>
        private void OnStateEnter(ParoleIntakeState state)
        {
            switch (state)
            {
                case ParoleIntakeState.Idle:
                    RestorePlayerAfterExplanation();
                    // Stay at entrance position
                    if (stationaryBehavior != null)
                    {
                        stationaryBehavior.SetMaintainPosition(true);
                        stationaryBehavior.ReturnToPosition();
                    }
                    break;

                case ParoleIntakeState.DetectingParolee:
                    // A supervising officer normally remains at their post. Suspend that
                    // behavior only while they walk out to start the release intake.
                    stationaryBehavior?.SetMaintainPosition(false);
                    nextApproachRepathTime = 0f;
                    loggedApproachFailure = false;
                    ModLogger.Info($"ParoleIntakeStateMachine: Supervisor approaching {currentParolee?.name ?? "released parolee"} for post-release intake");
                    break;

                case ParoleIntakeState.AwaitingReleaseSummary:
                    StopMovement();
                    break;

                case ParoleIntakeState.AwaitingIntroductionDialogue:
                    StopMovement();
                    MaintainFacingParolee();
                    // Start on the next NPC tick so the release-summary dismissal
                    // finishes relinquishing UI/camera ownership first.
                    initialDialogueStarted = false;
                    break;

                case ParoleIntakeState.EscortingToCheckIn:
                    if (currentParolee != null)
                    {
                        stationaryBehavior?.SetMaintainPosition(false);
                        paroleOfficer?.BeginIntakeEscort(currentParolee);
                        nextEscortRepathTime = 0f;
                        nextEscortReminderTime = 0f;
                        TrySendNPCMessage("I'm your supervising officer. Follow me to your first parole check-in.", 4f);
                    }
                    break;

                case ParoleIntakeState.ExplainingCheckInLocation:
                    if (currentParolee != null)
                    {
                        StopMovement();
                        MaintainFacingParolee();
                        locationDialogueStarted = checkInSystem?.BeginInitialLocationConversation(currentParolee) == true;
                        if (!locationDialogueStarted)
                        {
                            ModLogger.Warn("ParoleIntakeStateMachine: Location dialogue handler was not ready; intake will retry");
                        }
                    }
                    break;

                case ParoleIntakeState.ExplainingCheckInSchedule:
                    break;

                case ParoleIntakeState.FinalizingIntake:
                    break;

                case ParoleIntakeState.ReturningToPost:
                    if (stationaryBehavior != null)
                    {
                        stationaryBehavior.SetMaintainPosition(true);
                        stationaryBehavior.ReturnToPosition();
                    }
                    break;
            }
        }

        /// <summary>
        /// Builds the next check-in-window message without applying scheduling
        /// consequences.  If the parole manager cannot provide a window, the
        /// current implementation returns a generic return-here instruction.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private string BuildCheckInScheduleMessage(Player parolee)
        {
            var paroleManager = Core.ResolveParoleManager();
            if (paroleManager != null)
            {
                paroleManager.GetDailyCheckInStatus(parolee, out string windowText, applyConsequences: false);
                if (!string.IsNullOrWhiteSpace(windowText))
                {
                    return $"Your next check-in window is between {windowText}. Come back here during that time and speak with me.";
                }
            }

            return "Your check-in schedule will be sent to you. Return here during your assigned window and speak with me.";
        }

        /// <summary>
        /// Temporarily disables the player's movement and camera look while the
        /// officer explains the check-in location.  The flag is set only after
        /// the control handoff succeeds and is cleared by the matching restore.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void FreezePlayerForExplanation()
        {
            if (currentParolee == null || playerFrozenForExplanation)
            {
                return;
            }

            try
            {
                Vector3 lookDirection = transform.position - currentParolee.transform.position;
                lookDirection.y = 0f;
                if (lookDirection.sqrMagnitude > 0.001f)
                {
                    currentParolee.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
                }

#if MONO
                var playerMovement = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                var playerCamera = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerCamera>.Instance;
#else
                var playerMovement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                var playerCamera = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerCamera>.Instance;
#endif
                if (playerMovement != null)
                {
                    playerMovement.CanMove = false;
                }

                playerCamera?.SetCanLook(false);
                playerFrozenForExplanation = true;
                ModLogger.Info($"ParoleIntakeStateMachine: Froze {currentParolee.name} for check-in location explanation");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleIntakeStateMachine: Failed to freeze player for explanation: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores movement and camera look after an explanation, and always
        /// clears the local freeze flag even if a runtime lookup fails.  This is
        /// the authoritative cleanup used when returning to idle or disabling.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void RestorePlayerAfterExplanation()
        {
            if (!playerFrozenForExplanation)
            {
                return;
            }

            try
            {
#if MONO
                var playerMovement = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                var playerCamera = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerCamera>.Instance;
#else
                var playerMovement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                var playerCamera = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerCamera>.Instance;
#endif
                if (playerMovement != null)
                {
                    playerMovement.CanMove = true;
                }

                playerCamera?.SetCanLook(true);
                ModLogger.Info("ParoleIntakeStateMachine: Restored player controls after check-in location explanation");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleIntakeStateMachine: Failed to restore player controls after explanation: {ex.Message}");
            }
            finally
            {
                playerFrozenForExplanation = false;
            }
        }

        #endregion

        #region Update Loop

        /// <summary>
        /// Enables the base NPC tick path used to dispatch the intake state
        /// machine.  No intake session is acquired here.
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
        }

        /// <summary>
        /// Cleans up explanation controls and escort state before the base NPC is
        /// disabled.  This does not mark an intake as successfully completed.
        /// </summary>
        protected override void OnDisable()
        {
            RestorePlayerAfterExplanation();
            paroleOfficer?.CompleteIntakeEscort();
            checkInSystem?.EndInitialIntakeDialogue(currentParolee);
            base.OnDisable();
        }

        /// <summary>
        /// Dispatches the base NPC tick and then advances the intake state machine.
        /// State transitions themselves remain responsible for their entry
        /// side effects and player-control ownership.
        /// </summary>
        protected override void OnStateUpdateTick(float currentTime)
        {
            base.OnStateUpdateTick(currentTime);

            // Handle parole intake state machine
            ProcessIntakeState();
        }

        /// <summary>
        /// Routes the current intake state to its handler.  Handlers may issue
        /// navigation or dialogue work, but only a state transition changes the
        /// authoritative phase timestamp.
        /// </summary>
        private void ProcessIntakeState()
        {
            switch (currentState)
            {
                case ParoleIntakeState.Idle:
                    HandleIdleState();
                    break;

                case ParoleIntakeState.DetectingParolee:
                    HandleDetectingParoleeState();
                    break;

                case ParoleIntakeState.AwaitingReleaseSummary:
                    HandleAwaitingReleaseSummaryState();
                    break;

                case ParoleIntakeState.AwaitingIntroductionDialogue:
                    HandleAwaitingIntroductionDialogueState();
                    break;

                case ParoleIntakeState.EscortingToCheckIn:
                    HandleEscortingToCheckInState();
                    break;

                case ParoleIntakeState.ExplainingCheckInLocation:
                    HandleExplainingCheckInLocationState();
                    break;

                case ParoleIntakeState.ExplainingCheckInSchedule:
                    HandleExplainingCheckInScheduleState();
                    break;

                case ParoleIntakeState.FinalizingIntake:
                    HandleFinalizingIntakeState();
                    break;

                case ParoleIntakeState.ReturningToPost:
                    HandleReturningToPostState();
                    break;
            }
        }

        #endregion

        #region State Handlers

        /// <summary>
        /// Leaves intake entry to the dynamic manager; idle is intentionally a
        /// passive state so more than one arrival poller cannot be active.
        /// </summary>
        private void HandleIdleState()
        {
            // Intake entry is manager-driven so spawn state and officer availability
            // remain the single source of truth.
        }

        /// <summary>
        /// Approaches either the live parolee or the prepared courthouse meeting
        /// point, then waits for release-summary acknowledgement before escorting.
        /// Repathing is throttled to avoid competing navigation requests.
        /// </summary>
        private void HandleDetectingParoleeState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            Vector3 approachPosition = hasPreparedReleaseMeeting
                ? preparedReleaseMeetingPoint
                : currentParolee.transform.position;
            float distance = Vector3.Distance(transform.position, approachPosition);
            if (distance <= ParoleeGreetingDistance)
            {
                ModLogger.Info(hasPreparedReleaseMeeting
                    ? $"ParoleIntakeStateMachine: Supervisor reached the police-station release point for {currentParolee.name} ({distance:F2}m)"
                    : $"ParoleIntakeStateMachine: Supervisor reached {currentParolee.name} for post-release intake ({distance:F2}m)");
                ChangeIntakeState(releaseSummaryAcknowledged
                    ? ParoleIntakeState.AwaitingIntroductionDialogue
                    : ParoleIntakeState.AwaitingReleaseSummary);
                return;
            }

            if (Time.time < nextApproachRepathTime)
            {
                return;
            }

            nextApproachRepathTime = Time.time + ApproachRepathInterval;
            if (!MoveTo(approachPosition, ParoleeGreetingDistance) && !loggedApproachFailure)
            {
                loggedApproachFailure = true;
                ModLogger.Error($"ParoleIntakeStateMachine: Unable to approach parolee {currentParolee.name} for post-release intake");
            }
        }

        /// <summary>
        /// Holds the officer in place while the release-summary UI remains open.
        /// Acknowledgement advances to the escort phase; a lost player cancels to
        /// idle without attempting to infer a replacement target.
        /// </summary>
        private void HandleAwaitingReleaseSummaryState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            if (releaseSummaryAcknowledged)
            {
                ChangeIntakeState(ParoleIntakeState.AwaitingIntroductionDialogue);
            }
        }

        /// <summary>Waits for the handler-owned initial interview before beginning the escort.</summary>
        private void HandleAwaitingIntroductionDialogueState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            MaintainFacingParolee();
            checkInSystem ??= BBHelpers.GetComponentSafe<ParoleCheckInSystem>(gameObject);
            if (!initialDialogueStarted)
            {
                initialDialogueStarted = checkInSystem?.BeginInitialIntroduction(currentParolee) == true;
                return;
            }

            if (checkInSystem?.HasCompletedInitialIntroduction(currentParolee) == true)
            {
                ChangeIntakeState(ParoleIntakeState.EscortingToCheckIn);
            }
        }

        /// <summary>
        /// Keeps the player within the escort distance while navigating to the
        /// assigned check-in point.  The officer pauses and reminds the player if
        /// they fall behind rather than advancing the explanation early.
        /// </summary>
        private void HandleEscortingToCheckInState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            float paroleeDistance = Vector3.Distance(transform.position, currentParolee.transform.position);
            if (paroleeDistance > EscortFollowDistance + EscortFollowTolerance)
            {
                StopMovement();
                if (Time.time >= nextEscortReminderTime)
                {
                    nextEscortReminderTime = Time.time + 4f;
                    TrySendNPCMessage("Stay within three metres and follow me to the check-in location.", 3f);
                }

                return;
            }

            if (Vector3.Distance(transform.position, entrancePosition) <= CheckInLocationTolerance)
            {
                StopMovement();
                ChangeIntakeState(ParoleIntakeState.ExplainingCheckInLocation);
                return;
            }

            if (Time.time >= nextEscortRepathTime)
            {
                nextEscortRepathTime = Time.time + ApproachRepathInterval;
                if (!MoveTo(entrancePosition, CheckInLocationTolerance) && !loggedApproachFailure)
                {
                    loggedApproachFailure = true;
                    ModLogger.Error($"ParoleIntakeStateMachine: Unable to escort {currentParolee.name} to the parole check-in location");
                }
            }
        }

        /// <summary>
        /// Faces the player and holds the location explanation for two processing
        /// delays before entering the schedule explanation phase.
        /// </summary>
        private void HandleExplainingCheckInLocationState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            MaintainFacingParolee();

            checkInSystem ??= BBHelpers.GetComponentSafe<ParoleCheckInSystem>(gameObject);
            if (!locationDialogueStarted)
            {
                locationDialogueStarted = checkInSystem?.BeginInitialLocationConversation(currentParolee) == true;
                return;
            }

            if (checkInSystem?.HasCompletedInitialLocationConversation(currentParolee) == true)
            {
                CompleteIntake();
            }
        }

        /// <summary>
        /// Faces the player and holds the schedule explanation for three processing
        /// delays before finalization.
        /// </summary>
        private void HandleExplainingCheckInScheduleState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            MaintainFacingParolee();

            if (Time.time - stateStartTime >= processingDelay * 3f)
            {
                ChangeIntakeState(ParoleIntakeState.FinalizingIntake);
            }
        }

        /// <summary>
        /// Keeps the final intake message visible for two processing delays, then
        /// commits the intake and releases the player/control ownership.
        /// </summary>
        private void HandleFinalizingIntakeState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            MaintainFacingParolee();

            if (Time.time - stateStartTime >= processingDelay * 2f)
            {
                CompleteIntake();
            }
        }

        /// <summary>
        /// Applies a horizontal look-at toward the active parolee during the
        /// explanation phases.  The method is hidden from the IL2CPP surface and
        /// is called by the scheduler rather than an independent Update loop.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void MaintainFacingParolee()
        {
            if (currentParolee == null)
            {
                return;
            }

            Vector3 directionToParolee = currentParolee.transform.position - transform.position;
            directionToParolee.y = 0f;
            if (directionToParolee.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            Quaternion targetRotation = Quaternion.LookRotation(directionToParolee.normalized, Vector3.up);
            // This state is advanced by the event-driven NPC scheduler rather than a
            // per-frame Update, so use a high angular rate to converge within one
            // scheduler tick while still allowing a natural turn in normal frame flow.
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, 2160f * Time.deltaTime);
        }

        /// <summary>
        /// Waits for StationaryBehavior to report the return position reached,
        /// then transitions back to idle.  If the helper is absent, this state
        /// remains pending until external cleanup occurs.
        /// </summary>
        private void HandleReturningToPostState()
        {
            // Check if we've returned to entrance
            if (stationaryBehavior != null && stationaryBehavior.IsAtPosition())
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts a normal intake for the specified parolee when the machine is
        /// idle or already approaching that same player.  This resets release
        /// meeting flags and raises the Mono-only start event; coordinator
        /// ownership is established by the caller/manager before this entry point.
        /// </summary>
        /// <param name="parolee">The exact player to retain for this intake session.</param>
        public void StartParoleIntake(Player parolee)
        {
            if (parolee == null)
            {
                ModLogger.Warn("ParoleIntakeStateMachine: Cannot start intake, parolee is null");
                return;
            }

            if (currentParolee == parolee && IsProcessingIntake())
            {
                ModLogger.Debug($"ParoleIntakeStateMachine: Intake already active for {parolee.name}");
                return;
            }

            if (currentState != ParoleIntakeState.Idle && currentState != ParoleIntakeState.DetectingParolee)
            {
                ModLogger.Warn($"ParoleIntakeStateMachine: Cannot start intake, already processing (state: {currentState})");
                return;
            }

            currentParolee = parolee;
            hasPreparedReleaseMeeting = false;
            releaseSummaryAcknowledged = false;
            initialDialogueStarted = false;
            locationDialogueStarted = false;
#if MONO
            OnIntakeStarted?.Invoke(parolee);
#endif
            ChangeIntakeState(ParoleIntakeState.DetectingParolee);

            ModLogger.Info($"ParoleIntakeStateMachine: Started intake for {parolee.name}");
        }

        /// <summary>
        /// Begins walking to the police-station release point before the released player is
        /// there. This stays in the canonical intake state machine so normal check-ins still
        /// approach the live player.
        /// </summary>
        /// <param name="parolee">The player reserved for the upcoming release handoff.</param>
        /// <param name="meetingPoint">The authored point at which the supervisor should wait.</param>
        internal void PrepareForReleaseMeeting(Player parolee, Vector3 meetingPoint)
        {
            if (parolee == null)
            {
                return;
            }

            if (currentParolee != null && currentParolee != parolee && IsProcessingIntake())
            {
                ModLogger.Warn("ParoleIntakeStateMachine: Cannot prepare a release meeting while another parolee is being processed");
                return;
            }

            currentParolee = parolee;
            preparedReleaseMeetingPoint = meetingPoint;
            hasPreparedReleaseMeeting = true;
            releaseSummaryAcknowledged = false;
            initialDialogueStarted = false;
            locationDialogueStarted = false;
            ChangeIntakeState(ParoleIntakeState.DetectingParolee);
            ModLogger.Info($"ParoleIntakeStateMachine: Preparing to meet {parolee.name} at police-station release point {meetingPoint}");
        }

        /// <summary>
        /// Releases the supervisor from the pre-dismissal wait once the player closes the release summary.
        /// </summary>
        /// <param name="parolee">The player whose summary was dismissed.</param>
        /// <returns>True only when this machine owns that player and accepts the dismissal.</returns>
        public bool NotifyReleaseSummaryDismissed(Player parolee)
        {
            if (parolee == null || currentParolee != parolee || !IsProcessingIntake())
            {
                return false;
            }

            // Do not release the player into the world while a pre-dispatched supervisor
            // is still walking from the courthouse. The release handoff retries this once
            // the officer has actually reached the police-station door.
            if (hasPreparedReleaseMeeting && currentState != ParoleIntakeState.AwaitingReleaseSummary)
            {
                return false;
            }

            releaseSummaryAcknowledged = true;
            hasPreparedReleaseMeeting = false;
            if (currentState == ParoleIntakeState.AwaitingReleaseSummary)
            {
                ChangeIntakeState(ParoleIntakeState.AwaitingIntroductionDialogue);
            }

            return true;
        }

        /// <summary>
        /// Bypasses normal idle, pending-request, and release-summary gates and
        /// sends the supplied player into the detecting phase.  This is intended
        /// for controlled external/test recovery; normal gameplay should use
        /// <see cref="StartParoleIntake"/> or the release-meeting preparation path.
        /// </summary>
        /// <param name="parolee">The exact player to retain for the forced session.</param>
        public void ForceStartIntake(Player parolee)
        {
            currentParolee = parolee;
            initialDialogueStarted = false;
            locationDialogueStarted = false;
            ChangeIntakeState(ParoleIntakeState.DetectingParolee);
            ModLogger.Info($"ParoleIntakeStateMachine: Force started intake for {parolee.name}");
        }

        /// <summary>
        /// Commits the completed intake interaction, restores player controls,
        /// releases escort state, clears the retained player, and hands the
        /// supervising officer back to the roster manager when applicable.
        /// </summary>
        private void CompleteIntake()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            Player completedParolee = currentParolee;
            ModLogger.Info($"ParoleIntakeStateMachine: Completed intake for {completedParolee.name}");

            // Record interaction
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(completedParolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                rapSheet.CurrentParoleRecord.RecordInteraction();
            }

#if MONO
            OnIntakeCompleted?.Invoke(completedParolee);
#endif
            currentParolee = null;
            releaseSummaryAcknowledged = false;
            hasPreparedReleaseMeeting = false;
            RestorePlayerAfterExplanation();
            paroleOfficer?.CompleteIntakeEscort();
            checkInSystem?.EndInitialIntakeDialogue(completedParolee);

            // The supervising officer's normal post is the courthouse interior, not the
            // exterior report point.  Finish this state machine before handing the native
            // home action back to the roster manager; otherwise IsProcessingIntake blocks
            // the building transition and leaves the officer standing outside indefinitely.
            if (paroleOfficer != null &&
                paroleOfficer.GetAssignment() == ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                Core.ResolveDynamicParoleOfficerManager()?.CompleteSupervisingOfficerIntake(completedParolee, paroleOfficer);
                return;
            }

            // Return to post
            ChangeIntakeState(ParoleIntakeState.ReturningToPost);
        }

        /// <summary>
        /// Returns true for every active intake phase except idle and the transient
        /// return-to-post phase.
        /// </summary>
        /// <returns>Whether this component currently owns an intake workflow.</returns>
        public bool IsProcessingIntake()
        {
            return currentState != ParoleIntakeState.Idle && currentState != ParoleIntakeState.ReturningToPost;
        }

        /// <summary>
        /// Aborts the current intake, restores any explanation freeze, releases
        /// the officer's local escort mirror, and moves the officer toward its
        /// post.  Coordinator/manager ownership is not cleared here; the owning
        /// caller or watchdog must release that separate record.  This is
        /// cancellation cleanup, not a successful intake completion.
        /// </summary>
        public void StopIntakeProcess()
        {
            if (IsProcessingIntake())
            {
                ModLogger.Info($"ParoleIntakeStateMachine: Stopping intake process");
                Player cancelledParolee = currentParolee;
                currentParolee = null;
                releaseSummaryAcknowledged = false;
                hasPreparedReleaseMeeting = false;
                RestorePlayerAfterExplanation();
                paroleOfficer?.CompleteIntakeEscort();
                checkInSystem?.EndInitialIntakeDialogue(cancelledParolee);
                ChangeIntakeState(ParoleIntakeState.ReturningToPost);
            }
        }

        #endregion
    }
}

