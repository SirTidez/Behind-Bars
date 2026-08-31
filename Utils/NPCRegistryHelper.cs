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
    /// Helper class for accessing the game's NPCRegistry without a
    /// scene-wide <c>FindObjectsOfType</c> search.
    /// </summary>
    /// <remarks>
    /// The registry reference itself is direct, but each public query allocates
    /// a new managed list and enumerates the registry, so the overall work is
    /// still O(n). Destroyed/null registry entries are filtered where the
    /// individual query can observe them.
    /// </remarks>
    public static class NPCRegistryHelper
    {
        /// <summary>
        /// Get all NPCs from the game's NPCRegistry through direct registry access.
        /// Filters out null entries (NPCs that were destroyed but not removed from registry)
        /// </summary>
        /// <returns>A newly allocated managed list of non-null registry entries,
        /// or an empty list when the registry is unavailable or access fails.</returns>
        /// <remarks>Registry enumeration is O(n), despite the direct registry
        /// access, and exceptions are logged before returning an empty list.</remarks>
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
        /// <param name="id">The identifier to compare case-insensitively.</param>
        /// <returns>The first matching NPC, or <c>null</c> when none matches or
        /// the query throws.</returns>
        /// <remarks>Every call first allocates the list returned by
        /// <see cref="GetAllNPCs"/>. Comparison uses the current
        /// <c>ToLower()</c> behavior, is not trimmed, and a null input currently
        /// throws inside the predicate before being logged and converted to
        /// <c>null</c>.</remarks>
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
        /// <returns>A newly allocated list of NPCs with both Avatar and
        /// Avatar.CurrentSettings non-null; an empty list on query failure.</returns>
        /// <remarks>Starts from a newly allocated registry snapshot and logs
        /// exceptions before returning an empty list.</remarks>
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
        /// <returns>A newly allocated list containing registry NPCs whose
        /// <c>IsConscious</c> property is true, or an empty list on failure.</returns>
        /// <remarks>The registry snapshot and filtered result are both newly
        /// allocated; filtering exceptions are logged.</remarks>
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
        /// <param name="excludePatterns">Case-sensitive substrings that exclude
        /// an NPC GameObject name.</param>
        /// <returns>A newly allocated filtered list, or an empty list when the
        /// query throws.</returns>
        /// <remarks>NPCs without a GameObject are excluded. A null pattern
        /// array or null element can throw during LINQ evaluation; no trimming,
        /// case folding, or pattern normalization is performed. Exceptions are
        /// logged and converted to an empty list.</remarks>
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




