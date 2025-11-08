using System.Collections;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Harmony;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.Data;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems.CrimeTracking;
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
        private const float MIN_JAIL_TIME = Constants.DEFAULT_MIN_JAIL_TIME;
        private const float MAX_JAIL_TIME = Constants.DEFAULT_MAX_JAIL_TIME;

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

            // CRITICAL: Reset all previous jail/booking/release state before starting new arrest
            ResetPlayerJailState(player);

            // Inventory capture now handled in Harmony patch before any clearing happens
            // CreateInventorySnapshotIfNeeded(player); // MOVED TO HARMONY PATCH

            // Immediately take control of player state to prevent game systems from interfering
            SetPlayerJailState(player, true);

            // Show "Busted" effect like the original game
            yield return ShowBustedEffect();
            
            // Restore UI interactions so player can interact during booking process
            Behind_Bars.Harmony.HarmonyPatches.RestoreUIInteractions();

            // Assess the crime severity
            var sentence = AssessCrimeSeverity(player);

            ModLogger.Info($"Crime assessment: {sentence.Severity}, Time: {sentence.JailTime}s, Fine: ${sentence.FineAmount}");

            // Inventory capture already done at start of arrest process
            // CreateInventorySnapshotIfNeeded(player); // REMOVED - now done at start of HandleImmediateArrest

            // Determine jail time threshold for holding vs main cell
            // sentence.JailTime is in game minutes
            // 1 game day = 1440 game minutes (24 hours * 60 minutes)
            const float ONE_GAME_DAY_MINUTES = 1440f;

            if (sentence.JailTime < ONE_GAME_DAY_MINUTES)
            {
                // Short sentence - send directly to holding cell
                ModLogger.Info($"Short sentence ({sentence.JailTime} game minutes < {ONE_GAME_DAY_MINUTES} game minutes / {GameTimeManager.FormatGameTime(sentence.JailTime)}) - sending to holding cell");
                yield return SendPlayerToHoldingCell(player, sentence);
            }
            else
            {
                // Long sentence - start in holding cell, then process to main cell
                ModLogger.Info($"Long sentence ({sentence.JailTime} game minutes >= {ONE_GAME_DAY_MINUTES} game minutes / {GameTimeManager.FormatGameTime(sentence.JailTime)}) - processing to main jail cell");
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
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = false;
#endif
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
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
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

            // Show UI with crime information
            ShowJailInfoUI(sentence, player);

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
        /// Calculate total fines using FineCalculator (independent from sentences)
        /// </summary>
        private float CalculateTotalCrimeFines(Player player)
        {
            // Get RapSheet for repeat offender multiplier
            var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            
            // Use FineCalculator to calculate fines (rapSheet is optional)
            float totalFine = FineCalculator.Instance.CalculateTotalFine(player, rapSheet);
            
            ModLogger.Info($"Calculated total fines using FineCalculator: ${totalFine:F2}");
            return totalFine;
        }


        private void CalculateSentence(JailSentence sentence, Player player)
        {
            // Get RapSheet for sentence calculation
            var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            
            // Calculate fine using FineCalculator (independent from sentence)
            float actualFine = CalculateTotalCrimeFines(player);
            sentence.FineAmount = actualFine;

            // Calculate sentence using CrimeSentenceCalculator (in game minutes)
            var sentenceData = CrimeSentenceCalculator.Instance.CalculateSentence(player, rapSheet);
            
            // Convert game minutes to real-time seconds for JailTime
            // 1 game minute = 1 real second, so conversion is 1:1
            float jailTimeInSeconds = sentenceData.TotalGameMinutes;
            
            sentence.JailTime = jailTimeInSeconds;
            
            // Update description with formatted sentence
            sentence.Description = GetCrimeDescription(sentence.Severity, player);
            sentence.Description += $" - {sentenceData.FormattedSentence}";

            // For immediate jail system, we don't offer fine payment
            sentence.CanPayFine = false;
            
            ModLogger.Info($"[SENTENCE CALC] Calculated sentence: {sentenceData.FormattedSentence}");
            ModLogger.Info($"[SENTENCE CALC] TotalGameMinutes: {sentenceData.TotalGameMinutes}, JailTime (game minutes): {jailTimeInSeconds}");
            ModLogger.Info($"[SENTENCE CALC] Base: {sentenceData.BaseSentenceMinutes}, Severity: {sentenceData.SeverityMultiplier}, Repeat: {sentenceData.RepeatOffenderMultiplier}, Witness: {sentenceData.WitnessMultiplier}, Global: {sentenceData.GlobalMultiplier}");
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
        private InventoryPickupStation _inventoryPickupStation;

        /// <summary>
        /// Initialize the JailSystem and find required components
        /// </summary>
        public void Initialize()
        {
            ModLogger.Info("Initializing JailSystem components");

            // Find the inventory pickup station
            _inventoryPickupStation = UnityEngine.Object.FindObjectOfType<InventoryPickupStation>();
            if (_inventoryPickupStation != null)
            {
                ModLogger.Info("Found existing InventoryPickupStation reference");
            }
            else
            {
                ModLogger.Warn("InventoryPickupStation not found - creating one now");
                CreateInventoryPickupStation();

                // Verify it was created
                _inventoryPickupStation = UnityEngine.Object.FindObjectOfType<InventoryPickupStation>();
                if (_inventoryPickupStation != null)
                {
                    ModLogger.Info("InventoryPickupStation successfully created and found");
                }
                else
                {
                    ModLogger.Error("Failed to create or find InventoryPickupStation after creation attempt");
                }
            }
        }

        /// <summary>
        /// Get the stored exit position for a player
        /// </summary>
        public Vector3? GetPlayerExitPosition(string playerName)
        {
            if (_lastKnownPlayerPosition.ContainsKey(playerName))
            {
                var position = _lastKnownPlayerPosition[playerName];
                _lastKnownPlayerPosition.Remove(playerName); // Remove after use
                return position;
            }
            return null;
        }

        /// <summary>
        /// Create an InventoryPickupStation for the jail
        /// </summary>
        private void CreateInventoryPickupStation()
        {
            try
            {
                // Position pickup station at inventoryDropOff location (repurposing for returns)
                var jailController = Core.JailController;
                Vector3 stationPosition = new Vector3(0, 1, 0); // Default position

                if (jailController?.storage?.inventoryDropOff != null)
                {
                    // Use the inventoryDropOff location for both intake drops and release pickups
                    stationPosition = jailController.storage.inventoryDropOff.position;
                    ModLogger.Info($"Positioning InventoryPickupStation at inventoryDropOff: {stationPosition}");
                }
                else if (jailController?.booking?.guardSpawns != null && jailController.booking.guardSpawns.Count > 0)
                {
                    // Fallback to booking area if storage not available
                    var bookingArea = jailController.booking.guardSpawns[0];
                    stationPosition = bookingArea.position + new Vector3(2, 0, 0);
                    ModLogger.Warn("Storage inventoryDropOff not found - using booking area fallback");
                }

                // Create GameObject for the pickup station
                var stationObject = new GameObject("InventoryPickupStation");
                stationObject.transform.position = stationPosition;

                // Add the InventoryPickupStation component
                _inventoryPickupStation = stationObject.AddComponent<InventoryPickupStation>();

                ModLogger.Info($"Created InventoryPickupStation at position {stationPosition}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Failed to create InventoryPickupStation: {e.Message}");
            }
        }

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
                    PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                    PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                    PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true; // Allow movement
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true; // Allow movement
#endif

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
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
                    PlayerSingleton<PlayerInventory>.Instance.enabled = true;
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
                    PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                    PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true; // Enable movement in jail
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true; // Enable movement in jail
#endif
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
        /// Wait for jail sentence using game time tracking
        /// </summary>
        private IEnumerator WaitForJailSentence(float sentenceGameMinutes, Player player)
        {
            ModLogger.Info($"[JAIL TRACKING] Starting jail sentence tracking for {player.name}: {sentenceGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentenceGameMinutes)})");

            if (sentenceGameMinutes <= 0)
            {
                ModLogger.Warn($"[JAIL TRACKING] Invalid sentence time: {sentenceGameMinutes} game minutes - completing immediately");
                yield break;
            }

            bool sentenceComplete = false;
            System.Action<Player> onComplete = (p) => 
            { 
                sentenceComplete = true;
                ModLogger.Info($"[JAIL TRACKING] Sentence completion callback triggered for {p.name}");
            };

            // Start tracking with JailTimeTracker
            JailTimeTracker.Instance.StartTracking(player, sentenceGameMinutes, onComplete);

            // Wait while maintaining controls and checking for completion
            const float checkInterval = 1f; // Check every real second
            while (!sentenceComplete)
            {
                yield return new WaitForSeconds(checkInterval);

                // Ensure controls are still enabled
                try
                {
                    PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                    PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                    PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse();
#if MONO
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
                    PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
                    Singleton<HUD>.Instance.canvas.enabled = true;
                    Singleton<HUD>.Instance.SetCrosshairVisible(true);
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Control maintenance error: {ex.Message}");
                }

                // Check remaining time for logging
                float remaining = JailTimeTracker.Instance.GetRemainingTime(player);
                if (remaining > 0 && Mathf.FloorToInt(remaining) % 60 == 0) // Log every game hour
                {
                    ModLogger.Debug($"[JAIL TRACKING] Remaining: {JailTimeTracker.Instance.GetFormattedRemainingTime(player)}");
                }
            }

            ModLogger.Info($"[JAIL TRACKING] Jail sentence completed for {player.name}");
        }

        /// <summary>
        /// Send player directly to holding cell for short sentences
        /// </summary>
        private IEnumerator SendPlayerToHoldingCell(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Sending player {player.name} to holding cell for {sentence.JailTime}s");

            // Get jail system and find available holding cell
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("No active jail controller found, using fallback jail method");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Store current position before jailing
            if (_lastKnownPlayerPosition.ContainsKey(player.name))
                _lastKnownPlayerPosition[player.name] = new Vector3(14.2921f, 1.9777f, 37.8714f); // Police station exit
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

            // NEW: Start booking process instead of just waiting
            yield return StartBookingProcess(player, sentence, holdingCell);

            ModLogger.Info($"Player {player.name} has completed booking process");

            // Start actual jail time AFTER booking completion (using game time tracking)
            ModLogger.Info($"Booking complete - now starting full jail sentence of {sentence.JailTime} game minutes");
            yield return WaitForJailSentence(sentence.JailTime, player);

            // Release from holding cell
            holdingCell.ReleasePlayerFromSpawnPoint(player.name);
            holdingCell.cellDoor.UnlockDoor();

            // Use enhanced release system for time served
            SafeInitiateEnhancedRelease(player, ReleaseManager.ReleaseType.TimeServed);
        }
        
        /// <summary>
        /// Start the booking process for the player
        /// This handles the processing/booking time before sentence starts
        /// </summary>
        private IEnumerator StartBookingProcess(Player player, JailSentence sentence, CellDetail holdingCell)
        {
            ModLogger.Info($"Starting booking/processing for {player.name}");
            
            // CRITICAL: Start the actual booking process immediately
            var bookingProcess = BookingProcess.Instance;
            if (bookingProcess != null)
            {
                ModLogger.Info($"Starting BookingProcess for {player.name} with sentence: {sentence.JailTime}s, Fine: ${sentence.FineAmount}");
                bookingProcess.StartBooking(player, sentence);
                
                // Wait for booking to complete (monitored by BookingProcess)
                while (bookingProcess.IsBookingInProgress())
                {
                    yield return new WaitForSeconds(1f);
                }
                
                ModLogger.Info($"Booking process completed for {player.name}");
            }
            else
            {
                ModLogger.Error("BookingProcess.Instance is null - cannot start booking!");
                // Fallback: wait a short time then proceed
                yield return new WaitForSeconds(5f);
            }
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

            var jailController = Core.JailController;
            if (jailController == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Store current position
            if (_lastKnownPlayerPosition.ContainsKey(player.name))
                _lastKnownPlayerPosition[player.name] = new Vector3(14.2921f, 1.9777f, 37.8714f); // Police station exit
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

            // Start booking process immediately - it will handle the intake officer escort
            ModLogger.Info($"Starting booking process for {player.name} in holding cell");
            var bookingProcess = BookingProcess.Instance;
            if (bookingProcess != null)
            {
                bookingProcess.StartBooking(player, sentence);
                
                // Wait for booking to complete (monitored by BookingProcess)
                while (bookingProcess.IsBookingInProgress())
                {
                    yield return new WaitForSeconds(1f);
                }
                
                ModLogger.Info($"Booking process completed for {player.name}");
            }
            else
            {
                ModLogger.Error("BookingProcess.Instance is null - falling back to simple wait");
                yield return WaitWithControlMaintenance(5f, player);
            }

            // Release from holding cell (but don't release from jail yet)
            holdingCell.ReleasePlayerFromSpawnPoint(player.name);
            holdingCell.cellDoor.UnlockDoor();
        }

        private IEnumerator TransferToMainJailCell(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Transferring player {player.name} to main jail cell");

            var jailController = Core.JailController;
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
                        // Start tracking full sentence (processing time was separate)
                        ModLogger.Info($"Starting jail sentence tracking in holding cell: {sentence.JailTime} game minutes ({GameTimeManager.FormatGameTime(sentence.JailTime)})");
                        yield return WaitForJailSentence(sentence.JailTime, player);
                        holdingCell.ReleasePlayerFromSpawnPoint(player.name);
                        holdingCell.cellDoor.UnlockDoor();
                        // Use enhanced release system for time served
                        SafeInitiateEnhancedRelease(player, ReleaseManager.ReleaseType.TimeServed);
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

            ModLogger.Info($"Player {player.name} transferred to main cell {mainCell.cellName} for {sentence.JailTime} game minutes ({GameTimeManager.FormatGameTime(sentence.JailTime)})");

            // Wait for full sentence time (processing time was separate, already waited)
            yield return WaitForJailSentence(sentence.JailTime, player);

            ModLogger.Info($"Player {player.name} has served their main cell time");

            // Release from main cell
            mainCell.ReleasePlayerFromSpawnPoint(player.name);
            mainCell.cellDoor.UnlockDoor();

            // Use enhanced release system for time served
            SafeInitiateEnhancedRelease(player, ReleaseManager.ReleaseType.TimeServed);
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
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
            PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
            Singleton<BlackOverlay>.Instance.Open(2f);

            ModLogger.Info($"Player {player.name} using fallback jail method (screen blackout) for {sentence.JailTime}s");

            yield return WaitForJailSentence(sentence.JailTime, player);

            ModLogger.Info($"Player {player.name} has served their jail time (fallback method)");
            // Use enhanced release system for time served
            SafeInitiateEnhancedRelease(player, ReleaseManager.ReleaseType.TimeServed);
        }

        /// <summary>
        /// New enhanced release method that integrates with ReleaseManager
        /// </summary>
        public void InitiateEnhancedRelease(Player player, ReleaseManager.ReleaseType releaseType, float bailAmount = 0f)
        {
            if (player == null)
            {
                ModLogger.Error("Cannot initiate release for null player");
                return;
            }

            try
            {
                ModLogger.Info($"Initiating enhanced {releaseType} release for {player.name}");

                // DON'T create inventory snapshot during release - should have been done during arrest
                // CreateInventorySnapshotIfNeeded(player); // MOVED TO ARREST PROCESS

                // Store exit position
                StorePlayerExitPosition(player);

                // Use ReleaseManager for coordinated release
                if (ReleaseManager.Instance != null)
                {
                    string reason = releaseType switch
                    {
                        ReleaseManager.ReleaseType.TimeServed => "Time served",
                        ReleaseManager.ReleaseType.BailPayment => $"Bail paid: ${bailAmount:F0}",
                        ReleaseManager.ReleaseType.CourtOrder => "Court order",
                        ReleaseManager.ReleaseType.Emergency => "Emergency release",
                        _ => "Release ordered"
                    };

                    bool releaseStarted = ReleaseManager.Instance.InitiateRelease(player, releaseType, bailAmount, reason);
                    if (releaseStarted)
                    {
                        ModLogger.Info($"Enhanced release started for {player.name}");
                    }
                    else
                    {
                        ModLogger.Warn($"Failed to start enhanced release for {player.name} - falling back to direct release");
                        ReleasePlayerFromJail(player);
                    }
                }
                else
                {
                    ModLogger.Warn("ReleaseManager not available - using legacy release");
                    ReleasePlayerFromJail(player);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initiating enhanced release: {ex.Message}");
                // Fallback to legacy release
                ReleasePlayerFromJail(player);
            }
        }

        /// <summary>
        /// Safely initiate enhanced release, checking for existing releases first
        /// </summary>
        private void SafeInitiateEnhancedRelease(Player player, ReleaseManager.ReleaseType releaseType)
        {
            var releaseManager = ReleaseManager.Instance;
            if (releaseManager != null && releaseManager.IsReleaseInProgress(player))
            {
                ModLogger.Info($"Player {player.name} release skipped - release already in progress (early release system handling it)");
                // Don't trigger another release - early release system is handling it
            }
            else
            {
                ModLogger.Info($"Initiating {releaseType} release for {player.name}");
                InitiateEnhancedRelease(player, releaseType);
            }
        }

        /// <summary>
        /// Start jail time after booking process completes
        /// </summary>
        public IEnumerator StartJailTimeAfterBooking(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Starting jail time for {player.name} after booking completion - {sentence.JailTime}s");

            // Wait for the jail time with control maintenance
            yield return WaitForJailSentence(sentence.JailTime, player);

            // After jail time completes, safely trigger release (checks for existing releases)
            SafeInitiateEnhancedRelease(player, ReleaseManager.ReleaseType.TimeServed);
        }

        /// <summary>
        /// Create inventory snapshot for persistent storage
        /// </summary>
        private void CreateInventorySnapshotIfNeeded(Player player)
        {
            try
            {
                var persistentData = PersistentPlayerData.Instance;
                if (persistentData != null)
                {
                    string arrestId = persistentData.CreateInventorySnapshot(player);
                    if (!string.IsNullOrEmpty(arrestId))
                    {
                        ModLogger.Info($"Created inventory snapshot for {player.name} (ID: {arrestId})");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating inventory snapshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Store player's current position as exit position
        /// </summary>
        private void StorePlayerExitPosition(Player player)
        {
            try
            {
                // Always use the police station exit coordinates
                Vector3 exitPosition = new Vector3(14.2921f, 1.9777f, 37.8714f);
                ModLogger.Info($"Storing police station exit position for {player.name}: {exitPosition}");

                // Store in persistent data for cross-session support
                var persistentData = PersistentPlayerData.Instance;
                if (persistentData != null)
                {
                    persistentData.StorePlayerExitPosition(player.name, exitPosition);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error storing exit position: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear player's jail status (called by ReleaseManager after release completion)
        /// </summary>
        public void ClearPlayerJailStatus(Player player)
        {
            try
            {
                ModLogger.Info($"Clearing jail status for {player.name}");

                // Clear stored exit position
                if (_lastKnownPlayerPosition.ContainsKey(player.name))
                {
                    _lastKnownPlayerPosition.Remove(player.name);
                }

                // Update UI
                try
                {
                    BehindBarsUIManager.Instance?.DestroyJailInfoUI();
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Error clearing jail UI: {ex.Message}");
                }

                ModLogger.Info($"Jail status cleared for {player.name}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error clearing jail status: {ex.Message}");
            }
        }

        /// <summary>
        /// Legacy release method - still used as fallback
        /// </summary>
        private void ReleasePlayerFromJail(Player player)
        {
            ModLogger.Info($"Releasing player {player.name} from jail");

            // DON'T teleport immediately - let player collect belongings first
            // Keep jail exit position for after pickup
            // if (_lastKnownPlayerPosition.ContainsKey(player.name))
            // {
            //     player.transform.position = _lastKnownPlayerPosition[player.name];
            //     _lastKnownPlayerPosition.Remove(player.name);
            // }

            // Reset arrest state FIRST - this is critical for interaction to work
            player.IsArrested = false;
            ModLogger.Info("Player arrest state cleared");

            // Reset the arrest handling flag BEFORE any other operations
            Behind_Bars.Harmony.HarmonyPatches.ResetArrestHandlingFlag();
            ModLogger.Info("Harmony arrest handling flag reset");

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

            // Hide the jail info UI
            try
            {
                BehindBarsUIManager.Instance.DestroyJailInfoUI();
                ModLogger.Debug("Jail info UI hidden on player release");
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Could not hide jail info UI: {ex.Message}");
            }

            // Clear crimes from both native and enhanced systems (player has served their time)
            if (player.CrimeData != null)
            {
                player.CrimeData.ClearCrimes();
                ModLogger.Info("Cleared crimes from native system - player has served sentence");
            }

            // Also clear crimes from our enhanced crime detection system
            var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
            if (crimeDetectionSystem != null)
            {
                crimeDetectionSystem.ClearAllCrimes();
                ModLogger.Info("Cleared crimes from enhanced system - player has served sentence");
            }

            // Now call the game's native Player.Free() to properly restore all systems
            try
            {
                player.Free_Server();
                player.Free_Client();
                ModLogger.Info("Called Player.Free() to restore all systems");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error calling Player.Free(): {ex.Message}");

                // Fallback: manually restore player state
                SetPlayerJailState(player, false);
                ModLogger.Info("Used fallback player state restoration");
            }

            // Force enable all interaction systems explicitly
            try
            {
                // Enable player combat system - PlayerCombat class not found, skipping
                // if (PlayerSingleton<PlayerCombat>.Instance != null)
                // {
                //     PlayerSingleton<PlayerCombat>.Instance.enabled = true;
                //     ModLogger.Debug("Re-enabled PlayerCombat");
                // }

// Ensure interaction system is enabled - PlayerInteraction class not found, skipping
// if (PlayerSingleton<PlayerInteraction>.Instance != null)
// {
//     PlayerSingleton<PlayerInteraction>.Instance.enabled = true;
//     ModLogger.Debug("Re-enabled PlayerInteraction");
// }

// Make sure movement is fully enabled
#if MONO
                PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
                PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
                PlayerSingleton<PlayerMovement>.Instance.enabled = true;
                ModLogger.Debug("Re-enabled PlayerMovement");

                // DON'T unlock inventory immediately - enable pickup station instead
                // InventoryProcessor.UnlockPlayerInventory(player);
                // PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                // PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                // ModLogger.Debug("Re-enabled PlayerInventory");

                // Enable the inventory pickup station for item retrieval
                if (_inventoryPickupStation != null)
                {
                    _inventoryPickupStation.EnableForRelease(player);
                    ModLogger.Info("Enabled InventoryPickupStation for player to collect belongings");
                }
                else
                {
                    ModLogger.Warn("InventoryPickupStation reference not found - falling back to immediate inventory unlock");
                    InventoryProcessor.UnlockPlayerInventory(player);
                    PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                    PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                    PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                }

                // Enable camera controls
                PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
                PlayerSingleton<PlayerCamera>.Instance.enabled = true;
                ModLogger.Debug("Re-enabled PlayerCamera");

            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error force-enabling player systems: {ex.Message}");
            }

            ModLogger.Info($"Player {player.name} released from jail successfully - all controls and interactions restored");
        }

        /// <summary>
        /// Show the jail info UI with crime details
        /// </summary>
        private void ShowJailInfoUI(JailSentence sentence, Player player)
        {
            try
            {
                // Get crime details for display
                string crimeInfo = GetCrimeDescription(sentence.Severity, player);
                string timeInfo = FormatJailTime(sentence.JailTime);

                // Calculate proper bail amount (much higher than fine for serious crimes)
                float bailAmount = CalculateBailAmount(sentence.FineAmount, sentence.Severity);
                string bailInfo = FormatBailAmount(bailAmount);

                // Show the UI using the BehindBarsUIManager WITHOUT starting timer (timer starts after booking)
                BehindBarsUIManager.Instance.ShowJailInfoUI(
                    crimeInfo,
                    timeInfo,
                    bailInfo,
                    0f,  // Don't start timer yet - timer starts after booking completion
                    bailAmount // Pass bail amount for display
                );

                ModLogger.Info($"Jail info UI displayed with dynamic updates: Crime={crimeInfo}, Time={timeInfo}, Bail={bailInfo}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error showing jail info UI: {e.Message}");
            }
        }

        /// <summary>
        /// Get a user-friendly description of the crimes committed
        /// ENHANCED: Now includes crimes from our enhanced detection system
        /// </summary>
        private string GetCrimeDescription(JailSeverity severity, Player player)
        {
            var allCrimes = new System.Collections.Generic.List<string>();

            // First, get crimes from our enhanced crime detection system
            var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
            if (crimeDetectionSystem != null)
            {
                var crimeSummary = crimeDetectionSystem.GetCrimeSummary();
                foreach (var crimeEntry in crimeSummary)
                {
                    string crimeName = crimeEntry.Key;
                    int count = crimeEntry.Value;

                    if (count > 1)
                        allCrimes.Add($"{crimeName} ({count}x)");
                    else
                        allCrimes.Add(crimeName);
                }
            }

            // Then add crimes from Schedule I's native system
            if (player?.CrimeData?.Crimes != null && player.CrimeData.Crimes.Count > 0)
            {
                foreach (var crimeEntry in player.CrimeData.Crimes)
                {
                    var crime = crimeEntry.Key;
                    int count = crimeEntry.Value;
                    string crimeName = GetFriendlyCrimeName(crime.GetType().Name);

                    if (count > 1)
                        allCrimes.Add($"{crimeName} ({count}x)");
                    else
                        allCrimes.Add(crimeName);
                }
            }

            if (allCrimes.Count > 0)
                return string.Join(", ", allCrimes.ToArray());

            // Fallback to severity-based descriptions
            switch (severity)
            {
                case JailSeverity.Minor: return "Minor Infractions";
                case JailSeverity.Moderate: return "Moderate Offenses";
                case JailSeverity.Major: return "Serious Crimes";
                case JailSeverity.Severe: return "Major Criminal Activity";
                default: return "Unknown Charges";
            }
        }

        /// <summary>
        /// Convert technical crime names to user-friendly ones
        /// </summary>
        private string GetFriendlyCrimeName(string technicalName)
        {
            switch (technicalName)
            {
                // Original crimes
                case "Trespassing": return "Trespassing";
                case "Theft": return "Theft";
                case "Assault": return "Assault";
                case "DeadlyAssault": return "Assault with a Deadly Weapon";
                case "Burglary": return "Burglary";
                case "VehicleTheft": return "Vehicle Theft";
                case "VehicularAssault": return "Vehicular Assault";
                case "DrugPossession": return "Drug Possession";
                case "DrugTrafficking": return "Drug Trafficking";
                case "PublicIntoxication": return "Public Intoxication";
                case "DisturbingPeace": return "Disturbing the Peace";
                case "Speeding": return "Speeding";
                case "RecklessDriving": return "Reckless Driving";
                case "HitAndRun": return "Hit and Run";
                case "Vandalism": return "Vandalism";
                case "BrandishingWeapon": return "Brandishing a Weapon";
                case "DischargeFirearm": return "Illegal Discharge of Firearm";
                case "ViolatingCurfew": return "Curfew Violation";
                case "Evading": return "Evading Arrest";
                case "FailureToComply": return "Failure to Comply";
                case "AttemptingToSell": return "Attempted Sale of Controlled Substances";

                // NEW ENHANCED CRIME TYPES
                case "Murder": return "Murder";
                case "Manslaughter": return "Involuntary Manslaughter";
                case "AssaultOnCivilian": return "Assault on Civilian";
                case "WitnessIntimidation": return "Witness Intimidation";

                // Drug possession crimes
                case "PossessingControlledSubstances": return "Possession of Controlled Substances";
                case "PossessingLowSeverityDrug": return "Possession of Controlled Substances";
                case "PossessingModerateSeverityDrug": return "Possession of Illegal Drugs";
                case "PossessingHighSeverityDrug": return "Possession of High-Grade Narcotics";

                default: return technicalName.Replace("Crime", "").Replace("Data", "");
            }
        }

        /// <summary>
        /// Format jail time in a user-friendly way (now uses game time)
        /// </summary>
        private string FormatJailTime(float timeInGameMinutes)
        {
            // Use GameTimeManager to format game time
            return GameTimeManager.FormatGameTime(timeInGameMinutes);
        }

        /// <summary>
        /// Calculate bail amount based on fine amount and crime severity
        /// Bail should be significantly higher than the fine for serious crimes
        /// </summary>
        public float CalculateBailAmount(float fineAmount, JailSeverity severity)
        {
            if (fineAmount <= 0)
                return 0f;

            // Base bail multiplier starts at 3x the fine amount
            float bailMultiplier = 3.0f;

            // Adjust multiplier based on severity
            switch (severity)
            {
                case JailSeverity.Minor:
                    bailMultiplier = 2.0f; // 2x fine for minor crimes
                    break;
                case JailSeverity.Moderate:
                    bailMultiplier = 4.0f; // 4x fine for moderate crimes
                    break;
                case JailSeverity.Major:
                    bailMultiplier = 7.0f; // 7x fine for major crimes (murder, etc.)
                    break;
                case JailSeverity.Severe:
                    bailMultiplier = 12.0f; // 12x fine for severe crimes (multiple murders)
                    break;
            }

            // Also get additional crimes from our enhanced detection system
            var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
            if (crimeDetectionSystem != null)
            {
                var crimeSummary = crimeDetectionSystem.GetCrimeSummary();

                // Add extra multiplier for murder charges specifically
                if (crimeSummary.ContainsKey("Murder"))
                {
                    int murderCount = crimeSummary["Murder"];
                    bailMultiplier += murderCount * 5.0f; // +5x multiplier per murder
                    ModLogger.Info($"Adding murder bail multiplier: {murderCount} murders = +{murderCount * 5.0f}x multiplier");
                }

                // Add multiplier for witness intimidation (very serious)
                if (crimeSummary.ContainsKey("Witness Intimidation"))
                {
                    int intimidationCount = crimeSummary["Witness Intimidation"];
                    bailMultiplier += intimidationCount * 3.0f; // +3x multiplier per intimidation
                }
            }

            float calculatedBail = fineAmount * bailMultiplier;

            ModLogger.Info($"Calculated bail: ${calculatedBail:F0} (Fine: ${fineAmount:F0} x {bailMultiplier:F1} multiplier)");

            return calculatedBail;
        }

        private string FormatBailAmount(float amount)
        {
            if (amount <= 0)
                return "No Bail";
            else
                return $"${amount:F0}";
        }

        /// <summary>
        /// Reset all jail/booking/release state for a player before new arrest
        /// </summary>
        private void ResetPlayerJailState(Player player)
        {
            try
            {
                ModLogger.Info($"Resetting jail state for {player.name} before new arrest");

                // 1. Clear any active booking process
                // Note: BookingProcess handles its own cleanup when player is arrested
                var bookingProcess = UnityEngine.Object.FindObjectOfType<BookingProcess>();
                if (bookingProcess != null)
                {
                    ModLogger.Info("BookingProcess found - it will handle its own cleanup");
                }

                // 2. Clear any active release process
                var releaseManager = ReleaseManager.Instance;
                if (releaseManager != null)
                {
                    releaseManager.CancelPlayerRelease(player);
                    ModLogger.Info("Cancelled any active release process");
                }

                // 3. Clear escort registrations
                var officerCoordinator = OfficerCoordinator.Instance;
                if (officerCoordinator != null)
                {
                    officerCoordinator.UnregisterAllEscortsForPlayer(player);
                    ModLogger.Info("Cleared all escort registrations");
                }

                // 4. Reset station states
                ResetStationStates(player);

                ModLogger.Info($"Jail state reset completed for {player.name}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error resetting jail state for {player.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset all interaction stations to clean state
        /// </summary>
        private void ResetStationStates(Player player)
        {
            try
            {
                // Reset exit scanner station - most important for preventing "Already completed" issues
                var exitScannerStation = UnityEngine.Object.FindObjectOfType<ExitScannerStation>();
                if (exitScannerStation != null)
                {
                    // Reset completion flags
                    var completedField = exitScannerStation.GetType().GetField("isCompleted",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (completedField != null)
                    {
                        completedField.SetValue(exitScannerStation, false);
                        ModLogger.Info("Reset ExitScannerStation completion flag");
                    }
                }

                // Re-enable jail inventory pickup stations for new inmate
                var jailInventoryStations = UnityEngine.Object.FindObjectsOfType<JailInventoryPickupStation>();
                foreach (var station in jailInventoryStations)
                {
                    station.gameObject.SetActive(true);
                    // Reset the items taken flag to re-enable prefabs
                    var takenField = station.GetType().GetField("itemsCurrentlyTaken",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (takenField != null)
                    {
                        takenField.SetValue(station, false);
                    }
                }

                // Re-enable inventory pickup stations
                var inventoryPickupStations = UnityEngine.Object.FindObjectsOfType<InventoryPickupStation>();
                foreach (var station in inventoryPickupStations)
                {
                    station.gameObject.SetActive(true);
                }

                ModLogger.Info("Station states reset successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error resetting station states: {ex.Message}");
            }
        }
    }
}
