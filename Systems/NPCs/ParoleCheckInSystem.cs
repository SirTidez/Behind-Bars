using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppInterop.Runtime.Attributes;
#else
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

        #endregion

        #region Component References

        private ParoleOfficerBehavior paroleOfficer;
        private JailNPCDialogueController dialogueController;
        private StationaryBehavior stationaryBehavior;

        #endregion

        #region State

        private Player currentCheckInParolee;
        private bool isProcessingCheckIn = false;
        private float lastCheckInTime = 0f;
        private Dictionary<Player, float> lastCheckInTimes = new Dictionary<Player, float>();

        #endregion

        #region Initialization

        private void Awake()
        {
            paroleOfficer = GetComponent<ParoleOfficerBehavior>();
            dialogueController = GetComponent<JailNPCDialogueController>();
            stationaryBehavior = GetComponent<StationaryBehavior>();
        }

        private void Start()
        {
            // Start checking for nearby parolees
            MelonCoroutines.Start(CheckForNearbyParolees());
        }

        #endregion

        #region Check-In Detection

        private IEnumerator CheckForNearbyParolees()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f); // Check every 2 seconds

                if (isProcessingCheckIn) continue;
                if (paroleOfficer == null || paroleOfficer.GetRole() != ParoleOfficerBehavior.ParoleOfficerRole.SupervisingOfficer) continue;

                // Check for players on parole nearby
                var players = GameObject.FindObjectsOfType<Player>();
                if (players == null || players.Length == 0) continue;

                foreach (var player in players)
                {
                    if (player == null) continue;

                    // Check if player is on parole
                    var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                    if (rapSheet == null || rapSheet.CurrentParoleRecord == null) continue;
                    if (!rapSheet.CurrentParoleRecord.IsOnParole()) continue;

                    // Check distance
                    float distance = Vector3.Distance(transform.position, player.transform.position);
                    if (distance <= CHECK_IN_PROXIMITY)
                    {
                        // Check cooldown
                        if (lastCheckInTimes.ContainsKey(player))
                        {
                            float timeSinceLastCheckIn = Time.time - lastCheckInTimes[player];
                            if (timeSinceLastCheckIn < CHECK_IN_COOLDOWN) continue;
                        }

                        // Initiate check-in
                        InitiateCheckIn(player);
                        break; // Only one check-in at a time
                    }
                }
            }
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

            currentCheckInParolee = parolee;
            isProcessingCheckIn = true;

            ModLogger.Info($"ParoleCheckInSystem: Initiating check-in for {parolee.name}");
            MelonCoroutines.Start(ProcessCheckIn(parolee));
        }

        /// <summary>
        /// Process the check-in
        /// </summary>
        private IEnumerator ProcessCheckIn(Player parolee)
        {
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

            // Reset state
            isProcessingCheckIn = false;
            currentCheckInParolee = null;

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
        }

        /// <summary>
        /// Record check-in in parole record
        /// </summary>
        private void RecordCheckIn(Player parolee)
        {
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

