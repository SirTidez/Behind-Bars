using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Dialogue;
using Behind_Bars.Systems.Parole;
using Behind_Bars.Systems.Parole.Conditions;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.Dialogue;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Owns supervising-officer check-in interaction flow, dialogue, and session state.
    /// ParoleManager provides scheduling/validation; ParoleOfficerBehavior only ensures this controller exists.
    /// </summary>
    public class ParoleCheckInSystem : MonoBehaviour
    {
#if !MONO
        public ParoleCheckInSystem(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Configuration

        // Proximity/cooldown use real Unity seconds.  The parole manager remains
        // the authority for game-clock check-in windows and consequences.
        private const float CHECK_IN_PROXIMITY = 5f; // Distance to trigger check-in
        private const float CHECK_IN_COOLDOWN = 30f; // Cooldown between check-ins (real seconds)
        private const float CHECK_IN_PROCESSING_TIME = 3f; // Time to process check-in
        private const string CHECK_IN_DIALOGUE_CONTAINER_NAME = "ParoleOfficer_CheckIn";
        private const string CHECK_IN_CHOICE_LABEL = "checkin_request";

        #endregion

        #region Component References

        // These references are scene-owned helpers.  The check-in system may
        // start before dialogue components finish loading, so the trigger setup
        // coroutine is intentionally retryable.
        private ParoleOfficerBehavior paroleOfficer;
        private JailNPCDialogueController dialogueController;
        private StationaryBehavior stationaryBehavior;
        private NPCDialogueWrapper dialogueWrapper;
        private DialogueHandler dialogueHandler;
        private DialogueController baseDialogueController;
        private bool interactionHooked;

        #endregion

        #region State

        // Only this exact player may be completed or released from the coordinator
        // while a check-in is active; proximity searches must not replace it.
        private Player currentCheckInParolee;
        // Set for the duration of ProcessCheckIn and cleared by normal completion
        // or arrest abort.  The coordinator watchdog uses this state as evidence
        // that the check-in session is still alive.
        private bool isProcessingCheckIn = false;
        // Set when the routine pocket search starts an arrest so normal cleanup
        // does not restore movement or treat the check-in as successful.
        private bool checkInArrestInitiated;
        // Last attempted check-in timestamps are real-time cooldown entries keyed
        // by Player; scheduling eligibility is checked separately by ParoleManager.
        private Dictionary<Player, float> lastCheckInTimes = new Dictionary<Player, float>();

        #endregion

        #region Initialization

        /// <summary>
        /// Caches the supervising officer and scene helpers.  Dialogue ownership
        /// is established asynchronously in <see cref="Start"/> after native
        /// components have had a chance to attach.
        /// </summary>
        private void Awake()
        {
            paroleOfficer = BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(gameObject);
            dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
            stationaryBehavior = BBHelpers.GetComponentSafe<StationaryBehavior>(gameObject);
        }

        /// <summary>Starts retryable interaction-trigger setup after scene load.</summary>
        private void Start()
        {
            MelonCoroutines.Start(WaitForInteractionTrigger());
        }

        /// <summary>
        /// Disposes the dialogue wrapper and releases any exact check-in player
        /// still owned by the coordinator when this component is destroyed.
        /// </summary>
        private void OnDestroy()
        {
            dialogueWrapper?.Dispose();
            dialogueWrapper = null;

            if (currentCheckInParolee != null)
            {
                ReleaseCheckInOwnership(currentCheckInParolee);
            }
        }

        #endregion

        #region Check-In Detection

        /// <summary>
        /// Retries interaction setup at half-second intervals until the hook is
        /// installed or twenty attempts fail.  A failed hook leaves the component
        /// inactive rather than fabricating a dialogue controller.
        /// </summary>
        private IEnumerator WaitForInteractionTrigger()
        {
            int retries = 0;
            while (!interactionHooked && retries < 20)
            {
                SetupInteractionTrigger();
                if (!interactionHooked)
                {
                    retries++;
                    yield return new WaitForSeconds(0.5f);
                }
            }

            if (!interactionHooked)
            {
                ModLogger.Warn($"ParoleCheckInSystem: Failed to hook interaction trigger on {gameObject.name}");
            }
        }

        /// <summary>
        /// Installs the check-in dialogue wrapper, suppresses greeting overrides,
        /// registers the container, and binds the managed choice callback.  The
        /// operation is idempotent while <see cref="interactionHooked"/> is true.
        /// </summary>
        private void SetupInteractionTrigger()
        {
            if (interactionHooked)
            {
                return;
            }

            try
            {
                dialogueWrapper = new NPCDialogueWrapper(gameObject);
                dialogueWrapper.EnsureHandler();
                dialogueHandler = dialogueWrapper.Handler;
                if (dialogueHandler == null)
                {
                    ModLogger.Warn($"ParoleCheckInSystem: DialogueHandler not found on {gameObject.name}");
                    return;
                }

                baseDialogueController = BBHelpers.GetComponentSafe<DialogueController>(dialogueHandler.gameObject);
                if (baseDialogueController == null)
                {
                    ModLogger.Warn($"ParoleCheckInSystem: DialogueController not found on {gameObject.name}");
                    return;
                }

                baseDialogueController.DialogueEnabled = true;
                baseDialogueController.UseDialogueBehaviour = true;

                DisableGreetingOverrides();
                RegisterCheckInDialogueContainer();

                dialogueWrapper.ClearCallbacks();
                dialogueWrapper.OnChoiceSelected(CHECK_IN_CHOICE_LABEL, OnCheckInDialogueChoiceSelected);

                if (!dialogueWrapper.UseContainerOnInteract(CHECK_IN_DIALOGUE_CONTAINER_NAME))
                {
                    ModLogger.Warn($"ParoleCheckInSystem: Failed to set check-in container override on {gameObject.name}");
                    return;
                }

                interactionHooked = true;
                ModLogger.Debug($"ParoleCheckInSystem: Check-in interaction hooked on {gameObject.name}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleCheckInSystem: Failed to set up interaction trigger: {ex.Message}");
            }
        }

        /// <summary>
        /// Disables native greeting overrides so the check-in container remains
        /// the visible interaction surface.
        /// </summary>
        private void DisableGreetingOverrides()
        {
            if (baseDialogueController?.GreetingOverrides == null)
            {
                return;
            }

            foreach (var greetingOverride in baseDialogueController.GreetingOverrides)
            {
                greetingOverride.ShouldShow = false;
            }
        }

        /// <summary>
        /// Builds or replaces the check-in dialogue container and moves it to the
        /// front of the handler list.  The current implementation intentionally
        /// gives this container index-zero precedence over other containers.
        /// </summary>
        private void RegisterCheckInDialogueContainer()
        {
            if (dialogueHandler == null)
            {
                return;
            }

            var builder = new DialogueContainerBuilder();
            builder.AddNode("ENTRY", "Do you need to report for your parole check-in?", choices =>
            {
                choices.Add(CHECK_IN_CHOICE_LABEL, "Yes, I am here to check in.", "checkin_processing");
                choices.Add("checkin_later", "Not right now.", "checkin_later_node");
            });
            builder.AddNode("checkin_processing", "Understood. I am reviewing your parole record now.", null);
            builder.AddNode("checkin_later_node", "Return during your assigned check-in window when you are ready to report.", null);
            builder.SetAllowExit(true);

            var container = builder.Build(CHECK_IN_DIALOGUE_CONTAINER_NAME);

            if (dialogueHandler.dialogueContainers == null)
            {
#if !MONO
                dialogueHandler.dialogueContainers = new Il2CppSystem.Collections.Generic.List<DialogueContainer>();
#else
                dialogueHandler.dialogueContainers = new System.Collections.Generic.List<DialogueContainer>();
#endif
            }

            bool replaced = false;
            for (int i = 0; i < dialogueHandler.dialogueContainers.Count; i++)
            {
                var existing = dialogueHandler.dialogueContainers[i];
                if (existing != null && existing.name == CHECK_IN_DIALOGUE_CONTAINER_NAME)
                {
                    dialogueHandler.dialogueContainers[i] = container;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                dialogueHandler.dialogueContainers.Add(container);
            }

            dialogueHandler.dialogueContainers.Remove(container);
            dialogueHandler.dialogueContainers.Insert(0, container);
        }

        /// <summary>Re-applies the check-in container override after a dialogue exit.</summary>
        private void EnsureContainerOnInteract()
        {
            if (dialogueWrapper == null)
            {
                return;
            }

            DisableGreetingOverrides();
            dialogueWrapper.UseContainerOnInteract(CHECK_IN_DIALOGUE_CONTAINER_NAME);
        }

        /// <summary>
        /// Handles the check-in choice after rejecting active intake, missing
        /// nearby parolees, and non-supervising officers before starting the
        /// coordinator-backed check-in transaction.
        /// </summary>
        private void OnCheckInDialogueChoiceSelected()
        {
            if (isProcessingCheckIn)
            {
                return;
            }

            if (paroleOfficer == null || paroleOfficer.GetRole() != ParoleOfficerBehavior.ParoleOfficerRole.SupervisingOfficer)
            {
                return;
            }

            if (paroleOfficer.IsIntakeProcessingActive())
            {
                var baseNpcBusy = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
                baseNpcBusy?.TrySendNPCMessage("I am processing intake right now. Come back in a moment.", 3f);
                dialogueWrapper?.End();
                EnsureContainerOnInteract();
                return;
            }

            var parolee = FindNearbyParoleeForInteraction();
            if (parolee == null)
            {
                var baseNpcNoPlayer = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
                baseNpcNoPlayer?.TrySendNPCMessage("Step closer if you need to check in.", 3f);
                EnsureContainerOnInteract();
                return;
            }

            InitiateCheckIn(parolee);
        }

        /// <summary>
        /// Finds the nearest on-parole player within the interaction radius whose
        /// real-time attempt cooldown has expired.  The selected player becomes
        /// the exact session identity passed to <see cref="InitiateCheckIn"/>.
        /// </summary>
        private Player FindNearbyParoleeForInteraction()
        {
            var players = GameObject.FindObjectsOfType<Player>();
            if (players == null || players.Length == 0)
            {
                return null;
            }

            Player closest = null;
            float closestDistance = float.MaxValue;

            foreach (var player in players)
            {
                if (player == null)
                {
                    continue;
                }

                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet == null || rapSheet.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    continue;
                }

                float distance = Vector3.Distance(transform.position, player.transform.position);
                if (distance > CHECK_IN_PROXIMITY || distance >= closestDistance)
                {
                    continue;
                }

                if (lastCheckInTimes.TryGetValue(player, out float lastAttemptTime))
                {
                    if (Time.time - lastAttemptTime < CHECK_IN_COOLDOWN)
                    {
                        continue;
                    }
                }

                closest = player;
                closestDistance = distance;
            }

            return closest;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Reserves check-in ownership, asks ParoleManager to validate the current
        /// game-clock window, and starts the processing coroutine.  Failed manager
        /// validation rolls back coordinator ownership and shows rejection dialogue.
        /// </summary>
        /// <param name="parolee">The exact player to retain for the check-in.</param>
        public void InitiateCheckIn(Player parolee)
        {
            if (parolee == null)
            {
                ModLogger.Warn("ParoleCheckInSystem: Cannot initiate check-in, parolee is null");
                return;
            }

            if (isProcessingCheckIn)
            {
                ModLogger.Debug("ParoleCheckInSystem: Already processing a check-in");
                return;
            }

            if (paroleOfficer == null || paroleOfficer.GetRole() != ParoleOfficerBehavior.ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn("ParoleCheckInSystem: Officer is not a supervising officer");
                return;
            }

            var interactionCoordinator = Core.ResolveDynamicParoleOfficerManager();
            if (interactionCoordinator != null &&
                !interactionCoordinator.TryReserveCheckIn(parolee, paroleOfficer))
            {
                ModLogger.Debug($"ParoleCheckInSystem: Check-in already active or blocked for {parolee.name}");
                return;
            }

            var paroleManager = Core.ResolveParoleManager();
            if (paroleManager != null)
            {
                if (!paroleManager.TryBeginCheckInSession(parolee, out var status, out string windowText))
                {
                    interactionCoordinator?.CancelCheckIn(parolee, paroleOfficer);
                    ShowCheckInRejectedDialogue(status, windowText);
                    lastCheckInTimes[parolee] = Time.time;
                    EnsureContainerOnInteract();
                    return;
                }
            }

            currentCheckInParolee = parolee;
            isProcessingCheckIn = true;
            interactionCoordinator?.StartCheckIn(parolee, paroleOfficer);

            ModLogger.Info($"ParoleCheckInSystem: Initiating check-in for {parolee.name}");
            MelonCoroutines.Start(ProcessCheckIn(parolee));
        }

        /// <summary>
        /// Presents the scheduling rejection state and restores the check-in
        /// container after the rejection interaction closes.
        /// </summary>
        private void ShowCheckInRejectedDialogue(ParoleManager.CheckInStatus status, string windowText)
        {
            if (dialogueController == null)
            {
                dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
            }

            if (dialogueController == null)
            {
                var baseNpcFallback = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
                baseNpcFallback?.TrySendNPCMessage("You are not eligible to check in right now.", 3f);
                EnsureContainerOnInteract();
                return;
            }

            string state = status switch
            {
                ParoleManager.CheckInStatus.TooEarly => "CheckInTooEarly",
                ParoleManager.CheckInStatus.MissedWindow => "CheckInMissedWindow",
                ParoleManager.CheckInStatus.NoScheduledWindow => "CheckInNoSchedule",
                _ => "CheckInWarning"
            };

            dialogueController.UpdateGreetingForState(state);
            dialogueController.SendContextualMessage("interaction");

            if (status == ParoleManager.CheckInStatus.TooEarly && !string.IsNullOrWhiteSpace(windowText))
            {
                var baseNpc = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
                baseNpc?.TrySendNPCMessage($"Your check-in window is between {windowText}.", 4f);
            }

            EnsureContainerOnInteract();
        }

        /// <summary>
        /// Runs the ordered check-in phases: rapport greeting, compliance review,
        /// routine pocket search, optional conditions, recording, completion, and
        /// coordinator cleanup.  A pocket-search arrest exits through the abort
        /// path and must not continue to normal completion.
        /// </summary>
        /// <param name="parolee">The player captured by the active check-in session.</param>
        private IEnumerator ProcessCheckIn(Player parolee)
        {
            checkInArrestInitiated = false;
            if (dialogueController == null)
            {
                dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
            }

            // Get rapport tier for dialogue variant selection
            string greetingState = "CheckInGreeting";
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(parolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                var rapportTier = rapSheet.CurrentParoleRecord.GetRapportTier();
                switch (rapportTier)
                {
                    case RapportTier.Hostile:
                        greetingState = "CheckInGreetingHostile";
                        break;
                    case RapportTier.Friendly:
                        greetingState = "CheckInGreetingFriendly";
                        break;
                    case RapportTier.Trusted:
                        greetingState = "CheckInGreetingTrusted";
                        break;
                    default:
                        greetingState = "CheckInGreeting";
                        break;
                }
            }

            // Update dialogue to rapport-appropriate greeting
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState(greetingState);
                dialogueController.SendContextualMessage("greeting");
            }

            // Face the parolee
            if (parolee != null)
            {
                var baseNPC = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
                if (baseNPC != null)
                {
                    baseNPC.LookAt(parolee.transform.position);
                }
            }

            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

            // Review compliance
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("CheckInReviewing");
            }

            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

            // Get parole record
            if (rapSheet?.CurrentParoleRecord != null)
            {
                var paroleRecord = rapSheet.CurrentParoleRecord;
                float complianceScore = paroleRecord.GetComplianceScore();
                int violationCount = paroleRecord.GetViolationCount();

                // Determine feedback based on compliance
                string feedbackState;
                if (complianceScore >= 80f && violationCount == 0)
                {
                    feedbackState = "CheckInCompliant";
                }
                else if (complianceScore >= 50f || violationCount <= 1)
                {
                    feedbackState = "CheckInWarning";
                }
                else
                {
                    feedbackState = "CheckInWarning"; // Use warning for low compliance
                }

                // Update dialogue
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState(feedbackState);
                    dialogueController.SendContextualMessage("interaction");
                }

                yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

                // A scheduled check-in is a routine compliance event, not just a record
                // review. Inspect the player's carried inventory before marking the visit
                // complete; weapons are only classified as contraband in this parole path.
                yield return ProcessRoutinePocketSearch(parolee);
                if (checkInArrestInitiated)
                {
                    AbortCheckInForArrest(parolee);
                    yield break;
                }

                // Drug test phase (if condition is active)
                yield return ProcessDrugTest(parolee, rapSheet, paroleRecord);

                // Employment verification phase (if condition is active)
                yield return ProcessEmploymentCheck(parolee, rapSheet, paroleRecord);

                // Fee payment phase (if fees are owed)
                yield return ProcessFeePayment(parolee, rapSheet, paroleRecord);

                // Record check-in
                RecordCheckIn(parolee);

                yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

                // Complete check-in
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState("CheckInComplete");
                }

                yield return new WaitForSeconds(2f);
            }

            var completedParolee = currentCheckInParolee;

            // Reset state
            isProcessingCheckIn = false;
            currentCheckInParolee = null;

            if (completedParolee != null)
            {
                EndCheckInSession(completedParolee);
            }

            // Return to idle dialogue
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("Idle");
            }

            try
            {
                dialogueWrapper?.End();
            }
            catch { }

            // Return to entrance position if stationary
            if (stationaryBehavior != null)
            {
                stationaryBehavior.ReturnToPosition();
            }

            EnsureContainerOnInteract();
        }

        /// <summary>
        /// Performs the scheduled parole pocket search before recording completion.
        /// Contraband is handed to the shared parole-search classifier; an arrest
        /// sets the local abort flag and leaves custody cleanup to the arrest flow.
        /// </summary>
        private IEnumerator ProcessRoutinePocketSearch(Player parolee)
        {
            if (parolee == null || paroleOfficer == null)
            {
                yield break;
            }

            dialogueController?.UpdateGreetingForState("CheckInReviewing");
            paroleOfficer.UpdateSearchNotification("Routine pocket search - remain still");
            paroleOfficer.TrySendNPCMessage("Before I clear this check-in, I need to search your pockets. Remain still.", 4f);
            yield return new WaitForSeconds(1.25f);

            var crimeDetectionSystem = CrimeDetectionSystem.Instance;
            if (crimeDetectionSystem == null)
            {
                ModLogger.Warn("ParoleCheckInSystem: Skipped pocket search because CrimeDetectionSystem was unavailable");
                yield break;
            }

            var detectedCrimes = new ContrabandDetectionSystem(crimeDetectionSystem).PerformContrabandSearch(
                parolee,
                ContrabandSearchContext.Parole);

            if (detectedCrimes != null && detectedCrimes.Count > 0)
            {
                checkInArrestInitiated = ParoleSearchSystem.Instance.ProcessDetectedParoleContraband(
                    paroleOfficer,
                    parolee,
                    detectedCrimes,
                    "scheduled parole check-in pocket search");
                paroleOfficer.ShowSearchResults(true, detectedCrimes.Count);
                yield break;
            }

            paroleOfficer.ShowSearchResults(false);
            paroleOfficer.TrySendNPCMessage("Pocket search is clear. Continuing your check-in.", 3f);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Aborts check-in after a compliance-search arrest, clears local state,
        /// releases manager/coordinator ownership, and restores the interaction
        /// container without recording a successful visit.
        /// </summary>
        private void AbortCheckInForArrest(Player parolee)
        {
            ModLogger.Info($"ParoleCheckInSystem: Ended check-in for {parolee?.name ?? "unknown parolee"} because a compliance search initiated custody");

            isProcessingCheckIn = false;
            currentCheckInParolee = null;
            if (parolee != null)
            {
                EndCheckInSession(parolee);
            }

            try
            {
                dialogueWrapper?.End();
            }
            catch { }

            stationaryBehavior?.ReturnToPosition();
            EnsureContainerOnInteract();
        }

        /// <summary>
        /// Ends both ParoleManager and supervising-officer coordinator ownership
        /// for the exact player, if one is supplied.
        /// </summary>
        private void EndCheckInSession(Player parolee)
        {
            if (parolee == null)
            {
                return;
            }

            Core.ResolveParoleManager()?.EndCheckInSession(parolee);
            Core.ResolveDynamicParoleOfficerManager()?.CompleteSupervisingOfficerCheckIn(parolee, paroleOfficer);
        }

        /// <summary>Teardown alias used to release an active check-in during destruction.</summary>
        private void ReleaseCheckInOwnership(Player parolee)
        {
            if (parolee == null)
            {
                return;
            }

            EndCheckInSession(parolee);
        }

        /// <summary>
        /// Applies the manager's daily-check-in timing gate, then records the
        /// interaction in the parole record and marks the rap sheet changed.  A
        /// late/invalid window shows rejection dialogue instead of recording.
        /// </summary>
        /// <param name="parolee">The player whose check-in should be recorded.</param>
        private void RecordCheckIn(Player parolee)
        {
            var paroleManager = Core.ResolveParoleManager();
            if (paroleManager != null && !paroleManager.NotifyDailyCheckInCompleted(parolee))
            {
                ModLogger.Info($"ParoleCheckInSystem: Check-in denied for {parolee.name} due to timing/scheduling rules");
                var status = paroleManager.GetDailyCheckInStatus(parolee, out string windowText, applyConsequences: false);
                ShowCheckInRejectedDialogue(status, windowText);
                lastCheckInTimes[parolee] = Time.time;
                return;
            }

            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(parolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                rapSheet.CurrentParoleRecord.RecordCheckIn();
                rapSheet.CurrentParoleRecord.RecordInteraction();
                Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);
                ModLogger.Info($"ParoleCheckInSystem: Recorded check-in for {parolee.name}");
            }

            // Update last check-in time
            lastCheckInTimes[parolee] = Time.time;
        }

        /// <summary>
        /// Returns whether the check-in coroutine currently owns a parolee.
        /// </summary>
        /// <returns>True while normal check-in processing or its condition phases run.</returns>
        public bool IsProcessingCheckIn()
        {
            return isProcessingCheckIn;
        }

        /// <summary>
        /// Gets the exact player retained by the active check-in session.
        /// </summary>
        /// <returns>The active parolee, or null when no check-in is processing.</returns>
        public Player GetCurrentCheckInParolee()
        {
            return currentCheckInParolee;
        }

        /// <summary>
        /// Runs the optional drug-test condition using the parole record's LSI
        /// probability.  A failed test mutates compliance/rapport and records a
        /// contraband violation; a clean result only grants the current rapport
        /// adjustment.
        /// </summary>
        /// <param name="parolee">Player whose inventory is checked.</param>
        /// <param name="rapSheet">Current rap sheet used for LSI and persistence.</param>
        /// <param name="paroleRecord">Active parole record containing condition state.</param>
        private IEnumerator ProcessDrugTest(Player parolee, RapSheet rapSheet, ParoleRecord paroleRecord)
        {
            if (!paroleRecord.IsConditionActive("drug_test"))
                yield break;

            // Random chance of test based on LSI level
            float testChance = DrugTestCondition.GetTestProbability(rapSheet.LSILevel);
            float roll = UnityEngine.Random.Range(0f, 1f);

            if (roll > testChance)
            {
                ModLogger.Debug($"[DRUG TEST] Skipped for {parolee.name} (roll {roll:F2} > chance {testChance:F2})");
                yield break;
            }

            // Announce drug test
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("DrugTestAnnounce");
            }
            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

            // Check player inventory for drug items
            bool hasDrugs = Core.ResolveParoleManager() != null && CheckPlayerForDrugs(parolee);

            if (hasDrugs)
            {
                // Failed test
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState("DrugTestFail");
                }

                paroleRecord.AdjustComplianceScore(-15f);
                paroleRecord.AdjustRapport(-15f);

                var violation = new ViolationRecord(ViolationType.ContrabandPossession,
                    "Failed drug test during check-in - drug items found in inventory", 2.5f);
                rapSheet.AddParoleViolation(violation);
                Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);

                ModLogger.Info($"[DRUG TEST] {parolee.name} FAILED drug test");
            }
            else
            {
                // Passed test
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState("DrugTestPass");
                }

                paroleRecord.AdjustRapport(1f); // Small rapport boost for clean test
                Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);

                ModLogger.Info($"[DRUG TEST] {parolee.name} passed drug test");
            }

            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);
        }

        /// <summary>
        /// Checks the local player's hotbar slots for drug-like item names.  This
        /// is the current reduced compatibility implementation: it does not call
        /// the parole-system classifier and relies on case-insensitive substring
        /// matching after reflection resolves each ItemInstance name.
        /// </summary>
        /// <param name="parolee">The player being tested; the current implementation
        /// uses the process-wide PlayerInventory singleton rather than this object's
        /// inventory reference.</param>
        /// <returns>True when a recognized drug keyword is present in a hotbar slot.</returns>
        private bool CheckPlayerForDrugs(Player parolee)
        {
            try
            {
                // Access player inventory and check for drug items
                // This reuses the same detection logic as ParoleSystem.CheckForSearchViolations
#if !MONO
                var inventory = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
#else
                var inventory = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerInventory>.Instance;
#endif
                if (inventory == null) return false;

                // Check hotbar slots
                for (int i = 0; i < inventory.hotbarSlots.Count; i++)
                {
                    object slot = inventory.hotbarSlots[i];
                    var itemInstance = GetSlotItemInstance(slot);
                    if (itemInstance == null) continue;

                    string itemName = GetItemName(itemInstance).ToLowerInvariant();
                    if (itemName.Contains("weed") || itemName.Contains("meth") ||
                        itemName.Contains("coke") || itemName.Contains("cocaine") ||
                        itemName.Contains("heroin") || itemName.Contains("joint") ||
                        itemName.Contains("baggie") || itemName.Contains("brick"))
                    {
                        return true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[DRUG TEST] Error checking inventory: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Resolves an item display name through the supported property/definition
        /// shapes.  If no name is exposed, the runtime type name is returned so
        /// the reduced string classifier still has a deterministic input.
        /// </summary>
        private static string GetItemName(object itemInstance)
        {
            if (itemInstance == null)
            {
                return string.Empty;
            }

            try
            {
                var itemType = itemInstance.GetType();

                var nameProperty = itemType.GetProperty("Name");
                if (nameProperty != null)
                {
                    var nameValue = nameProperty.GetValue(itemInstance) as string;
                    if (!string.IsNullOrWhiteSpace(nameValue))
                    {
                        return nameValue;
                    }
                }

                var definitionProperty = itemType.GetProperty("Definition");
                if (definitionProperty != null)
                {
                    var definition = definitionProperty.GetValue(itemInstance);
                    if (definition != null)
                    {
                        var definitionType = definition.GetType();
                        var definitionNameProperty = definitionType.GetProperty("name") ?? definitionType.GetProperty("Name");
                        if (definitionNameProperty != null)
                        {
                            var definitionName = definitionNameProperty.GetValue(definition) as string;
                            if (!string.IsNullOrWhiteSpace(definitionName))
                            {
                                return definitionName;
                            }
                        }

                        var definitionNameField = definitionType.GetField("name") ?? definitionType.GetField("Name");
                        if (definitionNameField != null)
                        {
                            var definitionName = definitionNameField.GetValue(definition) as string;
                            if (!string.IsNullOrWhiteSpace(definitionName))
                            {
                                return definitionName;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"[DRUG TEST] Failed to resolve item name: {ex.Message}");
            }

            return itemInstance.GetType().Name;
        }

        /// <summary>
        /// Reads a slot's ItemInstance property through the compatibility path,
        /// returning null when the slot or property cannot be resolved.
        /// </summary>
        private static object GetSlotItemInstance(object slot)
        {
            if (slot == null)
            {
                return null;
            }

            try
            {
                var property = slot.GetType().GetProperty("ItemInstance");
                return property?.GetValue(slot);
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"[DRUG TEST] Failed to read ItemInstance from {slot.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Applies the optional employment condition, recording warnings and
        /// compliance consequences through the parole record before returning to
        /// the main check-in sequence.
        /// </summary>
        /// <param name="parolee">Player whose employment is evaluated.</param>
        /// <param name="rapSheet">Rap sheet receiving persisted changes.</param>
        /// <param name="paroleRecord">Active parole record containing condition state.</param>
        private IEnumerator ProcessEmploymentCheck(Player parolee, RapSheet rapSheet, ParoleRecord paroleRecord)
        {
            if (!paroleRecord.IsConditionActive("employment"))
                yield break;

            bool isEmployed = EmploymentCondition.IsPlayerEmployed();

            if (isEmployed)
            {
                // Employed - minor positive feedback
                paroleRecord.ResetConditionWarnings("employment");
                Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);
                ModLogger.Debug($"[EMPLOYMENT] {parolee.name} is employed (owns property/business)");
                yield break; // No special dialogue needed for positive result
            }

            // Not employed - graduated consequences.  Warning progression lives on
            // the persisted parole record rather than being inferred from formal
            // violations, which previously reset it after every save/load.
            int employmentWarnings = paroleRecord.RecordConditionWarning("employment");

            if (employmentWarnings < EmploymentCondition.WARNINGS_BEFORE_VIOLATION)
            {
                // Warning only
                paroleRecord.AdjustRapport(-3f);
                paroleRecord.AdjustComplianceScore(-2f);

                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState("CheckInWarning");
                }

                Core.ResolveParoleManager()?.SendSupervisingOfficerText(parolee,
                    $"Employment reminder: You need to maintain employment or income. Warning {employmentWarnings}/{EmploymentCondition.WARNINGS_BEFORE_VIOLATION}.");

                ModLogger.Info($"[EMPLOYMENT] Warning {employmentWarnings} for {parolee.name}");
            }
            else
            {
                // Formal violation after enough warnings
                paroleRecord.AdjustComplianceScore(-5f);
                paroleRecord.AdjustRapport(-5f);

                var violation = new ViolationRecord(ViolationType.Other,
                    $"Failed to maintain employment ({employmentWarnings} consecutive failures)", 1.5f);
                rapSheet.AddParoleViolation(violation);
                paroleRecord.ResetConditionWarnings("employment");

                Core.ResolveParoleManager()?.SendSupervisingOfficerText(parolee,
                    "Employment condition violated. Formal violation recorded.");

                ModLogger.Info($"[EMPLOYMENT] Formal violation for {parolee.name} after {employmentWarnings} failures");
            }

            Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);
            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);
        }

        /// <summary>
        /// Attempts automatic payment for any fees owed during check-in and
        /// records the current condition outcome through the parole manager.
        /// </summary>
        /// <param name="parolee">Player whose fees are evaluated and paid.</param>
        /// <param name="rapSheet">Rap sheet used by the fee system.</param>
        /// <param name="paroleRecord">Active parole record containing fee state.</param>
        private IEnumerator ProcessFeePayment(Player parolee, RapSheet rapSheet, ParoleRecord paroleRecord)
        {
            float feesOwed = paroleRecord.GetTotalFeesOwed();
            if (feesOwed <= 0f) yield break;

            // Attempt automatic payment
            bool paid = Core.ResolveParoleFeeSystem().AttemptPayment(parolee, rapSheet);

            if (paid)
            {
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState("CheckInCompliant");
                }
                ModLogger.Info($"[FEES] {parolee.name} paid supervision fees at check-in");
            }
            else
            {
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState("CheckInWarning");
                }
                ModLogger.Info($"[FEES] {parolee.name} could not pay supervision fees (${feesOwed:F0} owed)");
            }

            yield return new WaitForSeconds(1f);
        }

        #endregion
    }
}

