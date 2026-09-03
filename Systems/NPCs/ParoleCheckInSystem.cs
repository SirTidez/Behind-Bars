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
        private object checkInCoroutine;
        private bool checkInProcessingStarted;
        private Player dialogueSubject;
        private bool initialIntroductionComplete;
        private bool initialLocationConversationComplete;
        private string pendingViolationType = string.Empty;
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
            if (checkInCoroutine != null)
            {
                try { MelonCoroutines.Stop(checkInCoroutine); } catch { }
                checkInCoroutine = null;
            }

            var ownedParolee = currentCheckInParolee;
            isProcessingCheckIn = false;
            currentCheckInParolee = null;
            if (ownedParolee != null)
            {
                EndCheckInSession(ownedParolee);
            }
            dialogueWrapper?.Dispose();
            dialogueWrapper = null;
        }

        #endregion

        #region Check-In Detection

        /// <summary>
        /// Retries interaction setup at half-second intervals until the hook is
        /// installed or twenty attempts fail.  A failed hook leaves the component
        /// inactive rather than fabricating a dialogue controller.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
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

                RegisterDialogueCallbacks();

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

        /// <summary>Registers the stable choice/node callbacks owned by the supervising officer.</summary>
        private void RegisterDialogueCallbacks()
        {
            dialogueWrapper.ClearCallbacks();
            dialogueWrapper
                .OnChoiceSelected(CHECK_IN_CHOICE_LABEL, OnCheckInDialogueChoiceSelected)
                .OnChoiceSelected("conditions_request", OnConditionsDialogueChoiceSelected)
                .OnChoiceSelected("schedule_request", OnScheduleDialogueChoiceSelected)
                .OnChoiceSelected("checkin_response_compliant", () => ApplyRecurringDialogueOutcome(2f, 1f))
                .OnChoiceSelected("checkin_response_struggling", () => ApplyRecurringDialogueOutcome(0f, 2f))
                .OnChoiceSelected("checkin_response_dismissive", () => ApplyRecurringDialogueOutcome(-4f, -4f))
                .OnChoiceSelected("initial_cooperative", () => ApplyInitialInterviewOutcome(-3, 2f, 3f))
                .OnChoiceSelected("initial_uncertain", () => ApplyInitialInterviewOutcome(1, 0f, 1f))
                .OnChoiceSelected("initial_defiant", () => ApplyInitialInterviewOutcome(8, -5f, -5f))
                .OnChoiceSelected("initial_begin_escort", CompleteInitialIntroduction)
                .OnChoiceSelected("initial_location_acknowledge", () => ApplyInitialLocationOutcome(2f, 1f))
                .OnChoiceSelected("initial_location_complete_neutral", () => ApplyInitialLocationOutcome(0f, 1f))
                .OnChoiceSelected("initial_location_dismissive", () => ApplyInitialLocationOutcome(-3f, -3f))
                .OnChoiceSelected("initial_location_finish", CompleteInitialLocationConversation)
                .OnChoiceSelected("violation_accept", () => CompleteViolationConversation(1f, 1f))
                .OnChoiceSelected("violation_explain", () => CompleteViolationConversation(0f, 0f))
                .OnChoiceSelected("violation_dismiss", () => CompleteViolationConversation(-4f, -5f))
                .OnChoiceSelected("checkin_later", EndCurrentConversation)
                .OnChoiceSelected("conversation_end", EndCurrentConversation)
                .OnNodeDisplayed("checkin_processing", StartAcceptedCheckInProcessing);
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

            string scheduleText = BuildScheduleDialogueText(dialogueSubject);
            string conditionsText = BuildConditionsDialogueText(dialogueSubject);
            string violationText = string.IsNullOrWhiteSpace(pendingViolationType)
                ? "We need to discuss a reported parole violation."
                : $"We need to discuss this parole violation: {pendingViolationType}.";

            var builder = new DialogueContainerBuilder();
            builder.AddNode("ENTRY", "What do you need to discuss?", choices =>
            {
                choices.Add(CHECK_IN_CHOICE_LABEL, "I'm here for my scheduled check-in.", "checkin_assessment");
                choices.Add("conditions_request", "Review my parole conditions.");
                choices.Add("schedule_request", "When is my next check-in?");
                choices.Add("conversation_end", "Nothing right now.");
            });
            builder.AddNode("checkin_assessment", "Before I review the record, tell me how things have gone since your last report.", choices =>
            {
                choices.Add("checkin_response_compliant", "I've followed every condition.", "checkin_response_compliant_node");
                choices.Add("checkin_response_struggling", "I've struggled, but I'm trying to stay on track.", "checkin_response_struggling_node");
                choices.Add("checkin_response_dismissive", "This supervision is a waste of my time.", "checkin_response_dismissive_node");
            });
            builder.AddNode("checkin_response_compliant_node", "Good. Your record will tell me whether that matches what we have.", choices =>
                choices.Add("checkin_continue", "Understood.", "checkin_processing"));
            builder.AddNode("checkin_response_struggling_node", "Being honest matters. We'll review the record and address any problems.", choices =>
                choices.Add("checkin_continue", "Understood.", "checkin_processing"));
            builder.AddNode("checkin_response_dismissive_node", "Your attitude is part of this assessment. We are continuing the check-in.", choices =>
                choices.Add("checkin_continue", "Fine.", "checkin_processing"));
            builder.AddNode("checkin_processing", "I'm reviewing your parole record now.", null);
            builder.AddNode("checkin_compliant", "Your current record shows acceptable compliance.", null);
            builder.AddNode("checkin_warning", "Your record has compliance concerns. Further problems may result in a violation.", null);
            builder.AddNode("pocket_search", "Before I clear this check-in, I need to search your pockets. Remain still.", null);
            builder.AddNode("pocket_search_clear", "The pocket search is clear. We can continue.", null);
            builder.AddNode("drug_test_announce", "You were selected for a random urinalysis today.", null);
            builder.AddNode("drug_test_pass", "Your urinalysis is clear.", null);
            builder.AddNode("drug_test_fail", "Your urinalysis found non-expired drug-use evidence. This is a parole violation.", null);
            builder.AddNode("employment_verified", "Your employment condition is currently satisfied.", null);
            builder.AddNode("employment_warning", "Your employment condition is not satisfied. This warning is being recorded.", null);
            builder.AddNode("fee_received", "Your supervision fee has been paid and recorded.", null);
            builder.AddNode("fee_failed", "You do not have enough cash for the supervision fee. The missed payment is being recorded.", null);
            builder.AddNode("checkin_complete", "Your scheduled check-in is complete. Continue following every condition.", null);
            builder.AddNode("checkin_too_early", $"You are too early to report. {scheduleText}", choices =>
                choices.Add("conversation_end", "Understood."));
            builder.AddNode("checkin_missed", "You missed the assigned reporting window. That failure has been recorded.", choices =>
                choices.Add("conversation_end", "Understood."));
            builder.AddNode("checkin_no_schedule", "You do not have a scheduled check-in available right now.", choices =>
                choices.Add("conversation_end", "Understood."));
            builder.AddNode("checkin_busy", "I am handling another parole matter. Return in a moment.", choices =>
                choices.Add("conversation_end", "Understood."));
            builder.AddNode("checkin_step_closer", "Step closer before requesting a check-in.", choices =>
                choices.Add("conversation_end", "Understood."));
            builder.AddNode("conditions_overview", conditionsText, choices =>
            {
                choices.Add("conditions_checkins", "Clarify the check-in requirement.", "conditions_checkin_detail");
                choices.Add("conditions_violations", "What happens if I violate a condition?", "conditions_violation_detail");
                choices.Add("conversation_end", "I understand my conditions.");
            });
            builder.AddNode("conditions_checkin_detail", "Report during the assigned window and complete the officer's review. Reporting early, late, or leaving midway does not count.", choices =>
                choices.Add("conditions_back", "Review the other conditions.", "conditions_overview"));
            builder.AddNode("conditions_violation_detail", "Violations lower compliance, can increase your LSI supervision level, and may lead to a warrant or revocation.", choices =>
                choices.Add("conditions_back", "Review the other conditions.", "conditions_overview"));
            builder.AddNode("schedule_overview", scheduleText, choices =>
                choices.Add("conversation_end", "Understood."));

            builder.AddNode("initial_intro", "I'm your supervising parole officer. Before we begin, tell me how you intend to handle supervision.", choices =>
            {
                choices.Add("initial_cooperative", "I'll follow the conditions and report when ordered.", "initial_cooperative_response");
                choices.Add("initial_uncertain", "I'm worried I may need help staying on track.", "initial_uncertain_response");
                choices.Add("initial_defiant", "I don't need an officer watching me.", "initial_defiant_response");
            });
            builder.AddNode("initial_cooperative_response", "That is the right approach. Your choices from here will determine how closely you are supervised.", choices =>
                choices.Add("initial_begin_escort", "Show me where I report."));
            builder.AddNode("initial_uncertain_response", "Ask questions and report problems honestly. Hiding them will make supervision more restrictive.", choices =>
                choices.Add("initial_begin_escort", "Show me where I report."));
            builder.AddNode("initial_defiant_response", "That response raises my concern. Noncompliance will increase supervision and can return you to custody.", choices =>
                choices.Add("initial_begin_escort", "Fine. Show me where I report."));
            builder.AddNode("initial_location", $"This is your reporting location. {scheduleText}", choices =>
            {
                choices.Add("initial_location_acknowledge", "I understand and will report here on time.", "initial_location_closeout");
                choices.Add("initial_location_questions", "I need clarification first.", "initial_location_questions_node");
                choices.Add("initial_location_dismissive", "I heard you. Can I go now?", "initial_location_closeout");
            });
            builder.AddNode("initial_location_questions_node", "What needs clarification?", choices =>
            {
                choices.Add("initial_question_schedule", "How does the reporting window work?", "initial_schedule_detail");
                choices.Add("initial_question_conditions", "Which conditions are currently active?", "initial_conditions_detail");
                choices.Add("initial_question_violation", "What happens if I miss a check-in?", "initial_violation_detail");
            });
            builder.AddNode("initial_schedule_detail", $"You must begin and finish the check-in during the assigned window. {scheduleText}", choices =>
                choices.Add("initial_location_complete_neutral", "I understand.", "initial_location_closeout"));
            builder.AddNode("initial_conditions_detail", conditionsText, choices =>
                choices.Add("initial_location_complete_neutral", "I understand.", "initial_location_closeout"));
            builder.AddNode("initial_violation_detail", "A missed check-in lowers compliance and can become a formal violation. Repeated failures can produce a warrant or revocation.", choices =>
                choices.Add("initial_location_complete_neutral", "I understand.", "initial_location_closeout"));
            builder.AddNode("initial_location_closeout", "That completes your intake. Watch your phone for each day's reporting window. You may go.", choices =>
                choices.Add("initial_location_finish", "Understood."));

            builder.AddNode("violation_entry", violationText, choices =>
            {
                choices.Add("violation_accept", "I understand. I take responsibility.");
                choices.Add("violation_explain", "I want my explanation noted.");
                choices.Add("violation_dismiss", "This is being blown out of proportion.");
            });
            builder.SetAllowExit(false);

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

        /// <summary>Builds the schedule sentence used by both normal and intake conversations.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private string BuildScheduleDialogueText(Player parolee)
        {
            if (parolee != null)
            {
                var manager = Core.ResolveParoleManager();
                string windowText = string.Empty;
                manager?.GetDailyCheckInStatus(parolee, out windowText, applyConsequences: false);
                if (!string.IsNullOrWhiteSpace(windowText))
                {
                    return $"Your current reporting window is {windowText}.";
                }
            }

            return "Your reporting window will be issued through the parole schedule. Return here during that window.";
        }

        /// <summary>Builds a compact handler-safe condition summary for the active parolee.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private string BuildConditionsDialogueText(Player parolee)
        {
            var record = parolee == null
                ? null
                : Core.ResolveRapSheetManager().GetRapSheet(parolee)?.CurrentParoleRecord;
            if (record == null)
            {
                return "I cannot locate an active parole record to review.";
            }

            var conditionIds = record.GetActiveConditionIds();
            string activeConditions = conditionIds == null || conditionIds.Count == 0
                ? "standard reporting and law-abiding behavior"
                : string.Join(", ", conditionIds);
            return $"Your active conditions are: {activeConditions}. Current compliance is {record.GetComplianceScore():F0} out of 100.";
        }

        /// <summary>Moves the active handler conversation to a named node.</summary>
        private bool JumpToDialogueNode(string nodeLabel)
        {
            return dialogueWrapper != null &&
                   dialogueWrapper.JumpTo(CHECK_IN_DIALOGUE_CONTAINER_NAME, nodeLabel, enableBehaviour: false);
        }

        /// <summary>Ends the current conversation and restores the supervisor menu override.</summary>
        private void EndCurrentConversation()
        {
            dialogueWrapper?.End();
            dialogueSubject = null;
            pendingViolationType = string.Empty;
            RegisterCheckInDialogueContainer();
            EnsureContainerOnInteract();
        }

        /// <summary>Applies a recurring answer outcome to persistent compliance and rapport.</summary>
        private void ApplyRecurringDialogueOutcome(float complianceDelta, float rapportDelta)
        {
            var record = currentCheckInParolee == null
                ? null
                : Core.ResolveRapSheetManager().GetRapSheet(currentCheckInParolee)?.CurrentParoleRecord;
            if (record == null)
            {
                return;
            }

            record.AdjustComplianceScore(complianceDelta);
            record.AdjustRapport(rapportDelta);
            Core.ResolveRapSheetManager().MarkRapSheetChanged(currentCheckInParolee);
        }

        /// <summary>Scores the initial interview once and refreshes the persisted LSI tier.</summary>
        private void ApplyInitialInterviewOutcome(int riskModifier, float complianceDelta, float rapportDelta)
        {
            var rapSheet = dialogueSubject == null
                ? null
                : Core.ResolveRapSheetManager().GetRapSheet(dialogueSubject);
            if (rapSheet?.CurrentParoleRecord == null)
            {
                return;
            }

            if (rapSheet.CurrentParoleRecord.TryApplyInitialInterviewOutcome(riskModifier, complianceDelta, rapportDelta))
            {
                rapSheet.UpdateLSILevel();
                Core.ResolveRapSheetManager().MarkRapSheetChanged(dialogueSubject);
            }
        }

        /// <summary>Completes the pre-escort initial conversation after the player explicitly proceeds.</summary>
        private void CompleteInitialIntroduction()
        {
            initialIntroductionComplete = true;
            dialogueWrapper?.End();
        }

        /// <summary>Completes the reporting-location conversation and applies its answer outcome.</summary>
        private void ApplyInitialLocationOutcome(float complianceDelta, float rapportDelta)
        {
            var record = dialogueSubject == null
                ? null
                : Core.ResolveRapSheetManager().GetRapSheet(dialogueSubject)?.CurrentParoleRecord;
            if (record != null)
            {
                record.AdjustComplianceScore(complianceDelta);
                record.AdjustRapport(rapportDelta);
                record.RecordInteraction();
                Core.ResolveRapSheetManager().MarkRapSheetChanged(dialogueSubject);
            }

        }

        /// <summary>Ends intake only after the player acknowledges the officer's closing instruction.</summary>
        private void CompleteInitialLocationConversation()
        {
            initialLocationConversationComplete = true;
            dialogueWrapper?.End();
        }

        /// <summary>Applies the player's response to a recorded violation discussion.</summary>
        private void CompleteViolationConversation(float complianceDelta, float rapportDelta)
        {
            var record = dialogueSubject == null
                ? null
                : Core.ResolveRapSheetManager().GetRapSheet(dialogueSubject)?.CurrentParoleRecord;
            if (record != null)
            {
                record.AdjustComplianceScore(complianceDelta);
                record.AdjustRapport(rapportDelta);
                record.RecordInteraction();
                Core.ResolveRapSheetManager().MarkRapSheetChanged(dialogueSubject);
            }

            EndCurrentConversation();
        }

        /// <summary>Starts processing only after the player completes the recurring interview branch.</summary>
        private void StartAcceptedCheckInProcessing()
        {
            if (!isProcessingCheckIn || currentCheckInParolee == null || checkInProcessingStarted)
            {
                return;
            }

            checkInProcessingStarted = true;
            checkInCoroutine = MelonCoroutines.Start(ProcessCheckIn(currentCheckInParolee));
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
                JumpToDialogueNode("checkin_busy");
                return;
            }

            var parolee = FindNearbyParoleeForInteraction();
            if (parolee == null)
            {
                JumpToDialogueNode("checkin_step_closer");
                return;
            }

            InitiateCheckIn(parolee);
        }

        /// <summary>Resolves the speaking parolee before showing their dynamic condition summary.</summary>
        private void OnConditionsDialogueChoiceSelected()
        {
            var parolee = FindNearbyParoleeForInteraction(applySuccessfulCheckInCooldown: false);
            if (parolee == null)
            {
                MelonCoroutines.Start(JumpToDialogueNodeNextFrame("checkin_step_closer"));
                return;
            }

            dialogueSubject = parolee;
            RegisterCheckInDialogueContainer();
            MelonCoroutines.Start(JumpToDialogueNodeNextFrame("conditions_overview"));
        }

        /// <summary>Resolves the speaking parolee before showing their current schedule.</summary>
        private void OnScheduleDialogueChoiceSelected()
        {
            var parolee = FindNearbyParoleeForInteraction(applySuccessfulCheckInCooldown: false);
            if (parolee == null)
            {
                MelonCoroutines.Start(JumpToDialogueNodeNextFrame("checkin_step_closer"));
                return;
            }

            dialogueSubject = parolee;
            RegisterCheckInDialogueContainer();
            MelonCoroutines.Start(JumpToDialogueNodeNextFrame("schedule_overview"));
        }

        /// <summary>
        /// Finds the nearest on-parole player within the interaction radius whose
        /// real-time attempt cooldown has expired.  The selected player becomes
        /// the exact session identity passed to <see cref="InitiateCheckIn"/>.
        /// </summary>
        private Player FindNearbyParoleeForInteraction(bool applySuccessfulCheckInCooldown = true)
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

                if (applySuccessfulCheckInCooldown && lastCheckInTimes.TryGetValue(player, out float lastAttemptTime))
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
                MelonCoroutines.Start(JumpToDialogueNodeNextFrame("checkin_busy"));
                return;
            }

            if (paroleOfficer == null || paroleOfficer.GetRole() != ParoleOfficerBehavior.ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn("ParoleCheckInSystem: Officer is not a supervising officer");
                return;
            }

            dialogueSubject = parolee;
            RegisterCheckInDialogueContainer();

            var interactionCoordinator = Core.ResolveDynamicParoleOfficerManager();
            if (interactionCoordinator != null &&
                !interactionCoordinator.TryReserveCheckIn(parolee, paroleOfficer))
            {
                ModLogger.Debug($"ParoleCheckInSystem: Check-in already active or blocked for {parolee.name}");
                MelonCoroutines.Start(JumpToDialogueNodeNextFrame("checkin_busy"));
                return;
            }

            var paroleManager = Core.ResolveParoleManager();
            if (paroleManager != null)
            {
                if (!paroleManager.TryBeginCheckInSession(parolee, out var status, out string windowText))
                {
                    interactionCoordinator?.CancelCheckIn(parolee, paroleOfficer);
                    ShowCheckInRejectedDialogue(status, windowText);
                    return;
                }
            }

            currentCheckInParolee = parolee;
            isProcessingCheckIn = true;
            interactionCoordinator?.StartCheckIn(parolee, paroleOfficer);

            ModLogger.Info($"ParoleCheckInSystem: Accepted check-in interview for {parolee.name}");
        }

        /// <summary>
        /// Presents the scheduling rejection state and restores the check-in
        /// container after the rejection interaction closes.
        /// </summary>
        private void ShowCheckInRejectedDialogue(ParoleManager.CheckInStatus status, string windowText)
        {
            string node = status switch
            {
                ParoleManager.CheckInStatus.TooEarly => "checkin_too_early",
                ParoleManager.CheckInStatus.MissedWindow => "checkin_missed",
                ParoleManager.CheckInStatus.NoScheduledWindow => "checkin_no_schedule",
                _ => "checkin_no_schedule"
            };
            RegisterCheckInDialogueContainer();
            MelonCoroutines.Start(JumpToDialogueNodeNextFrame(node));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator JumpToDialogueNodeNextFrame(string nodeLabel)
        {
            yield return null;
            JumpToDialogueNode(nodeLabel);
        }

        /// <summary>
        /// Runs the ordered check-in phases: rapport greeting, compliance review,
        /// routine pocket search, optional conditions, recording, completion, and
        /// coordinator cleanup.  A pocket-search arrest exits through the abort
        /// path and must not continue to normal completion.
        /// </summary>
        /// <param name="parolee">The player captured by the active check-in session.</param>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessCheckIn(Player parolee)
        {
            checkInArrestInitiated = false;
            try
            {
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(parolee);
                var paroleRecord = rapSheet?.CurrentParoleRecord;
                if (paroleRecord == null)
                {
                    JumpToDialogueNode("checkin_no_schedule");
                    yield return new WaitForSeconds(2f);
                    yield break;
                }

                var baseNpc = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
                baseNpc?.LookAt(parolee.transform.position);

                JumpToDialogueNode("checkin_processing");
                yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

                float complianceScore = paroleRecord.GetComplianceScore();
                int violationCount = paroleRecord.GetViolationCount();
                JumpToDialogueNode(complianceScore >= 80f && violationCount == 0
                    ? "checkin_compliant"
                    : "checkin_warning");
                yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

                yield return ProcessRoutinePocketSearch(parolee);
                if (checkInArrestInitiated)
                {
                    ModLogger.Info($"ParoleCheckInSystem: Check-in aborted because the pocket search initiated custody for {parolee.name}");
                    yield break;
                }

                yield return ProcessDrugTest(parolee, rapSheet, paroleRecord);
                yield return ProcessEmploymentCheck(parolee, rapSheet, paroleRecord);
                yield return ProcessFeePayment(parolee, rapSheet, paroleRecord);

                if (!RecordCheckIn(parolee))
                {
                    yield return new WaitForSeconds(2f);
                    yield break;
                }

                JumpToDialogueNode("checkin_complete");
                yield return new WaitForSeconds(2f);
            }
            finally
            {
                checkInCoroutine = null;
                CleanupCheckIn(parolee, restoreOfficer: !checkInArrestInitiated);
            }
        }

        /// <summary>
        /// Performs the scheduled parole pocket search before recording completion.
        /// Contraband is handed to the shared parole-search classifier; an arrest
        /// sets the local abort flag and leaves custody cleanup to the arrest flow.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessRoutinePocketSearch(Player parolee)
        {
            if (parolee == null || paroleOfficer == null)
            {
                yield break;
            }

            JumpToDialogueNode("pocket_search");
            paroleOfficer.UpdateSearchNotification("Routine pocket search - remain still");
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
            JumpToDialogueNode("pocket_search_clear");
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Aborts check-in after a compliance-search arrest, clears local state,
        /// releases manager/coordinator ownership, and restores the interaction
        /// container without recording a successful visit.
        /// </summary>
        private void CleanupCheckIn(Player parolee, bool restoreOfficer)
        {
            isProcessingCheckIn = false;
            checkInProcessingStarted = false;
            currentCheckInParolee = null;
            if (parolee != null)
            {
                EndCheckInSession(parolee);
            }

            try { dialogueWrapper?.End(); } catch { }
            if (restoreOfficer)
            {
                stationaryBehavior?.ReturnToPosition();
            }

            dialogueSubject = null;
            pendingViolationType = string.Empty;
            RegisterCheckInDialogueContainer();
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

            // Manager teardown can precede injected component destruction during a
            // Main-scene exit. Release each owner independently so one unavailable
            // manager cannot prevent the other cleanup leg or escape the trampoline.
            try
            {
                Core.ResolveParoleManager()?.EndCheckInSession(parolee);
            }
            catch (InvalidOperationException)
            {
                ModLogger.Debug("ParoleCheckInSystem: ParoleManager already unavailable during check-in cleanup");
            }

            try
            {
                Core.ResolveDynamicParoleOfficerManager()?.CompleteSupervisingOfficerCheckIn(parolee, paroleOfficer);
            }
            catch (InvalidOperationException)
            {
                ModLogger.Debug("ParoleCheckInSystem: Dynamic parole manager already unavailable during check-in cleanup");
            }
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
        private bool RecordCheckIn(Player parolee)
        {
            var paroleManager = Core.ResolveParoleManager();
            if (paroleManager != null && !paroleManager.NotifyDailyCheckInCompleted(parolee))
            {
                ModLogger.Info($"ParoleCheckInSystem: Check-in denied for {parolee.name} due to timing/scheduling rules");
                var status = paroleManager.GetDailyCheckInStatus(parolee, out string windowText, applyConsequences: false);
                ShowCheckInRejectedDialogue(status, windowText);
                return false;
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
            return true;
        }

        /// <summary>Starts the choice-driven initial supervising-officer interview.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool BeginInitialIntroduction(Player parolee)
        {
            if (parolee == null)
            {
                return false;
            }

            SetupInteractionTrigger();
            if (dialogueWrapper == null || dialogueHandler == null)
            {
                return false;
            }

            dialogueSubject = parolee;
            initialIntroductionComplete = false;
            RegisterCheckInDialogueContainer();
            return JumpToDialogueNode("initial_intro");
        }

        /// <summary>Returns whether the exact parolee completed the pre-escort interview.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool HasCompletedInitialIntroduction(Player parolee)
        {
            return parolee != null && dialogueSubject == parolee && initialIntroductionComplete;
        }

        /// <summary>Starts the handler-owned reporting-location and schedule explanation.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool BeginInitialLocationConversation(Player parolee)
        {
            if (parolee == null)
            {
                return false;
            }

            SetupInteractionTrigger();
            dialogueSubject = parolee;
            initialLocationConversationComplete = false;
            RegisterCheckInDialogueContainer();
            return JumpToDialogueNode("initial_location");
        }

        /// <summary>Returns whether the exact parolee completed the location explanation.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool HasCompletedInitialLocationConversation(Player parolee)
        {
            return parolee != null && dialogueSubject == parolee && initialLocationConversationComplete;
        }

        /// <summary>Releases initial-intake dialogue ownership and restores the normal supervisor menu.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void EndInitialIntakeDialogue(Player parolee)
        {
            if (parolee == null || dialogueSubject != parolee)
            {
                return;
            }

            try { dialogueWrapper?.End(); } catch { }
            dialogueSubject = null;
            initialIntroductionComplete = false;
            initialLocationConversationComplete = false;
            RegisterCheckInDialogueContainer();
            EnsureContainerOnInteract();
        }

        /// <summary>Starts a handler-owned discussion for a violation already recorded by the parole system.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool BeginViolationDialogue(Player parolee, string violationType)
        {
            if (parolee == null || isProcessingCheckIn)
            {
                return false;
            }

            SetupInteractionTrigger();
            dialogueSubject = parolee;
            pendingViolationType = violationType ?? string.Empty;
            RegisterCheckInDialogueContainer();
            return JumpToDialogueNode("violation_entry");
        }

        /// <summary>Starts the conditions branch directly for legacy callers.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool BeginConditionsReviewDialogue(Player parolee)
        {
            if (parolee == null || isProcessingCheckIn)
            {
                return false;
            }

            SetupInteractionTrigger();
            dialogueSubject = parolee;
            RegisterCheckInDialogueContainer();
            return JumpToDialogueNode("conditions_overview");
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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

            JumpToDialogueNode("drug_test_announce");
            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);

            // UA evaluates saved, timestamped consumption evidence. Possessing an
            // item is handled by the separate pocket search and is not a positive UA.
            var activeDrugUse = DrugUseHistoryService.GetActiveRecords(rapSheet);
            bool hasDrugs = activeDrugUse != null && activeDrugUse.Count > 0;

            if (hasDrugs)
            {
                JumpToDialogueNode("drug_test_fail");

                paroleRecord.AdjustComplianceScore(-15f);
                paroleRecord.AdjustRapport(-15f);

                var detectedTypes = new List<string>();
                for (int i = 0; i < activeDrugUse.Count; i++)
                {
                    detectedTypes.Add(activeDrugUse[i].DrugType.ToString());
                }
                var violation = new ViolationRecord(ViolationType.Other,
                    $"Failed random UA during check-in - active drug-use evidence: {string.Join(", ", detectedTypes)}", 2.5f);
                rapSheet.AddParoleViolation(violation);
                Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);

                ModLogger.Info($"[DRUG TEST] {parolee.name} FAILED drug test");
            }
            else
            {
                JumpToDialogueNode("drug_test_pass");

                paroleRecord.AdjustRapport(1f); // Small rapport boost for clean test
                Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);

                ModLogger.Info($"[DRUG TEST] {parolee.name} passed drug test");
            }

            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);
        }

        /// <summary>
        /// Applies the optional employment condition, recording warnings and
        /// compliance consequences through the parole record before returning to
        /// the main check-in sequence.
        /// </summary>
        /// <param name="parolee">Player whose employment is evaluated.</param>
        /// <param name="rapSheet">Rap sheet receiving persisted changes.</param>
        /// <param name="paroleRecord">Active parole record containing condition state.</param>
#if !MONO
        [HideFromIl2Cpp]
#endif
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
                JumpToDialogueNode("employment_verified");
                yield return new WaitForSeconds(1.5f);
                yield break;
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

                JumpToDialogueNode("employment_warning");

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

                JumpToDialogueNode("employment_warning");

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
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessFeePayment(Player parolee, RapSheet rapSheet, ParoleRecord paroleRecord)
        {
            float feesOwed = paroleRecord.GetTotalFeesOwed();
            if (feesOwed <= 0f) yield break;

            // Attempt automatic payment
            bool paid = Core.ResolveParoleFeeSystem().AttemptPayment(parolee, rapSheet);

            if (paid)
            {
                JumpToDialogueNode("fee_received");
                ModLogger.Info($"[FEES] {parolee.name} paid supervision fees at check-in");
            }
            else
            {
                JumpToDialogueNode("fee_failed");
                ModLogger.Info($"[FEES] {parolee.name} could not pay supervision fees (${feesOwed:F0} owed)");
            }

            yield return new WaitForSeconds(1f);
        }

        #endregion
    }
}

