using System.Collections.Generic;
using System.Linq;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;

#if !MONO
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
#else
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
#endif

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Helper class for accessing the game's NPCRegistry (O(1) access instead of O(n) FindObjectsOfType)
    /// Based on decompiled game code: NPCManager.NPCRegistry
    /// </summary>
    public static class NPCRegistryHelper
    {
        /// <summary>
        /// Get all NPCs from the game's NPCRegistry (O(1) access)
        /// Filters out null entries (NPCs that were destroyed but not removed from registry)
        /// </summary>
        public static List<NPC> GetAllNPCs()
        {
            try
            {
#if !MONO
                var registry = Il2CppScheduleOne.NPCs.NPCManager.NPCRegistry;
#else
                var registry = ScheduleOne.NPCs.NPCManager.NPCRegistry;
#endif
                if (registry == null)
                {
                    ModLogger.Warn("NPCRegistry is null - returning empty list");
                    return new List<NPC>();
                }

                var npcs = new List<NPC>();
                for (int i = 0; i < registry.Count; i++)
                {
                    var npc = registry[i];
                    if (npc != null)
                    {
                        npcs.Add(npc);
                    }
                }

                return npcs;
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error accessing NPCRegistry: {e.Message}");
                return new List<NPC>();
            }
        }

        /// <summary>
        /// Find an NPC by ID from the registry
        /// </summary>
        public static NPC GetNPCById(string id)
        {
            try
            {
                var allNPCs = GetAllNPCs();
                return allNPCs.FirstOrDefault(npc => npc.ID != null && npc.ID.ToLower() == id.ToLower());
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error finding NPC by ID {id}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Find NPCs with working Avatar components
        /// </summary>
        public static List<NPC> GetNPCsWithWorkingAvatars()
        {
            try
            {
                var allNPCs = GetAllNPCs();
                return allNPCs.Where(npc => npc.Avatar != null && npc.Avatar.CurrentSettings != null).ToList();
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error finding NPCs with working avatars: {e.Message}");
                return new List<NPC>();
            }
        }

        /// <summary>
        /// Get all conscious NPCs from the registry
        /// </summary>
        public static List<NPC> GetConsciousNPCs()
        {
            try
            {
                var allNPCs = GetAllNPCs();
                return allNPCs.Where(npc => npc.IsConscious).ToList();
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error finding conscious NPCs: {e.Message}");
                return new List<NPC>();
            }
        }

        /// <summary>
        /// Get NPCs excluding those with certain name patterns (e.g., exclude mod-spawned NPCs)
        /// </summary>
        public static List<NPC> GetNPCsExcluding(params string[] excludePatterns)
        {
            try
            {
                var allNPCs = GetAllNPCs();
                return allNPCs.Where(npc =>
                {
                    if (npc.gameObject == null) return false;
                    string npcName = npc.gameObject.name;
                    return !excludePatterns.Any(pattern => npcName.Contains(pattern));
                }).ToList();
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error filtering NPCs: {e.Message}");
                return new List<NPC>();
            }
        }
    }
}




