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
    /// <summary>
    /// Coordinates arrest, custody, booking, sentence tracking, and release handoffs.
    /// </summary>
    /// <remarks>
    /// Sentence durations are represented as game minutes throughout the active jail flow,
    /// even where legacy log text and local variable names say “seconds.” The current path
    /// captures the arrest snapshot, clears native crimes and wanted state, runs the jail-manager
    /// booking seam, and then delegates sentence timing/release to the custody services. Release
    /// helpers clear native/enhanced crime collections, with the legacy fallback repeating that
    /// cleanup; callers must not infer that a single helper owns all cleanup.
    /// </remarks>
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
            /// <summary>Parole-specific violation preserved across the native arrest callback.</summary>
            public ViolationType ViolationType;
            /// <summary>UTC deadline after which the transient cause is discarded.</summary>
            public DateTime ExpiresAtUtc;
        }

        // Scene-transient causes are keyed by stable player ID and expire after a short
        // handoff window; ClearSceneTransientParoleArrestCauses removes them on scene reset.
        private readonly Dictionary<string, PendingParoleArrestCause> pendingParoleArrestCauses =
            new Dictionary<string, PendingParoleArrestCause>();

        /// <summary>
        /// Severity used to select sentence/fine presentation for an arrest.
        /// </summary>
        public enum JailSeverity
        {
            Minor = 0,      // Traffic violations, small theft
            Moderate = 1,   // Assault, larger theft
            Major = 2,      // Drug dealing, major assault
            Severe = 3      // Murder, major drug operations
        }

        /// <summary>
        /// Calculated custody sentence and its player-facing financial metadata.
        /// </summary>
        /// <remarks>
        /// <see cref="JailTime"/> is a game-minute duration, despite historical log messages
        /// that append an “s” and the legacy property name.
        /// </remarks>
        public class JailSentence
        {
            /// <summary>Severity selected from the combined native/enhanced crime data.</summary>
            public JailSeverity Severity { get; set; }
            /// <summary>Sentence duration in game minutes consumed by the jail tracker.</summary>
            public float JailTime { get; set; }
            /// <summary>Fine calculated independently from the sentence duration.</summary>
            public float FineAmount { get; set; }
            /// <summary>Whether the sentence UI offers a fine-payment alternative.</summary>
            public bool CanPayFine { get; set; }
            /// <summary>Formatted crime/sentence explanation shown in custody UI.</summary>
            public string Description { get; set; } = "";
        }

        /// <summary>
        /// Handle an immediate arrest without the native police-station ticket GUI.
        /// </summary>
        /// <param name="player">Player entering custody.</param>
        /// <remarks>
        /// The active order is: mark custody, record an applicable parole violation, reset
        /// prior jail state, capture/log native crimes, clear native wanted state, suppress
        /// parole UI, apply custody state, show the busted effect, restore UI interactions,
        /// assess the sentence, and hand off to the jail flow. The legacy crime-sync block
        /// remains commented out because the Harmony path owns that capture.
        /// </remarks>
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
        /// <remarks>
        /// The primary BlackOverlay call and the control-disabling fallback both wait with
        /// Unity-scaled <see cref="WaitForSeconds"/>; pausing or changing time scale changes
        /// the effect duration. The fallback re-enables controls only when it disabled them.
        /// </remarks>
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
        /// Forward the legacy arrest entry point to the immediate-arrest flow.
        /// </summary>
        /// <param name="player">Player entering custody.</param>
        /// <remarks>This compatibility wrapper adds no separate arrest behavior.</remarks>
        public IEnumerator HandlePlayerArrest(Player player)
        {
            ModLogger.Info($"Processing LEGACY arrest for player: {player.name}");

            // Use the new immediate arrest system
            yield return HandleImmediateArrest(player);
        }

        /// <summary>
        /// Assess the player's charges, calculate sentence/fine values, and populate custody UI.
        /// </summary>
        /// <param name="player">Player whose current crime data should be assessed.</param>
        /// <returns>A sentence record using game-minute custody duration.</returns>
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

        /// <summary>
        /// Select the highest matching severity from enhanced and native crime type names.
        /// </summary>
        /// <param name="crimeData">Crime-data object retained for the caller's assessment context; the current implementation reads the local player systems.</param>
        /// <returns>Severe/major/moderate for recognized charges, moderate without a local player, or minor when no recognized charge is found.</returns>
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


        /// <summary>
        /// Populate fine, game-minute sentence, description, and fine-payment fields.
        /// </summary>
        /// <param name="sentence">Sentence object to populate.</param>
        /// <param name="player">Player whose RapSheet and crimes determine the result.</param>
        /// <remarks>
        /// The calculator returns game minutes and that value is stored directly in
        /// <see cref="JailSentence.JailTime"/>. No real-time conversion occurs here; the
        /// historical local variable/log wording is retained only for compatibility.
        /// </remarks>
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
            
            // Preserve the calculator's game-minute value in JailTime. The historical local
            // name and old “real-time seconds” comment were misleading: downstream
            // JailTimeTracker/WaitForJailSentence interprets this value as game minutes.
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



        // Exit positions are process-local and consumed when the caller requests them; the
        // persistent player-data service stores the release position separately.
        private Dictionary<string, Vector3> _lastKnownPlayerPosition = new();
        // Optional station reference created through the IL2CPP-safe helper path.
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

        /// <summary>
        /// Read and consume a process-local exit position by stable player key.
        /// </summary>
        /// <param name="playerKey">Stable player key used when the position was stored.</param>
        /// <returns>The stored position, or <see langword="null"/> when none exists.</returns>
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

        /// <summary>
        /// Resolve the stable key used by process-local jail state maps.
        /// </summary>
        /// <param name="player">Player whose key should be resolved.</param>
        /// <returns>The shared player identity key.</returns>
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
        /// <param name="player">Player whose custody state should be applied.</param>
        /// <param name="inJail">Whether the player should be marked in custody.</param>
        /// <remarks>
        /// The current owner is <see cref="JailManager"/> when available; without it this
        /// helper only logs a warning and does not apply a fallback state mutation.
        /// </remarks>
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
        /// <param name="waitTime">Duration in the caller's scaled Unity-second convention.</param>
        /// <param name="player">Player whose custody controls should be maintained.</param>
        /// <remarks>
        /// Polling uses Unity-scaled <see cref="WaitForSeconds"/> intervals and aborts when the
        /// gameplay scene is no longer active. The player parameter is retained for the
        /// existing signature but control maintenance is routed through the jail manager.
        /// </remarks>
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
        /// <param name="sentenceGameMinutes">Sentence duration in game minutes.</param>
        /// <param name="player">Player currently serving the sentence.</param>
        /// <remarks>
        /// The method starts tracker ownership through <see cref="JailManager"/> when present,
        /// otherwise through the fallback jail tracker. Bail is offered only after intake
        /// release readiness and cash checks succeed; payment stages a pending release and the
        /// custody owner later performs the actual release. The 0.1-second polling loop and
        /// one-second cash/log cadence use Unity-scaled waits/time, while tracker durations are
        /// game minutes. A scene transition aborts the coroutine before normal cleanup.
        /// </remarks>
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

                        // Payment can fail after the affordability pre-check
                        // (missing MoneyManager, a concurrent cash change, or
                        // no release-manager handoff). Keep custody tracking
                        // and the normal UI alive until a bail release has
                        // actually been authorized.
                        yield return bailSystem.ProcessBailPayment(player, bailAmount, false);

                        if (jailManager != null ? jailManager.HasPendingReleaseType(player) : HasPendingReleaseType(player))
                        {
                            Core.ResolveUIManager().HideBailUI();
                            var uiWrapper = Core.ResolveUIManager().GetUIWrapper();
                            uiWrapper?.SetBailedOutStatus();

                            if (jailManager != null)
                            {
                                jailManager.StopSentenceTracking(player);
                            }
                            else
                            {
                                Core.ResolveJailTimeTracker().StopTracking(player);
                            }

                            bailPaid = true;
                            ModLogger.Info($"[BAIL] Bail payment completed for {player.name}; release will proceed after custody cleanup");
                            break;
                        }

                        ModLogger.Warn($"[BAIL] Bail payment was not authorized for {player.name}; sentence tracking remains active");
                        Core.ResolveUIManager().ShowNotification(
                            "Bail payment could not be completed. You remain in custody.",
                            NotificationType.Warning);
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
        /// <param name="player">Player whose pending release should be marked.</param>
        /// <param name="releaseType">Release reason to retain until custody cleanup.</param>
        /// <remarks>
        /// The jail manager owns the marker when available. Without it this compatibility seam
        /// only logs a warning and does not retain a local pending-release value.
        /// </remarks>
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
        /// <param name="player">Player whose pending release should be queried.</param>
        /// <returns><see langword="true"/> only when the jail manager reports a pending release.</returns>
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
        /// <param name="player">Player whose pending release should be consumed.</param>
        /// <returns>The manager-owned release type, or time served when the manager is unavailable.</returns>
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
        /// Send a player to holding custody, run booking, and wait the game-minute sentence.
        /// </summary>
        /// <param name="player">Player entering the holding-cell flow.</param>
        /// <param name="sentence">Calculated sentence and financial metadata.</param>
        /// <remarks>
        /// This path requires an available jail controller, holding cell, and spawn point;
        /// otherwise it uses the blackout fallback. Booking completes before sentence timing,
        /// and post-sentence release is handed to <see cref="JailManager"/> when available.
        /// </remarks>
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
        /// <param name="player">Player being booked.</param>
        /// <param name="sentence">Sentence passed to the booking manager.</param>
        /// <param name="holdingCell">Holding cell currently assigned to the player.</param>
        /// <remarks>
        /// When the jail manager exists, booking is delegated to its attached booking-process
        /// seam. Without it, the method logs an error and waits five scaled seconds as a
        /// compatibility fallback; it does not perform the full intake flow itself.
        /// </remarks>
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
        /// Process the player through the active holding-cell intake path.
        /// </summary>
        /// <param name="player">Player entering custody.</param>
        /// <param name="sentence">Calculated sentence passed to intake.</param>
        /// <remarks>
        /// The method currently sends the player to holding-cell processing only. The follow-up
        /// main-cell transfer call remains commented out, so the active path does not perform
        /// the historical holding-to-main-cell move here.
        /// </remarks>
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

        /// <summary>
        /// Place a player in holding, run booking, and release the temporary holding assignment.
        /// </summary>
        /// <param name="player">Player entering intake processing.</param>
        /// <param name="sentence">Sentence supplied to the booking process.</param>
        /// <remarks>
        /// The jail manager owns the intake officer/booking orchestration. This method only
        /// handles cell assignment, door state, manager handoff, and temporary holding cleanup;
        /// sentence timing is performed by the caller after this coroutine returns.
        /// </remarks>
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

        /// <summary>
        /// Find the first holding cell with an available spawn point.
        /// </summary>
        /// <param name="jailController">Active jail controller containing holding cells.</param>
        /// <returns>The first available holding cell, or <see langword="null"/>.</returns>
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

        /// <summary>
        /// Find the first unoccupied main cell.
        /// </summary>
        /// <param name="jailController">Active jail controller containing main cells.</param>
        /// <returns>The first unoccupied cell, or <see langword="null"/>.</returns>
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
        /// <param name="player">Player whose custody sentence should continue.</param>
        /// <param name="sentence">Sentence tracked while the screen is blacked out.</param>
        /// <remarks>
        /// The fallback keeps movement enabled, opens a two-second overlay, uses the normal
        /// game-minute sentence tracker, and then delegates release to the jail manager or the
        /// legacy release seam. It is a location/UI fallback, not a separate sentence unit.
        /// </remarks>
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
        /// Start the manager-owned enhanced release flow, with a legacy fallback when unavailable.
        /// </summary>
        /// <param name="player">Player leaving custody.</param>
        /// <param name="releaseType">Reason/authority for the release.</param>
        /// <param name="bailAmount">Bail amount associated with a bail release, if applicable.</param>
        /// <remarks>
        /// When the jail manager exists it owns this handoff. Otherwise the method bootstraps or
        /// resolves ReleaseManager, stores the exit position, and falls back to
        /// <see cref="ReleasePlayerFromJail"/> if coordinated release cannot start.
        /// </remarks>
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
        /// <param name="player">Player whose release should be started.</param>
        /// <param name="releaseType">Reason/authority for the release.</param>
        /// <param name="bailAmount">Bail amount associated with a bail release, if applicable.</param>
        /// <remarks>
        /// Existing in-progress releases are left untouched. Otherwise this helper delegates
        /// to the enhanced release seam and thereby centralizes duplicate-release protection.
        /// </remarks>
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
        /// <param name="player">Player whose sentence starts after booking.</param>
        /// <param name="sentence">Sentence containing game-minute duration.</param>
        /// <remarks>
        /// The jail manager owns the active path when available. The fallback waits through the
        /// same game-minute tracker, then uses the normal post-sentence release handoff.
        /// </remarks>
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
        /// <param name="player">Player whose inventory should be snapshotted.</param>
        /// <remarks>
        /// The current arrest path leaves this compatibility helper commented out because a
        /// Harmony patch captures inventory before native clearing. Calling this method directly
        /// remains best effort and logs failures.
        /// </remarks>
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
        /// <param name="player">Player whose release position should be stored.</param>
        /// <remarks>
        /// Despite the historical summary, the current implementation always stores the fixed
        /// police-station exit coordinates; it does not capture the player's current transform.
        /// </remarks>
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
        /// <param name="player">Player whose custody, UI, transient position, and crime state should be cleared.</param>
        /// <remarks>
        /// The method prefers JailManager for custody state, removes the process-local exit
        /// position, destroys jail UI, and clears native/enhanced crimes. The legacy
        /// <see cref="ReleasePlayerFromJail"/> fallback repeats the crime cleanup, so both
        /// release paths intentionally remain aligned rather than assuming this is the only
        /// possible cleanup caller.
        /// </remarks>
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

                // Clear crimes from both native and enhanced systems after release. The legacy
                // ReleasePlayerFromJail fallback repeats this cleanup; keep both paths aligned.
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
        /// <param name="player">Player whose legacy release cleanup should run.</param>
        /// <remarks>
        /// This fallback clears arrest/UI/crime state, attempts the native Free calls, restores
        /// movement/camera/inventory access, and enables the pickup station. It does not perform
        /// the manager-owned escort/release sequence and may duplicate cleanup done by
        /// <see cref="ClearPlayerJailStatus"/>.
        /// </remarks>
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
        /// <param name="sentence">Calculated custody sentence to display.</param>
        /// <param name="player">Player whose charges and bail should be shown.</param>
        /// <remarks>
        /// The visible timer is initialized with zero here and begins after booking; bail is
        /// resolved from the stored offer or recalculated through <see cref="BailSystem"/>.
        /// </remarks>
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

        /// <summary>
        /// Resolve the parole-specific charge associated with the current arrest, if available.
        /// </summary>
        /// <param name="player">Player whose pending cause or recent violation should be checked.</param>
        /// <returns>A display name for the pending/recent cause, or an empty string.</returns>
        /// <remarks>
        /// The transient pending-cause map takes precedence; otherwise the newest non-generic
        /// RapSheet violation from the last two minutes is used as a best-effort fallback.
        /// </remarks>
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

        /// <summary>
        /// Preserve a parole-specific arrest cause across the native pursuit-to-custody callback.
        /// </summary>
        /// <param name="player">Player whose next arrest should carry the cause.</param>
        /// <param name="violationType">Parole violation to preserve.</param>
        /// <remarks>
        /// The entry is keyed by stable player ID and expires after
        /// <c>PendingParoleArrestCauseLifetimeSeconds</c>; it is scene-transient rather than
        /// persisted RapSheet state.
        /// </remarks>
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

        /// <summary>
        /// Returns the explicit parole violation that initiated the current arrest, when one
        /// is available. The native wanted system needs a concrete game Crime to begin a
        /// pursuit, but that carrier crime is not the charge the player should receive for a
        /// parole warrant.
        /// </summary>
        internal bool TryGetPendingParoleArrestCauseForCustody(Player player, out ViolationType violationType)
        {
            return TryGetPendingParoleArrestCause(player, out violationType);
        }

        /// <summary>
        /// Clear all scene-transient parole arrest causes during scene teardown/reset.
        /// </summary>
        internal void ClearSceneTransientParoleArrestCauses()
        {
            pendingParoleArrestCauses.Clear();
        }

        /// <summary>
        /// Read a non-expired pending parole arrest cause and remove expired entries.
        /// </summary>
        /// <param name="player">Player whose transient cause should be read.</param>
        /// <param name="violationType">Resolved cause, or <see cref="ViolationType.Other"/> when absent.</param>
        /// <returns><see langword="true"/> when a current cause exists.</returns>
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
        /// <param name="timeInGameMinutes">Duration in game minutes.</param>
        /// <returns>A compact game-time string such as <c>2h 5m</c>.</returns>
        private string FormatJailTime(float timeInGameMinutes)
        {
            // Use GameTimeManager to format game time
            return GameTimeManager.FormatGameTime(timeInGameMinutes);
        }

        /// <summary>
        /// Calculate bail amount based on fine amount and crime severity
        /// Bail should be significantly higher than the fine for serious crimes
        /// </summary>
        /// <param name="fineAmount">Base fine amount used by the severity multiplier.</param>
        /// <param name="severity">Severity selecting the base bail multiplier.</param>
        /// <returns>The calculated bail amount, or zero for a non-positive fine.</returns>
        /// <remarks>
        /// Enhanced crime summary may add murder and witness-intimidation multipliers. This
        /// calculation is separate from <see cref="BailSystem.CalculateBailAmount"/> and is
        /// retained as a jail-system compatibility path.
        /// </remarks>
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
        /// <param name="player">Player whose active custody flow should be reset.</param>
        /// <remarks>
        /// Reset order is booking/release cancellation, escort unregister, release-grace
        /// clearing, then station-state reset. The manager seam owns the active flow when
        /// available; the fallback only asks legacy services to clean themselves up.
        /// </remarks>
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
        /// <param name="player">Player whose active parole record should be evaluated.</param>
        /// <remarks>
        /// A pending specific arrest cause or a fresh non-generic violation suppresses the
        /// generic <see cref="ViolationType.NewCrime"/> record. Otherwise an on-parole arrest
        /// adds that generic violation and updates LSI. Missing RapSheet/state is treated as a
        /// no-op with diagnostic logging.
        /// </remarks>
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
        /// <param name="player">Player associated with the reset; currently used only for the surrounding arrest context.</param>
        /// <remarks>
        /// This is a best-effort reflection bridge for scene stations. Missing station objects
        /// or private fields are skipped; failures are logged rather than preventing arrest
        /// state reset.
        /// </remarks>
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
