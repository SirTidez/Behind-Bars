using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using HarmonyLib;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Combat;
using Il2CppScheduleOne.Police;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using ScheduleOne.NPCs;
using ScheduleOne.Combat;
using ScheduleOne.Police;
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

        // NEW: Intercept arrests immediately at the Player.Arrest() level
        [HarmonyPatch(typeof(Player), "RpcLogic___Arrest_2166136261")]
        [HarmonyPostfix]
        public static void Player_RpcLogic_Arrest_Postfix(Player __instance)
        {
            if (_core == null)
            {
                MelonLogger.Error("Core instance is null in Player_RpcLogic_Arrest_Postfix");
                return;
            }
            
            // Only handle local player arrests for now
            if (__instance != Player.Local)
                return;
                
            ModLogger.Info($"[ARREST] Player {__instance.name} arrested - performing contraband search and jail processing");
            
            // CONTRABAND DETECTION: Search for drugs/weapons during arrest
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
            
            // Start immediate jail processing
            MelonCoroutines.Start(_core.JailSystem.HandleImmediateArrest(__instance));
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
        
        // NEW: Prevent Player.Free() from teleporting during our jail system processing, but allow it during our release
        [HarmonyPatch(typeof(Player), "Free")]
        [HarmonyPrefix]
        public static bool Player_Free_Prefix(Player __instance)
        {
            // Only handle local player
            if (__instance != Player.Local)
                return true; // Let normal execution continue for other players
                
            //// If our jail system is handling the arrest but hasn't cleared the flag yet, block the Free() call
            //// Once we reset the flag in our release process, Player.Free() will be allowed to run
            //if (_jailSystemHandlingArrest)
            //{
            //    ModLogger.Info("Jail system handling arrest - preventing premature Player.Free() call");
                
            //    // Prevent the default Free() logic from running while we're still processing
            //    return false;
            //}
            
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
                    
                    // Exception: if NPC died from vehicle collision, might be accidental
                    if (__instance.Movement != null && __instance.Movement.timeSinceHitByCar < 2f)
                    {
                        wasIntentional = false; // Vehicle deaths are often accidental
                    }
                    
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
                if (player != Player.Local)
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
    }
}
