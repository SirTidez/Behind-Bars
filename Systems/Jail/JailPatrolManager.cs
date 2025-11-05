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
#if MONO
    public sealed class JailPatrolManager : MonoBehaviour
#else
    public sealed class JailPatrolManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Patrol System")]
#endif
        public List<Transform> patrolPoints = new List<Transform>();

#if MONO
        [Header("Patrol Configuration")]
#endif
        public bool enablePatrolSystem = true;
        public float patrolPointRadius = 2f;
        public bool showPatrolGizmos = false;

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
                    ModLogger.Info($"✓ Registered patrol point: {pointName}");
                }
                else
                {
                    ModLogger.Warn($"⚠️  Could not find patrol point: {pointName}");
                }
            }

            ModLogger.Info($"✓ Initialized {patrolPoints.Count} patrol points in JailPatrolManager");
        }

        public List<Transform> GetPatrolPoints()
        {
            return new List<Transform>(patrolPoints);
        }

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

        public Transform GetRandomPatrolPoint()
        {
            if (patrolPoints.Count == 0) return null;

            int randomIndex = UnityEngine.Random.Range(0, patrolPoints.Count);
            return patrolPoints[randomIndex];
        }

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

        public bool IsPositionNearPatrolPoint(Vector3 position, out Transform nearestPoint, float threshold = -1f)
        {
            if (threshold < 0) threshold = patrolPointRadius;

            nearestPoint = GetNearestPatrolPoint(position);
            if (nearestPoint == null) return false;

            float distance = Vector3.Distance(position, nearestPoint.position);
            return distance <= threshold;
        }

        public void AddPatrolPoint(Transform patrolPoint)
        {
            if (patrolPoint != null && !patrolPoints.Contains(patrolPoint))
            {
                patrolPoints.Add(patrolPoint);
                ModLogger.Info($"✓ Added patrol point: {patrolPoint.name}");
            }
        }

        public void RemovePatrolPoint(Transform patrolPoint)
        {
            if (patrolPoints.Remove(patrolPoint))
            {
                ModLogger.Info($"✓ Removed patrol point: {patrolPoint.name}");
            }
        }

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