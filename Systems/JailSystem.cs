using System.Collections;
using Behind_Bars.Helpers;
using UnityEngine;
using MelonLoader;




#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.AvatarFramework;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    public class JailSystem
    {
        private const float MIN_JAIL_TIME = 30f; // 30 seconds minimum
        private const float MAX_JAIL_TIME = 300f; // 5 minutes maximum

        public enum JailSeverity
        {
            Minor = 0,      // Traffic violations, small theft
            Moderate = 1,   // Assault, larger theft
            Major = 2,      // Drug dealing, major assault
            Severe = 3      // Murder, major drug operations
        }

        public class JailSentence
        {
            public JailSeverity Severity { get; set; }
            public float JailTime { get; set; }
            public float FineAmount { get; set; }
            public bool CanPayFine { get; set; }
            public string Description { get; set; } = "";
        }

        /// <summary>
        /// NEW: Handle immediate arrest without going through police station/ticket GUI
        /// </summary>
        public IEnumerator HandleImmediateArrest(Player player)
        {
            ModLogger.Info($"Processing IMMEDIATE arrest for player: {player.name}");

            // Immediately take control of player state to prevent game systems from interfering
            SetPlayerJailState(player, true);

            // Show "Busted" effect like the original game
            yield return ShowBustedEffect();
            
            // Assess the crime severity
            var sentence = AssessCrimeSeverity(player);
            
            ModLogger.Info($"Crime assessment: {sentence.Severity}, Time: {sentence.JailTime}s, Fine: ${sentence.FineAmount}");

            // Determine jail time threshold for holding vs main cell
            // 1 game day = 24 real-world minutes (from TimeManager CYCLE_DURATION_MINS)
            // Convert to seconds: 24 * 60 = 1440 seconds
            const float ONE_GAME_DAY_SECONDS = 1440f;
            
            if (sentence.JailTime < ONE_GAME_DAY_SECONDS)
            {
                // Short sentence - send directly to holding cell
                ModLogger.Info($"Short sentence ({sentence.JailTime}s < {ONE_GAME_DAY_SECONDS}s) - sending to holding cell");
                yield return SendPlayerToHoldingCell(player, sentence);
            }
            else
            {
                // Long sentence - start in holding cell, then process to main cell
                ModLogger.Info($"Long sentence ({sentence.JailTime}s >= {ONE_GAME_DAY_SECONDS}s) - processing to main jail cell");
                yield return ProcessPlayerToJail(player, sentence);
            }
        }
        
        /// <summary>
        /// Show "Busted" fade effect like the original game
        /// </summary>
        private IEnumerator ShowBustedEffect()
        {
            ModLogger.Info("Showing 'Busted' fade effect");
            
            // Try to use the BlackOverlay system like the original game does
            bool overlayWorked = false;
            try
            {
                // Use the BlackOverlay system - try different Open method signatures
                Singleton<BlackOverlay>.Instance.Open(2f);
                overlayWorked = true;
                ModLogger.Info("BlackOverlay opened successfully");
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"BlackOverlay error (trying fallback): {ex.Message}");
            }
            
            if (!overlayWorked)
            {
                // Fallback - try simpler approach
                try
                {
                    // Just disable player controls briefly to simulate the "busted" pause
                    PlayerSingleton<PlayerMovement>.Instance.canMove = false;
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(false);
                    ModLogger.Info("Using fallback 'busted' effect - controls disabled briefly");
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Fallback busted effect error: {ex.Message}");
                }
            }
            
            // Wait for the effect duration
            yield return new WaitForSeconds(2f);
            
            // Re-enable controls if we disabled them in fallback
            if (!overlayWorked)
            {
                try
                {
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true;
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    ModLogger.Info("Re-enabled controls after fallback busted effect");
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Error re-enabling controls: {ex.Message}");
                }
            }
            
            ModLogger.Info("'Busted' effect completed");
        }

        /// <summary>
        /// LEGACY: Original arrest handler (kept for compatibility)
        /// </summary>
        public IEnumerator HandlePlayerArrest(Player player)
        {
            ModLogger.Info($"Processing LEGACY arrest for player: {player.name}");
            
            // Use the new immediate arrest system
            yield return HandleImmediateArrest(player);
        }

        private JailSentence AssessCrimeSeverity(Player player)
        {
            var sentence = new JailSentence();

            // Analyze player's crime data
            if (player.CrimeData != null)
            {
                // This would need to be expanded based on actual CrimeData structure
                // For now, using placeholder logic
                sentence.Severity = DetermineSeverityFromCrimeData(player.CrimeData);
            }
            else
            {
                // Default to moderate if no crime data
                sentence.Severity = JailSeverity.Moderate;
            }

            // Calculate jail time and fine based on severity
            CalculateSentence(sentence, player);

            ModLogger.Info($"Assessed crime severity: {sentence.Severity}, " +
                          $"Jail time: {sentence.JailTime}s, Fine: ${sentence.FineAmount}");

            return sentence;
        }

        private JailSeverity DetermineSeverityFromCrimeData(object crimeData)
        {
            // Calculate based on total crime fine amount (like PenaltyHandler does)
            float totalFine = CalculateTotalCrimeFines(Player.Local);
            
            // Convert fine amount to severity levels
            if (totalFine <= 100f) return JailSeverity.Minor;        // Traffic violations, small stuff
            if (totalFine <= 300f) return JailSeverity.Moderate;     // Moderate crimes  
            if (totalFine <= 800f) return JailSeverity.Major;        // Serious crimes
            return JailSeverity.Severe;                              // Major criminal activity
        }
        
        /// <summary>
        /// Calculate total fines based on the same logic as PenaltyHandler.ProcessCrimeList
        /// </summary>
        private float CalculateTotalCrimeFines(Player player)
        {
            if (player?.CrimeData?.Crimes == null)
                return 50f; // Default minor fine
                
            float totalFine = 0f;
            
            // Process each crime type like PenaltyHandler does
            foreach (var crimeEntry in player.CrimeData.Crimes)
            {
                var crime = crimeEntry.Key;
                int count = crimeEntry.Value;
                
                // Match PenaltyHandler fine calculations exactly
                string crimeName = crime.GetType().Name;
                
                switch (crimeName)
                {
                    case "PossessingControlledSubstances":
                        totalFine += 5f * count;
                        break;
                    case "PossessingLowSeverityDrug":
                        totalFine += 10f * count;
                        break;
                    case "PossessingModerateSeverityDrug":
                        totalFine += 20f * count;
                        break;
                    case "PossessingHighSeverityDrug":
                        totalFine += 30f * count;
                        break;
                    case "Evading":
                        totalFine += 50f;
                        break;
                    case "FailureToComply":
                        totalFine += 50f;
                        break;
                    case "ViolatingCurfew":
                        totalFine += 100f;
                        break;
                    case "AttemptingToSell":
                        totalFine += 150f;
                        break;
                    case "Assault":
                        totalFine += 75f;
                        break;
                    case "DeadlyAssault":
                        totalFine += 150f;
                        break;
                    case "Vandalism":
                        totalFine += 50f;
                        break;
                    case "Theft":
                        totalFine += 50f;
                        break;
                    case "BrandishingWeapon":
                        totalFine += 50f;
                        break;
                    case "DischargeFirearm":
                        totalFine += 50f;
                        break;
                    default:
                        // Unknown crime type - assign moderate fine
                        totalFine += 25f;
                        break;
                }
            }
            
            // Check for evaded arrest (not stored in Crimes dict)
            if (player.CrimeData.EvadedArrest)
            {
                totalFine += 50f;
            }
            
            ModLogger.Info($"Calculated total crime fines: ${totalFine}");
            return totalFine;
        }

        private void CalculateSentence(JailSentence sentence, Player player)
        {
            // Calculate actual fine amount
            float actualFine = CalculateTotalCrimeFines(player);
            sentence.FineAmount = actualFine;
            
            // Convert fine to jail time - more realistic scaling
            // Base conversion: $1 = 2 seconds jail time (but with minimums per severity)
            float baseJailTime = actualFine * 2f;
            
            switch (sentence.Severity)
            {
                case JailSeverity.Minor:
                    baseJailTime = Mathf.Max(baseJailTime, 120f);  // At least 2 minutes
                    sentence.Description = $"Minor offenses (${actualFine} in fines)";
                    break;
                case JailSeverity.Moderate:
                    baseJailTime = Mathf.Max(baseJailTime, 300f);  // At least 5 minutes  
                    sentence.Description = $"Moderate offenses (${actualFine} in fines)";
                    break;
                case JailSeverity.Major:
                    baseJailTime = Mathf.Max(baseJailTime, 600f);  // At least 10 minutes
                    sentence.Description = $"Major offenses (${actualFine} in fines)";
                    break;
                case JailSeverity.Severe:
                    baseJailTime = Mathf.Max(baseJailTime, 1200f); // At least 20 minutes
                    sentence.Description = $"Severe offenses (${actualFine} in fines)";
                    break;
            }

            // Apply level multiplier but keep reasonable bounds
            float levelMultiplier = GetPlayerLevelMultiplier(player);
            sentence.JailTime = Mathf.Clamp(baseJailTime * levelMultiplier, MIN_JAIL_TIME, 1800f); // Max 30 min

            // For immediate jail system, we don't offer fine payment
            sentence.CanPayFine = false;
        }

        private float GetPlayerLevelMultiplier(Player player)
        {
            // TODO: Implement actual level-based calculation
            // This should consider player level, reputation, etc.
            return 1.0f; // Default multiplier
        }

        private bool CanPlayerAffordFine(Player player, float fineAmount)
        {
            // TODO: Implement actual money checking
            // This should check the player's actual money/currency
            return true; // Placeholder
        }



        private Dictionary<string, Vector3> _lastKnownPlayerPosition = new();
        
        /// <summary>
        /// Set player state for jail (enable/disable controls properly)
        /// </summary>
        private void SetPlayerJailState(Player player, bool inJail)
        {
            if (inJail)
            {
                // Player is going to jail - ensure they maintain all controls
                ModLogger.Info("Setting player state for jail - keeping all controls enabled");
                
                try
                {
                    // Enable all controls - player should be able to move around in the cell
                    PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true; // Allow movement
                    
                    // Keep HUD enabled
                    Singleton<HUD>.Instance.canvas.enabled = true;
                    Singleton<HUD>.Instance.SetCrosshairVisible(true);
                    
                    ModLogger.Info("Jail state set - player can move, use inventory, and look around");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"Error setting jail state: {ex.Message}");
                }
            }
            else
            {
                // Player is being released - ensure all controls are enabled
                ModLogger.Info("Setting player state for release - enabling all controls");
                
                try
                {
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true;
                    PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
                    Singleton<HUD>.Instance.canvas.enabled = true;
                    Singleton<HUD>.Instance.SetCrosshairVisible(true);
                    
                    ModLogger.Info("Release state set - all controls enabled");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"Error setting release state: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Wait for the specified time while maintaining player controls in jail
        /// </summary>
        private IEnumerator WaitWithControlMaintenance(float waitTime, Player player)
        {
            ModLogger.Info($"Starting jail time with control maintenance for {waitTime}s");
            
            float elapsed = 0f;
            const float checkInterval = 1f; // Check every second
            
            while (elapsed < waitTime)
            {
                // Wait for the check interval or remaining time, whichever is shorter
                float timeToWait = Mathf.Min(checkInterval, waitTime - elapsed);
                yield return new WaitForSeconds(timeToWait);
                elapsed += timeToWait;
                
                // Ensure controls are still enabled
                try
                {
                    PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true; // Enable movement in jail
                    Singleton<HUD>.Instance.canvas.enabled = true;
                    Singleton<HUD>.Instance.SetCrosshairVisible(true);
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Control maintenance error: {ex.Message}");
                }
            }
            
            ModLogger.Info($"Jail time completed after {elapsed}s with control maintenance");
        }

        /// <summary>
        /// Send player directly to holding cell for short sentences
        /// </summary>
        private IEnumerator SendPlayerToHoldingCell(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Sending player {player.name} to holding cell for {sentence.JailTime}s");

            // Get jail system and find available holding cell
            var jailController = Core.ActiveJailController;
            if (jailController == null)
            {
                ModLogger.Error("No active jail controller found, using fallback jail method");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Store current position before jailing
            if (_lastKnownPlayerPosition.ContainsKey(player.name))
                _lastKnownPlayerPosition[player.name] = player.transform.position;
            else
                _lastKnownPlayerPosition.Add(player.name, player.transform.position);

            // Find an available holding cell
            var holdingCell = GetAvailableHoldingCell(jailController);
            if (holdingCell == null)
            {
                ModLogger.Error("No holding cells available, using fallback jail method");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Get spawn point in the holding cell
            Transform spawnPoint = holdingCell.AssignPlayerToSpawnPoint(player.name);
            if (spawnPoint == null)
            {
                ModLogger.Error($"No spawn points available in holding cell {holdingCell.cellName}");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            ModLogger.Info($"Teleporting player to holding cell: {holdingCell.cellName} at {spawnPoint.name}");

            // Teleport player to holding cell
            player.transform.position = spawnPoint.position;

            // Lock the holding cell door
            holdingCell.cellDoor.LockDoor();
            holdingCell.cellDoor.CloseDoor();

            // Keep player controls enabled in jail - they can still access inventory and look around
            // Don't disable inventory - let them use items and hotbar
            // Don't disable mouse - let them look around
            // Only movement is restricted by the locked cell door
            ModLogger.Info("Player controls left enabled during jail time - can access inventory and look around");

            ModLogger.Info($"Player {player.name} placed in {holdingCell.cellName} for {sentence.JailTime}s");

            // Wait for jail time while ensuring controls stay enabled
            yield return WaitWithControlMaintenance(sentence.JailTime, player);

            ModLogger.Info($"Player {player.name} has served their holding cell time");

            // Release from holding cell
            holdingCell.ReleasePlayerFromSpawnPoint(player.name);
            holdingCell.cellDoor.UnlockDoor();

            ReleasePlayerFromJail(player);
        }

        /// <summary>
        /// Process player to main jail cell (starts in holding, then transfers)
        /// </summary>
        private IEnumerator ProcessPlayerToJail(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Processing player {player.name} to main jail cell for {sentence.JailTime}s");

            // First, put them in holding cell for "processing"
            yield return SendPlayerToHoldingCellForProcessing(player, sentence);
            
            // Then move to main jail cell
            yield return TransferToMainJailCell(player, sentence);
        }

        private IEnumerator SendPlayerToHoldingCellForProcessing(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Sending player {player.name} to holding cell for processing");

            var jailController = Core.ActiveJailController;
            if (jailController == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Store current position
            if (_lastKnownPlayerPosition.ContainsKey(player.name))
                _lastKnownPlayerPosition[player.name] = player.transform.position;
            else
                _lastKnownPlayerPosition.Add(player.name, player.transform.position);

            var holdingCell = GetAvailableHoldingCell(jailController);
            if (holdingCell == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            Transform spawnPoint = holdingCell.AssignPlayerToSpawnPoint(player.name);
            if (spawnPoint == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Teleport to holding cell
            player.transform.position = spawnPoint.position;
            holdingCell.cellDoor.LockDoor();
            holdingCell.cellDoor.CloseDoor();

            // Keep controls enabled during processing
            ModLogger.Info("Player controls kept enabled during processing");

            ModLogger.Info($"Player {player.name} in holding cell for processing - waiting 60 seconds");

            // Processing delay - 60 seconds with control maintenance
            yield return WaitWithControlMaintenance(60f, player);

            // Release from holding cell (but don't release from jail yet)
            holdingCell.ReleasePlayerFromSpawnPoint(player.name);
            holdingCell.cellDoor.UnlockDoor();
        }

        private IEnumerator TransferToMainJailCell(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Transferring player {player.name} to main jail cell");

            var jailController = Core.ActiveJailController;
            if (jailController == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Find available main jail cell
            var mainCell = GetAvailableMainCell(jailController);
            if (mainCell == null)
            {
                ModLogger.Error("No main cells available, keeping in holding cell");
                // Continue with remaining sentence in holding cell
                var holdingCell = GetAvailableHoldingCell(jailController);
                if (holdingCell != null)
                {
                    Transform holdingSpawn = holdingCell.AssignPlayerToSpawnPoint(player.name);
                    if (holdingSpawn != null)
                    {
                        player.transform.position = holdingSpawn.position;
                        holdingCell.cellDoor.LockDoor();
                        yield return WaitWithControlMaintenance(sentence.JailTime - 60f, player); // Subtract processing time
                        holdingCell.ReleasePlayerFromSpawnPoint(player.name);
                        holdingCell.cellDoor.UnlockDoor();
                        ReleasePlayerFromJail(player);
                    }
                }
                yield break;
            }

            // Get spawn point in main cell
            Transform cellSpawnPoint = mainCell.AssignPlayerToSpawnPoint(player.name);
            if (cellSpawnPoint == null)
            {
                ModLogger.Error($"No spawn point in main cell {mainCell.cellName}");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Teleport to main cell
            player.transform.position = cellSpawnPoint.position;
            mainCell.cellDoor.LockDoor();
            mainCell.cellDoor.CloseDoor();

            ModLogger.Info($"Player {player.name} transferred to main cell {mainCell.cellName} for remaining {sentence.JailTime - 60f}s");

            // Wait for remaining sentence time (minus the 60s processing time)
            float remainingTime = sentence.JailTime - 60f;
            yield return WaitWithControlMaintenance(remainingTime, player);

            ModLogger.Info($"Player {player.name} has served their main cell time");

            // Release from main cell
            mainCell.ReleasePlayerFromSpawnPoint(player.name);
            mainCell.cellDoor.UnlockDoor();

            ReleasePlayerFromJail(player);
        }

        private CellDetail GetAvailableHoldingCell(JailController jailController)
        {
            // Find holding cell with available spawn points
            foreach (var holdingCell in jailController.holdingCells)
            {
                if (holdingCell.GetAvailableSpawnPoint() != null)
                {
                    return holdingCell;
                }
            }
            return null;
        }

        private CellDetail GetAvailableMainCell(JailController jailController)
        {
            // Find main cell that's not occupied
            foreach (var cell in jailController.cells)
            {
                if (!cell.isOccupied)
                {
                    return cell;
                }
            }
            return null;
        }


        /// <summary>
        /// Fallback method when holding cells are not available
        /// </summary>
        private IEnumerator FallbackJailMethod(Player player, JailSentence sentence)
        {
            // Keep all controls enabled even in fallback
            PlayerSingleton<PlayerMovement>.Instance.canMove = true;
            Singleton<BlackOverlay>.Instance.Open(2f);

            ModLogger.Info($"Player {player.name} using fallback jail method (screen blackout) for {sentence.JailTime}s");

            yield return WaitWithControlMaintenance(sentence.JailTime, player);

            ModLogger.Info($"Player {player.name} has served their jail time (fallback method)");
            ReleasePlayerFromJail(player);
        }

        private void ReleasePlayerFromJail(Player player)
        {
            ModLogger.Info($"Releasing player {player.name} from jail");

            // Teleport player to jail exit location (outside the jail)
            if (_lastKnownPlayerPosition.ContainsKey(player.name))
            {
                player.transform.position = _lastKnownPlayerPosition[player.name];
                _lastKnownPlayerPosition.Remove(player.name);
            }

            // Reset arrest state FIRST
            player.IsArrested = false;
            
            // Use our state management method
            SetPlayerJailState(player, false);

            // Remove any active UI elements from arrest
            try
            {
                PlayerSingleton<PlayerCamera>.Instance.RemoveActiveUIElement("Arrested");
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Could not remove 'Arrested' UI element: {ex.Message}");
            }

            // Close black overlay if it's open (for fallback method)
            try
            {
                Singleton<BlackOverlay>.Instance.Close(2f);
            }
            catch
            {
                // BlackOverlay might not have isOpen property, just try to close it
            }

            // Reset the arrest handling flag for future arrests
            Behind_Bars.Harmony.HarmonyPatches.ResetArrestHandlingFlag();

            ModLogger.Info($"Player {player.name} released from jail successfully - all controls restored");
        }
    }
}
