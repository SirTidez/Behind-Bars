using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using MelonLoader;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Drives inmate movement.  The jail lifecycle selects either the inmate's
    /// assigned tier for recreation or their assigned cell for count/bedtime;
    /// temporary wandering remains only as a startup-safe fallback.
    /// </summary>
    public class InmateBehavior : MonoBehaviour
    {
#if !MONO
        public InmateBehavior(System.IntPtr ptr) : base(ptr) { }
#endif

        // Movement parameters
        private float moveSpeed = 1.5f; // Slow wandering speed
        private float minWaitTime = 2f;
        private float maxWaitTime = 8f;
        private float minMoveDistance = 0.5f;
        private float maxMoveDistance = 2.5f;

        // Cell ownership/home bounds. These are intentionally not used to
        // constrain temporary wandering: the prison NavMesh does not align
        // exactly with individual cell colliders.
        private Bounds cellBounds;
        private int assignedCellNumber = -1;
        private bool hasCellBounds = false;

        // Movement state
        private NavMeshAgent navAgent;
        private bool isMoving = false;
        private float nextMoveTime = 0f;
        private Vector3 currentDestination;
        private float nextNavMeshDiagnosticTime = 0f;

        private enum ScheduledActivity
        {
            TemporaryWander,
            Recreation,
            ReturningToCell,
            Confined
        }

        private ScheduledActivity scheduledActivity = ScheduledActivity.TemporaryWander;
        private readonly List<Transform> scheduledRecreationAnchors = new List<Transform>();

        // Animation variations
        private float animationVariation = 0f;
        private bool isPacing = false;
        private int paceDirection = 1;

        // References
        private NPC npcComponent;
        private PrisonInmate inmateComponent;
        private Coroutine inmateBehaviorCoroutine;
        private Coroutine lookAroundCoroutine;
        private bool isShuttingDown;

        void Start()
        {
            isShuttingDown = false;
            Initialize();
        }

        void Initialize()
        {
            // Get components
            npcComponent = GetComponent<NPC>();
            inmateComponent = BBHelpers.GetComponentSafe<PrisonInmate>(gameObject);
            navAgent = GetComponent<NavMeshAgent>();

            // Ensure NavMeshAgent is present and configured
            if (navAgent == null)
            {
                ModLogger.Error($"InmateBehavior: No NavMeshAgent found on {gameObject.name}");
                enabled = false;
                return;
            }

            // Configure nav agent for temporary jail wandering.
            navAgent.enabled = true;
            navAgent.speed = moveSpeed;
            navAgent.angularSpeed = 180f;
            navAgent.stoppingDistance = 0.3f;
            navAgent.radius = 0.3f;

            // Get assigned cell number from inmate component
            if (inmateComponent != null)
            {
                assignedCellNumber = inmateComponent.assignedCell;
            }

            // Initialize cell bounds
            InitializeCellBounds();

            // The native NPC may have been activated immediately before this component was added.
            // Ensure the agent has completed its placement before the wandering coroutine starts.
            EnsureAgentOnNavMesh();

            // Add some variation to each inmate's behavior
            animationVariation = UnityEngine.Random.Range(0f, 1f);

            // Some inmates pace more, others wander more randomly
            isPacing = UnityEngine.Random.Range(0f, 1f) > 0.6f; // 40% chance to be a pacer

            // Start behavior using MelonCoroutines.
            inmateBehaviorCoroutine = MelonCoroutines.Start(InmateWanderBehavior()) as Coroutine;
        }

        void InitializeCellBounds()
        {
            if (assignedCellNumber < 0)
            {
                ModLogger.Warn($"InmateBehavior: No cell assigned to {gameObject.name}");
                return;
            }

            var jailController = Core.JailController;
            if (jailController == null || assignedCellNumber >= jailController.cells.Count)
            {
                ModLogger.Error($"InmateBehavior: Invalid cell number {assignedCellNumber}");
                return;
            }

            var cell = jailController.cells[assignedCellNumber];
            if (cell == null)
            {
                ModLogger.Error($"InmateBehavior: Cell {assignedCellNumber} is null");
                return;
            }

            // Try to get bounds from cell bounds object
            if (cell.cellBounds != null)
            {
                var boxCollider = cell.cellBounds.GetComponent<BoxCollider>();
                if (boxCollider != null)
                {
                    cellBounds = boxCollider.bounds;
                    hasCellBounds = true;
                    return;
                }
            }

            // Try to get bounds from cell transform
            if (cell.cellTransform != null)
            {
                var boxCollider = cell.cellTransform.GetComponent<BoxCollider>();
                if (boxCollider != null)
                {
                    cellBounds = boxCollider.bounds;
                    hasCellBounds = true;
                    return;
                }

                // Fallback: Create approximate bounds based on cell position
                cellBounds = new Bounds(cell.cellTransform.position, new Vector3(3f, 2.5f, 3f));
                hasCellBounds = true;
            }
            else
            {
                ModLogger.Error($"InmateBehavior: Could not determine bounds for cell {assignedCellNumber}");
            }
        }

        private IEnumerator InmateWanderBehavior()
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(0.5f, 2f)); // Initial random delay

            while (!isShuttingDown)
            {
                // MelonCoroutines are global rather than component-owned. Scene unload
                // can resume this enumerator after the native inmate has been destroyed.
                if (isShuttingDown)
                {
                    yield break;
                }

                // A missing home-cell collider must not immobilize an inmate.
                // Assigned cells are needed for custody logic, whereas the
                // temporary movement path only needs a live NavMesh agent.
                if (navAgent == null || !navAgent.enabled)
                {
                    yield return new WaitForSeconds(5f);
                    continue;
                }

                if (!EnsureAgentOnNavMesh())
                {
                    LogNavMeshDiagnostic("agent is not on a NavMesh after placement recovery");
                    nextMoveTime = Time.time + 2f;
                    yield return new WaitForSeconds(2f);
                    continue;
                }

                if (scheduledActivity == ScheduledActivity.ReturningToCell)
                {
                    if (!isMoving && TryGetCellReturnDestination(out Vector3 returnDestination) &&
                        TrySetWanderDestination(returnDestination))
                    {
                        isMoving = true;
                        currentDestination = returnDestination;
                    }
                    else if (!isMoving)
                    {
                        LogNavMeshDiagnostic("could not find a complete path back to the assigned cell");
                        nextMoveTime = Time.time + 2f;
                    }
                }
                else if (scheduledActivity == ScheduledActivity.Confined)
                {
                    if (isMoving)
                    {
                        navAgent.ResetPath();
                        isMoving = false;
                    }
                }
                // Check if it's time to move
                else if (Time.time >= nextMoveTime && !isMoving)
                {
                    Vector3 destination;
                    bool foundDestination;
                    if (scheduledActivity == ScheduledActivity.Recreation)
                    {
                        foundDestination = TryGetScheduledRecreationDestination(out destination);
                    }
                    else
                    {
                        foundDestination = TryGetTemporaryJailWanderDestination(out destination);
                    }

                    if (foundDestination && TrySetWanderDestination(destination))
                    {
                        isMoving = true;
                        currentDestination = destination;
                    }
                    else
                    {
                        LogNavMeshDiagnostic(scheduledActivity == ScheduledActivity.Recreation
                            ? "could not find a complete path to a scheduled recreation destination"
                            : "could not find a complete path to a temporary jail wander destination");
                        nextMoveTime = Time.time + 2f;
                    }
                }

                // Check if reached destination using NavMeshAgent
                if (isMoving && navAgent.enabled)
                {
                    // Check if the path is complete and we're close to destination
                    if (!navAgent.pathPending && navAgent.remainingDistance < 0.5f)
                    {
                        OnReachedDestination();
                    }
                }

                yield return new WaitForSeconds(0.5f);
            }
        }

        // Removed - no longer needed with simple movement

        private bool TryGetTemporaryJailWanderDestination(out Vector3 destination)
        {
            destination = Vector3.zero;
            if (navAgent == null || !navAgent.isOnNavMesh)
            {
                return false;
            }

            // Authored patrol markers define a jail-only navigation domain.
            // Sampling around them lets temporary inmates circulate through
            // the cell block/day-room without choosing the city's global
            // NavMesh. A future daily lifecycle can replace these candidates
            // with scheduled activity locations while retaining this path
            // validation seam.
            List<Transform> jailAnchors = Core.JailController?.GetPatrolPoints();
            const int attempts = 12;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                Vector3 requestedPoint;
                // Prefer authored shared-area markers, then try local
                // reachable movement on the last few attempts. The latter
                // keeps an inmate active if their cell connection is blocked
                // while doors or streamed NavMesh links are settling.
                if (jailAnchors != null && jailAnchors.Count > 0 && attempt < attempts - 4)
                {
                    Transform anchor = jailAnchors[UnityEngine.Random.Range(0, jailAnchors.Count)];
                    if (anchor == null)
                    {
                        continue;
                    }

                    Vector2 offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(0.35f, 2.25f);
                    requestedPoint = anchor.position + new Vector3(offset.x, 0f, offset.y);
                }
                else
                {
                    // Authoring markers are not available in every legacy
                    // jail bundle. Keep a local, complete-path fallback
                    // rather than falling back to an unrestricted world-wide
                    // NavMesh sample.
                    Vector2 offset = UnityEngine.Random.insideUnitCircle.normalized *
                                     UnityEngine.Random.Range(minMoveDistance, maxMoveDistance * 3f);
                    requestedPoint = transform.position + new Vector3(offset.x, 0f, offset.y);
                }

                if (!NavMesh.SamplePosition(requestedPoint, out NavMeshHit hit, 2.5f, NavMesh.AllAreas))
                {
                    continue;
                }

                if (Vector3.Distance(navAgent.nextPosition, hit.position) < minMoveDistance ||
                    !HasCompletePathTo(hit.position))
                {
                    continue;
                }

                destination = hit.position;
                return true;
            }

            return false;
        }

        private bool TryGetScheduledRecreationDestination(out Vector3 destination)
        {
            destination = Vector3.zero;
            if (scheduledRecreationAnchors.Count == 0 || navAgent == null || !navAgent.isOnNavMesh)
            {
                return false;
            }

            const int attempts = 12;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                Transform anchor = scheduledRecreationAnchors[UnityEngine.Random.Range(0, scheduledRecreationAnchors.Count)];
                if (anchor == null)
                {
                    continue;
                }

                Vector2 offset = UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(0.35f, 2.0f);
                Vector3 requestedPoint = anchor.position + new Vector3(offset.x, 0f, offset.y);
                if (!NavMesh.SamplePosition(requestedPoint, out NavMeshHit hit, 2.5f, NavMesh.AllAreas) ||
                    Vector3.Distance(navAgent.nextPosition, hit.position) < minMoveDistance ||
                    !HasCompletePathTo(hit.position))
                {
                    continue;
                }

                destination = hit.position;
                return true;
            }

            return false;
        }

        private bool TryGetCellReturnDestination(out Vector3 destination)
        {
            destination = Vector3.zero;
            var cell = Core.JailController?.GetCellByIndex(assignedCellNumber);
            if (cell == null)
            {
                return false;
            }

            var candidates = new List<Vector3>();
            if (cell.spawnPoints != null)
            {
                for (int index = 0; index < cell.spawnPoints.Count; index++)
                {
                    if (cell.spawnPoints[index] != null)
                    {
                        candidates.Add(cell.spawnPoints[index].position);
                    }
                }
            }

            if (cell.cellBounds != null)
            {
                candidates.Add(cell.cellBounds.position);
            }
            if (cell.cellTransform != null)
            {
                candidates.Add(cell.cellTransform.position);
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                if (!NavMesh.SamplePosition(candidates[index], out NavMeshHit hit, 4f, NavMesh.AllAreas) ||
                    !HasCompletePathTo(hit.position))
                {
                    continue;
                }

                destination = hit.position;
                return true;
            }

            return false;
        }

        private bool EnsureAgentOnNavMesh()
        {
            if (navAgent == null)
            {
                return false;
            }

            if (!navAgent.enabled)
            {
                navAgent.enabled = true;
            }

            if (navAgent.isOnNavMesh)
            {
                return true;
            }

            if (NavMesh.SamplePosition(transform.position, out var hit, 8f, NavMesh.AllAreas) &&
                navAgent.Warp(hit.position) && navAgent.isOnNavMesh)
            {
                ModLogger.Debug($"[NPC Spawn] Recovered inmate {gameObject.name} onto NavMesh at {hit.position}");
                return true;
            }

            return false;
        }

        private bool HasCompletePathTo(Vector3 destination)
        {
            if (!EnsureAgentOnNavMesh())
            {
                return false;
            }

            var path = new NavMeshPath();
            return navAgent.CalculatePath(destination, path) &&
                   path.status == NavMeshPathStatus.PathComplete &&
                   path.corners != null && path.corners.Length >= 2;
        }

        private bool TrySetWanderDestination(Vector3 destination)
        {
            if (!EnsureAgentOnNavMesh() || !HasCompletePathTo(destination))
            {
                return false;
            }

            return navAgent.SetDestination(destination);
        }

        private void LogNavMeshDiagnostic(string reason)
        {
            if (Time.time < nextNavMeshDiagnosticTime)
            {
                return;
            }

            nextNavMeshDiagnosticTime = Time.time + 5f;
            ModLogger.Warn(
                $"[NPC Spawn] Inmate {gameObject.name} {reason}. " +
                $"Position={transform.position}, AgentEnabled={navAgent?.enabled}, OnNavMesh={navAgent?.isOnNavMesh}, Cell={assignedCellNumber}");
        }

        bool IsPointValid(Vector3 point)
        {
            // Check if point is within cell bounds
            if (!cellBounds.Contains(point))
            {
                return false;
            }

            // Check if point is on NavMesh
            NavMeshHit hit;
            if (NavMesh.SamplePosition(point, out hit, 1.0f, NavMesh.AllAreas))
            {
                return true;
            }

            return false;
        }

        void SetDestination(Vector3 destination)
        {
            if (TrySetWanderDestination(destination))
            {
                currentDestination = destination;
                isMoving = true;

                // Vary movement speed slightly for each movement
                navAgent.speed = moveSpeed * UnityEngine.Random.Range(0.8f, 1.2f);
            }
        }

        void OnReachedDestination()
        {
            isMoving = false;

            if (scheduledActivity == ScheduledActivity.ReturningToCell)
            {
                scheduledActivity = ScheduledActivity.Confined;
                nextMoveTime = float.PositiveInfinity;
                return;
            }

            // Perform random idle action
            PerformIdleAction();

            // Determine wait time based on behavior type
            float waitTime;
            if (isPacing)
            {
                // Pacers wait less between movements
                waitTime = UnityEngine.Random.Range(minWaitTime * 0.3f, maxWaitTime * 0.3f);
            }
            else
            {
                // Random wanderers wait a bit
                waitTime = UnityEngine.Random.Range(minWaitTime * 0.5f, maxWaitTime * 0.7f);

                // Occasionally take a longer "rest"
                if (UnityEngine.Random.Range(0f, 1f) > 0.9f)
                {
                    waitTime *= 1.5f;
                }
            }

            nextMoveTime = Time.time + waitTime;
        }

        void PerformIdleAction()
        {
            if (isShuttingDown)
            {
                return;
            }

            // Random idle actions when stopped
            float rand = UnityEngine.Random.Range(0f, 1f);
            if (rand < 0.3f)
            {
                // Look around
                if (lookAroundCoroutine == null)
                {
                    lookAroundCoroutine = MelonCoroutines.Start(LookAround()) as Coroutine;
                }
            }
            else if (rand < 0.5f)
            {
                // Face a random direction
                transform.rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0, 360), 0);
            }
            // Otherwise just stand
        }

        IEnumerator LookAround()
        {
            if (isShuttingDown)
            {
                lookAroundCoroutine = null;
                yield break;
            }

            // Look left and right
            float startRotation = transform.eulerAngles.y;

            // Look left
            float targetRotation = startRotation - 45f;
            float elapsedTime = 0f;
            while (elapsedTime < 0.5f)
            {
                if (isShuttingDown)
                {
                    lookAroundCoroutine = null;
                    yield break;
                }
                transform.rotation = Quaternion.Euler(0, Mathf.Lerp(startRotation, targetRotation, elapsedTime / 0.5f), 0);
                elapsedTime += Time.deltaTime;
                yield return null;
            }

            yield return new WaitForSeconds(0.2f);

            // Look right
            targetRotation = startRotation + 90f;
            elapsedTime = 0f;
            while (elapsedTime < 1f)
            {
                if (isShuttingDown)
                {
                    lookAroundCoroutine = null;
                    yield break;
                }
                float currentY = transform.eulerAngles.y;
                transform.rotation = Quaternion.Euler(0, Mathf.Lerp(currentY, targetRotation, elapsedTime / 1f), 0);
                elapsedTime += Time.deltaTime;
                yield return null;
            }

            yield return new WaitForSeconds(0.2f);

            // Return to start
            elapsedTime = 0f;
            while (elapsedTime < 0.5f)
            {
                if (isShuttingDown)
                {
                    lookAroundCoroutine = null;
                    yield break;
                }
                float currentY = transform.eulerAngles.y;
                transform.rotation = Quaternion.Euler(0, Mathf.Lerp(currentY, startRotation, elapsedTime / 0.5f), 0);
                elapsedTime += Time.deltaTime;
                yield return null;
            }

            lookAroundCoroutine = null;
        }

        void OnDrawGizmosSelected()
        {
            if (hasCellBounds)
            {
                // Draw cell bounds
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(cellBounds.center, cellBounds.size);

                // Draw current destination
                if (isMoving)
                {
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireSphere(currentDestination, 0.3f);
                    Gizmos.DrawLine(transform.position, currentDestination);
                }
            }
        }

        void OnDisable()
        {
            StopBehaviorCoroutines();
        }

        void OnDestroy()
        {
            StopBehaviorCoroutines();
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        private void StopBehaviorCoroutines()
        {
            isShuttingDown = true;

            if (inmateBehaviorCoroutine != null)
            {
                MelonCoroutines.Stop(inmateBehaviorCoroutine);
                inmateBehaviorCoroutine = null;
            }

            if (lookAroundCoroutine != null)
            {
                MelonCoroutines.Stop(lookAroundCoroutine);
                lookAroundCoroutine = null;
            }

            StopAllCoroutines();
        }

        public void SetCellNumber(int cellNumber)
        {
            assignedCellNumber = cellNumber;
            InitializeCellBounds();
        }

        public void SetPacingBehavior(bool shouldPace)
        {
            isPacing = shouldPace;
        }

        public void SetMovementSpeed(float speed)
        {
            moveSpeed = Mathf.Clamp(speed, 0.5f, 3f);
            if (navAgent != null)
            {
                navAgent.speed = moveSpeed;
            }
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void BeginScheduledRecreation(List<Transform> recreationAnchors)
        {
            scheduledRecreationAnchors.Clear();
            if (recreationAnchors != null)
            {
                for (int index = 0; index < recreationAnchors.Count; index++)
                {
                    if (recreationAnchors[index] != null)
                    {
                        scheduledRecreationAnchors.Add(recreationAnchors[index]);
                    }
                }
            }

            scheduledActivity = ScheduledActivity.Recreation;
            if (navAgent != null && navAgent.enabled && navAgent.isOnNavMesh)
            {
                navAgent.ResetPath();
            }
            isMoving = false;
            nextMoveTime = Time.time + UnityEngine.Random.Range(0.2f, 1.5f);
        }

        public void ReturnToAssignedCell()
        {
            scheduledActivity = ScheduledActivity.ReturningToCell;
            scheduledRecreationAnchors.Clear();
            if (navAgent != null && navAgent.enabled && navAgent.isOnNavMesh)
            {
                navAgent.ResetPath();
            }
            isMoving = false;
            nextMoveTime = Time.time;
        }

        public int GetAssignedCellNumber()
        {
            return assignedCellNumber;
        }

        public bool IsConfinedToAssignedCell()
        {
            if (scheduledActivity != ScheduledActivity.Confined)
            {
                return false;
            }

            var cell = Core.JailController?.GetCellByIndex(assignedCellNumber);
            Transform home = cell?.cellBounds ?? cell?.cellTransform;
            return home != null && Vector3.Distance(transform.position, home.position) <= 3.5f;
        }
    }
}
