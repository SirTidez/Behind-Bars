using System;
using System.Collections;
using System.Collections.Generic;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Harmony;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.Data;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems;
using UnityEngine;
using MelonLoader;
using BBHelpers = Behind_Bars.Helpers.Helpers;


#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.AvatarFramework;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems
{
    public class JailSystem
    {
        private const float MIN_JAIL_TIME = Constants.DEFAULT_MIN_JAIL_TIME;
        private const float MAX_JAIL_TIME = Constants.DEFAULT_MAX_JAIL_TIME;
        private const float PendingParoleArrestCauseLifetimeSeconds = 120f;

        // Native arrest callbacks can run before the saved RapSheet violation collection
        // reflects a search result. This runtime-only state carries the already-known
        // custody cause across that narrow boundary. It is keyed by stable player ID and
        // explicitly cleared on scene shutdown.
        private sealed class PendingParoleArrestCause
        {
            public ViolationType ViolationType;
            public DateTime ExpiresAtUtc;
        }

        private readonly Dictionary<string, PendingParoleArrestCause> pendingParoleArrestCauses =
            new Dictionary<string, PendingParoleArrestCause>();

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
            if (player == null || !Core.IsGameplaySceneActive)
            {
                yield break;
            }

            ModLogger.Info($"Processing IMMEDIATE arrest for player: {player.name}");

            // Mark jail status immediately so delayed witness calls can be suppressed reliably.
            Core.Instance?.JailManager?.MarkPlayerInJail(player);

            // CRITICAL: Check if player is on parole and record violation BEFORE any other processing
            RecordParoleViolationIfNeeded(player);

            // CRITICAL: Reset all previous jail/booking/release state before starting new arrest
            ResetPlayerJailState(player);
            
            // CRITICAL: Sync crimes from player.CrimeData.Crimes to CrimeDetectionSystem BEFORE clearing
            // This ensures crimes are tracked even if they were added by the game's native system
            
            //TODO: Commented out crime syncting for now, should be handled by Harmony patch instead
            /*try
            {
                var crimeDetectionSystem = CrimeDetectionSystem.Instance;
                if (crimeDetectionSystem != null && player.CrimeData != null && player.CrimeData.Crimes != null)
                {
                    int syncedCount = 0;
                    
                    foreach (var crimeEntry in player.CrimeData.Crimes)
                    {
                        if (crimeEntry.Key != null)
                        {
                            var crime = crimeEntry.Key;
                            int count = crimeEntry.Value;
                            
                            // Add each crime instance to CrimeDetectionSystem
                            for (int i = 0; i < count; i++)
                            {
                                var crimeInstance = new CrimeTracking.CrimeInstance(
                                    crime: crime,
                                    location: player.transform.position,
                                    severity: CalculateCrimeSeverityForSync(crime)
                                );
                                crimeDetectionSystem.CrimeRecord.AddCrime(crimeInstance);
                                syncedCount++;
                            }
                        }
                    }
                    
                    if (syncedCount > 0)
                    {
                        ModLogger.Info($"[CRIME SYNC] Synced {syncedCount} crimes from player.CrimeData.Crimes to CrimeDetectionSystem");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[CRIME SYNC] Error syncing crimes: {ex.Message}");
            }*/
            
            // Capture native crimes before clearing them. The Harmony rap-sheet path merges
            // this stable arrest snapshot with enhanced crimes and deduplicates the event.
            try
            {
                ModLogger.Info($"[RAP SHEET] Logging arrest to rap sheet for {player.name}");
                
                // DEBUG: Log CrimeData state BEFORE processing
                if (player.CrimeData != null)
                {
                    ModLogger.Info($"[RAP SHEET] [DEBUG] CrimeData is not null");
                    if (player.CrimeData.Crimes != null)
                    {
                        ModLogger.Info($"[RAP SHEET] [DEBUG] CrimeData.Crimes is not null, Count: {player.CrimeData.Crimes.Count}");
                        if (player.CrimeData.Crimes.Count > 0)
                        {
                            foreach (var crimeEntry in player.CrimeData.Crimes)
                            {
                                ModLogger.Info($"[RAP SHEET] [DEBUG] Crime in CrimeData: {crimeEntry.Key?.CrimeName ?? "NULL"} (Value: {crimeEntry.Value})");
                            }
                        }
                    }
                    else
                    {
                        ModLogger.Warn($"[RAP SHEET] [DEBUG] CrimeData.Crimes is NULL!");
                    }
                }
                else
                {
                    ModLogger.Warn($"[RAP SHEET] [DEBUG] CrimeData is NULL!");
                }
                var nativeCrimeSnapshots = Behind_Bars.Harmony.HarmonyPatches.CaptureNativeCrimesForArrest(player);
                Behind_Bars.Harmony.HarmonyPatches.LogCrimesToRapSheet(player, nativeCrimeSnapshots);
                player.CrimeData.ClearCrimes();
                CrimeDetectionSystem.Instance.CrimeRecord.ClearWantedLevel();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[RAP SHEET] Error logging to rap sheet: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
            
            player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.None);
            
            // Hide parole status UI - player is going to jail, not staying on parole
            Core.ResolveUIManager().HideParoleStatus();
            ModLogger.Info($"Hid parole status UI for {player.name} - entering jail");

            // Inventory capture now handled in Harmony patch before any clearing happens
            // CreateInventorySnapshotIfNeeded(player); // MOVED TO HARMONY PATCH

            // Immediately take control of player state to prevent game systems from interfering
            SetPlayerJailState(player, true);

            // Show "Busted" effect like the original game
            yield return ShowBustedEffect();
            if (!Core.IsGameplaySceneActive)
            {
                yield break;
            }
            
            // Restore UI interactions so player can interact during booking process
            Behind_Bars.Harmony.HarmonyPatches.RestoreUIInteractions();

            // Assess the crime severity
            var sentence = AssessCrimeSeverity(player);

            ModLogger.Info($"Crime assessment: {sentence.Severity}, Time: {sentence.JailTime}s, Fine: ${sentence.FineAmount}");

            // Inventory capture already done at start of arrest process
            // CreateInventorySnapshotIfNeeded(player); // REMOVED - now done at start of HandleImmediateArrest

            yield return ProcessPlayerToJail(player, sentence);
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
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
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
                    PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
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
            // Calculate severity based on actual crime charges, not fine amounts
            var player = Player.Local;
            if (player == null) return JailSeverity.Moderate;

            // Get crimes from both enhanced detection system and native system
            var allCrimeTypes = new System.Collections.Generic.HashSet<string>();
            
            // Get crimes from enhanced detection system
            var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
            if (crimeDetectionSystem != null)
            {
                var crimeSummary = crimeDetectionSystem.GetCrimeSummary();
                foreach (var crimeEntry in crimeSummary)
                {
                    allCrimeTypes.Add(crimeEntry.Key);
                }
            }

            // Get crimes from native system
            if (player.CrimeData?.Crimes != null)
            {
                foreach (var crimeEntry in player.CrimeData.Crimes)
                {
                    if (crimeEntry.Key != null)
                    {
                        string crimeName = crimeEntry.Key.GetType().Name;
                        allCrimeTypes.Add(crimeName);
                    }
                }
            }

            // Determine severity based on most serious crime present
            // Check for severe crimes first
            foreach (var crimeType in allCrimeTypes)
            {
                if (crimeType == "Murder" || crimeType == "Manslaughter")
                {
                    return JailSeverity.Severe;
                }
            }

            // Check for major crimes
            foreach (var crimeType in allCrimeTypes)
            {
                if (crimeType == "DeadlyAssault" || crimeType == "AssaultOnOfficer" || 
                    crimeType == "Burglary" || crimeType == "DrugTrafficking" || 
                    crimeType == "DrugTraffickingCrime" || crimeType == "WitnessIntimidation")
                {
                    return JailSeverity.Major;
                }
            }

            // Check for moderate crimes
            foreach (var crimeType in allCrimeTypes)
            {
                if (crimeType == "Theft" || crimeType == "VehicleTheft" || 
                    crimeType == "Assault" || crimeType == "AssaultOnCivilian" || 
                    crimeType == "VehicularAssault" || crimeType == "HitAndRun" ||
                    crimeType == "Evading" || crimeType == "EvadingArrest" ||
                    crimeType == "FailureToComply")
                {
                    return JailSeverity.Moderate;
                }
            }

            // Default to minor for traffic violations and small infractions
            return JailSeverity.Minor;
        }

        /// <summary>
        /// Calculate total fines using FineCalculator (independent from sentences)
        /// </summary>
        private float CalculateTotalCrimeFines(Player player)
        {
            // Get RapSheet for repeat offender multiplier
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            
            // Use FineCalculator to calculate fines (rapSheet is optional)
            float totalFine = FineCalculator.Instance.CalculateTotalFine(player, rapSheet);
            
            ModLogger.Info($"Calculated total fines using FineCalculator: ${totalFine:F2}");
            return totalFine;
        }

        /// <summary>
        /// Manager-facing compatibility seam for calculating total crime fines while the jail
        /// sentence calculation logic still lives in <see cref="JailSystem"/>.
        /// </summary>
        internal float CalculateTotalCrimeFinesForManager(Player player)
        {
            return CalculateTotalCrimeFines(player);
        }


        private void CalculateSentence(JailSentence sentence, Player player)
        {
            // Get RapSheet for sentence calculation
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
            
            // Check if player was on parole when arrested (for sentence multiplier)
            bool wasOnParole = false;
            if (rapSheet?.CurrentParoleRecord != null)
            {
                wasOnParole = rapSheet.CurrentParoleRecord.IsOnParole();
                ModLogger.Info($"[SENTENCE CALC] Player was on parole at time of arrest: {wasOnParole}");
            }
            
            // Calculate fine using FineCalculator (independent from sentence)
            float actualFine = CalculateTotalCrimeFines(player);
            sentence.FineAmount = actualFine;

            // Calculate sentence using CrimeSentenceCalculator (in game minutes)
            // Pass parole status so it can apply appropriate multiplier
            var sentenceData = CrimeSentenceCalculator.Instance.CalculateSentence(player, rapSheet, wasOnParole);
            
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
            ModLogger.Info($"[SENTENCE CALC] Base: {sentenceData.BaseSentenceMinutes}, Severity: {sentenceData.SeverityMultiplier}, Repeat: {sentenceData.RepeatOffenderMultiplier}, Witness: {sentenceData.WitnessMultiplier}, Parole: {sentenceData.ParoleViolationMultiplier}, Global: {sentenceData.GlobalMultiplier}");
        }

        /// <summary>
        /// Calculate crime severity for syncing crimes from player.CrimeData.Crimes
        /// Uses the same logic as CrimeSentenceCalculator.CalculateCrimeSeverity
        /// </summary>
        private float CalculateCrimeSeverityForSync(Crime crime)
        {
            if (crime == null)
                return 1.5f; // Default moderate severity
            
            string crimeName = crime.GetType().Name;

            return crimeName switch
            {
                // Minor crimes
                "Speeding" or "Trespassing" or "DisturbingPeace" => 1.0f,
                "Vandalism" or "PublicIntoxication" or "DrugPossessionLow" => 1.0f,
                "RecklessDriving" or "DischargeFirearm" => 1.5f,

                // Moderate crimes
                "Theft" or "Assault" => 1.5f,
                "VehicleTheft" or "AssaultOnCivilian" => 2.0f,
                "HitAndRun" => 2.5f,

                // Major crimes
                "DeadlyAssault" or "Burglary" => 3.0f,
                "AssaultOnOfficer" or "WitnessIntimidation" => 3.5f,
                "DrugTraffickingCrime" => 4.0f,

                // Severe crimes
                "Manslaughter" => 4.0f,
                "Murder" => 4.0f,

                _ => 1.5f // Default moderate severity
            };
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
            ModLogger.Debug("Initializing JailSystem components");

            // Find the inventory pickup station
            _inventoryPickupStation = BBHelpers.FindObjectOfTypeSafe<InventoryPickupStation>();
            if (_inventoryPickupStation != null)
            {
                ModLogger.Debug("Found existing InventoryPickupStation reference");
            }
            else
            {
                ModLogger.Debug("InventoryPickupStation not found - creating one now");
                CreateInventoryPickupStation();

                // Verify it was created
                _inventoryPickupStation = BBHelpers.FindObjectOfTypeSafe<InventoryPickupStation>();
                if (_inventoryPickupStation != null)
                {
                    ModLogger.Debug("InventoryPickupStation successfully created and found");
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
        public Vector3? GetPlayerExitPosition(Player player)
        {
            if (player == null)
            {
                return null;
            }

            return GetStoredExitPositionByKey(GetPlayerStateKey(player));
        }

        private Vector3? GetStoredExitPositionByKey(string playerKey)
        {
            if (string.IsNullOrEmpty(playerKey))
            {
                return null;
            }

            if (_lastKnownPlayerPosition.ContainsKey(playerKey))
            {
                var position = _lastKnownPlayerPosition[playerKey];
                _lastKnownPlayerPosition.Remove(playerKey); // Remove after use
                return position;
            }

            return null;
        }

        private string GetPlayerStateKey(Player player)
        {
            return Core.ResolvePlayerKey(player);
        }

        /// <summary>
        /// Create an InventoryPickupStation for the jail
        /// </summary>
        private void CreateInventoryPickupStation()
        {
            try
            {
                var jailController = Core.JailController;
                GameObject stationObject = null;
                Vector3 stationPosition = new Vector3(0, 1, 0); // Default position

                if (jailController?.storage?.inventoryPickup != null)
                {
                    stationObject = jailController.storage.inventoryPickup.gameObject;
                    stationPosition = jailController.storage.inventoryPickup.position;
                    ModLogger.Debug($"Attaching InventoryPickupStation to storage.inventoryPickup at {stationPosition}");
                }
                else if (jailController?.storage?.inventoryDropOff != null)
                {
                    stationPosition = jailController.storage.inventoryDropOff.position;
                    ModLogger.Warn($"storage.inventoryPickup not found - falling back to inventoryDropOff position {stationPosition}");
                }
                else if (jailController?.booking?.guardSpawns != null && jailController.booking.guardSpawns.Count > 0)
                {
                    var bookingArea = jailController.booking.guardSpawns[0];
                    stationPosition = bookingArea.position + new Vector3(2, 0, 0);
                    ModLogger.Warn("Storage inventoryDropOff not found - using booking area fallback");
                }

                if (stationObject == null)
                {
                    stationObject = new GameObject("InventoryPickupStation");
                    stationObject.transform.position = stationPosition;
                }

                _inventoryPickupStation = BBHelpers.AddComponentSafe<InventoryPickupStation>(stationObject);

                ModLogger.Debug($"Created InventoryPickupStation at position {stationPosition}");
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
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                jailManager.ApplyCustodyState(player, inJail);
                return;
            }

            ModLogger.Warn("JailManager unavailable while applying custody state");
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
                if (!Core.IsGameplaySceneActive)
                {
                    yield break;
                }
                elapsed += timeToWait;

                // Ensure controls are still enabled
                try
                {
                    Core.Instance?.JailManager?.MaintainCustodyControls();
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
        internal IEnumerator WaitForJailSentence(float sentenceGameMinutes, Player player)
        {
            ModLogger.Info($"[JAIL TRACKING] Starting jail sentence tracking for {player.name}: {sentenceGameMinutes} game minutes ({GameTimeManager.FormatGameTime(sentenceGameMinutes)})");

            if (sentenceGameMinutes <= 0)
            {
                ModLogger.Warn($"[JAIL TRACKING] Invalid sentence time: {sentenceGameMinutes} game minutes - completing immediately");
                yield break;
            }

            // Get bail amount from Core BailSystem (where it was stored during booking)
            float bailAmount = 0f;
            var jailManager = Core.Instance?.JailManager;
            var bailSystem = Core.ResolveBailSystem();

            if (jailManager != null)
            {
                bailAmount = jailManager.ResolveBailAmount(player);
            }
            else if (bailSystem != null)
            {
                // First try to get the stored bail amount (set during booking)
                bailAmount = bailSystem.GetBailAmount(player);
                
                // If no bail was stored, calculate it now (fallback for direct jail entry)
                if (bailAmount <= 0)
                {
                    float fineAmount = CalculateTotalCrimeFines(player);
                    if (fineAmount > 0)
                    {
                        var bailOffer = bailSystem.CalculateBailAmount(player, fineAmount);
                        bailAmount = bailOffer.Amount;
                        bailSystem.StoreBailAmount(player, bailAmount);
                        ModLogger.Info($"[BAIL] Calculated bail amount: ${bailAmount:F0} for {player.name} (based on fine: ${fineAmount:F0})");
                    }
                }
                else
                {
                    ModLogger.Info($"[BAIL] Retrieved stored bail amount: ${bailAmount:F0} for {player.name}");
                }
            }
            else
            {
                ModLogger.Warn("[BAIL] BailSystem not available - bail payment will not work");
            }

            bool sentenceComplete = false;
            bool bailPaid = false;
            System.Action<Player> onComplete = (p) => 
            { 
                sentenceComplete = true;
                ModLogger.Info($"[JAIL TRACKING] Sentence completion callback triggered for {p.name}");
            };

            // Start tracking with JailTimeTracker
            if (jailManager != null)
            {
                jailManager.StartSentenceTracking(player, sentenceGameMinutes, onComplete);
            }
            else
            {
                Core.ResolveJailTimeTracker().StartTracking(player, sentenceGameMinutes, onComplete);
            }

            // Update jail status UI with the correct bail amount (ensure consistency)
            var jailStatusUIWrapper = Core.ResolveUIManager().GetUIWrapper();
            if (jailStatusUIWrapper != null && bailAmount > 0)
            {
                // Update the bail amount in the jail status UI to match the payment amount
                jailStatusUIWrapper.UpdateBailAmount(bailAmount);
                ModLogger.Info($"[BAIL] Updated jail status UI bail amount to ${bailAmount:F0}");
            }

            // Bail cannot begin until the intake officer has returned to post.  This keeps a
            // fast bailout from assigning the release officer while intake still owns the cell
            // handoff and door state.
            bool bailReleaseReady = jailManager?.IsBailReleaseReady(player) == true;

            // Show bail UI only when the release path is ready and the player can afford it.
            if (bailAmount > 0 && bailReleaseReady && bailSystem != null && bailSystem.CanPlayerAffordBail(player, bailAmount))
            {
                Core.ResolveUIManager().ShowBailUI(bailAmount);
                ModLogger.Info($"[BAIL] Showing bail UI for {player.name}: ${bailAmount:F0}");
            }
            else if (bailAmount > 0 && !bailReleaseReady)
            {
                Core.ResolveUIManager().HideBailUI();
                ModLogger.Info($"[BAIL] Bail controls are deferred for {player.name} until the intake officer returns to post");
            }
            else if (bailAmount > 0)
            {
                ModLogger.Info($"[BAIL] Player {player.name} cannot afford bail of ${bailAmount:F0}");
            }

            // Wait while maintaining controls and checking for completion or bail payment
            const float checkInterval = 0.1f; // Check more frequently for key presses
            float lastBailCheck = 0f;
            const float bailCheckInterval = 1f; // Check cash balance every second
            bool bailKeyWasPressed = false; // Track previous frame key state to detect key press
            int lastLoggedHour = -1; // Track last logged game hour to prevent duplicate logs
            
            ModLogger.Info($"[BAIL DEBUG] Starting bail key detection loop - checking for key {Core.BailoutKey} every {checkInterval}s");
            
            while (!sentenceComplete && !bailPaid)
            {
                yield return new WaitForSeconds(checkInterval);
                if (!Core.IsGameplaySceneActive)
                {
                    yield break;
                }

                if (jailManager != null ? jailManager.HasPendingReleaseType(player) : HasPendingReleaseType(player))
                {
                    ModLogger.Info($"[BAIL] Pending release detected for {player.name}; ending sentence wait for custody cleanup");
                    break;
                }

                // Ensure controls are still enabled
                try
                {
                    Core.Instance?.JailManager?.MaintainCustodyControls();
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Control maintenance error: {ex.Message}");
                }

                // Check for bail payment key press using flag-based detection
                // This is more reliable than GetKeyDown in coroutines that check every 0.1s
                bool bailKeyCurrentlyPressed = Input.GetKey(Core.BailoutKey);
                bool bailKeyJustPressed = bailKeyCurrentlyPressed && !bailKeyWasPressed;
                bailKeyWasPressed = bailKeyCurrentlyPressed;
                
                // Debug: Log key press detection
                if (bailKeyJustPressed)
                {
                    ModLogger.Info($"[BAIL DEBUG] Key {Core.BailoutKey} pressed! bailAmount: {bailAmount}, bailSystem: {(bailSystem != null ? "available" : "null")}");
                }
                
                bailReleaseReady = jailManager?.IsBailReleaseReady(player) == true;
                if (bailAmount > 0 && bailKeyJustPressed && !bailReleaseReady)
                {
                    ModLogger.Debug($"[BAIL] Ignoring bailout key for {player.name}; intake officer has not returned to post");
                }
                else if (bailAmount > 0 && bailKeyJustPressed && bailSystem != null)
                {
                    if (bailSystem.CanPlayerAffordBail(player, bailAmount))
                    {
                        ModLogger.Info($"[BAIL] Player {player.name} pressed bail payment key");
                        
                        // Hide bail UI immediately
                        Core.ResolveUIManager().HideBailUI();
                        
                        // Update jail status UI to show "Bailed Out"
                        var uiWrapper = Core.ResolveUIManager().GetUIWrapper();
                        if (uiWrapper != null)
                        {
                            uiWrapper.SetBailedOutStatus();
                        }
                        
                        // Stop sentence tracking (cancel time-based release)
                        if (jailManager != null)
                        {
                            jailManager.StopSentenceTracking(player);
                        }
                        else
                        {
                            Core.ResolveJailTimeTracker().StopTracking(player);
                        }
                        
                        // Process bail payment and wait for it to mark the pending bail release state.
                        yield return bailSystem.ProcessBailPayment(player, bailAmount, false);

                        if (jailManager != null ? jailManager.HasPendingReleaseType(player) : HasPendingReleaseType(player))
                        {
                            bailPaid = true;
                            ModLogger.Info($"[BAIL] Bail payment completed for {player.name}; release will proceed after custody cleanup");
                            break;
                        }

                        ModLogger.Info($"[BAIL] Bail payment initiated for {player.name}");
                    }
                    else
                    {
                        // Show notification that they can't afford bail
                        Core.ResolveUIManager().ShowNotification(
                            $"Insufficient cash for bail. Required: ${bailAmount:F0}",
                            NotificationType.Warning
                        );
                    }
                }

                // Periodically check cash balance and update UI visibility
                float currentTime = Time.time;
                if (currentTime - lastBailCheck >= bailCheckInterval)
                {
                    lastBailCheck = currentTime;
                    
                    if (bailAmount > 0 && bailSystem != null)
                    {
                        bailReleaseReady = jailManager?.IsBailReleaseReady(player) == true;
                        bool canAfford = bailSystem.CanPlayerAffordBail(player, bailAmount);
                        bool uiVisible = Core.ResolveUIManager().IsBailUIVisible();
                        
                        if (bailReleaseReady && canAfford && !uiVisible)
                        {
                            // Player gained enough cash after intake finished - show UI.
                            Core.ResolveUIManager().ShowBailUI(bailAmount);
                        }
                        else if ((!bailReleaseReady || !canAfford) && uiVisible)
                        {
                            // Bail must disappear again if release readiness is lost or cash is unavailable.
                            Core.ResolveUIManager().HideBailUI();
                        }
                    }
                }

                // Check remaining time for logging (log once per game hour, prevent duplicates)
                float remaining = jailManager != null
                    ? jailManager.GetRemainingSentenceTime(player)
                    : Core.ResolveJailTimeTracker().GetRemainingTime(player);
                if (remaining > 0)
                {
                    int currentHour = Mathf.FloorToInt(remaining / 60f); // Convert to game hours
                    if (currentHour != lastLoggedHour && remaining % 60f < 1f) // Log when crossing to a new hour
                    {
                        lastLoggedHour = currentHour;
                        string formattedRemaining = jailManager != null
                            ? jailManager.GetFormattedRemainingSentenceTime(player)
                            : Core.ResolveJailTimeTracker().GetFormattedRemainingTime(player);
                        ModLogger.Debug($"[JAIL TRACKING] Remaining: {formattedRemaining}");
                    }
                }
            }

            if (!bailPaid && (jailManager != null ? jailManager.HasPendingReleaseType(player) : HasPendingReleaseType(player)))
            {
                bailPaid = true;
            }

            // Hide bail UI if sentence completed normally
            if (sentenceComplete && !bailPaid)
            {
                Core.ResolveUIManager().HideBailUI();
            }

            ModLogger.Info($"[JAIL TRACKING] Jail sentence completed for {player.name} (bail paid: {bailPaid})");
        }

        /// <summary>
        /// Mark a pending release type for the player so custody cleanup can complete before the final release.
        /// </summary>
        public void MarkPendingReleaseType(Player player, ReleaseManager.ReleaseType releaseType)
        {
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                jailManager.MarkPendingReleaseType(player, releaseType);
                return;
            }

            if (player == null)
            {
                return;
            }

            ModLogger.Warn($"JailManager unavailable while marking pending release type {releaseType} for {player.name}");
        }

        /// <summary>
        /// Check whether the player has a pending release type waiting for custody cleanup.
        /// </summary>
        public bool HasPendingReleaseType(Player player)
        {
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                return jailManager.HasPendingReleaseType(player);
            }

            return false;
        }

        /// <summary>
        /// Consume the pending release type for the player, defaulting to time served when no pending bail exists.
        /// </summary>
        public ReleaseManager.ReleaseType ConsumePendingReleaseType(Player player)
        {
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                return jailManager.ConsumePendingReleaseType(player);
            }

            return ReleaseManager.ReleaseType.TimeServed;
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
            string playerStateKey = GetPlayerStateKey(player);
            if (_lastKnownPlayerPosition.ContainsKey(playerStateKey))
                _lastKnownPlayerPosition[playerStateKey] = new Vector3(14.2921f, 1.9777f, 37.8714f); // Police station exit
            else
                _lastKnownPlayerPosition.Add(playerStateKey, player.transform.position);

            // Find an available holding cell
            var holdingCell = GetAvailableHoldingCell(jailController);
            if (holdingCell == null)
            {
                ModLogger.Error("No holding cells available, using fallback jail method");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Get spawn point in the holding cell
            Transform spawnPoint = holdingCell.AssignPlayerToSpawnPoint(player);
            if (spawnPoint == null)
            {
                ModLogger.Error($"No spawn points available in holding cell {holdingCell.cellName}");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            ModLogger.Info($"Teleporting player to holding cell: {holdingCell.cellName} at {spawnPoint.name}");

            // Teleport player to holding cell
            player.transform.position = spawnPoint.position;
            player.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            ModLogger.Debug($"Placed {player.name} in holding cell facing west for intake");

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
            if (!Core.IsGameplaySceneActive)
            {
                yield break;
            }

            ModLogger.Info($"Player {player.name} has completed booking process");

            // Start actual jail time AFTER booking completion (using game time tracking)
            ModLogger.Info($"Booking complete - now starting full jail sentence of {sentence.JailTime} game minutes");
            yield return WaitForJailSentence(sentence.JailTime, player);

            if (!Core.IsGameplaySceneActive || player == null)
            {
                yield break;
            }

            // Release from holding cell
            holdingCell.ReleasePlayerFromSpawnPoint(player);
            holdingCell.cellDoor.UnlockDoor();

            // Use the manager-owned post-sentence release seam when available.
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                jailManager.CompletePostSentenceRelease(player);
            }
            else
            {
                var releaseType = ConsumePendingReleaseType(player);
                float bailAmount = releaseType == ReleaseManager.ReleaseType.BailPayment
                    ? Core.ResolveBailSystem()?.GetBailAmount(player) ?? 0f
                    : 0f;
                SafeInitiateEnhancedRelease(player, releaseType, bailAmount);
            }
        }
        
        /// <summary>
        /// Start the booking process for the player
        /// This handles the processing/booking time before sentence starts
        /// </summary>
        private IEnumerator StartBookingProcess(Player player, JailSentence sentence, CellDetail holdingCell)
        {
            ModLogger.Info($"Starting booking/processing for {player.name}");

            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                jailManager.AttachBookingProcess(Core.ResolveBookingProcess());
                yield return jailManager.RunBookingProcess(player, sentence);
                yield break;
            }

            ModLogger.Error("JailManager is null - cannot start booking through manager seam");
            yield return new WaitForSeconds(5f);
        }

        /// <summary>
        /// Process player to main jail cell (starts in holding, then transfers)
        /// </summary>
        private IEnumerator ProcessPlayerToJail(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Processing player {player.name} to main jail cell for {sentence.JailTime}s");

            // First, put them in holding cell for "processing"
            yield return SendPlayerToHoldingCellForProcessing(player, sentence);
            if (!Core.IsGameplaySceneActive)
            {
                yield break;
            }

            // Then move to main jail cell
            //yield return TransferToMainJailCell(player, sentence);
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
            string playerStateKey = GetPlayerStateKey(player);
            if (_lastKnownPlayerPosition.ContainsKey(playerStateKey))
                _lastKnownPlayerPosition[playerStateKey] = new Vector3(14.2921f, 1.9777f, 37.8714f); // Police station exit
            else
                _lastKnownPlayerPosition.Add(playerStateKey, player.transform.position);

            var holdingCell = GetAvailableHoldingCell(jailController);
            if (holdingCell == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            Transform spawnPoint = holdingCell.AssignPlayerToSpawnPoint(player);
            if (spawnPoint == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Teleport to holding cell
            player.transform.position = spawnPoint.position;
            player.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            ModLogger.Debug($"Placed {player.name} in holding cell facing west for intake");
            holdingCell.cellDoor.LockDoor();
            holdingCell.cellDoor.CloseDoor();

            // Keep controls enabled during processing
            ModLogger.Info("Player controls kept enabled during processing");

            // Start booking through the jail-manager seam - it will handle the intake officer escort.
            ModLogger.Info($"Starting booking process for {player.name} in holding cell");
            yield return StartBookingProcess(player, sentence, holdingCell);

            // Release from holding cell (but don't release from jail yet)
            holdingCell.ReleasePlayerFromSpawnPoint(player);
            holdingCell.cellDoor.UnlockDoor();
        }

        // SirTidez: Commented out for testing 11/16/25
        /*private IEnumerator TransferToMainJailCell(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Transferring player {player.name} to main jail cell");

            var jailController = Core.JailController;
            if (jailController == null)
            {
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Check if player already has a cell assigned (from booking process)
            JailCell mainCell = null;
            var cellManager = Core.ResolveCellAssignmentManager();
            if (cellManager != null)
            {
                int assignedCellNumber = cellManager.GetPlayerCellNumber(player);
                if (assignedCellNumber >= 0 && assignedCellNumber < jailController.cells.Count)
                {
                    mainCell = jailController.cells[assignedCellNumber];
                    ModLogger.Info($"Using already-assigned cell {assignedCellNumber} for {player.name}");
                }
            }

            // If no cell was assigned, find an available main jail cell
            if (mainCell == null)
            {
                mainCell = GetAvailableMainCell(jailController);
            }

            if (mainCell == null)
            {
                ModLogger.Error("No main cells available, keeping in holding cell");
                // Continue with remaining sentence in holding cell
                var holdingCell = GetAvailableHoldingCell(jailController);
                if (holdingCell != null)
                {
                    Transform holdingSpawn = holdingCell.AssignPlayerToSpawnPoint(player);
                    if (holdingSpawn != null)
                    {
                        player.transform.position = holdingSpawn.position;
                        holdingCell.cellDoor.LockDoor();
                        // Start tracking full sentence (processing time was separate)
                        ModLogger.Info($"Starting jail sentence tracking in holding cell: {sentence.JailTime} game minutes ({GameTimeManager.FormatGameTime(sentence.JailTime)})");
                        yield return WaitForJailSentence(sentence.JailTime, player);
                        if (!Core.IsGameplaySceneActive || player == null)
                        {
                            yield break;
                        }
                        holdingCell.ReleasePlayerFromSpawnPoint(player);
                        holdingCell.cellDoor.UnlockDoor();
                        // Use enhanced release system for time served
                        SafeInitiateEnhancedRelease(player, ReleaseManager.ReleaseType.TimeServed);
                    }
                }
                yield break;
            }

            // Get spawn point in main cell
            Transform cellSpawnPoint = mainCell.AssignPlayerToSpawnPoint(player);
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

            if (!Core.IsGameplaySceneActive || player == null)
            {
                yield break;
            }

            ModLogger.Info($"Player {player.name} has served their main cell time");

            // Release from main cell
            mainCell.ReleasePlayerFromSpawnPoint(player);
            mainCell.cellDoor.UnlockDoor();

            // Use enhanced release system for time served
            SafeInitiateEnhancedRelease(player, ReleaseManager.ReleaseType.TimeServed);
        }*/

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
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#endif
            Singleton<BlackOverlay>.Instance.Open(2f);

            ModLogger.Info($"Player {player.name} using fallback jail method (screen blackout) for {sentence.JailTime}s");

            yield return WaitForJailSentence(sentence.JailTime, player);

            if (!Core.IsGameplaySceneActive || player == null)
            {
                yield break;
            }

            ModLogger.Info($"Player {player.name} has served their jail time (fallback method)");
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                jailManager.CompletePostSentenceRelease(player);
            }
            else
            {
                var releaseType = ConsumePendingReleaseType(player);
                float bailAmount = releaseType == ReleaseManager.ReleaseType.BailPayment
                    ? Core.ResolveBailSystem()?.GetBailAmount(player) ?? 0f
                    : 0f;
                SafeInitiateEnhancedRelease(player, releaseType, bailAmount);
            }
        }

        /// <summary>
        /// New enhanced release method that integrates with ReleaseManager
        /// </summary>
        public void InitiateEnhancedRelease(Player player, ReleaseManager.ReleaseType releaseType, float bailAmount = 0f)
        {
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                jailManager.InitiateEnhancedRelease(player, releaseType, bailAmount);
                return;
            }

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
                var releaseManager = Core.ResolveReleaseManager();
                if (releaseManager == null)
                {
                    ModLogger.Warn("JailSystem: release manager missing during enhanced release; retrying bootstrap");
                    releaseManager = ReleaseManager.BootstrapManagedInstance();
                }
                if (releaseManager != null)
                {
                    string reason = releaseType switch
                    {
                        ReleaseManager.ReleaseType.TimeServed => "Time served",
                        ReleaseManager.ReleaseType.BailPayment => $"Bail paid: ${bailAmount:F0}",
                        ReleaseManager.ReleaseType.CourtOrder => "Court order",
                        ReleaseManager.ReleaseType.Emergency => "Emergency release",
                        _ => "Release ordered"
                    };

                    bool releaseStarted = releaseManager.InitiateRelease(player, releaseType, bailAmount, reason);
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
        private void SafeInitiateEnhancedRelease(Player player, ReleaseManager.ReleaseType releaseType, float bailAmount = 0f)
        {
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                jailManager.SafeInitiateEnhancedRelease(player, releaseType, bailAmount);
                return;
            }

            var releaseManager = Core.ResolveReleaseManager();
            if (releaseManager == null)
            {
                releaseManager = ReleaseManager.BootstrapManagedInstance();
            }
            if (releaseManager != null && releaseManager.IsReleaseInProgress(player))
            {
                ModLogger.Info($"Player {player.name} release skipped - release already in progress (early release system handling it)");
                // Don't trigger another release - early release system is handling it
            }
            else
            {
                ModLogger.Info($"Initiating {releaseType} release for {player.name}");
                InitiateEnhancedRelease(player, releaseType, bailAmount);
            }
        }

        /// <summary>
        /// Start jail time after booking process completes
        /// </summary>
        public IEnumerator StartJailTimeAfterBooking(Player player, JailSentence sentence)
        {
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null)
            {
                yield return jailManager.StartJailTimeAfterBooking(player, sentence);
                yield break;
            }

            ModLogger.Info($"Starting jail time for {player.name} after booking completion - {sentence.JailTime}s");

            // Wait for the jail time with control maintenance
            yield return WaitForJailSentence(sentence.JailTime, player);

            if (!Core.IsGameplaySceneActive || player == null)
            {
                yield break;
            }

            // After jail time completes, safely trigger release (checks for existing releases)
            var activeJailManager = Core.Instance?.JailManager;
            if (activeJailManager != null)
            {
                activeJailManager.CompletePostSentenceRelease(player);
            }
            else
            {
                var releaseType = ConsumePendingReleaseType(player);
                float bailAmount = releaseType == ReleaseManager.ReleaseType.BailPayment
                    ? Core.ResolveBailSystem()?.GetBailAmount(player) ?? 0f
                    : 0f;
                SafeInitiateEnhancedRelease(player, releaseType, bailAmount);
            }
        }

        /// <summary>
        /// Create inventory snapshot for persistent storage
        /// </summary>
        private void CreateInventorySnapshotIfNeeded(Player player)
        {
            try
            {
                var persistentData = Core.ResolvePersistentPlayerData();
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
        internal void StorePlayerExitPosition(Player player)
        {
            try
            {
                // Always use the police station exit coordinates
                Vector3 exitPosition = new Vector3(14.2921f, 1.9777f, 37.8714f);
                ModLogger.Info($"Storing police station exit position for {player.name}: {exitPosition}");

                // Store in persistent data for cross-session support
                var persistentData = Core.ResolvePersistentPlayerData();
                if (persistentData != null)
                {
                    persistentData.StorePlayerExitPosition(player, exitPosition);
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

                var jailManager = Core.Instance?.JailManager;
                if (jailManager != null)
                {
                    jailManager.ClearPlayerInJail(player);
                }
                else
                {
                    Core.ResolveJailTimeTracker().ClearInJail(player);
                }

                // Clear stored exit position
                string playerStateKey = GetPlayerStateKey(player);
                if (_lastKnownPlayerPosition.ContainsKey(playerStateKey))
                {
                    _lastKnownPlayerPosition.Remove(playerStateKey);
                }

                // Update UI
                try
                {
                    Core.ResolveUIManager().DestroyJailInfoUI();
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Error clearing jail UI: {ex.Message}");
                }

                // CRITICAL: Clear crimes from both native and enhanced systems (player has been released)
                // This is the ONLY place crimes should be cleared - after release, not during arrest
                if (player.CrimeData != null)
                {
                    player.CrimeData.ClearCrimes();
                    ModLogger.Info($"[CRIME CLEAR] Cleared crimes from native system - player {player.name} has been released");
                }

                // Also clear crimes from our enhanced crime detection system
                var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
                if (crimeDetectionSystem != null)
                {
                    crimeDetectionSystem.ClearAllCrimes();
                    ModLogger.Info($"[CRIME CLEAR] Cleared crimes from enhanced system - player {player.name} has been released");
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
        internal void ReleasePlayerFromJail(Player player)
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
                Core.ResolveUIManager().DestroyJailInfoUI();
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
                PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
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

                // Calculate proper bail amount using BailSystem (consistent with payment system)
                // Try to get stored bail amount first, otherwise calculate it
                var bailSystemForUI = Core.ResolveBailSystem();
                float bailAmount = bailSystemForUI?.GetBailAmount(player) ?? 0f;
                
                // If no stored bail amount, calculate it
                if (bailAmount <= 0)
                {
                    float fineAmount = CalculateTotalCrimeFines(player);
                    if (fineAmount > 0)
                    {
                        if (bailSystemForUI != null)
                        {
                            var bailOffer = bailSystemForUI.CalculateBailAmount(player, fineAmount);
                            bailAmount = bailOffer.Amount;
                            // Store it for consistency
                            bailSystemForUI.StoreBailAmount(player, bailAmount);
                        }
                    }
                }
                string bailInfo = FormatBailAmount(bailAmount);

                // Show the UI using the BehindBarsUIManager WITHOUT starting timer (timer starts after booking)
                Core.ResolveUIManager().ShowJailInfoUI(
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
        /// Refresh the active jail sidebar after a custody-only charge is added. The
        /// booking UI is otherwise intentionally static, which left the visible
        /// charge and bail amount stale after an in-jail assault penalty.
        /// </summary>
        public void RefreshCustodyChargeDisplay(Player player)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                JailSeverity severity = DetermineSeverityFromCrimeData(player.CrimeData);
                string crimeInfo = GetCrimeDescription(severity, player);
                float fineAmount = CalculateTotalCrimeFines(player);
                var bailSystem = Core.ResolveBailSystem();
                float bailAmount = bailSystem != null
                    ? bailSystem.CalculateBailAmount(player, fineAmount).Amount
                    : CalculateBailAmount(fineAmount, severity);

                bailSystem?.StoreBailAmount(player, bailAmount);

                var uiWrapper = Core.ResolveUIManager().GetUIWrapper();
                if (uiWrapper != null)
                {
                    uiWrapper.SetCrimeInfo(crimeInfo);
                    uiWrapper.UpdateBailAmount(bailAmount);

                    var timeTracker = Core.ResolveJailTimeTracker();
                    if (timeTracker.IsTracking(player))
                    {
                        uiWrapper.SetTimeInfo(timeTracker.GetFormattedRemainingTime(player));
                    }
                }

                ModLogger.Info($"[LOCKDOWN] Refreshed custody charge sidebar: Crime={crimeInfo}; Bail=${bailAmount:F0}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LOCKDOWN] Could not refresh custody charge sidebar: {ex.Message}");
            }
        }

        /// <summary>
        /// Get a user-friendly description of the crimes committed
        /// ENHANCED: Now includes crimes from our enhanced detection system
        /// </summary>
        private string GetCrimeDescription(JailSeverity severity, Player player)
        {
            // A street officer assault is represented in both the mod record and
            // the native mirror. Merge by display name and retain the larger count,
            // rather than showing the same event twice in the booking sidebar.
            var crimeCounts = new System.Collections.Generic.Dictionary<string, int>();

            void MergeCount(string crimeName, int count)
            {
                if (string.IsNullOrWhiteSpace(crimeName) || count <= 0)
                {
                    return;
                }

                if (crimeCounts.TryGetValue(crimeName, out int currentCount))
                {
                    crimeCounts[crimeName] = Mathf.Max(currentCount, count);
                }
                else
                {
                    crimeCounts[crimeName] = count;
                }
            }

            // First, get crimes from our enhanced crime detection system
            var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
            if (crimeDetectionSystem != null)
            {
                var crimeSummary = crimeDetectionSystem.GetCrimeSummary();
                foreach (var crimeEntry in crimeSummary)
                {
                    MergeCount(crimeEntry.Key, crimeEntry.Value);
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

                    MergeCount(crimeName, count);
                }
            }

            // A parole search can be the sole arrest source and deliberately does not
            // create a normal street wanted crime. Surface the freshly recorded violation
            // here so the custody UI names the actual reason for this arrest rather than
            // falling back to the generic severity label (for example, "Minor Infractions").
            string paroleViolationName = GetCurrentArrestParoleViolationName(player);
            if (!string.IsNullOrEmpty(paroleViolationName))
            {
                MergeCount(paroleViolationName, 1);
            }

            if (crimeCounts.Count > 0)
            {
                return string.Join(", ", crimeCounts
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Value > 1 ? $"{entry.Key} ({entry.Value}x)" : entry.Key)
                    .ToArray());
            }

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

        private string GetCurrentArrestParoleViolationName(Player player)
        {
            try
            {
                if (TryGetPendingParoleArrestCause(player, out var pendingCause))
                {
                    return GetParoleViolationDisplayName(pendingCause);
                }

                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                var paroleRecord = rapSheet?.CurrentParoleRecord;
                if (paroleRecord == null)
                {
                    return string.Empty;
                }

                var violation = paroleRecord.GetViolations()
                    ?.Where(candidate => candidate != null && candidate.ViolationTime >= DateTime.Now.AddMinutes(-2))
                    .OrderByDescending(candidate => candidate.ViolationTime)
                    .FirstOrDefault();

                return violation == null ? string.Empty : GetParoleViolationDisplayName(violation.ViolationType);
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Unable to resolve current parole violation for custody UI: {ex.Message}");
                return string.Empty;
            }
        }

        internal void RegisterPendingParoleArrestCause(Player player, ViolationType violationType)
        {
            if (player == null)
            {
                return;
            }

            string playerKey = Core.ResolvePlayerKey(player);
            if (string.IsNullOrWhiteSpace(playerKey))
            {
                return;
            }

            pendingParoleArrestCauses[playerKey] = new PendingParoleArrestCause
            {
                ViolationType = violationType,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(PendingParoleArrestCauseLifetimeSeconds)
            };

            ModLogger.Info($"[PAROLE VIOLATION] Registered pending custody cause '{GetParoleViolationDisplayName(violationType)}' for {player.name}");
        }

        internal void ClearSceneTransientParoleArrestCauses()
        {
            pendingParoleArrestCauses.Clear();
        }

        private bool TryGetPendingParoleArrestCause(Player player, out ViolationType violationType)
        {
            violationType = ViolationType.Other;
            if (player == null)
            {
                return false;
            }

            string playerKey = Core.ResolvePlayerKey(player);
            if (string.IsNullOrWhiteSpace(playerKey) ||
                !pendingParoleArrestCauses.TryGetValue(playerKey, out var pendingCause))
            {
                return false;
            }

            if (pendingCause == null || pendingCause.ExpiresAtUtc < DateTime.UtcNow)
            {
                pendingParoleArrestCauses.Remove(playerKey);
                return false;
            }

            violationType = pendingCause.ViolationType;
            return true;
        }

        private static string GetParoleViolationDisplayName(ViolationType violationType)
        {
            return violationType switch
            {
                ViolationType.IllegalWeaponPossession => "Parole Violation - Illegal Weapon",
                ViolationType.ContrabandPossession => "Parole Violation - Contraband Possession",
                ViolationType.MissedCheckIn => "Parole Violation - Missed Check-In",
                ViolationType.NewCrime => "Parole Violation - New Crime",
                ViolationType.RestrictedAreaViolation => "Parole Violation - Restricted Area",
                ViolationType.CurfewViolation => "Parole Violation - Curfew Violation",
                ViolationType.ContactWithKnownCriminals => "Parole Violation - Contact with Known Criminals",
                ViolationType.Other => "Parole Violation",
                _ => string.Empty
            };
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
                case "AssaultOnOfficer": return "Assault on an LEO";
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

                // 1. Clear any active booking/release process through the jail-manager seam.
                var jailManager = Core.Instance?.JailManager;
                if (jailManager != null)
                {
                    jailManager.ResetActiveJailFlow(player);
                    ModLogger.Info("Reset active booking and release process state");
                }
                else
                {
                    // Note: BookingProcess handles its own cleanup when player is arrested.
                    var bookingProcess = BBHelpers.FindObjectOfTypeSafe<BookingProcess>();
                    if (bookingProcess != null)
                    {
                        ModLogger.Info("BookingProcess found - it will handle its own cleanup");
                    }

                    var releaseManager = Core.ResolveReleaseManager();
                    releaseManager?.CancelPlayerRelease(player);
                    ModLogger.Info("Cancelled any active release process");
                }

                // 2. Clear escort registrations
                var officerCoordinator = OfficerCoordinator.Instance;
                if (officerCoordinator != null)
                {
                    officerCoordinator.UnregisterAllEscortsForPlayer(player);
                    ModLogger.Info("Cleared all escort registrations");
                }

                // 3. Clear release grace period (player is being arrested)
                ParoleSearchSystem.Instance.ClearReleaseTime(player);

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
        /// Record a parole violation if the player was on parole when arrested
        /// </summary>
        private void RecordParoleViolationIfNeeded(Player player)
        {
            try
            {
                // Get rap sheet to check parole status
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet == null)
                {
                    ModLogger.Debug($"[PAROLE VIOLATION] No rap sheet found for {player.name} - skipping violation check");
                    return;
                }

                // Check if player is currently on parole
                if (rapSheet.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    if (TryGetPendingParoleArrestCause(player, out var pendingCause))
                    {
                        ModLogger.Info($"[PAROLE VIOLATION] Preserving pending search cause '{GetParoleViolationDisplayName(pendingCause)}' for {player.name}; generic NewCrime record skipped");
                        return;
                    }

                    // A parole search can already have recorded a specific violation
                    // (for example, illegal weapon possession) immediately before it
                    // invokes the arrest path. Do not overwrite that cause with a second,
                    // generic NewCrime violation during the same incident.
                    bool hasFreshSpecificViolation = rapSheet.CurrentParoleRecord
                        .GetViolations()
                        ?.Any(violation =>
                            violation != null &&
                            violation.ViolationType != ViolationType.NewCrime &&
                            violation.ViolationTime >= DateTime.Now.AddSeconds(-30)) == true;

                    if (hasFreshSpecificViolation)
                    {
                        ModLogger.Info($"[PAROLE VIOLATION] Preserving freshly recorded specific violation for {player.name}; generic NewCrime record skipped");
                        return;
                    }

                    ModLogger.Info($"[PAROLE VIOLATION] Player {player.name} was on parole at time of arrest - recording violation");

                    // Create violation record for being arrested while on parole
                    var arrestViolation = new ViolationRecord(
                        ViolationType.NewCrime,
                        $"Arrested and charged with new crimes while on parole supervision. Location: {player.transform.position}",
                        3.0f // High severity - being arrested is a serious violation
                    );

                    // Add violation to parole record using helper method that marks RapSheet as changed
                    bool violationAdded = rapSheet.AddParoleViolation(arrestViolation);
                    
                    if (violationAdded)
                    {
                        ModLogger.Info($"[PAROLE VIOLATION] Successfully recorded parole violation for {player.name}. Total violations: {rapSheet.CurrentParoleRecord.GetViolationCount()}");
                        
                        // Update LSI level since violations affect risk assessment
                        rapSheet.UpdateLSILevel();
                        ModLogger.Info($"[PAROLE VIOLATION] Updated LSI level after violation: {rapSheet.LSILevel}");
                    }
                    else
                    {
                        ModLogger.Warn($"[PAROLE VIOLATION] Failed to add violation to parole record for {player.name}");
                    }
                }
                else
                {
                    ModLogger.Debug($"[PAROLE VIOLATION] Player {player.name} is not on parole - no violation to record");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[PAROLE VIOLATION] Error recording parole violation for {player.name}: {ex.Message}");
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
                var exitScannerStation = BBHelpers.FindObjectOfTypeSafe<ExitScannerStation>();
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
                var jailInventoryStations = BBHelpers.FindObjectsOfTypeSafe<JailInventoryPickupStation>();
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
                var inventoryPickupStations = BBHelpers.FindObjectsOfTypeSafe<InventoryPickupStation>();
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
