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
        public static void Initialize(Core core)
        {
            _core = core;
        }

        [HarmonyPatch(typeof(ArrestNoticeScreen), "Close")]
        [HarmonyPostfix]
        public static void ArrestNoticeScreen_Close_Postfix(ArrestNoticeScreen __instance)
        {
            if (_core == null)
            {
                MelonLogger.Error("Core instance is null in ArrestNoticeScreen_Close_Postfix");
                return;
            }
            // Log the closure of the arrest notice screen
            ModLogger.Info("Arrest notice screen closed, processing player arrest");
            // Get the player from the screen
            // Handle the player's arrest
            MelonCoroutines.Start(_core.JailSystem.HandlePlayerArrest(Player.Local));
        }
    }
}
