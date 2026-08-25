using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Crimes;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.NPCs;
using HarmonyLib;
using System.Collections;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Combat;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using ScheduleOne.Persistence;
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
using UnityEngine.SceneManagement;

namespace Behind_Bars.Harmony
{
    [HarmonyPatch]
    public static class HarmonyPatches
    {
        private static Core? _core;
        private static bool _jailSystemHandlingArrest = false;
        private static CrimeDetectionSystem? _crimeDetectionSystem;
        private static bool _mugshotInProgress = false;
        private static bool _fingerprintScanInputLocked = false;
        private static bool _guardLockdownInputLocked = false;
        private static bool _nativeLoadingHoldRequested;
        private static bool _nativeLoadingCloseAllowed;
        private static bool _nativeLoadingCloseCoroutineActive;
        private static int _nativeLoadingSession;
        private static string _nativeLoadingStatus = "Preparing Behind Bars...";
        
        // Cooldown tracking for assault detection to prevent duplicates
        private static Dictionary<string, float> _assaultCooldown = new Dictionary<string, float>();
        private const float ASSAULT_COOLDOWN_SECONDS = 3f; // Prevent duplicate processing within 3 seconds
        private const float PENDING_CIVILIAN_ASSAULT_SECONDS = 1.5f;
        private static PendingCivilianAssault? _pendingCivilianAssault;
        private static readonly HashSet<string> _persistedArrestCrimeEventKeys = new HashSet<string>();
        private static int _nativeArrestCaptureSequence;

        internal sealed class NativeArrestCrimeSnapshot
        {
            public int CaptureId { get; }
            public Crime Crime { get; }
            public int Quantity { get; }
            public Vector3 Location { get; }

            public NativeArrestCrimeSnapshot(int captureId, Crime crime, int quantity, Vector3 location)
            {
                CaptureId = captureId;
                Crime = crime;
                Quantity = Mathf.Max(0, quantity);
                Location = location;
            }
        }

        /// <summary>
        /// Correlates the game's generic native Assault submission with the civilian damage
        /// event that caused it. We retain damage handling, but defer the native wanted
        /// escalation to the witness/police-call path owned by CrimeDetectionSystem.
        /// </summary>
        private sealed class PendingCivilianAssault
        {
            public string PlayerKey = string.Empty;
            public string VictimId = string.Empty;
            public float ExpiresAt;
            public bool NativeAddCrimeSuppressed;
        }
        
        public static void Initialize(Core core)
        {
            _core = core;
            _crimeDetectionSystem = new CrimeDetectionSystem();
        }

        /// <summary>
        /// Keeps the game's existing loading screen visible during the final load phase
        /// while Behind Bars initializes its scene-owned systems.
        /// </summary>
        public static void BeginNativeLoadingScreenHold(int gameplaySession)
        {
            _nativeLoadingSession = gameplaySession;
            _nativeLoadingHoldRequested = true;
            _nativeLoadingCloseAllowed = false;
            _nativeLoadingCloseCoroutineActive = false;
            _nativeLoadingStatus = "Preparing Behind Bars...";
            ModLogger.Debug($"[Native Loading] Requested final loading-screen step for gameplay session {gameplaySession}");
        }

