using System.Collections;
using Behind_Bars.Helpers;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;
using MelonLoader;

#if MONO
using FishNet;
#else
using Il2CppFishNet;
#endif

namespace Behind_Bars.Systems
{
    public class NPCCreationSystem
    {
        public void Initialize()
        {
            ModLogger.Info("NPC Creation System initialized");
        }

        public void Cleanup()
        {
            ModLogger.Info("NPC Creation System cleaned up");
        }
    }
}
