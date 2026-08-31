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
        /// Cache for NavMeshUtility.GetReachableAccessPoint to reduce redundant pathfinding calls
        /// </summary>
        [HarmonyPatch(typeof(NavMeshUtility), "GetReachableAccessPoint")]
        private static class NavMeshUtilityGetAccessPointPatch
        {
            /// <summary>
            /// Identifies one access-point query by the runtime instance IDs of its
            /// NPC and transit entity; the pair prevents results leaking between
            /// actors or destinations.
            /// </summary>
            private struct CacheKey
            {
                /// <summary>Runtime instance ID of the NPC making the query.</summary>
                public int NpcId;

                /// <summary>Runtime instance ID of the transit entity being queried.</summary>
                public int EntityId;

                /// <summary>
                /// Compares both IDs so cached results cannot cross either side of the
                /// NPC/entity query pair.
                /// </summary>
                public override bool Equals(object obj)
                {
                    if (!(obj is CacheKey))
                        return false;

                    var other = (CacheKey)obj;
                    return NpcId == other.NpcId && EntityId == other.EntityId;
                }

                /// <summary>
                /// Combines both query IDs for dictionary lookup of the cache entry.
                /// </summary>
                public override int GetHashCode()
                {
                    return NpcId.GetHashCode() ^ EntityId.GetHashCode();
                }
            }

            /// <summary>
            /// Stores the last access-point result plus the NPC position/time at which
            /// it was computed, allowing callers to reject stale or spatially invalid
            /// entries before bypassing the original pathfinding method.
            /// </summary>
            private class CacheEntry
            {
                /// <summary>The access point returned by the original query, if any.</summary>
                public Transform BestAccessPoint;

                /// <summary>Whether the original query found a reachable access point.</summary>
                public bool Reachable;

                /// <summary>NPC position captured when this entry was written.</summary>
                public Vector3 NpcPosition;

                /// <summary>Unity scaled-time timestamp when this entry was written.</summary>
                public float Timestamp;
            }

            // Entries are reusable only for the same NPC/entity pair while they are
            // fresh and the NPC remains within the revalidation radius. There is no
            // separate eviction pass; later queries overwrite entries for each key.
            private static readonly Dictionary<CacheKey, CacheEntry> _cache = new Dictionary<CacheKey, CacheEntry>();
            private const float CACHE_TTL = 2.75f; // Maximum scaled seconds to reuse a result
            private const float REVALIDATE_DISTANCE_SQR = 1.5f; // Squared NPC movement threshold

            /// <summary>
            /// Supplies a cached access point when the key, age, and NPC movement are
            /// still valid; otherwise leaves the original method enabled so its result
            /// can be captured by <see cref="Postfix"/>.
            /// </summary>
            /// <param name="entity">Transit entity used to form the cache key.</param>
            /// <param name="npc">NPC whose movement invalidates the cached result.</param>
            /// <param name="__result">Harmony result slot to fill on a cache hit.</param>
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

            /// <summary>
            /// Records the original access-point result with the NPC position and
            /// scaled-time timestamp used by the next prefix validation.
            /// </summary>
            /// <param name="entity">Transit entity used to form the cache key.</param>
            /// <param name="npc">NPC whose position is captured for revalidation.</param>
            /// <param name="__result">Access point returned by the original method.</param>
            static void Postfix(ITransitEntity entity, NPC npc, ref Transform __result)
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
            /// <summary>
            /// Reuses a path cached by the native NPC movement component when possible,
            /// preserving the original method for uncached targets. Early decisions
            /// write the ref path/result slots and return false to skip the original.
            /// </summary>
            /// <param name="__instance">Movement component performing the query.</param>
            /// <param name="position">Requested world-space destination.</param>
            /// <param name="proximityReq">Distance at which the destination is considered reached.</param>
            /// <param name="path">Harmony ref slot receiving a cached path, when available.</param>
            /// <param name="__result">Harmony result slot for the reachability decision.</param>
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
            // Per-instance timestamps throttle only the Harmony prefix. The dictionary
            // is intentionally keyed by Unity instance ID and has no scene-wide purge;
            // IDs are overwritten when an employee next passes the interval.
            private static readonly Dictionary<int, float> _lastUpdateTimes = new Dictionary<int, float>();
            private const float UPDATE_INTERVAL = 1.5f; // Update every 1.5 seconds instead of every frame

            /// <summary>
            /// Allows one Employee.UpdateBehaviour call per instance per interval and
            /// skips intermediate invocations to reduce redundant work.
            /// </summary>
            /// <param name="__instance">Employee whose update is being throttled.</param>
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