        /// <summary>
        /// Updates the game-owned loading-screen text for the active Behind Bars scene
        /// startup. The session check prevents stale coroutines from writing into a later load.
        /// </summary>
        public static void SetNativeLoadingScreenStatus(int gameplaySession, string status)
        {
            if (!_nativeLoadingHoldRequested || _nativeLoadingSession != gameplaySession || string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            _nativeLoadingStatus = status;
            ModLogger.Debug($"[Native Loading] {status}");
        }

        /// <summary>
        /// Completes the final Behind Bars startup step and lets the game close its own
        /// loading screen on the next close request.
        /// </summary>
        public static void CompleteNativeLoadingScreenHold(int gameplaySession)
        {
            if (!_nativeLoadingHoldRequested || _nativeLoadingSession != gameplaySession)
            {
                return;
            }

            _nativeLoadingHoldRequested = false;
            ModLogger.Debug($"[Native Loading] Behind Bars startup complete for gameplay session {gameplaySession}");
        }

        /// <summary>
        /// Cancels a pending native loading-screen hold during scene exit. This never
        /// attempts to close a loading screen outside the Main scene.
        /// </summary>
        public static void CancelNativeLoadingScreenHold()
        {
            _nativeLoadingHoldRequested = false;
            _nativeLoadingCloseAllowed = false;
            _nativeLoadingCloseCoroutineActive = false;
            _nativeLoadingSession = 0;
            _nativeLoadingStatus = "Preparing Behind Bars...";
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
        /// Suppresses only the local player's native punch input while the
        /// fingerprint scanner owns the primary mouse button for hand dragging.
        /// </summary>
        public static void SetFingerprintScanInputLocked(bool locked)
        {
            _fingerprintScanInputLocked = locked;
        }

        /// <summary>
        /// Suppress the local player's combat input while the guard-assault blackout and
        /// custody transfer are running. Scanner ownership remains independent.
        /// </summary>
        public static void SetGuardLockdownInputLocked(bool locked)
        {
            _guardLockdownInputLocked = locked;
        }

        /// <summary>
        /// Clears transient scene-only patch state before the Main scene unloads.
        /// Saved RapSheet data is intentionally not touched here; the live crime record
        /// and pending witness work are scene-owned and must be discarded.
        /// </summary>
        public static void ResetSceneTransientState()
        {
            _jailSystemHandlingArrest = false;
            _mugshotInProgress = false;
            _fingerprintScanInputLocked = false;
            _guardLockdownInputLocked = false;
            CancelNativeLoadingScreenHold();
            _assaultCooldown.Clear();
            _pendingCivilianAssault = null;
            _persistedArrestCrimeEventKeys.Clear();
            _nativeArrestCaptureSequence = 0;
            _crimeDetectionSystem?.ResetSceneRuntimeState();
            Core.Instance?.JailSystem?.ClearSceneTransientParoleArrestCauses();
            ModLogger.Debug("Harmony transient jail state reset for scene exit");
        }

        /// <summary>
        /// Takes an in-memory copy of the game's current crime collection before custody
        /// clears that transient native state. The snapshot is scoped to one arrest.
        /// </summary>
        internal static List<NativeArrestCrimeSnapshot> CaptureNativeCrimesForArrest(Player player)
        {
            var snapshots = new List<NativeArrestCrimeSnapshot>();
            if (player?.CrimeData?.Crimes == null)
            {
                return snapshots;
            }

            int captureId = ++_nativeArrestCaptureSequence;
            foreach (var crimeEntry in player.CrimeData.Crimes)
            {
                if (crimeEntry.Key == null || crimeEntry.Value <= 0)
                {
                    continue;
                }

                snapshots.Add(new NativeArrestCrimeSnapshot(
                    captureId,
                    crimeEntry.Key,
                    crimeEntry.Value,
                    player.transform.position));
            }

            ModLogger.Info($"[RAP SHEET] Captured {snapshots.Sum(snapshot => snapshot.Quantity)} native crime event(s) before clearing CrimeData for {player.name}");
            return snapshots;
        }

        [HarmonyPatch(typeof(PunchController), "UpdateInput")]
        [HarmonyPrefix]
        private static bool PunchController_UpdateInput_Prefix(PunchController __instance)
        {
            return !(_fingerprintScanInputLocked || _guardLockdownInputLocked) || __instance == null || __instance.player != Player.Local;
        }

        [HarmonyPatch(typeof(LoadManager), "GetLoadStatusText")]
        [HarmonyPostfix]
        private static void LoadManager_GetLoadStatusText_Postfix(ref string __result)
        {
            if (ShouldHoldNativeLoadingScreen())
            {
                __result = _nativeLoadingStatus;
            }
        }

        [HarmonyPatch(typeof(LoadingScreen), "Close")]
        [HarmonyPrefix]
        private static bool LoadingScreen_Close_Prefix(LoadingScreen __instance)
        {
            if (_nativeLoadingCloseAllowed || !ShouldHoldNativeLoadingScreen())
            {
                return true;
            }

            if (!_nativeLoadingCloseCoroutineActive)
            {
                _nativeLoadingCloseCoroutineActive = true;
                MelonCoroutines.Start(WaitForBehindBarsStartupThenClose(__instance, _nativeLoadingSession));
                ModLogger.Debug("[Native Loading] Delaying the game's loading-screen close for Behind Bars startup");
            }

            return false;
        }

        private static bool ShouldHoldNativeLoadingScreen()
        {
            if (!_nativeLoadingHoldRequested || !Core.IsGameplaySceneActive ||
                !string.Equals(SceneManager.GetActiveScene().name, "Main", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var loadManager = LoadManager.Instance;
                return loadManager != null && loadManager.IsLoading;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerator WaitForBehindBarsStartupThenClose(LoadingScreen loadingScreen, int gameplaySession)
        {
            const float safetyTimeoutSeconds = 90f;
            float elapsed = 0f;

            while (_nativeLoadingHoldRequested &&
                   _nativeLoadingSession == gameplaySession &&
                   Core.IsGameplaySceneActive &&
                   elapsed < safetyTimeoutSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            bool timedOut = _nativeLoadingHoldRequested && _nativeLoadingSession == gameplaySession && elapsed >= safetyTimeoutSeconds;
            if (timedOut)
            {
                ModLogger.Warn($"[Native Loading] Behind Bars startup exceeded {safetyTimeoutSeconds:F0}s; releasing the game's loading screen to avoid a permanent load lock");
                _nativeLoadingHoldRequested = false;
            }

            _nativeLoadingCloseCoroutineActive = false;
            if (!Core.IsGameplaySceneActive ||
                !string.Equals(SceneManager.GetActiveScene().name, "Main", StringComparison.OrdinalIgnoreCase) ||
                loadingScreen == null)
            {
                yield break;
            }

            try
            {
                _nativeLoadingCloseAllowed = true;
                loadingScreen.Close();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[Native Loading] Could not resume the game's loading screen close: {ex.Message}");
            }
            finally
            {
                _nativeLoadingCloseAllowed = false;
            }
        }

        /// <summary>
        /// Routes an in-jail guard attack through the single emergency-lockdown owner.
        /// </summary>
        public static bool TryBeginJailGuardAssault(GuardBehavior guard, Player player)
        {
            return GuardAssaultLockdownManager.TryBeginJailStaffAssault(guard, player, _crimeDetectionSystem);
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
                    playerMovement.CanMove = true;
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
                var persistentData = Core.ResolvePersistentPlayerData();
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
        [HarmonyPrefix]
        public static bool PlayerCrimeData_AddCrime_Prefix(PlayerCrimeData __instance, Crime crime, int quantity)
        {
            if (!TrySuppressImmediateCivilianAssault(__instance, crime))
            {
                return true;
            }

            ModLogger.Info("[Crime Tracking] Deferred native civilian Assault escalation until a witness calls police");
            return false;
        }

        [HarmonyPatch(typeof(PlayerCrimeData), "AddCrime")]
        [HarmonyPostfix]
        public static void PlayerCrimeData_AddCrime_PostFix(PlayerCrimeData __instance, Crime crime, int quantity)
        {
            try
            {
                // Harmony may still run postfixes after a prefix skips the original method.
                // Do not turn the deferred civilian offense back into an immediate record here.
                if (TryConsumeSuppressedCivilianAssault(__instance, crime))
                {
                    return;
                }

                var cds = CrimeDetectionSystem.Instance;
                if (cds != null && crime != null)
                {
                    // The base game submits generic Assault after a street officer is hit.
                    // Convert that authoritative native-crime event here instead of relying
                    // on the NPC health callback, which does not fire for every police hit
                    // path on current IL2CPP builds.
                    if (TryConvertNativeStreetOfficerAssault(__instance, crime, quantity, out var officer))
                    {
                        cds.ProcessOfficerAssault(
                            officer,
                            __instance.Player,
                            applyWantedLevel: true,
                            persistToRapSheet: false,
                            mirrorNativeCrime: false);
                        ModLogger.Info($"[Crime Tracking] Converted native street Assault to Assault on an LEO for {__instance.Player.name}");
                        return;
                    }

                    if (!cds.ShouldMirrorNativeCrime(__instance.Player, crime))
                    {
                        ModLogger.Debug($"[Crime Tracking] Skipping mirrored native crime {crime.CrimeName} (mod-managed event already recorded)");
                        return;
                    }

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
        /// Reclassifies the generic native Assault emitted for a player striking a nearby
        /// police officer.  This runs after the game's AddCrime call, so it preserves the
        /// native wanted/pursuit behavior while replacing only the record label.
        /// </summary>
        private static bool TryConvertNativeStreetOfficerAssault(
            PlayerCrimeData crimeData,
            Crime crime,
            int quantity,
            out NPC officer)
        {
            officer = null;
            var player = crimeData?.Player;
            if (player == null || player != Player.Local || crime == null ||
                !string.Equals(crime.CrimeName, "Assault", StringComparison.OrdinalIgnoreCase) ||
                Core.ResolveJailTimeTracker().IsInJail(player))
            {
                return false;
            }

            const float officerAssaultRadius = 5f;
            PoliceOfficer closestOfficer = null;
            float closestDistance = officerAssaultRadius;
            foreach (var candidate in Behind_Bars.Helpers.Helpers.FindObjectsOfTypeSafe<PoliceOfficer>())
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy)
                {
                    continue;
                }

                float distance = Vector3.Distance(candidate.transform.position, player.transform.position);
                if (distance <= closestDistance)
                {
                    closestOfficer = candidate;
                    closestDistance = distance;
                }
            }

            if (closestOfficer == null || crimeData.Crimes == null)
            {
                return false;
            }

            int assaultQuantity = Math.Max(1, quantity);
            if (crimeData.Crimes.ContainsKey(crime))
            {
                assaultQuantity = Math.Max(assaultQuantity, crimeData.Crimes[crime]);
                crimeData.Crimes.Remove(crime);
            }

            crimeData.Crimes.Add(new AssaultOnOfficer(), assaultQuantity);
            officer = closestOfficer;
            return true;
        }

        private static void TrackPendingCivilianAssault(NPC victim, Player player)
        {
            if (victim == null || player == null)
            {
                return;
            }

            _pendingCivilianAssault = new PendingCivilianAssault
            {
                PlayerKey = GetPlayerCrimeKey(player),
                VictimId = victim.ID ?? string.Empty,
                ExpiresAt = Time.time + PENDING_CIVILIAN_ASSAULT_SECONDS
            };
        }

        private static bool TrySuppressImmediateCivilianAssault(PlayerCrimeData crimeData, Crime crime)
        {
            var pending = _pendingCivilianAssault;
            var player = crimeData?.Player;
            if (pending == null || player == null || player != Player.Local || crime == null ||
                pending.NativeAddCrimeSuppressed ||
                Time.time > pending.ExpiresAt ||
                !string.Equals(pending.PlayerKey, GetPlayerCrimeKey(player), StringComparison.Ordinal) ||
                !string.Equals(crime.CrimeName, "Assault", StringComparison.OrdinalIgnoreCase))
            {
                if (pending != null && Time.time > pending.ExpiresAt)
                {
                    _pendingCivilianAssault = null;
                }

                return false;
            }

            pending.NativeAddCrimeSuppressed = true;
            return true;
        }

        private static bool TryConsumeSuppressedCivilianAssault(PlayerCrimeData crimeData, Crime crime)
        {
            var pending = _pendingCivilianAssault;
            var player = crimeData?.Player;
            if (pending == null || player == null || crime == null ||
                !pending.NativeAddCrimeSuppressed ||
                Time.time > pending.ExpiresAt ||
                !string.Equals(pending.PlayerKey, GetPlayerCrimeKey(player), StringComparison.Ordinal) ||
                !string.Equals(crime.CrimeName, "Assault", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _pendingCivilianAssault = null;
            return true;
        }

        private static string GetPlayerCrimeKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return !string.IsNullOrEmpty(player.PlayerCode) ? player.PlayerCode : player.name ?? string.Empty;
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
                    var persistentData = Core.ResolvePersistentPlayerData();
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

        private static bool TryPersistArrestCrime(
            RapSheet rapSheet,
            CrimeInstance crimeInstance,
            ISet<string> persistedEventKeys,
            string explicitEventKey = null)
        {
            string eventKey = explicitEventKey ?? BuildCrimeEventKey(crimeInstance);
            if (persistedEventKeys.Contains(eventKey) || !_persistedArrestCrimeEventKeys.Add(eventKey))
            {
                ModLogger.Debug($"[RAP SHEET] Skipped duplicate arrest crime event: {crimeInstance.GetCrimeName()}");
                return false;
            }

            if (!rapSheet.AddCrime(crimeInstance))
            {
                _persistedArrestCrimeEventKeys.Remove(eventKey);
                return false;
            }

            persistedEventKeys.Add(eventKey);
            return true;
        }

        private static bool IsRepresentedByEnhancedCrime(NativeArrestCrimeSnapshot snapshot, IEnumerable<CrimeInstance> activeCrimes)
        {
            if (snapshot?.Crime == null || activeCrimes == null)
            {
                return false;
            }

            string nativeFamily = GetCrimeFamily(snapshot.Crime.GetType().Name, snapshot.Crime.CrimeName);
            foreach (var activeCrime in activeCrimes)
            {
                if (activeCrime == null)
                {
                    continue;
                }

                string activeFamily = GetCrimeFamily(activeCrime.GetCrimeTypeName(), activeCrime.GetCrimeName());
                if (string.Equals(nativeFamily, activeFamily, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetCrimeFamily(string typeName, string displayName)
        {
            string combined = $"{typeName} {displayName}";
            if (combined.IndexOf("assault", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "assault";
            }

            return string.IsNullOrWhiteSpace(typeName) ? displayName ?? string.Empty : typeName;
        }

        private static string BuildCrimeEventKey(CrimeInstance crimeInstance)
        {
            if (crimeInstance == null)
            {
                return "null";
            }

            Vector3 location = crimeInstance.Location;
            return string.Join("|",
                crimeInstance.GetCrimeTypeName(),
                Mathf.Round(crimeInstance.Timestamp * 100f).ToString(),
                Mathf.Round(location.x * 10f).ToString(),
                Mathf.Round(location.y * 10f).ToString(),
                Mathf.Round(location.z * 10f).ToString(),
                Mathf.Round(crimeInstance.Severity * 100f).ToString(),
                crimeInstance.CountsTowardWantedLevel ? "wanted" : "custody");
        }
        
        /// <summary>
        /// Log all crimes to the player's rap sheet on arrest
        /// </summary>
        internal static void LogCrimesToRapSheet(Player player, IEnumerable<NativeArrestCrimeSnapshot> nativeCrimeSnapshots = null)
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
                var rapSheet = Core.GetRapSheet(player);
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
                        rapSheet.PauseParole(); // Use helper method that marks RapSheet as changed
                        ModLogger.Info($"[PAROLE] Player {player.name} was on parole at time of arrest - parole time paused");

                        // Add a violation for being arrested while on parole
                        var arrestViolation = new ViolationRecord(
                            ViolationType.NewCrime,
                            "Player was arrested and charged with new crimes while on parole supervision",
                            3.0f
                        );
                        rapSheet.AddParoleViolation(arrestViolation); // Use helper method that marks RapSheet as changed
                        ModLogger.Info($"[PAROLE] Added violation for arrest while on parole");
                    }
                    else
                    {
                        ModLogger.Info($"[PAROLE] Player {player.name} parole was already paused");
                    }
                }

                var persistedEventKeys = new HashSet<string>(
                    rapSheet.GetAllCrimes().Where(crime => crime != null).Select(BuildCrimeEventKey));

                // A server arrest callback and the local custody coroutine can both reach
                // this method. Persist each enhanced event once across those callbacks.
                if (activeCrimes != null && activeCrimes.Count > 0)
                {
                    ModLogger.Info($"[RAP SHEET] Adding {activeCrimes.Count} active crimes from CrimeDetectionSystem to rap sheet");
                    foreach (var crimeInstance in activeCrimes)
                    {
                        if (crimeInstance != null && TryPersistArrestCrime(rapSheet, crimeInstance, persistedEventKeys))
                        {
                            ModLogger.Info($"[RAP SHEET] Logged crime from CrimeDetectionSystem: {crimeInstance.Description} (Severity: {crimeInstance.Severity})");
                        }
                    }
                }

                // Native CrimeData is cleared during jail entry. Persist its pre-clear
                // snapshot after enhanced records, skipping only a native event already
                // represented by the enhanced crime pipeline for this same arrest.
                if (nativeCrimeSnapshots != null)
                {
                    foreach (var snapshot in nativeCrimeSnapshots)
                    {
                        if (snapshot?.Crime == null || IsRepresentedByEnhancedCrime(snapshot, activeCrimes))
                        {
                            continue;
                        }

                        for (int occurrence = 0; occurrence < snapshot.Quantity; occurrence++)
                        {
                            var nativeCrimeInstance = new CrimeInstance(
                                snapshot.Crime,
                                snapshot.Location,
                                CalculateCrimeSeverity(snapshot.Crime));
                            string nativeEventKey = $"native:{snapshot.CaptureId}:{snapshot.Crime.GetType().FullName}:{occurrence}";
                            if (TryPersistArrestCrime(rapSheet, nativeCrimeInstance, persistedEventKeys, nativeEventKey))
                            {
                                ModLogger.Info($"[RAP SHEET] Logged captured native crime: {snapshot.Crime.CrimeName}");
                            }
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
                Core.MarkRapSheetChanged(player);
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
        
        /// <summary>
        /// Intercept jail-staff damage before the native NPC health path can create a
        /// street wanted/pursuit incident. The custody-local lockdown manager owns
        /// the legal consequence and subdual while the player is already in jail.
        /// </summary>
        [HarmonyPatch(typeof(NPCHealth), "TakeDamage")]
        [HarmonyPrefix]
        public static bool NPCHealth_TakeDamage_Prefix(NPCHealth __instance, float damage, bool isLethal)
        {
            if (_crimeDetectionSystem == null || __instance?.npc == null)
                return true;

            try
            {
                var localPlayer = Player.Local;
                if (localPlayer == null)
                    return true;

                var guard = Behind_Bars.Helpers.Helpers.GetComponentSafe<GuardBehavior>(__instance.npc.gameObject);
                float distanceToPlayer = Vector3.Distance(__instance.npc.transform.position, localPlayer.transform.position);
                if (Core.ResolveJailTimeTracker().IsInJail(localPlayer) &&
                    guard != null &&
                    distanceToPlayer <= 5f &&
                    TryBeginJailGuardAssault(guard, localPlayer))
                {
                    ModLogger.Info($"[LOCKDOWN] Suppressed native damage/wanted path for in-jail assault on {__instance.npc.name}");
                    return false;
                }

                // Native police normally submit the generic Assault crime. Own this
                // narrow player-nearby hit path so street officer assaults retain
                // ordinary pursuit while their record uses Assault on an LEO.
                bool isNativePolice = __instance.npc is PoliceOfficer ||
                    Behind_Bars.Helpers.Helpers.GetComponentSafe<PoliceOfficer>(__instance.npc.gameObject) != null;
                if (!Core.ResolveJailTimeTracker().IsInJail(localPlayer) &&
                    isNativePolice &&
                    distanceToPlayer <= 5f &&
                    TryRegisterStreetOfficerAssault(__instance.npc, localPlayer))
                {
                    return false;
                }

                // Damage must still reach the victim. Only remember the narrow native
                // Assault that the game submits for this player-caused civilian hit, then
                // let the postfix record it and let witnesses decide when police are called.
                if (!Core.ResolveJailTimeTracker().IsInJail(localPlayer) &&
                    !isNativePolice &&
                    !_crimeDetectionSystem.IsModLawEnforcementNpc(__instance.npc) &&
                    damage > 0f &&
                    distanceToPlayer <= 5f)
                {
                    TrackPendingCivilianAssault(__instance.npc, localPlayer);
                }
            }
            catch (Exception ex)
            {
                // Fail open outside the confirmed custody-local path so the game's
                // ordinary damage behavior remains intact.
                ModLogger.Warn($"[LOCKDOWN] Could not evaluate pre-damage jail assault routing: {ex.Message}");
            }

            return true;
        }

        private static bool TryRegisterStreetOfficerAssault(NPC officer, Player player)
        {
            if (_crimeDetectionSystem == null || officer == null || player == null)
            {
                return false;
            }

            string officerKey = officer.ID;
            float now = Time.time;
            if (!string.IsNullOrEmpty(officerKey) &&
                _assaultCooldown.TryGetValue(officerKey, out float lastAssaultTime) &&
                now - lastAssaultTime < ASSAULT_COOLDOWN_SECONDS)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(officerKey))
            {
                _assaultCooldown[officerKey] = now;
            }

            _crimeDetectionSystem.ProcessOfficerAssault(officer, player, applyWantedLevel: true);
            ModLogger.Info($"[Crime Tracking] Recorded street officer assault as Assault on an LEO for {player.name}");
            return true;
        }

        /// <summary>
        /// Detect assaults on civilian NPCs
        /// </summary>
        [HarmonyPatch(typeof(NPCHealth), "TakeDamage")]
        [HarmonyPostfix]
        public static void NPCHealth_TakeDamage_Postfix(NPCHealth __instance, float damage, bool isLethal)
        {
            if (_crimeDetectionSystem == null || __instance == null || __instance.npc == null)
                return;
                
            try
            {
                var localPlayer = Player.Local;
                if (localPlayer == null)
                    return;
                 
                // Skip if this is a native police officer (handled by game's system)
                if (__instance.npc is PoliceOfficer || Behind_Bars.Helpers.Helpers.GetComponentSafe<PoliceOfficer>(__instance.npc.gameObject) != null)
                    return;
                
                // Unarmed punches can deal less than five damage. Any positive damage
                // confirmed by HoursSinceAttackedByPlayer is a real assault event.
                if (damage <= 0f)
                    return;
                
                // Check if player is nearby (likely the attacker)
                float distanceToPlayer = Vector3.Distance(__instance.npc.transform.position, localPlayer.transform.position);
                
                if (distanceToPlayer <= 5f) // Player is close enough to have caused damage
                {
                    // Check cooldown to prevent duplicate processing
                    string npcId = __instance.npc.ID;
                    float currentTime = Time.time;
                    
                    if (_assaultCooldown.ContainsKey(npcId))
                    {
                        float timeSinceLastAssault = currentTime - _assaultCooldown[npcId];
                        if (timeSinceLastAssault < ASSAULT_COOLDOWN_SECONDS)
                        {
                            ModLogger.Debug($"Skipping duplicate assault detection for {__instance.npc.name} (cooldown: {timeSinceLastAssault:F2}s)");
                            return;
                        }
                    }

                    // Check if NPC was recently attacked by player
                    // HoursSinceAttackedByPlayer is 0 when just attacked, 9999 if never attacked
                    bool playerAttacked = false;
                    try
                    {
                        if (__instance.HoursSinceAttackedByPlayer == 0 || __instance.HoursSinceAttackedByPlayer < 1)
                        {
                            playerAttacked = true;
                        }
                    }
                    catch
                    {
                        // Fallback: if we can't check, use proximity as indicator
                        playerAttacked = true;
                    }

                    if (!playerAttacked)
                    {
                        return;
                    }

                    // Update cooldown
                    _assaultCooldown[npcId] = currentTime;

                    // Clean up old cooldown entries (older than cooldown period)
                    var keysToRemove = _assaultCooldown.Where(kvp => currentTime - kvp.Value > ASSAULT_COOLDOWN_SECONDS).Select(kvp => kvp.Key).ToList();
                    foreach (var key in keysToRemove)
                    {
                        _assaultCooldown.Remove(key);
                    }

                    // Jail guards use the custody-local lockdown path. This deliberately does
                    // not alter wanted state; street police retain their normal escalation.
                    if (_crimeDetectionSystem.IsModLawEnforcementNpc(__instance.npc))
                    {
                        var guard = Behind_Bars.Helpers.Helpers.GetComponentSafe<GuardBehavior>(__instance.npc.gameObject);
                        if (guard != null && TryBeginJailGuardAssault(guard, localPlayer))
                        {
                            return;
                        }

                        ModLogger.Info($"Officer assault detected: Player attacked law enforcement NPC {__instance.npc.name} (damage: {damage:F1}, distance: {distanceToPlayer:F1}m)");
                        _crimeDetectionSystem.ProcessOfficerAssault(__instance.npc, localPlayer);
                        TriggerImmediateOfficerReArrest(localPlayer, __instance.npc.name);
                        return;
                    }
                    
                    // CRITICAL: Check if this NPC recently witnessed a crime
                    // If they did, they might be taking damage from reacting (fleeing/backing away)
                    // Only count it as assault if damage is very high (clearly intentional)
                    try
                    {
                        var witnessSystem = _crimeDetectionSystem._witnessSystem;
                            
                        if (witnessSystem != null && witnessSystem.HasWitnessedCrimes(npcId))
                        {
                            // This NPC has witnessed crimes - check if damage is high enough to be intentional
                            // Low damage (< 20) while reacting is likely accidental/environmental
                            if (damage < 20f)
                            {
                                ModLogger.Debug($"Skipping assault detection for witness {__instance.npc.name} - low damage ({damage:F1}) likely from reaction, not direct attack");
                                return;
                            }
                            else
                            {
                                ModLogger.Info($"High damage ({damage:F1}) to witness {__instance.npc.name} - treating as intentional assault despite witness status");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Debug($"Could not check witness status: {ex.Message}");
                        // Continue with normal processing if we can't check
                    }

                    ModLogger.Info($"Assault detected: Player attacked {__instance.npc.name} (damage: {damage:F1}, lethal: {isLethal}, distance: {distanceToPlayer:F1}m)");
                    _crimeDetectionSystem.ProcessCivilianAssault(__instance.npc, localPlayer, isLethal);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error in assault detection: {ex.Message}");
            }
        }

        private static void TriggerImmediateOfficerReArrest(Player player, string officerName)
        {
            if (player == null)
            {
                return;
            }

            var jailTimeTracker = Core.ResolveJailTimeTracker();
            if (jailTimeTracker != null && jailTimeTracker.IsInJail(player))
            {
                ModLogger.Debug($"Skipping immediate re-arrest for {player.name}; already in jail status");
                return;
            }

            if (player.IsArrested)
            {
                ModLogger.Debug($"Skipping immediate re-arrest for {player.name}; already arrested");
                return;
            }

            if (_core?.JailSystem == null)
            {
                ModLogger.Error($"Cannot trigger immediate re-arrest after officer assault on {officerName}: JailSystem unavailable");
                return;
            }

            MelonCoroutines.Start(_core.JailSystem.HandleImmediateArrest(player));
            ModLogger.Info($"Triggered immediate re-arrest for {player.name} after assaulting officer {officerName}");
        }
        
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
