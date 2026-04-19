using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
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

        private void OnDestroy()
        {
            if (currentCheckInParolee != null)
            {
                ReleaseCheckInOwnership(currentCheckInParolee);
            }
        }

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

        private void ShowCheckInRejectedDialogue(ParoleManager.CheckInStatus status, string windowText)
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
                ParoleManager.CheckInStatus.TooEarly => "CheckInTooEarly",
                ParoleManager.CheckInStatus.MissedWindow => "CheckInMissedWindow",
                ParoleManager.CheckInStatus.NoScheduledWindow => "CheckInNoSchedule",
                _ => "CheckInWarning"
            };

            dialogueController.UpdateGreetingForState(state);
            dialogueController.SendContextualMessage("interaction");

            if (status == ParoleManager.CheckInStatus.TooEarly && !string.IsNullOrWhiteSpace(windowText))
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

            // Return to entrance position if stationary
            if (stationaryBehavior != null)
            {
                stationaryBehavior.ReturnToPosition();
            }

            EnsureContainerOnInteract();
        }

        private void EndCheckInSession(Player parolee)
        {
            if (parolee == null)
            {
                return;
            }

            Core.ResolveParoleManager()?.EndCheckInSession(parolee);
            Core.ResolveDynamicParoleOfficerManager()?.CompleteSupervisingOfficerCheckIn(parolee, paroleOfficer);
        }

        private void ReleaseCheckInOwnership(Player parolee)
        {
            if (parolee == null)
            {
                return;
            }

            EndCheckInSession(parolee);
        }

        /// <summary>
        /// Record check-in in parole record
        /// </summary>
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

        /// <summary>
        /// Process drug test during check-in if the drug test condition is active
        /// </summary>
        private IEnumerator ProcessDrugTest(Player parolee, RapSheet rapSheet, ParoleRecord paroleRecord)
        {
            if (!Core.ResolveParoleConditionManager().IsConditionActive("drug_test"))
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
        /// Check if player has drug items in inventory (reuses ParoleSystem.IsDrugItem logic)
        /// </summary>
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
        /// Process employment verification during check-in
        /// </summary>
        private IEnumerator ProcessEmploymentCheck(Player parolee, RapSheet rapSheet, ParoleRecord paroleRecord)
        {
            if (!Core.ResolveParoleConditionManager().IsConditionActive("employment"))
                yield break;

            bool isEmployed = EmploymentCondition.IsPlayerEmployed();

            if (isEmployed)
            {
                // Employed - minor positive feedback
                ModLogger.Debug($"[EMPLOYMENT] {parolee.name} is employed (owns property/business)");
                yield break; // No special dialogue needed for positive result
            }

            // Not employed - graduated consequences
            // Track unemployment warnings using a simple counter approach via missed check-ins
            // We'll use the violation count for employment-type violations as the counter
            int employmentWarnings = 0;
            foreach (var v in paroleRecord.GetViolations())
            {
                if (v.Details != null && v.Details.Contains("employment"))
                    employmentWarnings++;
            }

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
                    $"Employment reminder: You need to maintain employment or income. Warning {employmentWarnings + 1}/{EmploymentCondition.WARNINGS_BEFORE_VIOLATION}.");

                ModLogger.Info($"[EMPLOYMENT] Warning {employmentWarnings + 1} for {parolee.name}");
            }
            else
            {
                // Formal violation after enough warnings
                paroleRecord.AdjustComplianceScore(-5f);
                paroleRecord.AdjustRapport(-5f);

                var violation = new ViolationRecord(ViolationType.Other,
                    $"Failed to maintain employment ({employmentWarnings + 1} consecutive failures)", 1.5f);
                rapSheet.AddParoleViolation(violation);

                Core.ResolveParoleManager()?.SendSupervisingOfficerText(parolee,
                    "Employment condition violated. Formal violation recorded.");

                ModLogger.Info($"[EMPLOYMENT] Formal violation for {parolee.name} after {employmentWarnings + 1} failures");
            }

            Core.ResolveRapSheetManager().MarkRapSheetChanged(parolee);
            yield return new WaitForSeconds(CHECK_IN_PROCESSING_TIME);
        }

        /// <summary>
        /// Process fee payment during check-in
        /// </summary>
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

