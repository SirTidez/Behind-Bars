using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Discovers and queries authored jail patrol anchors.
    /// This manager is a point registry/diagnostic utility; it does not spawn patrol NPCs,
    /// drive an agent, or pathfind between points. <see cref="GetPatrolRoute"/> currently
    /// returns only the requested endpoints.
    /// </summary>
#if MONO
    public sealed class JailPatrolManager : MonoBehaviour
#else
    public sealed class JailPatrolManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Patrol System")]
#endif
        // Ordered scene anchors used by callers such as inmate/guard movement systems.
        // Initialize rebuilds this list and silently omits missing named points.
        public List<Transform> patrolPoints = new List<Transform>();

#if MONO
        [Header("Patrol Configuration")]
#endif
        // Configuration for proximity queries and editor gizmos. These values do not
        // schedule patrols or move an NPC by themselves.
        public bool enablePatrolSystem = true;
        public float patrolPointRadius = 2f;
        public bool showPatrolGizmos = false;

        /// <summary>
        /// Rebuild the patrol-point list from the authored jail hierarchy.
        /// </summary>
        /// <param name="jailRoot">Root containing named points or a <c>PatrolPoints</c> child.</param>
        /// <remarks>When disabled, existing points are left untouched.</remarks>
        public void Initialize(Transform jailRoot)
        {
            if (!enablePatrolSystem)
            {
                ModLogger.Info("Patrol system disabled, skipping initialization");
                return;
            }

            InitializePatrolPoints(jailRoot);
        }

        void InitializePatrolPoints(Transform jailRoot)
        {
            patrolPoints.Clear();

            string[] patrolPointNames = {
                "Patrol_Upper_Right",
                "Patrol_Upper_Left",
                "Patrol_Lower_Left",
                "Patrol_Kitchen",
                "Patrol_Laundry"
            };

            foreach (string pointName in patrolPointNames)
            {
                Transform patrolPoint = jailRoot.Find(pointName);
                if (patrolPoint == null)
                {
                    Transform patrolContainer = jailRoot.Find("PatrolPoints");
                    if (patrolContainer != null)
                    {
                        patrolPoint = patrolContainer.Find(pointName);
                    }
                }

                if (patrolPoint != null)
                {
                    patrolPoints.Add(patrolPoint);
                    ModLogger.Debug($"✓ Registered patrol point: {pointName}");
                }
                else
                {
                    ModLogger.Warn($"⚠️  Could not find patrol point: {pointName}");
                }
            }

            ModLogger.Debug($"✓ Initialized {patrolPoints.Count} patrol points in JailPatrolManager");
        }

        /// <summary>
        /// Return a shallow copy of the current patrol-point list.
        /// </summary>
        /// <returns>A new list whose transforms remain scene-owned.</returns>
        public List<Transform> GetPatrolPoints()
        {
            return new List<Transform>(patrolPoints);
        }

        /// <summary>
        /// Find the scene point with the smallest Euclidean distance to a position.
        /// </summary>
        /// <param name="position">World-space position to compare.</param>
        /// <returns>The nearest point, or <c>null</c> when no points are registered.</returns>
        public Transform GetNearestPatrolPoint(Vector3 position)
        {
            if (patrolPoints.Count == 0) return null;

            Transform nearest = patrolPoints[0];
            float nearestDistance = Vector3.Distance(position, nearest.position);

            for (int i = 1; i < patrolPoints.Count; i++)
            {
                float distance = Vector3.Distance(position, patrolPoints[i].position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = patrolPoints[i];
                }
            }

            return nearest;
        }

        /// <summary>
        /// Select a uniformly random registered patrol point.
        /// </summary>
        /// <returns>A random point, or <c>null</c> when no points are registered.</returns>
        public Transform GetRandomPatrolPoint()
        {
            if (patrolPoints.Count == 0) return null;

            int randomIndex = UnityEngine.Random.Range(0, patrolPoints.Count);
            return patrolPoints[randomIndex];
        }

        /// <summary>
        /// Build the current minimal route representation between two points.
        /// </summary>
        /// <param name="startPoint">Route start.</param>
        /// <param name="endPoint">Route end.</param>
        /// <returns>A list containing start and, when distinct, end; no intermediate pathfinding occurs.</returns>
        public List<Transform> GetPatrolRoute(Transform startPoint, Transform endPoint)
        {
            List<Transform> route = new List<Transform>();

            if (startPoint == null || endPoint == null)
            {
                ModLogger.Warn("Cannot create patrol route: start or end point is null");
                return route;
            }

            route.Add(startPoint);

            if (startPoint != endPoint)
            {
                route.Add(endPoint);
            }

            return route;
        }

        /// <summary>
        /// Test whether the nearest registered point is within a threshold distance.
        /// </summary>
        /// <param name="position">World-space position to compare.</param>
        /// <param name="nearestPoint">Nearest registered point, or <c>null</c> when none exist.</param>
        /// <param name="threshold">Distance threshold; negative values use <see cref="patrolPointRadius"/>.</param>
        public bool IsPositionNearPatrolPoint(Vector3 position, out Transform nearestPoint, float threshold = -1f)
        {
            if (threshold < 0) threshold = patrolPointRadius;

            nearestPoint = GetNearestPatrolPoint(position);
            if (nearestPoint == null) return false;

            float distance = Vector3.Distance(position, nearestPoint.position);
            return distance <= threshold;
        }

        /// <summary>
        /// Append a non-null point when it is not already registered.
        /// </summary>
        /// <param name="patrolPoint">Scene transform to add.</param>
        public void AddPatrolPoint(Transform patrolPoint)
        {
            if (patrolPoint != null && !patrolPoints.Contains(patrolPoint))
            {
                patrolPoints.Add(patrolPoint);
                ModLogger.Info($"✓ Added patrol point: {patrolPoint.name}");
            }
        }

        /// <summary>
        /// Remove a registered point by transform identity.
        /// </summary>
        /// <param name="patrolPoint">Point to remove.</param>
        public void RemovePatrolPoint(Transform patrolPoint)
        {
            if (patrolPoints.Remove(patrolPoint))
            {
                ModLogger.Info($"✓ Removed patrol point: {patrolPoint.name}");
            }
        }

        /// <summary>
        /// Remove all registered patrol-point references without destroying scene objects.
        /// </summary>
        public void ClearPatrolPoints()
        {
            patrolPoints.Clear();
            ModLogger.Info("✓ Cleared all patrol points");
        }

        void OnDrawGizmos()
        {
            if (!showPatrolGizmos || patrolPoints == null) return;

            Gizmos.color = Color.yellow;
            foreach (Transform patrolPoint in patrolPoints)
            {
                if (patrolPoint != null)
                {
                    Gizmos.DrawWireSphere(patrolPoint.position, patrolPointRadius);
                    Gizmos.DrawIcon(patrolPoint.position, "PatrolPoint", true);
                }
            }

            if (patrolPoints.Count > 1)
            {
                Gizmos.color = Color.blue;
                for (int i = 0; i < patrolPoints.Count - 1; i++)
                {
                    if (patrolPoints[i] != null && patrolPoints[i + 1] != null)
                    {
                        Gizmos.DrawLine(patrolPoints[i].position, patrolPoints[i + 1].position);
                    }
                }
            }
        }

        void OnDrawGizmosSelected()
        {
            if (patrolPoints == null) return;

            Gizmos.color = Color.green;
            foreach (Transform patrolPoint in patrolPoints)
            {
                if (patrolPoint != null)
                {
                    Gizmos.DrawSphere(patrolPoint.position, 0.5f);
                }
            }
        }

        /// <summary>
        /// Log current patrol configuration and point positions for diagnostics.
        /// </summary>
        public void LogPatrolStatus()
        {
            ModLogger.Info($"=== PATROL SYSTEM STATUS ===");
            ModLogger.Info($"Enabled: {enablePatrolSystem}");
            ModLogger.Info($"Patrol Points: {patrolPoints.Count}");
            ModLogger.Info($"Patrol Radius: {patrolPointRadius}");

            for (int i = 0; i < patrolPoints.Count; i++)
            {
                Transform point = patrolPoints[i];
                string status = point != null ? $"Position: {point.position}" : "NULL";
                ModLogger.Info($"  [{i}] {point?.name}: {status}");
            }
            ModLogger.Info($"=========================");
        }
    }
}
