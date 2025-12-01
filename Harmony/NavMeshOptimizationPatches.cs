using HarmonyLib;
using Behind_Bars.Helpers;
using System.Collections.Generic;
using System.Reflection;
#if !MONO
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.Employees;
using UnityEngine;
using UnityEngine.AI;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.NPCs;
using ScheduleOne.Management;
using ScheduleOne.Employees;
using UnityEngine;
using UnityEngine.AI;
#endif

namespace Behind_Bars.Harmony
{
    /// <summary>
    /// NavMesh optimization patches to reduce redundant pathfinding calls
    /// Based on BotanistFix optimization techniques
    /// </summary>
    [HarmonyPatch]
    public static class NavMeshOptimizationPatches
    {
        /// <summary>
        /// Cache for NavMeshUtility.GetAccessPoint to reduce redundant pathfinding calls
        /// </summary>
        [HarmonyPatch(typeof(NavMeshUtility), nameof(NavMeshUtility.GetAccessPoint))]
        private static class NavMeshUtilityGetAccessPointPatch
        {
            private struct CacheKey
            {
                public int NpcId;
                public int EntityId;

                public override bool Equals(object obj)
                {
                    if (!(obj is CacheKey))
                        return false;

                    var other = (CacheKey)obj;
                    return NpcId == other.NpcId && EntityId == other.EntityId;
                }

                public override int GetHashCode()
                {
                    return NpcId.GetHashCode() ^ EntityId.GetHashCode();
                }
            }

            private class CacheEntry
            {
                public Transform BestAccessPoint;
                public bool Reachable;
                public Vector3 NpcPosition;
                public float Timestamp;
            }

            private static readonly Dictionary<CacheKey, CacheEntry> _cache = new Dictionary<CacheKey, CacheEntry>();
            private const float CACHE_TTL = 2.75f;
            private const float REVALIDATE_DISTANCE_SQR = 1.5f;

            static bool Prefix(ITransitEntity entity, NPC npc, ref Transform __result)
            {
                if (entity == null)
                {
                    __result = null;
                    return false;
                }

                var key = new CacheKey
                {
                    NpcId = npc.GetInstanceID(),
#if MONO
                    EntityId = ((Component)entity).GetInstanceID()
#else
                    EntityId = (entity.Cast<Component>()).GetInstanceID()
#endif
                };

                if (_cache.TryGetValue(key, out var entry))
                {
                    // If cache entry is fresh and NPC hasn't moved significantly, use cached result
                    if (Time.time - entry.Timestamp < CACHE_TTL &&
                        (npc.transform.position - entry.NpcPosition).sqrMagnitude < REVALIDATE_DISTANCE_SQR)
                    {
                        __result = entry.Reachable ? entry.BestAccessPoint : null;
                        return false; // Skip original method
                    }
                }

                // Let original method run and we'll cache the result in Postfix
                return true;
            }

            static void Postfix(ITransitEntity entity, NPC npc, Transform __result)
            {
                if (entity == null) return;

                var key = new CacheKey
                {
                    NpcId = npc.GetInstanceID(),
#if MONO
                    EntityId = ((Component)entity).GetInstanceID()
#else
                    EntityId = (entity.Cast<Component>()).GetInstanceID()
#endif
                };

                _cache[key] = new CacheEntry
                {
                    BestAccessPoint = __result,
                    Reachable = __result != null,
                    NpcPosition = npc.transform.position,
                    Timestamp = Time.time
                };
            }
        }

        /// <summary>
        /// Patch for NPCMovement.CanGetTo to use cached paths
        /// This is manually patched in Core.cs due to ref parameter
        /// </summary>
        public static class NPCMovementCanGetToPatch
        {
            public static bool Prefix(NPCMovement __instance, Vector3 position, float proximityReq, ref NavMeshPath path, ref bool __result)
            {
                // Early out checks (keeping original logic)
                path = null;
                if (Vector3.Distance(position, __instance.transform.position) <= proximityReq)
                {
                    __result = true;
                    return false;
                }

                if (!__instance.Agent.isOnNavMesh)
                {
                    __result = false;
                    return false;
                }

                // Sample position on NavMesh
                NavMeshHit hit;
                if (!NavMeshUtility.SamplePosition(position, out hit, 2f, -1))
                {
                    __result = false;
                    return false;
                }

                // Check if we have this path in cache already
                NavMeshPath cachedPath = __instance.PathCache.GetPath(__instance.transform.position, hit.position, 1f);
                if (cachedPath != null)
                {
                    path = cachedPath;
                    if (path.corners.Length < 2)
                    {
                        __result = false;
                        return false;
                    }

                    float endToHitDist = Vector3.Distance(path.corners[path.corners.Length - 1], hit.position);
                    float hitToTargetDist = Vector3.Distance(hit.position, position);

                    if (endToHitDist <= proximityReq)
                    {
                        __result = hitToTargetDist <= proximityReq;
                        return false;
                    }

                    __result = false;
                    return false;
                }

                // No cached path, let the original method calculate one
                return true;
            }
        }

        /// <summary>
        /// Throttle Employee.UpdateBehaviour to reduce call frequency
        /// </summary>
        [HarmonyPatch(typeof(Employee), "UpdateBehaviour")]
        private static class EmployeeUpdateBehaviourPatch
        {
            private static readonly Dictionary<int, float> _lastUpdateTimes = new Dictionary<int, float>();
            private const float UPDATE_INTERVAL = 1.5f; // Update every 1.5 seconds instead of every frame

            static bool Prefix(Employee __instance)
            {
                int instanceId = __instance.GetInstanceID();
                float currentTime = Time.time;

                if (!_lastUpdateTimes.TryGetValue(instanceId, out float lastUpdate) ||
                    (currentTime - lastUpdate) >= UPDATE_INTERVAL)
                {
                    _lastUpdateTimes[instanceId] = currentTime;
                    return true; // Run original method
                }

                return false; // Skip original method
            }
        }
    }
}
