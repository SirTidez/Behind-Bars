using Behind_Bars.Helpers;
using HarmonyLib;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
#endif
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Behind_Bars.Harmony
{
    [HarmonyPatch]
    public static class HarmonyPatches
    {
        private static Core? _core;
        private static bool _jailSystemHandlingArrest = false;
        
        public static void Initialize(Core core)
        {
            _core = core;
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
                
            ModLogger.Info($"Player {__instance.name} arrested - initiating immediate jail transfer");
            
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
        
        // NEW: Prevent Player.Free() from teleporting when our jail system has already handled release
        [HarmonyPatch(typeof(Player), "Free")]
        [HarmonyPrefix]
        public static bool Player_Free_Prefix(Player __instance)
        {
            // Only handle local player
            if (__instance != Player.Local)
                return true; // Let normal execution continue for other players
                
            // If our jail system handled the arrest, it has already freed the player properly
            if (_jailSystemHandlingArrest)
            {
                ModLogger.Info("Jail system handled arrest - preventing Player.Free() teleportation");
                
                // The flag will be reset by our jail system, so don't reset it here
                // Just prevent the default Free() logic from running
                return false;
            }
            
            // Let normal execution continue if we didn't handle the arrest
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
    }
}
