using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems;
using Behind_Bars.UI;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
using Il2CppFishNet;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
using FishNet;
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

        // Grace period after release before searches can occur (in game minutes)
        private Dictionary<Player, float> releaseTime = new Dictionary<Player, float>();
        private const float RELEASE_GRACE_PERIOD_GAME_MINUTES = 30f; // Half an in-game hour

        // Base detection range (used as fallback)
        private const float BASE_DETECTION_RANGE = 20f;
        
        // Distance threshold for officer to be considered "at player" for search
        private const float SEARCH_DISTANCE_THRESHOLD = 1.5f;

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

            // CRITICAL: Don't search if release summary UI is still visible
            if (BehindBarsUIManager.Instance != null && BehindBarsUIManager.Instance.IsParoleConditionsUIVisible())
            {
                ModLogger.Debug($"Player {player.name} has release summary UI visible - skipping search");
                return false;
            }

            // Check grace period after release - don't search immediately after release
            if (releaseTime.ContainsKey(player))
            {
                float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
                float timeSinceRelease = currentGameTime - releaseTime[player];
                
                if (timeSinceRelease < RELEASE_GRACE_PERIOD_GAME_MINUTES)
                {
                    float remainingGrace = RELEASE_GRACE_PERIOD_GAME_MINUTES - timeSinceRelease;
                    ModLogger.Debug($"Player {player.name} is in grace period after release: {timeSinceRelease:F1} game minutes since release (grace period: {RELEASE_GRACE_PERIOD_GAME_MINUTES} game minutes, {remainingGrace:F1} remaining)");
                    return false;
                }
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

            // Freeze player movement during search
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
            PlayerSingleton<PlayerMovement>.Instance.canMove = false;
#endif
            ModLogger.Debug($"Frozen player {player.name} movement for parole search");

            // Announce search - notification already shown in CheckForSearchOpportunities
            officer.PlayGuardVoiceCommand(
                JailNPCAudioController.GuardCommandType.Stop,
                "Parole compliance check. Stay where you are.",
                true
            );

            ModLogger.Info($"Officer {officer.GetBadgeNumber()} initiating parole search on {player.name} - navigating to player position");

            // Make officer walk directly to player and wait until they're within 5f
            // DO NOT call StopMovement() here - we need the officer to actually move!
            // The activity is already set to SearchingParolee which stops patrol logic
            Vector3 playerPosition = player.transform.position;
            float maxWaitTime = 15f; // Increased wait time to allow officer to walk from detection radius
            float elapsed = 0f;
            bool officerReachedPlayer = false;
            
            // Start moving to player immediately
            // Ensure navAgent is enabled and ready
            var navAgent = officer.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                if (!navAgent.enabled)
                {
                    ModLogger.Warn($"Officer {officer.GetBadgeNumber()} navAgent is disabled - enabling it");
                    navAgent.enabled = true;
                }
                
                if (!navAgent.isOnNavMesh)
                {
                    ModLogger.Warn($"Officer {officer.GetBadgeNumber()} navAgent is not on NavMesh - may cause movement issues");
                }
            }
            
            bool moveStarted = officer.MoveTo(playerPosition);
            ModLogger.Info($"Officer {officer.GetBadgeNumber()} MoveTo() called: success={moveStarted}, destination={playerPosition}, current position={officer.transform.position}, distance={Vector3.Distance(officer.transform.position, playerPosition):F2}m");
            
            if (!moveStarted)
            {
                ModLogger.Warn($"Officer {officer.GetBadgeNumber()} failed to start movement to player - navAgent may not be ready");
            }
            
            while (elapsed < maxWaitTime && !officerReachedPlayer)
            {
                // Get current player position (should be frozen, but update target just in case)
                playerPosition = player.transform.position;
                float distance = Vector3.Distance(officer.transform.position, playerPosition);
                
                // If officer is not close enough, keep moving toward player
                if (distance > SEARCH_DISTANCE_THRESHOLD)
                {
                    // Update destination to player's current position (in case player moved slightly)
                    // Only update if navAgent is not already moving or if destination changed significantly
                    if (navAgent != null && navAgent.enabled)
                    {
                        // Update destination if navAgent has stopped or if we're far from target
                        if (!navAgent.pathPending && navAgent.remainingDistance > SEARCH_DISTANCE_THRESHOLD)
                        {
                            bool updated = officer.MoveTo(playerPosition);
                            if (updated)
                            {
                                ModLogger.Debug($"Officer {officer.GetBadgeNumber()} updated destination to player (distance: {distance:F2}m)");
                            }
                        }
                        
                        // Check if navAgent has reached destination or is close enough
                        if (!navAgent.pathPending && navAgent.remainingDistance < SEARCH_DISTANCE_THRESHOLD)
                        {
                            officerReachedPlayer = true;
                            ModLogger.Debug($"Officer {officer.GetBadgeNumber()} reached player via navAgent (remainingDistance: {navAgent.remainingDistance:F2}m, direct distance: {distance:F2}m)");
                            break;
                        }
                        
                        // Log progress every 2 seconds for debugging
                        if (elapsed % 2f < Time.deltaTime)
                        {
                            ModLogger.Debug($"Officer {officer.GetBadgeNumber()} moving to player: distance={distance:F2}m, navAgent.remainingDistance={navAgent.remainingDistance:F2}m, pathPending={navAgent.pathPending}, hasPath={navAgent.hasPath}, enabled={navAgent.enabled}, isOnNavMesh={navAgent.isOnNavMesh}");
                        }
                    }
                    else
                    {
                        // NavAgent not available - use direct distance check
                        if (distance < SEARCH_DISTANCE_THRESHOLD)
                        {
                            officerReachedPlayer = true;
                            ModLogger.Debug($"Officer {officer.GetBadgeNumber()} reached player (distance: {distance:F2}m)");
                            break;
                        }
                    }
                }
                else
                {
                    // Officer is within 5f - stop movement and proceed with search
                    officer.StopMovement();
                    officerReachedPlayer = true;
                    ModLogger.Debug($"Officer {officer.GetBadgeNumber()} reached player for search (distance: {distance:F2}m)");
                    break;
                }
                
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            if (!officerReachedPlayer)
            {
                ModLogger.Warn($"Officer {officer.GetBadgeNumber()} did not reach player within {maxWaitTime}s (final distance: {Vector3.Distance(officer.transform.position, player.transform.position):F2}m), proceeding with search anyway");
                // Stop movement before proceeding
                officer.StopMovement();
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

            bool arrestInitiated = false;
            if (detectedCrimes != null && detectedCrimes.Count > 0)
            {
                // Contraband found!
                arrestInitiated = HandleContrabandFound(officer, player, detectedCrimes);
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

            // Only restore player movement if arrest was NOT initiated
            // If arrest was initiated, the arrest process will handle player state
            if (!arrestInitiated)
            {
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
            else
            {
                ModLogger.Info($"Arrest initiated for {player.name} - skipping movement restoration and patrol resume");
                // Don't restore movement - arrest process will handle it
                // Don't resume patrol - officer is handling the arrest
            }
        }
        
        /// <summary>
        /// Restore player movement after search completes
        /// </summary>
        private void RestorePlayerMovement(Player player)
        {
            if (player == null) return;
            
            // Check if release summary UI is visible - if so, player should remain frozen
            bool shouldBeMovable = true;
            if (BehindBarsUIManager.Instance != null && BehindBarsUIManager.Instance.IsParoleConditionsUIVisible())
            {
                shouldBeMovable = false;
                ModLogger.Debug($"Release summary UI is visible - keeping player {player.name} frozen after search");
            }
            
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = shouldBeMovable;
#else
            PlayerSingleton<PlayerMovement>.Instance.canMove = shouldBeMovable;
#endif
            ModLogger.Debug($"Restored player {player.name} movement to {shouldBeMovable}");
        }

        /// <summary>
        /// Handle contraband detection during search
        /// Returns true if arrest was initiated, false otherwise
        /// </summary>
        private bool HandleContrabandFound(ParoleOfficerBehavior officer, Player player, List<CrimeInstance> crimes)
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
                    rapSheet.AddParoleViolation(violation); // Use helper method that marks RapSheet as changed

                    // Re-assess LSI level after violation
                    rapSheet.UpdateLSILevel();
                }
                RapSheetManager.Instance.MarkRapSheetChanged(player);
            }

            // Use the game's built-in arrest methods instead of HandleImmediateArrest
            // This should prevent the black screen issue
            try
            {
                if (player == null)
                {
                    ModLogger.Error("Cannot arrest - player is null");
                    return false;
                }

                ModLogger.Info($"Initiating arrest for {player.name} due to parole violation using built-in arrest methods");

                // Check if we're the server (for network games)
                bool isServer = false;
#if !MONO
                var networkManager = Il2CppFishNet.InstanceFinder.NetworkManager;
                if (networkManager != null)
                {
                    isServer = networkManager.IsServer;
                }
#else
                var networkManager = FishNet.InstanceFinder.NetworkManager;
                if (networkManager != null)
                {
                    isServer = networkManager.IsServer;
                }
#endif

                // Call the appropriate arrest method based on network role
                if (isServer)
                {
                    // Server calls Arrest_Server
                    player.Arrest_Server();
                    ModLogger.Info($"Called Arrest_Server() for {player.name}");
                }
                else
                {
                    // Client calls Arrest_Client
                    player.Arrest_Client();
                    ModLogger.Info($"Called Arrest_Client() for {player.name}");
                }

                // The Harmony patches will intercept these calls and trigger HandleImmediateArrest
                // This ensures proper integration with the jail system while using the game's built-in methods
                return true; // Arrest was initiated
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error calling built-in arrest methods for {player.name}: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
                
                // Fallback to HandleImmediateArrest if built-in methods fail
                var jailSystem = Core.Instance?.JailSystem;
                if (jailSystem != null)
                {
                    ModLogger.Info($"Falling back to HandleImmediateArrest for {player.name}");
                    MelonCoroutines.Start(jailSystem.HandleImmediateArrest(player));
                    return true;
                }
                else
                {
                    ModLogger.Error("JailSystem not available - cannot initiate arrest for parole violation");
                    return false;
                }
            }
        }

        /// <summary>
        /// Record the release time for a player (used for grace period)
        /// Call this when parole starts after release
        /// </summary>
        public void RecordReleaseTime(Player player)
        {
            if (player == null) return;
            
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            releaseTime[player] = currentGameTime;
            ModLogger.Info($"Recorded release time for {player.name}: {currentGameTime} game minutes (grace period: {RELEASE_GRACE_PERIOD_GAME_MINUTES} game minutes / {GameTimeManager.FormatGameTime(RELEASE_GRACE_PERIOD_GAME_MINUTES)})");
        }

        /// <summary>
        /// Clear release time for a player (when parole ends or player is arrested)
        /// </summary>
        public void ClearReleaseTime(Player player)
        {
            if (player != null && releaseTime.ContainsKey(player))
            {
                releaseTime.Remove(player);
                ModLogger.Debug($"Cleared release time for {player.name}");
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

