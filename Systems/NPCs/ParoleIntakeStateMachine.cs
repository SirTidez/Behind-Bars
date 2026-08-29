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

        public enum ParoleIntakeState
        {
            Idle,                    // Waiting at police station entrance
            DetectingParolee,        // Monitoring for new parolee arrival
            AwaitingReleaseSummary,  // Supervisor has reached the player while the release UI is visible
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
        private ParoleIntakeState currentState = ParoleIntakeState.Idle;
        private Player currentParolee;
        private Vector3 entrancePosition;
        private float stateStartTime;
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

        #endregion

        #region Events

#if MONO
        public System.Action<ParoleIntakeState> OnStateChanged;
        public System.Action<Player> OnIntakeStarted;
        public System.Action<Player> OnIntakeCompleted;
#endif

        #endregion

        #region Initialization

        protected override void Awake()
        {
            base.Awake();
            paroleOfficer = BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(gameObject);
            stationaryBehavior = BBHelpers.GetComponentSafe<StationaryBehavior>(gameObject);
        }

        protected override void Start()
        {
            var savedState = currentState;
            base.Start();
            currentState = savedState;

            InitializeDialogueSystem();
            FindEntrancePosition();

            ModLogger.Debug($"ParoleIntakeStateMachine initialized for {gameObject.name}");
        }

        protected override void InitializeNPC()
        {
            ChangeIntakeState(ParoleIntakeState.Idle);
        }

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

        private void InitializeDialogueSystem()
        {
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

        private void UpdateDialogueForState(ParoleIntakeState state)
        {
            if (dialogueController == null) return;

            string dialogueState = state switch
            {
                ParoleIntakeState.Idle => "Idle",
                ParoleIntakeState.DetectingParolee => "DetectingParolee",
                ParoleIntakeState.AwaitingReleaseSummary => "Idle",
                ParoleIntakeState.EscortingToCheckIn => "Escorting",
                ParoleIntakeState.ExplainingCheckInLocation => "ReviewingConditions",
                ParoleIntakeState.ExplainingCheckInSchedule => "ReviewingConditions",
                ParoleIntakeState.FinalizingIntake => "FinalizingIntake",
                ParoleIntakeState.ReturningToPost => "Idle",
                _ => "Idle"
            };

            dialogueController.UpdateGreetingForState(dialogueState);
        }

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
                        FreezePlayerForExplanation();
                        MaintainFacingParolee();
                        TrySendNPCMessage("This is your parole check-in location. Return here whenever you are instructed to report.", 5f);
                    }
                    break;

                case ParoleIntakeState.ExplainingCheckInSchedule:
                    if (currentParolee != null)
                    {
                        MaintainFacingParolee();
                        TrySendNPCMessage(BuildCheckInScheduleMessage(currentParolee), 6f);
                    }
                    break;

                case ParoleIntakeState.FinalizingIntake:
                    if (currentParolee != null)
                    {
                        MaintainFacingParolee();
                        TrySendNPCMessage("Your initial parole intake is complete. Return to this location for your scheduled check-ins.", 5f);
                    }
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
        /// Performance: Override OnEnable to use custom state update handler
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            RestorePlayerAfterExplanation();
            paroleOfficer?.CompleteIntakeEscort();
            base.OnDisable();
        }

        /// <summary>
        /// Custom state update handler that includes parole intake state machine logic
        /// </summary>
        protected override void OnStateUpdateTick(float currentTime)
        {
            base.OnStateUpdateTick(currentTime);

            // Handle parole intake state machine
            ProcessIntakeState();
        }

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

        private void HandleIdleState()
        {
            // Intake entry is manager-driven so spawn state and officer availability
            // remain the single source of truth.
        }

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
                    ? ParoleIntakeState.EscortingToCheckIn
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

        private void HandleAwaitingReleaseSummaryState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            if (releaseSummaryAcknowledged)
            {
                ChangeIntakeState(ParoleIntakeState.EscortingToCheckIn);
            }
        }

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

        private void HandleExplainingCheckInLocationState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            MaintainFacingParolee();

            if (Time.time - stateStartTime >= processingDelay * 2f)
            {
                ChangeIntakeState(ParoleIntakeState.ExplainingCheckInSchedule);
            }
        }

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
        /// Start parole intake process for a parolee
        /// </summary>
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
            ChangeIntakeState(ParoleIntakeState.DetectingParolee);
            ModLogger.Info($"ParoleIntakeStateMachine: Preparing to meet {parolee.name} at police-station release point {meetingPoint}");
        }

        /// <summary>
        /// Releases the supervisor from the pre-dismissal wait once the player closes the release summary.
        /// </summary>
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
                ChangeIntakeState(ParoleIntakeState.EscortingToCheckIn);
            }

            return true;
        }

        /// <summary>
        /// Force start intake (for external calls)
        /// </summary>
        public void ForceStartIntake(Player parolee)
        {
            currentParolee = parolee;
            ChangeIntakeState(ParoleIntakeState.DetectingParolee);
            ModLogger.Info($"ParoleIntakeStateMachine: Force started intake for {parolee.name}");
        }

        /// <summary>
        /// Complete the intake process
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
        /// Check if intake is currently processing
        /// </summary>
        public bool IsProcessingIntake()
        {
            return currentState != ParoleIntakeState.Idle && currentState != ParoleIntakeState.ReturningToPost;
        }

        /// <summary>
        /// Stop intake process
        /// </summary>
        public void StopIntakeProcess()
        {
            if (IsProcessingIntake())
            {
                ModLogger.Info($"ParoleIntakeStateMachine: Stopping intake process");
                currentParolee = null;
                releaseSummaryAcknowledged = false;
                hasPreparedReleaseMeeting = false;
                RestorePlayerAfterExplanation();
                paroleOfficer?.CompleteIntakeEscort();
                ChangeIntakeState(ParoleIntakeState.ReturningToPost);
            }
        }

        #endregion
    }
}

