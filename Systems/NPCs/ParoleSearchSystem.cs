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
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
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

        // Base detection range (used as fallback)
        private const float BASE_DETECTION_RANGE = 20f;
        
        // Distance threshold for officer to be considered "at player" for search
        private const float SEARCH_DISTANCE_THRESHOLD = 2.5f;
        
        // Track player movement state during searches
        private Dictionary<Player, bool> playerMovementState = new Dictionary<Player, bool>();

        /// <summary>
        /// Get detection range based on LSI level
        /// Higher risk levels = larger detection radius (more intensive supervision)
        /// </summary>
        private float GetDetectionRange(LSILevel lsiLevel)
        {
            switch (lsiLevel)
            {
                case LSILevel.None:
                    return 0f;           // Not on parole
                case LSILevel.Minimum:
                    return 17f;         // Low risk - smaller radius
                case LSILevel.Medium:
                    return 23f;         // Moderate risk - medium radius
                case LSILevel.High:
                    return 30f;         // High risk - larger radius
                case LSILevel.Severe:
                    return 40f;         // Maximum risk - largest radius
                default:
                    return BASE_DETECTION_RANGE; // Fallback
            }
        }

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

            // Get cached rap sheet early (needed for LSI-based detection range)
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

            // Get dynamic detection range based on LSI level
            float detectionRange = GetDetectionRange(rapSheet.LSILevel);

            // Check distance using dynamic range
            float distance = Vector3.Distance(officer.transform.position, player.transform.position);
            if (distance > detectionRange)
            {
                ModLogger.Debug($"Player {player.name} out of detection range: {distance:F1}m > {detectionRange:F1}m (LSI: {rapSheet.LSILevel})");
                return false;
            }

            // Check search cooldown
            if (lastSearchTime.ContainsKey(player))
            {
                float timeSinceLastSearch = Time.time - lastSearchTime[player];
                if (timeSinceLastSearch < SEARCH_COOLDOWN)
                {
                    ModLogger.Debug($"Search cooldown active for {player.name}: {timeSinceLastSearch:F1}s / {SEARCH_COOLDOWN}s");
                    return false;
                }
                else
                {
                    ModLogger.Debug($"Search cooldown expired for {player.name}: {timeSinceLastSearch:F1}s since last search");
                }
            }

            // Get search probability based on LSI level
            float searchChance = rapSheet.GetSearchProbability();

            // Roll for random search
            float roll = UnityEngine.Random.Range(0f, 1f);
            bool shouldSearch = roll < searchChance;

            ModLogger.Debug($"Search check for {player.name}: distance={distance:F1}m (range={detectionRange:F1}m), cooldown OK, LSI={rapSheet.LSILevel}, chance={searchChance:P0}, roll={roll:F2} => {shouldSearch}");

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

            // CRITICAL: Freeze player movement immediately when search starts
            bool wasMovable = false;
            try
            {
#if MONO
                wasMovable = PlayerSingleton<PlayerMovement>.Instance.CanMove;
                PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
                wasMovable = PlayerSingleton<PlayerMovement>.Instance.canMove;
                PlayerSingleton<PlayerMovement>.Instance.canMove = false;
#endif
                playerMovementState[player] = wasMovable;
                ModLogger.Debug($"Froze player {player.name} movement for parole search");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error freezing player movement: {ex.Message}");
            }

            // Stop officer movement
            officer.StopMovement();

            // Announce search - notification already shown in CheckForSearchOpportunities
            officer.PlayGuardVoiceCommand(
                JailNPCAudioController.GuardCommandType.Stop,
                "Parole compliance check. Stay where you are.",
                true
            );

            ModLogger.Info($"Officer {officer.GetBadgeNumber()} initiating parole search on {player.name}");

            // Make officer walk to player and wait until they're close enough
            Vector3 playerPosition = player.transform.position;
            officer.MoveTo(playerPosition);
            
            // Wait for officer to reach player (check distance and navAgent status)
            float maxWaitTime = 10f; // Maximum wait time to prevent infinite loops
            float elapsed = 0f;
            bool officerReachedPlayer = false;
            
            while (elapsed < maxWaitTime && !officerReachedPlayer)
            {
                float distance = Vector3.Distance(officer.transform.position, playerPosition);
                
                // Check if officer is close enough (accounting for navAgent precision)
                var navAgent = officer.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (distance < SEARCH_DISTANCE_THRESHOLD || 
                    (navAgent != null && !navAgent.pathPending && navAgent.remainingDistance < SEARCH_DISTANCE_THRESHOLD))
                {
                    officerReachedPlayer = true;
                    ModLogger.Debug($"Officer {officer.GetBadgeNumber()} reached player for search (distance: {distance:F2}m)");
                    break;
                }
                
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            if (!officerReachedPlayer)
            {
                ModLogger.Warn($"Officer {officer.GetBadgeNumber()} did not reach player within {maxWaitTime}s, proceeding with search anyway");
            }

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
                
                // Restore player movement before exiting
                RestorePlayerMovement(player);
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
            
            // Restore player movement after search completes
            RestorePlayerMovement(player);
            
            // If still in search activity, resume patrol
            if (officer.GetCurrentActivity() == ParoleOfficerBehavior.ParoleOfficerActivity.SearchingParolee)
            {
                officer.StartPatrol();
            }
        }
        
        /// <summary>
        /// Restore player movement after search completes
        /// </summary>
        private void RestorePlayerMovement(Player player)
        {
            if (player == null) return;
            
            if (playerMovementState.ContainsKey(player))
            {
                bool wasMovable = playerMovementState[player];
                try
                {
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = wasMovable;
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = wasMovable;
#endif
                    ModLogger.Debug($"Restored player {player.name} movement (wasMovable: {wasMovable})");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error restoring player movement: {ex.Message}");
                }
                finally
                {
                    playerMovementState.Remove(player);
                }
            }
            else
            {
                // If we don't have the state, just enable movement (safety fallback)
                try
                {
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
                    ModLogger.Debug($"Restored player {player.name} movement (fallback - enabled)");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error restoring player movement (fallback): {ex.Message}");
                }
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

