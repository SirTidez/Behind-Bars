using System;
using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Dialogue;
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
    /// Handles check-in interactions between parolees and supervising officer
    /// Manages check-in process, compliance review, and feedback
    /// </summary>
    public class ParoleCheckInSystem : MonoBehaviour
    {
#if !MONO
        public ParoleCheckInSystem(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Configuration

        private const float CHECK_IN_PROXIMITY = 5f; // Distance to trigger check-in
        private const float CHECK_IN_COOLDOWN = 30f; // Cooldown between check-ins (real seconds)
        private const float CHECK_IN_PROCESSING_TIME = 3f; // Time to process check-in
        private const string CHECK_IN_DIALOGUE_CONTAINER_NAME = "ParoleOfficer_CheckIn";
        private const string CHECK_IN_CHOICE_LABEL = "checkin_request";

        #endregion

        #region Component References

        private ParoleOfficerBehavior paroleOfficer;
        private JailNPCDialogueController dialogueController;
        private StationaryBehavior stationaryBehavior;
        private NPCDialogueWrapper dialogueWrapper;
        private DialogueHandler dialogueHandler;
        private DialogueController baseDialogueController;
        private bool interactionHooked;

        #endregion

        #region State

        private Player currentCheckInParolee;
        private bool isProcessingCheckIn = false;
        private Dictionary<Player, float> lastCheckInTimes = new Dictionary<Player, float>();

        #endregion

        #region Initialization

        private void Awake()
        {
            paroleOfficer = BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(gameObject);
            dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
            stationaryBehavior = GetComponent<StationaryBehavior>();
        }

        private void Start()
        {
            MelonCoroutines.Start(WaitForInteractionTrigger());
        }

        private void OnDestroy() { }

        #endregion

        #region Check-In Detection

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

        private void RegisterCheckInDialogueContainer()
        {
            if (dialogueHandler == null)
            {
                return;
            }

            var builder = new DialogueContainerBuilder();
            builder.AddNode("ENTRY", "Do you need to report for your parole check-in?", choices =>
            {
                choices.Add(CHECK_IN_CHOICE_LABEL, "Yes, I am here to check in.", "end");
                choices.Add("checkin_later", "Not right now.", "end");
            });
            builder.AddNode("end", string.Empty, null);
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

        private void EnsureContainerOnInteract()
        {
            if (dialogueWrapper == null)
            {
                return;
            }

            DisableGreetingOverrides();
            dialogueWrapper.UseContainerOnInteract(CHECK_IN_DIALOGUE_CONTAINER_NAME);
        }

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
                var baseNpcBusy = GetComponent<BaseJailNPC>();
                baseNpcBusy?.TrySendNPCMessage("I am processing intake right now. Come back in a moment.", 3f);
                EnsureContainerOnInteract();
                return;
            }

            var parolee = FindNearbyParoleeForInteraction();
            if (parolee == null)
            {
                var baseNpcNoPlayer = GetComponent<BaseJailNPC>();
                baseNpcNoPlayer?.TrySendNPCMessage("Step closer if you need to check in.", 3f);
                EnsureContainerOnInteract();
                return;
            }

            try
            {
                dialogueWrapper?.End();
            }
            catch { }

            InitiateCheckIn(parolee);
        }

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

                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
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
        /// Initiate check-in process for a parolee
        /// </summary>
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

            var paroleSystem = Core.Instance?.ParoleSystem;
            if (paroleSystem != null)
            {
                if (!paroleSystem.TryBeginCheckInSession(parolee, out var status, out string windowText))
                {
                    ShowCheckInRejectedDialogue(status, windowText);
                    lastCheckInTimes[parolee] = Time.time;
                    EnsureContainerOnInteract();
                    return;
                }
            }

            currentCheckInParolee = parolee;
            isProcessingCheckIn = true;

            ModLogger.Info($"ParoleCheckInSystem: Initiating check-in for {parolee.name}");
            MelonCoroutines.Start(ProcessCheckIn(parolee));
        }

        private void ShowCheckInRejectedDialogue(ParoleSystem.DailyCheckInStatus status, string windowText)
        {
            if (dialogueController == null)
            {
                dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
            }

            if (dialogueController == null)
            {
                var baseNpcFallback = GetComponent<BaseJailNPC>();
                baseNpcFallback?.TrySendNPCMessage("You are not eligible to check in right now.", 3f);
                EnsureContainerOnInteract();
                return;
            }

            string state = status switch
            {
                ParoleSystem.DailyCheckInStatus.TooEarly => "CheckInTooEarly",
                ParoleSystem.DailyCheckInStatus.MissedWindow => "CheckInMissedWindow",
                ParoleSystem.DailyCheckInStatus.NoScheduledWindow => "CheckInNoSchedule",
                _ => "CheckInWarning"
            };

            dialogueController.UpdateGreetingForState(state);
            dialogueController.SendContextualMessage("interaction");

            if (status == ParoleSystem.DailyCheckInStatus.TooEarly && !string.IsNullOrWhiteSpace(windowText))
            {
                var baseNpc = GetComponent<BaseJailNPC>();
                baseNpc?.TrySendNPCMessage($"Your check-in window is between {windowText}.", 4f);
            }

            EnsureContainerOnInteract();
        }

        /// <summary>
        /// Process the check-in
        /// </summary>
        private IEnumerator ProcessCheckIn(Player parolee)
        {
            if (dialogueController == null)
            {
                dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
            }

            // Update dialogue to check-in greeting
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("CheckInGreeting");
                dialogueController.SendContextualMessage("greeting");
            }

            // Face the parolee
            if (parolee != null)
            {
                var baseNPC = GetComponent<BaseJailNPC>();
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
            var rapSheet = RapSheetManager.Instance.GetRapSheet(parolee);
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
                Core.Instance?.ParoleSystem?.EndCheckInSession(completedParolee);
            }

            // Return to idle dialogue
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("Idle");
            }

            // Return to entrance position if stationary
            if (stationaryBehavior != null)
            {
                stationaryBehavior.ReturnToPosition();
            }

            EnsureContainerOnInteract();
        }

        /// <summary>
        /// Record check-in in parole record
        /// </summary>
        private void RecordCheckIn(Player parolee)
        {
            var paroleSystem = Core.Instance?.ParoleSystem;
            if (paroleSystem != null && !paroleSystem.NotifyDailyCheckInCompleted(parolee))
            {
                ModLogger.Info($"ParoleCheckInSystem: Check-in denied for {parolee.name} due to timing/scheduling rules");
                var status = paroleSystem.GetDailyCheckInStatus(parolee, out string windowText, applyConsequences: false);
                ShowCheckInRejectedDialogue(status, windowText);
                lastCheckInTimes[parolee] = Time.time;
                return;
            }

            var rapSheet = RapSheetManager.Instance.GetRapSheet(parolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                rapSheet.CurrentParoleRecord.RecordCheckIn();
                rapSheet.CurrentParoleRecord.RecordInteraction();
                RapSheetManager.Instance.MarkRapSheetChanged(parolee);
                ModLogger.Info($"ParoleCheckInSystem: Recorded check-in for {parolee.name}");
            }

            // Update last check-in time
            lastCheckInTimes[parolee] = Time.time;
        }

        /// <summary>
        /// Check if currently processing a check-in
        /// </summary>
        public bool IsProcessingCheckIn()
        {
            return isProcessingCheckIn;
        }

        /// <summary>
        /// Get the current parolee being checked in
        /// </summary>
        public Player GetCurrentCheckInParolee()
        {
            return currentCheckInParolee;
        }

        #endregion
    }
}

