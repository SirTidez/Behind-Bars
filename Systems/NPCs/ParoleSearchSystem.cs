using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.CrimeTracking;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Handles random contraband searches by parole officers
    /// Integrates with patrol system and LSI risk assessment
    /// </summary>
    public class ParoleSearchSystem
    {
        private static ParoleSearchSystem _instance;
        public static ParoleSearchSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new ParoleSearchSystem();
                }
                return _instance;
            }
        }

        // Search cooldowns per player to prevent spam
        private Dictionary<Player, float> lastSearchTime = new Dictionary<Player, float>();
        private const float SEARCH_COOLDOWN = 120f; // 2 minutes minimum between searches

        // Detection range for officers to notice parolees
        private const float DETECTION_RANGE = 15f;

        /// <summary>
        /// Check if a parole officer should initiate a random search
        /// Called from patrol logic when officer is near a player
        /// </summary>
        public bool ShouldInitiateSearch(ParoleOfficerBehavior officer, Player player)
        {
            // Pre-checks
            if (officer == null || player == null) return false;
            if (officer.GetCurrentActivity() == ParoleOfficerBehavior.ParoleOfficerActivity.ProcessingIntake) return false;
            if (officer.GetCurrentActivity() == ParoleOfficerBehavior.ParoleOfficerActivity.EscortingParolee) return false;
            if (officer.GetCurrentActivity() == ParoleOfficerBehavior.ParoleOfficerActivity.SearchingParolee) return false;

            // Check distance
            float distance = Vector3.Distance(officer.transform.position, player.transform.position);
            if (distance > DETECTION_RANGE) return false;

            // Check search cooldown
            if (lastSearchTime.ContainsKey(player))
            {
                if (Time.time - lastSearchTime[player] < SEARCH_COOLDOWN)
                {
                    return false;
                }
            }

            // Get cached rap sheet
            var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            if (rapSheet == null)
            {
                return false; // No rap sheet found
            }

            // Only search players on parole
            if (rapSheet.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
            {
                return false;
            }

            // Get search probability based on LSI level
            float searchChance = rapSheet.GetSearchProbability();

            // Roll for random search
            float roll = UnityEngine.Random.Range(0f, 1f);
            bool shouldSearch = roll < searchChance;

            ModLogger.Debug($"Search roll for {player.name}: {roll:F2} vs {searchChance:F2} (LSI: {rapSheet.LSILevel}) = {shouldSearch}");

            return shouldSearch;
        }

        /// <summary>
        /// Initiate a contraband search on the player
        /// </summary>
        public IEnumerator PerformParoleSearch(ParoleOfficerBehavior officer, Player player)
        {
            if (officer == null || player == null) yield break;

            // Record search time
            lastSearchTime[player] = Time.time;

            // Stop officer movement
            officer.StopMovement();

            // Announce search - notification already shown in CheckForSearchOpportunities
            officer.PlayGuardVoiceCommand(
                JailNPCAudioController.GuardCommandType.Stop,
                "Parole compliance check. Stay where you are.",
                true
            );

            ModLogger.Info($"Officer {officer.GetBadgeNumber()} initiating parole search on {player.name}");

            // Wait for officer to reach player
            officer.MoveTo(player.transform.position);
            yield return new WaitForSeconds(2f);

            // Update notification - searching inventory
            officer.UpdateSearchNotification("Searching inventory - don't move");
            officer.TrySendNPCMessage("I'm going to search your inventory. Don't move.", 3f);
            yield return new WaitForSeconds(1.5f);

            // Perform actual contraband search
            var crimeDetectionSystem = CrimeDetectionSystem.Instance;
            if (crimeDetectionSystem == null)
            {
                ModLogger.Error("CrimeDetectionSystem not available for contraband search");
                officer.ShowSearchResults(false);
                yield break;
            }

            var contrabandSystem = new ContrabandDetectionSystem(crimeDetectionSystem);
            var detectedCrimes = contrabandSystem.PerformContrabandSearch(player);

            if (detectedCrimes != null && detectedCrimes.Count > 0)
            {
                // Contraband found!
                HandleContrabandFound(officer, player, detectedCrimes);
                officer.ShowSearchResults(true, detectedCrimes.Count);
            }
            else
            {
                // Clean search
                officer.PlayGuardVoiceCommand(
                    JailNPCAudioController.GuardCommandType.AllClear,
                    "You're clean. Stay out of trouble.",
                    true
                );

                ModLogger.Info($"Officer {officer.GetBadgeNumber()}: Clean search for {player.name}");
                officer.ShowSearchResults(false);
            }

            // Resume patrol after delay (notification will auto-hide)
            yield return new WaitForSeconds(2f);
            
            // If still in search activity, resume patrol
            if (officer.GetCurrentActivity() == ParoleOfficerBehavior.ParoleOfficerActivity.SearchingParolee)
            {
                officer.StartPatrol();
            }
        }

        /// <summary>
        /// Handle contraband detection during search
        /// </summary>
        private void HandleContrabandFound(ParoleOfficerBehavior officer, Player player, List<CrimeInstance> crimes)
        {
            officer.PlayGuardVoiceCommand(
                JailNPCAudioController.GuardCommandType.Alert,
                "Contraband detected! You're in violation of parole!",
                true
            );

            ModLogger.Info($"Officer {officer.GetBadgeNumber()}: Found {crimes.Count} contraband items on {player.name}");

            // Get cached rap sheet
            var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            if (rapSheet != null)
            {
                foreach (var crime in crimes)
                {
                    rapSheet.AddCrime(crime);
                }

                // Add parole violation
                if (rapSheet.CurrentParoleRecord != null)
                {
                    var violation = new ViolationRecord
                    {
                        ViolationType = ViolationType.ContrabandPossession,
                        ViolationTime = DateTime.Now,
                        Details = $"Found {crimes.Count} contraband items during parole search"
                    };
                    rapSheet.CurrentParoleRecord.AddViolation(violation);

                    // Re-assess LSI level after violation
                    rapSheet.UpdateLSILevel();
                }

                // Save the updated rap sheet and invalidate cache
                RapSheetManager.Instance.SaveRapSheet(player, invalidateCache: true);
            }

            // Initiate arrest for parole violation
            var jailSystem = Core.Instance?.JailSystem;
            if (jailSystem != null)
            {
                MelonCoroutines.Start(jailSystem.HandleImmediateArrest(player));
            }
        }

        /// <summary>
        /// Clear search cooldown for a player (for testing)
        /// </summary>
        public void ClearSearchCooldown(Player player)
        {
            if (lastSearchTime.ContainsKey(player))
            {
                lastSearchTime.Remove(player);
            }
        }
    }
}

