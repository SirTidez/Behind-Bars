using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Jail;
using HarmonyLib;
using System.Collections;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Combat;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using ScheduleOne.NPCs;
using ScheduleOne.Combat;
using ScheduleOne.Police;
using ScheduleOne.Law;
#endif
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Behind_Bars.Harmony
{
    [HarmonyPatch]
    public static class HarmonyPatches
    {
        private static Core? _core;
        private static bool _jailSystemHandlingArrest = false;
        private static CrimeDetectionSystem? _crimeDetectionSystem;
        private static bool _mugshotInProgress = false;
        
        // Flags to prevent duplicate processing of the same arrest event
        private static bool _isProcessingArrestServer = false;
        private static bool _isProcessingArrestClient = false;
        
        // Track last arrest processing time as secondary guard
        private static DateTime _lastArrestServerTime = DateTime.MinValue;
        private static DateTime _lastArrestClientTime = DateTime.MinValue;
        
        public static void Initialize(Core core)
        {
            _core = core;
            _crimeDetectionSystem = new CrimeDetectionSystem();
        }
        
        /// <summary>
        /// Reset the arrest handling flag (called by JailSystem when arrest processing is complete)
        /// </summary>
        public static void ResetArrestHandlingFlag()
        {
            _jailSystemHandlingArrest = false;
            ModLogger.Info("Reset arrest handling flag - future arrests will use default system unless jail system intercepts");
        }
        
        /// <summary>
        /// Set mugshot mode to override player visibility
        /// </summary>
        public static void SetMugshotInProgress(bool inProgress)
        {
            _mugshotInProgress = inProgress;
            ModLogger.Info($"Mugshot mode set to: {inProgress}");
        }
        
        /// <summary>
        /// Restore UI interactions without teleporting (used during jail processing)
        /// </summary>
        public static void RestoreUIInteractions()
        {
            var localPlayer = Player.Local;
            if (localPlayer == null)
            {
                ModLogger.Error("Cannot restore UI interactions - local player is null");
                return;
            }
            
            try
            {
                ModLogger.Info("Restoring UI interactions during jail processing");
                
                // Restore the main HUD canvas (this is what Player.Free() does)
#if !MONO
                var hud = Il2CppScheduleOne.UI.HUD.Instance;
#else
                var hud = ScheduleOne.UI.HUD.Instance;
#endif
                if (hud?.canvas != null)
                {
                    hud.canvas.enabled = true;
                    ModLogger.Debug("HUD canvas re-enabled");
                }
                
                // Note: DO NOT re-enable inventory here during jail processing
                // Individual slots are locked via InventoryProcessor and should remain locked
                // Inventory will be properly unlocked when player is released from jail
                ModLogger.Debug("Inventory remains locked during jail time (individual slots locked)");
                
                // Re-enable camera look controls
#if !MONO
                var playerCamera = Il2CppScheduleOne.PlayerScripts.PlayerCamera.Instance;
#else
                var playerCamera = ScheduleOne.PlayerScripts.PlayerCamera.Instance;
#endif
                if (playerCamera != null)
                {
                    playerCamera.SetCanLook(true);
                    ModLogger.Debug("Camera look controls re-enabled");
                }
                
                // Re-enable movement
#if !MONO
                var playerMovement = Il2CppScheduleOne.PlayerScripts.PlayerMovement.Instance;
#else
                var playerMovement = ScheduleOne.PlayerScripts.PlayerMovement.Instance;
#endif
                if (playerMovement != null)
                {
#if MONO
                    playerMovement.CanMove = true;
#else
                    playerMovement.canMove = true;
#endif
                    ModLogger.Debug("Player movement re-enabled");
                }
                
                // Show crosshair again
                if (hud != null)
                {
                    hud.SetCrosshairVisible(true);
                    ModLogger.Debug("Crosshair visibility restored");
                }
                
                // Clear arrest status so player can be arrested again if needed
                localPlayer.IsArrested = false;
                ModLogger.Debug("IsArrested flag cleared - player can be arrested again");
                
                // Remove the "Arrested" UI element from PlayerCamera
                if (playerCamera != null)
                {
                    playerCamera.RemoveActiveUIElement("Arrested");
                    ModLogger.Debug("Removed 'Arrested' UI element");
                }
                
                ModLogger.Info("UI interactions successfully restored without teleportation");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error restoring UI interactions: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }

        // ====== ARREST SYSTEM PATCHES ======
        // NOTE: Game update split the arrest system into Server/Client RPC methods
        // - Server method (Arrest_Server): Calls the RPC handler for authoritative game logic
        // - Client method (Arrest_Client): Calls the RPC handler for UI and visual feedback
        // This separation prepares for future multiplayer support where:
        //   - Server logic runs once on the host/server
        //   - Client logic runs on each affected player's client
        
        // COMMENTED OUT: RPC Handler patches (trying wrapper methods instead)
        // The game calls Arrest_Client() and Arrest_Server() which then trigger the RPC handlers
        // We should patch the wrapper methods, not the RPC handlers directly
        /*
        /// <summary>
        /// SERVER-SIDE ARREST PATCH: Handles authoritative game logic when player is arrested
        /// This runs on the server/host and processes all gameplay-affecting operations
        /// </summary>
        [HarmonyPatch(typeof(Player), "RpcLogic___Arrest_Server_2166136261")]
        [HarmonyPostfix]
        public static void Player_ArrestServer_RpcHandler_Postfix(Player __instance)
        {
            if (_core == null)
            {
                MelonLogger.Error("Core instance is null in Player_ArrestServer_Postfix");
                return;
            }
            
            // Only handle local player arrests for now
            // TODO: For multiplayer support, also check __instance.IsOwner instead of just Player.Local
            if (__instance != Player.Local)
                return;
                
            ModLogger.Info($"[ARREST SERVER] Player {__instance.name} arrested - processing authoritative game logic");

            // STEP 1: Remove ALL ammo BEFORE capturing inventory (ammo is never returned)
            try
            {
                ModLogger.Info($"[ARREST SERVER] Removing ammunition before inventory capture");
                var playerInventory = __instance.GetComponent<PlayerInventory>();
                if (playerInventory == null)
                {
#if !MONO
                    playerInventory = Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance;
#else
                    playerInventory = ScheduleOne.PlayerScripts.PlayerInventory.Instance;
#endif
                }

                if (playerInventory != null)
                {
                    InventoryProcessor.RemoveAllAmmo(playerInventory);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ARREST SERVER] Error removing ammo: {ex.Message}");
            }

            // STEP 2: Capture player's inventory AFTER ammo removal
            try
            {
                ModLogger.Info($"[ARREST SERVER] Capturing {__instance.name}'s inventory after ammo removal");
                var persistentData = Behind_Bars.Systems.Data.PersistentPlayerData.Instance;
                if (persistentData != null)
                {
                    string snapshotId = persistentData.CreateInventorySnapshot(__instance);
                    ModLogger.Info($"[ARREST SERVER] Inventory snapshot created: {snapshotId}");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ARREST SERVER] Error capturing inventory: {ex.Message}");
            }

            // INVENTORY LOCKING: Lock inventory during jail time
            try
            {
                ModLogger.Info($"[INVENTORY] Locking inventory for arrested player: {__instance.name}");
                InventoryProcessor.LockPlayerInventory(__instance);
                ModLogger.Info($"[INVENTORY] Inventory locked - player cannot access items during jail time");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error locking inventory: {ex.Message}");
            }

            // CONTRABAND DETECTION: Additional crime detection for drugs/weapons
            if (_crimeDetectionSystem != null)
            {
                try
                {
                    ModLogger.Info($"[CONTRABAND] Performing arrest contraband search on {__instance.name}");
                    _crimeDetectionSystem.ProcessContrabandSearch(__instance);
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[CONTRABAND] Error during arrest contraband search: {ex.Message}");
                }
            }
            else
            {
                ModLogger.Error("[CONTRABAND] Crime detection system is null during arrest!");
            }

            // RAP SHEET LOGGING: Log all crimes to player's rap sheet
            try
            {
                ModLogger.Info($"[RAP SHEET] Logging arrest to rap sheet for {__instance.name}");
                LogCrimesToRapSheet(__instance);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[RAP SHEET] Error logging to rap sheet: {ex.Message}\nStack trace: {ex.StackTrace}");
            }

            // Set flag to prevent default teleportation in Player.Free()
            _jailSystemHandlingArrest = true;
            
            ModLogger.Info($"[ARREST SERVER] Server-side arrest processing complete for {__instance.name}");
        }
        
        /// <summary>
        /// CLIENT-SIDE ARREST PATCH: Handles UI and visual feedback when player is arrested
        /// This runs on the arrested player's client and manages local presentation
        /// </summary>
        [HarmonyPatch(typeof(Player), "RpcLogic___Arrest_Client_2166136261")]
        [HarmonyPostfix]
        public static void Player_ArrestClient_RpcHandler_Postfix(Player __instance)
        {
            if (_core == null)
            {
                MelonLogger.Error("Core instance is null in Player_ArrestClient_Postfix");
                return;
            }
            
            // Only handle local player arrests for now
            // TODO: For multiplayer support, also check __instance.IsOwner instead of just Player.Local
            if (__instance != Player.Local)
                return;
                
            ModLogger.Info($"[ARREST CLIENT] Player {__instance.name} arrested - handling UI and visual feedback");
            
            // Start immediate jail processing (booking, UI, camera control, etc.)
            try
            {
                MelonCoroutines.Start(_core.JailSystem.HandleImmediateArrest(__instance));
                ModLogger.Info($"[ARREST CLIENT] Jail processing coroutine started for {__instance.name}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ARREST CLIENT] Error starting jail processing: {ex.Message}");
            }
        }
        */

        [HarmonyPatch(typeof(PlayerCrimeData), "AddCrime")]
        [HarmonyPostfix]
        public static void PlayerCrimeData_AddCrime_PostFix(PlayerCrimeData __instance, Crime crime, int quantity)
        {
            try
            {
                var cds = CrimeDetectionSystem.Instance;
                if (cds != null && crime != null)
                {
                    var crimeInstance = new CrimeInstance(
                        crime: crime,
                        location: __instance.Player.transform.position,
                        severity: CalculateCrimeSeverity(crime)
                        );
                    cds.CrimeRecord.AddCrime(crimeInstance);
                    ModLogger.Debug($"[Crime Tracking] Added {crime.CrimeName} to players record");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[Crime Tracking] Error adding crime to record: {ex.Message}");
            }
        }
        
        /// <summary>
        /// SERVER-SIDE ARREST WRAPPER PATCH: Intercepts Arrest_Server() method
        /// This is the actual method the game calls, which then triggers the RPC handler
        /// </summary>
        [HarmonyPatch(typeof(Player), "Arrest_Server")]
        [HarmonyPostfix]
        public static void Player_ArrestServer_Postfix(Player __instance)
        {
            if (_core == null)
            {
                MelonLogger.Error("Core instance is null in Player_ArrestServer_Postfix");
                return;
            }

            // Only handle local player arrests for now
            // TODO: For multiplayer support, also check __instance.IsOwner instead of just Player.Local
            if (__instance != Player.Local)
                return;
            
            // Guard against duplicate execution - use immediate flag to prevent concurrent execution
            if (_isProcessingArrestServer)
            {
                ModLogger.Debug($"[ARREST SERVER] Skipping duplicate arrest processing for {__instance.name} - already processing");
                return;
            }
            
            // Secondary guard: check timestamp
            var timeSinceLastArrest = DateTime.Now - _lastArrestServerTime;
            if (timeSinceLastArrest.TotalSeconds < 2.0)
            {
                ModLogger.Debug($"[ARREST SERVER] Skipping duplicate arrest processing for {__instance.name} (last arrest was {timeSinceLastArrest.TotalSeconds:F2}s ago)");
                return;
            }
            
            // Set processing flag immediately to prevent concurrent execution
            _isProcessingArrestServer = true;
            _lastArrestServerTime = DateTime.Now;
            
            try
            {
                ModLogger.Info($"[ARREST SERVER] Player {__instance.name} arrested - processing authoritative game logic");

                //TODO: Capturing inventory and removing ammo
                // STEP 1: Remove ALL ammo BEFORE capturing inventory (ammo is never returned)
                try
                {
                    ModLogger.Info($"[ARREST SERVER] Removing ammunition before inventory capture");
                    var playerInventory = __instance.GetComponent<PlayerInventory>();
                    if (playerInventory == null)
                    {
#if !MONO
                        playerInventory = Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance;
#else
                        playerInventory = ScheduleOne.PlayerScripts.PlayerInventory.Instance;
#endif
                    }

                    if (playerInventory != null)
                    {
                        InventoryProcessor.RemoveAllAmmo(playerInventory);
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[ARREST SERVER] Error removing ammo: {ex.Message}");
                }

                // STEP 2: Capture player's inventory AFTER ammo removal
                try
                {
                    ModLogger.Info($"[ARREST SERVER] Capturing {__instance.name}'s inventory after ammo removal");
                    var persistentData = Behind_Bars.Systems.Data.PersistentPlayerData.Instance;
                    if (persistentData != null)
                    {
                        string snapshotId = persistentData.CreateInventorySnapshot(__instance);
                        ModLogger.Info($"[ARREST SERVER] Inventory snapshot created: {snapshotId}");
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[ARREST SERVER] Error capturing inventory: {ex.Message}");
                }

                // INVENTORY LOCKING: Lock inventory during jail time
                //TODO: Editor Note, Locking inventory at this location may be breaking things later
                try
                {
                    ModLogger.Info($"[INVENTORY] Locking inventory for arrested player: {__instance.name}");
                    InventoryProcessor.LockPlayerInventory(__instance);
                    ModLogger.Info($"[INVENTORY] Inventory locked - player cannot access items during jail time");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[INVENTORY] Error locking inventory: {ex.Message}");
                }

                // CONTRABAND DETECTION: Additional crime detection for drugs/weapons
                if (_crimeDetectionSystem != null)
                {
                    try
                    {
                        ModLogger.Info($"[CONTRABAND] Performing arrest contraband search on {__instance.name}");
                        _crimeDetectionSystem.ProcessContrabandSearch(__instance);
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"[CONTRABAND] Error during arrest contraband search: {ex.Message}");
                    }
                }
                else
                {
                    ModLogger.Error("[CONTRABAND] Crime detection system is null during arrest!");
                }

                // Set flag to prevent default teleportation in Player.Free()
                _jailSystemHandlingArrest = true;
                
                ModLogger.Info($"[ARREST SERVER] Server-side arrest processing complete for {__instance.name}");
            }
            finally
            {
                // Reset processing flag after a delay to catch any delayed duplicate events
                MelonCoroutines.Start(ResetArrestServerFlagAfterDelay());
            }
        }
        
        private static IEnumerator ResetArrestServerFlagAfterDelay()
        {
            yield return new WaitForSeconds(0.1f); // 100ms delay
            _isProcessingArrestServer = false;
        }
        
        /// <summary>
        /// CLIENT-SIDE ARREST WRAPPER PATCH: Intercepts Arrest_Client() method
        /// This is the actual method the game calls, which then triggers the RPC handler
        /// </summary>
        [HarmonyPatch(typeof(Player), "Arrest_Client")]
        [HarmonyPostfix]
        public static void Player_ArrestClient_Postfix(Player __instance)
        {
            if (_core == null)
            {
                MelonLogger.Error("Core instance is null in Player_ArrestClient_Postfix");
                return;
            }
            
            // Only handle local player arrests for now
            // TODO: For multiplayer support, also check __instance.IsOwner instead of just Player.Local
            if (__instance != Player.Local)
                return;
            
            // Guard against duplicate execution - use immediate flag to prevent concurrent execution
            if (_isProcessingArrestClient)
            {
                ModLogger.Debug($"[ARREST CLIENT] Skipping duplicate arrest processing for {__instance.name} - already processing");
                return;
            }
            
            // Secondary guard: check timestamp
            var timeSinceLastArrest = DateTime.Now - _lastArrestClientTime;
            if (timeSinceLastArrest.TotalSeconds < 2.0)
            {
                ModLogger.Debug($"[ARREST CLIENT] Skipping duplicate arrest processing for {__instance.name} (last arrest was {timeSinceLastArrest.TotalSeconds:F2}s ago)");
                return;
            }
            
            // Set processing flag immediately to prevent concurrent execution
            _isProcessingArrestClient = true;
            _lastArrestClientTime = DateTime.Now;
            
            try
            {
                ModLogger.Info($"[ARREST CLIENT] Player {__instance.name} arrested - handling UI and visual feedback");
            
                // Start immediate jail processing (booking, UI, camera control, etc.)
                try
                {
                    //TODO: Commented out rapsheet logic from harmony patches for the moment. We are attempting to set it in multiple places and it is running into issues.
                    /*// RAP SHEET LOGGING: Log all crimes to player's rap sheet
                    try
                    {
                        ModLogger.Info($"[RAP SHEET] Logging arrest to rap sheet for {__instance.name}");
                    
                        // DEBUG: Log CrimeData state BEFORE processing
                        if (__instance.CrimeData != null)
                        {
                            ModLogger.Info($"[RAP SHEET] [DEBUG] CrimeData is not null");
                            if (__instance.CrimeData.Crimes != null)
                            {
                                ModLogger.Info($"[RAP SHEET] [DEBUG] CrimeData.Crimes is not null, Count: {__instance.CrimeData.Crimes.Count}");
                                if (__instance.CrimeData.Crimes.Count > 0)
                                {
                                    foreach (var crimeEntry in __instance.CrimeData.Crimes)
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
                    
                        LogCrimesToRapSheet(__instance);
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"[RAP SHEET] Error logging to rap sheet: {ex.Message}\nStack trace: {ex.StackTrace}");
                    }*/
                    MelonCoroutines.Start(_core.JailSystem.HandleImmediateArrest(__instance));
                    //ModLogger.Info($"[ARREST CLIENT] Jail processing coroutine started for {__instance.name}");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[ARREST CLIENT] Error starting jail processing: {ex.Message}");
                }
            }
            finally
            {
                // Reset processing flag after a delay to catch any delayed duplicate events
                MelonCoroutines.Start(ResetArrestClientFlagAfterDelay());
            }
        }
        
        private static IEnumerator ResetArrestClientFlagAfterDelay()
        {
            yield return new WaitForSeconds(0.1f); // 100ms delay
            _isProcessingArrestClient = false;
        }
        
        /// <summary>
        /// Log all crimes to the player's rap sheet on arrest
        /// </summary>
        public static void LogCrimesToRapSheet(Player player)
        {
            if (player == null)
            {
                ModLogger.Warn("[RAP SHEET] Cannot log crimes - player is null");
                return;
            }

            try
            {
                // Get active crimes from CrimeDetectionSystem
                List<CrimeInstance> activeCrimes = null;
                
                if (_crimeDetectionSystem != null)
                {
                    activeCrimes = _crimeDetectionSystem.GetAllActiveCrimes();
                    ModLogger.Info($"[RAP SHEET] CrimeDetectionSystem found {activeCrimes?.Count ?? 0} active crimes");
                }
                
                // Get cached rap sheet (loads from file only once)
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet == null)
                {
                    ModLogger.Warn($"[RAP SHEET] Failed to get rap sheet for {player.name}");
                    return;
                }

                // Check if player is on parole - if so, pause it during incarceration
                if (rapSheet.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    if (!rapSheet.CurrentParoleRecord.IsPaused())
                    {
                        rapSheet.CurrentParoleRecord.PauseParole();
                        ModLogger.Info($"[PAROLE] Player {player.name} was on parole at time of arrest - parole time paused");

                        // Add a violation for being arrested while on parole
                        var arrestViolation = new ViolationRecord(
                            ViolationType.NewCrime,
                            "Player was arrested and charged with new crimes while on parole supervision",
                            3.0f
                        );
                        rapSheet.CurrentParoleRecord.AddViolation(arrestViolation);
                        ModLogger.Info($"[PAROLE] Added violation for arrest while on parole");
                    }
                    else
                    {
                        ModLogger.Info($"[PAROLE] Player {player.name} parole was already paused");
                    }
                }

                // Add active crimes from CrimeDetectionSystem to rap sheet
                if (activeCrimes != null && activeCrimes.Count > 0)
                {
                    ModLogger.Info($"[RAP SHEET] Adding {activeCrimes.Count} active crimes from CrimeDetectionSystem to rap sheet");
                    foreach (var crimeInstance in activeCrimes)
                    {
                        if (crimeInstance != null)
                        {
                            rapSheet.AddCrime(crimeInstance);
                            ModLogger.Info($"[RAP SHEET] Logged crime from CrimeDetectionSystem: {crimeInstance.Description} (Severity: {crimeInstance.Severity})");
                        }
                    }
                }
                
                // Get player's current crimes from CrimeData (native system)
                //TODO: Commented out logging native crimes in this spot, migrated to a harmony patch instead.
                /*ModLogger.Info($"[RAP SHEET] [DEBUG] Checking CrimeData.Crimes - CrimeData is {(player.CrimeData == null ? "NULL" : "NOT NULL")}");
                if (player.CrimeData != null)
                {
                    ModLogger.Info($"[RAP SHEET] [DEBUG] CrimeData.Crimes is {(player.CrimeData.Crimes == null ? "NULL" : "NOT NULL")}");
                    if (player.CrimeData.Crimes != null)
                    {
                        ModLogger.Info($"[RAP SHEET] [DEBUG] CrimeData.Crimes.Count = {player.CrimeData.Crimes.Count}");
                    }
                }
                
                if (player.CrimeData != null && player.CrimeData.Crimes != null && player.CrimeData.Crimes.Count > 0)
                {
                    ModLogger.Info($"[RAP SHEET] Player also has {player.CrimeData.Crimes.Count} crimes from native CrimeData system");
                    
                    // Convert player's CrimeData.Crimes to CrimeInstance records
                    foreach (var crimeEntry in player.CrimeData.Crimes)
                    {
                        if (crimeEntry.Key != null)
                        {
                            var crime = crimeEntry.Key;
                            var crimeInstance = new CrimeInstance(
                                crime: crime,
                                location: player.transform.position,
                                severity: CalculateCrimeSeverity(crime)
                            );
                            
                            rapSheet.AddCrime(crimeInstance);
                            ModLogger.Info($"[RAP SHEET] Logged crime from CrimeData: {crime.CrimeName}");
                        }
                        else
                        {
                            ModLogger.Warn($"[RAP SHEET] [DEBUG] Found null crime key in CrimeData.Crimes!");
                        }
                    }
                }
                else
                {
                    ModLogger.Warn($"[RAP SHEET] [DEBUG] No crimes found in CrimeData - CrimeData is {(player.CrimeData == null ? "NULL" : "NOT NULL")}, Crimes is {(player.CrimeData?.Crimes == null ? "NULL" : $"NOT NULL (Count: {player.CrimeData.Crimes.Count})")}");
                }*/

                // Final verification - LSI should have been calculated during AddCrime calls
                ModLogger.Info($"[RAP SHEET] === Arrest Processing Complete ===");
                ModLogger.Info($"[RAP SHEET] Total crimes recorded: {rapSheet.GetCrimeCount()}");
                ModLogger.Info($"[RAP SHEET] Current LSI Level: {rapSheet.LSILevel}");
                ModLogger.Info($"[RAP SHEET] Last LSI Assessment: {rapSheet.LastLSIAssessment}");

                // Mark rap sheet as changed - game's save system handles saving automatically
                // The game will save RapSheet data through the ISaveable system
                RapSheetManager.Instance.MarkRapSheetChanged(player);
                ModLogger.Info($"[RAP SHEET] ✓ Rap sheet marked as changed - game will save automatically");
                
                // CRITICAL: DO NOT clear crimes here - they need to remain until player is released
                // Crimes will be cleared in ClearPlayerJailStatus() when player is released from jail
                ModLogger.Info($"[RAP SHEET] Crimes logged and saved - will remain until release");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[RAP SHEET] Error logging arrest to rap sheet: {ex.Message}");
                ModLogger.Error($"[RAP SHEET] Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Calculate severity based on crime type
        /// </summary>
        private static float CalculateCrimeSeverity(Crime crime)
        {
            // Default severity
            float severity = 1.0f;
            
            // Check crime name for severity indicators
            string crimeName = crime.CrimeName?.ToLower() ?? "";
            
            // Major crimes (severity 3.0)
            if (crimeName.Contains("murder") || crimeName.Contains("manslaughter"))
                severity = 3.0f;
            // Serious crimes (severity 2.5)
            else if (crimeName.Contains("assault") && crimeName.Contains("officer"))
                severity = 2.0f;
            // Moderate crimes (severity 2.0)
            else if (crimeName.Contains("assault") || crimeName.Contains("robbery") || crimeName.Contains("possession"))
                severity = 1.5f;
            // Minor crimes (severity 1.0)
            else if (crimeName.Contains("disturbance") || crimeName.Contains("trespass"))
                severity = 1.0f;
            
            return severity;
        }
        
        // NEW: Prevent ArrestNoticeScreen from opening when our jail system is handling the arrest
        [HarmonyPatch(typeof(ArrestNoticeScreen), "RecordCrimes")]
        [HarmonyPrefix]
        public static bool ArrestNoticeScreen_RecordCrimes_Prefix()
        {
            // If our jail system is handling the arrest, prevent the arrest notice screen
            if (_jailSystemHandlingArrest)
            {
                ModLogger.Info("Jail system is handling arrest - preventing ArrestNoticeScreen from opening");
                
                // Don't run the original RecordCrimes method which would open the arrest notice screen
                return false;
            }
            
            // Let normal execution continue if we're not handling it
            return true;
        }
        
        // NEW: Also prevent ArrestNoticeScreen.Open from being called
        [HarmonyPatch(typeof(ArrestNoticeScreen), "Open")]
        [HarmonyPrefix]
        public static bool ArrestNoticeScreen_Open_Prefix()
        {
            // If our jail system is handling the arrest, prevent the arrest notice screen from opening
            if (_jailSystemHandlingArrest)
            {
                ModLogger.Info("Jail system is handling arrest - preventing ArrestNoticeScreen.Open()");
                
                // Don't run the original Open method
                return false;
            }
            
            // Let normal execution continue if we're not handling it
            return true;
        }
        
        // ====== PLAYER FREE SYSTEM PATCHES ======
        // NOTE: Game update split Free into Server/Client methods, matching the arrest system
        // - Free_Server: Handles server-side release logic
        // - Free_Client: Handles client-side UI and visual feedback
        
        // COMMENTED OUT: Old unified Free() patch (trying wrapper methods instead)
        /*
        [HarmonyPatch(typeof(Player), "Free")]
        [HarmonyPrefix]
        public static bool Player_Free_Old_Prefix(Player __instance)
        {
            // Only handle local player
            if (__instance != Player.Local)
                return true; // Let normal execution continue for other players
                
            // If our jail system is handling the arrest but hasn't cleared the flag yet, block the Free() call
            // Once we reset the flag in our release process, Player.Free() will be allowed to run
            if (_jailSystemHandlingArrest)
            {
                ModLogger.Info("Jail system handling arrest - preventing premature Player.Free() call");
                
                // Prevent the default Free() logic from running while we're still processing
                return false;
            }
            
            // Let normal execution continue if we didn't handle the arrest or have finished processing
            return true;
        }
        */
        
        /// <summary>
        /// SERVER-SIDE FREE PATCH: Prevents Free_Server() during jail processing
        /// </summary>
        [HarmonyPatch(typeof(Player), "Free_Server")]
        [HarmonyPrefix]
        public static bool Player_FreeServer_Prefix(Player __instance)
        {
            // Only handle local player
            // TODO: For multiplayer support, also check __instance.IsOwner instead of just Player.Local
            if (__instance != Player.Local)
                return true; // Let normal execution continue for other players
                
            // If our jail system is handling the arrest but hasn't cleared the flag yet, block the Free_Server() call
            // Once we reset the flag in our release process, Player.Free_Server() will be allowed to run
            if (_jailSystemHandlingArrest)
            {
                ModLogger.Info("[FREE SERVER] Jail system handling arrest - preventing premature Free_Server() call");
                
                // Prevent the default Free() logic from running while we're still processing
                return false;
            }
            
            ModLogger.Info("[FREE SERVER] Allowing Free_Server() to execute - jail processing complete");
            // Let normal execution continue if we didn't handle the arrest or have finished processing
            return true;
        }
        
        /// <summary>
        /// CLIENT-SIDE FREE PATCH: Prevents Free_Client() during jail processing
        /// </summary>
        [HarmonyPatch(typeof(Player), "Free_Client")]
        [HarmonyPrefix]
        public static bool Player_FreeClient_Prefix(Player __instance)
        {
            // Only handle local player
            // TODO: For multiplayer support, also check __instance.IsOwner instead of just Player.Local
            if (__instance != Player.Local)
                return true; // Let normal execution continue for other players
                
            // If our jail system is handling the arrest but hasn't cleared the flag yet, block the Free_Client() call
            // Once we reset the flag in our release process, Player.Free_Client() will be allowed to run
            if (_jailSystemHandlingArrest)
            {
                ModLogger.Info("[FREE CLIENT] Jail system handling arrest - preventing premature Free_Client() call");
                
                // Prevent the default Free() logic from running while we're still processing
                return false;
            }
            
            ModLogger.Info("[FREE CLIENT] Allowing Free_Client() to execute - jail processing complete");
            // Let normal execution continue if we didn't handle the arrest or have finished processing
            return true;
        }

        // LEGACY: Keep the old arrest notice handling as fallback
        [HarmonyPatch(typeof(ArrestNoticeScreen), "Close")]
        [HarmonyPostfix]
        public static void ArrestNoticeScreen_Close_Postfix(ArrestNoticeScreen __instance)
        {
            // This is now a fallback - should not normally be reached with new flow
            ModLogger.Debug("ArrestNoticeScreen closed - using legacy fallback arrest handling");
        }
        
        // ====== CRIME DETECTION PATCHES ======
        
        /// <summary>
        /// Detect NPC deaths and classify as murders or manslaughter
        /// </summary>
        [HarmonyPatch(typeof(NPC), "OnDie")]
        [HarmonyPostfix]
        public static void NPC_OnDie_Postfix(NPC __instance)
        {
            if (_crimeDetectionSystem == null || __instance == null)
                return;
                
            try
            {
                // Check if player caused this death
                var localPlayer = Player.Local;
                if (localPlayer == null)
                    return;
                    
                // Simple heuristic: if player is close and was recently in combat, assume player caused death
                float distanceToPlayer = Vector3.Distance(__instance.transform.position, localPlayer.transform.position);
                
                if (distanceToPlayer <= 10f) // Player is close to death
                {
                    // Check if this was intentional (simplified - could be enhanced with weapon tracking)
                    bool wasIntentional = true; // For now, assume most close deaths are intentional
                    
                    ModLogger.Info($"Player-caused NPC death detected: {__instance.name} (distance: {distanceToPlayer:F1}m, intentional: {wasIntentional})");
                    _crimeDetectionSystem.ProcessNPCDeath(__instance, localPlayer, wasIntentional);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error in NPC death detection: {ex.Message}");
            }
        }
        
        // NOTE: Assault detection disabled due to NPCHealth.TakeDamage method signature mismatch
        // The actual method is TakeDamage(float damage, bool isLethal = true), not TakeDamage(Impact impact)
        // TODO: Find alternative way to detect player-caused NPC damage
        
        /// <summary>
        /// Detect witness intimidation (attacking NPCs who have witnessed crimes)
        /// </summary>
        [HarmonyPatch(typeof(NPC), "OnDie")]
        [HarmonyPrefix]
        public static void NPC_OnDie_WitnessCheck_Prefix(NPC __instance)
        {
            if (_crimeDetectionSystem == null || __instance == null)
                return;
                
            try
            {
                var localPlayer = Player.Local;
                if (localPlayer == null)
                    return;
                    
                // Check if this NPC witnessed any crimes
                var witnessSystem = typeof(CrimeDetectionSystem)
                    .GetField("_witnessSystem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(_crimeDetectionSystem) as WitnessSystem;
                    
                if (witnessSystem != null && witnessSystem.HasWitnessedCrimes(__instance.ID))
                {
                    float distanceToPlayer = Vector3.Distance(__instance.transform.position, localPlayer.transform.position);
                    
                    if (distanceToPlayer <= 10f) // Player killed a witness
                    {
                        ModLogger.Info($"Witness intimidation detected: Player killed witness {__instance.name}");
                        _crimeDetectionSystem.ProcessWitnessIntimidation(__instance, localPlayer);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error in witness intimidation detection: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Detect contraband during police body searches
        /// </summary>
        [HarmonyPatch(typeof(PoliceOfficer), "ConductBodySearch")]
        [HarmonyPostfix]
        public static void PoliceOfficer_ConductBodySearch_Postfix(PoliceOfficer __instance, Player player)
        {
            ModLogger.Info($"[CONTRABAND] PoliceOfficer.ConductBodySearch patch triggered! Officer: {__instance?.name}, Player: {player?.name}");
            
            if (_crimeDetectionSystem == null)
            {
                ModLogger.Error("[CONTRABAND] Crime detection system is null!");
                return;
            }
            
            if (__instance == null)
            {
                ModLogger.Error("[CONTRABAND] Police officer instance is null!");
                return;
            }
            
            if (player == null)
            {
                ModLogger.Error("[CONTRABAND] Player instance is null!");
                return;
            }
                
            try
            {
                // Only process local player searches to avoid multiplayer issues
                if (player != Player.Local && !_core.ParoleSystem.IsPlayerOnParole(player))
                {
                    ModLogger.Info($"[CONTRABAND] Skipping non-local player: {player.name}");
                    return;
                }
                    
                ModLogger.Info($"[CONTRABAND] Processing contraband search for local player: {player.name}");
                _crimeDetectionSystem.ProcessContrabandSearch(player);
                ModLogger.Info($"[CONTRABAND] Contraband search completed for {player.name}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CONTRABAND] Error in contraband detection during body search: {ex.Message}\nStack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Public method to get crime detection system for other systems
        /// </summary>
        public static CrimeDetectionSystem GetCrimeDetectionSystem()
        {
            return _crimeDetectionSystem;
        }
        
        /// <summary>
        /// Override player visibility during mugshot capture to keep avatar on Player layer
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.SetVisibleToLocalPlayer))]
        [HarmonyPrefix]
        public static bool Player_SetVisibleToLocalPlayer_Prefix(Player __instance, ref bool vis)
        {
            // Only override for local player during mugshot
            if (__instance == Player.Local && _mugshotInProgress)
            {
                ModLogger.Debug($"Mugshot in progress - overriding SetVisibleToLocalPlayer({vis}) to true");
                vis = true;
                return true; // Continue with modified parameter
            }
            
            return true; // Normal execution
        }
    }
}
