using System;
using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.UI;

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
            GreetingParolee,         // Initial greeting and identification
            ReviewingConditions,      // Explaining parole conditions
            IssuingParoleCard,       // Providing parole documentation
            FinalizingIntake,        // Completing intake process
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

        // Dialogue system
        private JailNPCDialogueController dialogueController;

        #endregion

        #region Events

        public System.Action<ParoleIntakeState> OnStateChanged;
        public System.Action<Player> OnIntakeStarted;
        public System.Action<Player> OnIntakeCompleted;

        #endregion

        #region Initialization

        protected override void Awake()
        {
            base.Awake();
            paroleOfficer = GetComponent<ParoleOfficerBehavior>();
            stationaryBehavior = GetComponent<StationaryBehavior>();
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
                // Fallback to police station route first waypoint
                var policeStationRoute = PresetParoleOfficerRoutes.GetRoute("PoliceStation");
                if (policeStationRoute != null && policeStationRoute.points != null && policeStationRoute.points.Length > 0)
                {
                    entrancePosition = policeStationRoute.points[0];
                }
                else
                {
                    entrancePosition = transform.position;
                }
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
        private IEnumerator WaitForDialogueController()
#endif
        {
            int retryCount = 0;
            const int maxRetries = 10;

            while (retryCount < maxRetries)
            {
                dialogueController = GetComponent<JailNPCDialogueController>();
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

            OnStateChanged?.Invoke(newState);
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
                ParoleIntakeState.GreetingParolee => "GreetingParolee",
                ParoleIntakeState.ReviewingConditions => "ReviewingConditions",
                ParoleIntakeState.IssuingParoleCard => "IssuingParoleCard",
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
                    // Stay at entrance position
                    if (stationaryBehavior != null)
                    {
                        stationaryBehavior.ReturnToPosition();
                    }
                    break;

                case ParoleIntakeState.DetectingParolee:
                    // Look for nearby parolee
                    break;

                case ParoleIntakeState.GreetingParolee:
                    // Face the parolee
                    if (currentParolee != null)
                    {
                        LookAt(currentParolee.transform.position);
                    }
                    break;

                case ParoleIntakeState.ReviewingConditions:
                    // Continue facing parolee
                    break;

                case ParoleIntakeState.IssuingParoleCard:
                    // Issue documentation
                    break;

                case ParoleIntakeState.FinalizingIntake:
                    // Complete intake
                    break;

                case ParoleIntakeState.ReturningToPost:
                    // Return to entrance
                    if (stationaryBehavior != null)
                    {
                        stationaryBehavior.ReturnToPosition();
                    }
                    break;
            }
        }

        #endregion

        #region Update Loop

        protected override void Update()
        {
            base.Update();

            if (!isInitialized) return;

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

                case ParoleIntakeState.GreetingParolee:
                    HandleGreetingParoleeState();
                    break;

                case ParoleIntakeState.ReviewingConditions:
                    HandleReviewingConditionsState();
                    break;

                case ParoleIntakeState.IssuingParoleCard:
                    HandleIssuingParoleCardState();
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
            // Check for new parolees nearby
            CheckForNewParolees();
        }

        private void HandleDetectingParoleeState()
        {
            // Wait a moment, then greet
            if (Time.time - stateStartTime >= processingDelay)
            {
                ChangeIntakeState(ParoleIntakeState.GreetingParolee);
            }
        }

        private void HandleGreetingParoleeState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            // Wait for greeting to complete, then review conditions
            if (Time.time - stateStartTime >= processingDelay * 2f)
            {
                ChangeIntakeState(ParoleIntakeState.ReviewingConditions);
            }
        }

        private void HandleReviewingConditionsState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            // Review conditions for a few seconds
            if (Time.time - stateStartTime >= processingDelay * 3f)
            {
                ChangeIntakeState(ParoleIntakeState.IssuingParoleCard);
            }
        }

        private void HandleIssuingParoleCardState()
        {
            if (currentParolee == null)
            {
                ChangeIntakeState(ParoleIntakeState.Idle);
                return;
            }

            // Issue parole card/documentation
            if (Time.time - stateStartTime >= processingDelay * 2f)
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

            // Finalize intake
            if (Time.time - stateStartTime >= processingDelay)
            {
                CompleteIntake();
            }
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

        #region Parolee Detection

        private void CheckForNewParolees()
        {
            // Check for players on parole nearby
            var players = GameObject.FindObjectsOfType<Player>();
            if (players == null || players.Length == 0) return;

            float detectionRange = 10f; // Detection range for new parolees

            foreach (var player in players)
            {
                if (player == null) continue;

                // Check if player is on parole
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet == null || rapSheet.CurrentParoleRecord == null) continue;
                if (!rapSheet.CurrentParoleRecord.IsOnParole()) continue;

                // Check if player just started parole (within last minute of game time)
                float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
                float paroleStartTime = rapSheet.CurrentParoleRecord.GetParoleStartTime();
                float timeSinceStart = currentGameTime - paroleStartTime;

                // Only process if parole started recently (within 1 game minute) and player is nearby
                if (timeSinceStart > 1f) continue; // Already processed or too old

                float distance = Vector3.Distance(transform.position, player.transform.position);
                if (distance <= detectionRange)
                {
                    // Found a new parolee - start intake
                    StartParoleIntake(player);
                    break;
                }
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

            if (currentState != ParoleIntakeState.Idle && currentState != ParoleIntakeState.DetectingParolee)
            {
                ModLogger.Warn($"ParoleIntakeStateMachine: Cannot start intake, already processing (state: {currentState})");
                return;
            }

            currentParolee = parolee;
            OnIntakeStarted?.Invoke(parolee);
            ChangeIntakeState(ParoleIntakeState.DetectingParolee);

            ModLogger.Info($"ParoleIntakeStateMachine: Started intake for {parolee.name}");
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

            ModLogger.Info($"ParoleIntakeStateMachine: Completed intake for {currentParolee.name}");

            // Record interaction
            var rapSheet = RapSheetManager.Instance.GetRapSheet(currentParolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                rapSheet.CurrentParoleRecord.RecordInteraction();
            }

            OnIntakeCompleted?.Invoke(currentParolee);
            currentParolee = null;

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
                ChangeIntakeState(ParoleIntakeState.ReturningToPost);
            }
        }

        #endregion
    }
}

