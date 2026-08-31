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
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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

        // Movement parameters are expressed in Unity world units and seconds.
        /// <summary>Base NavMesh speed for temporary and scheduled inmate movement, in world units per second.</summary>
        private float moveSpeed = 1.5f; // Slow wandering speed
        /// <summary>Lower bound for post-arrival idle time, in Unity seconds.</summary>
        private float minWaitTime = 2f;
        /// <summary>Upper bound for post-arrival idle time, in Unity seconds.</summary>
        private float maxWaitTime = 8f;
        /// <summary>Minimum candidate displacement accepted for a movement request, in world units.</summary>
        private float minMoveDistance = 0.5f;
        /// <summary>Maximum local candidate displacement used by temporary wandering, in world units.</summary>
        private float maxMoveDistance = 2.5f;

        // Cell ownership/home bounds. These are intentionally not used to
        // constrain temporary wandering: the prison NavMesh does not align
        // exactly with individual cell colliders.
        /// <summary>Resolved bounds used only for editor visualization and cell ownership diagnostics.</summary>
        private Bounds cellBounds;
        /// <summary>Assigned jail-cell index, or -1 until a canonical inmate assignment is available.</summary>
        private int assignedCellNumber = -1;
        /// <summary>Whether a real or approximate cell bounds volume was resolved.</summary>
        private bool hasCellBounds = false;

        // Movement state
        /// <summary>Native agent used by both scheduled recreation and return-to-cell movement.</summary>
        private NavMeshAgent navAgent;
        /// <summary>Reusable path object used to reject incomplete routes before assignment.</summary>
        private NavMeshPath reusablePath;
#if !MONO
        /// <summary>IL2CPP-compatible reusable corner buffer for path completeness checks.</summary>
        private readonly Il2CppStructArray<Vector3> reusablePathCorners = new Il2CppStructArray<Vector3>(2);
#else
        /// <summary>MONO reusable corner buffer for path completeness checks.</summary>
        private readonly Vector3[] reusablePathCorners = new Vector3[2];
#endif
        /// <summary>Destination for which <see cref="reusablePath"/> was most recently validated.</summary>
        private Vector3 validatedPathDestination;
        /// <summary>True only between successful path validation and the matching path assignment.</summary>
        private bool hasValidatedPath;
        /// <summary>Whether the scheduler currently owns an active movement request.</summary>
        private bool isMoving = false;
        /// <summary>Next Unity-time at which a non-moving inmate may select a destination.</summary>
        private float nextMoveTime = 0f;
        /// <summary>Last destination assigned to the NavMesh agent for diagnostics and gizmos.</summary>
        private Vector3 currentDestination;
        /// <summary>Next Unity-time at which a repeated NavMesh warning may be emitted.</summary>
        private float nextNavMeshDiagnosticTime = 0f;

        /// <summary>Activity selected by the jail lifecycle for this inmate's current custody schedule.</summary>
        private enum ScheduledActivity
        {
            /// <summary>Startup-safe movement used until a daily schedule selects another activity.</summary>
            TemporaryWander,
            /// <summary>Movement constrained to the active recreation anchors.</summary>
            Recreation,
            /// <summary>One-way movement toward the assigned cell before confinement.</summary>
            ReturningToCell,
            /// <summary>Stationary state after the assigned-cell return completes.</summary>
            Confined
        }

        /// <summary>Current lifecycle-selected activity; initialized to the startup-safe temporary mode.</summary>
        private ScheduledActivity scheduledActivity = ScheduledActivity.TemporaryWander;
        /// <summary>Filtered, live recreation anchors supplied by the jail lifecycle.</summary>
        private readonly List<Transform> scheduledRecreationAnchors = new List<Transform>();

        // Animation variations
        /// <summary>Legacy variation seed retained for compatibility; current movement code does not consume it.</summary>
        private float animationVariation = 0f;
        /// <summary>Whether post-arrival timing uses the shorter pacing profile.</summary>
        private bool isPacing = false;
        /// <summary>Legacy pacing direction retained for compatibility; current movement does not consume it.</summary>
        private int paceDirection = 1;

        // References
        /// <summary>Native NPC component resolved for the inmate object.</summary>
        private NPC npcComponent;
        /// <summary>Native prisoner component supplying the assigned cell index.</summary>
        private PrisonInmate inmateComponent;
        /// <summary>Opaque global handle for the scheduled movement coroutine.</summary>
        private Coroutine inmateBehaviorCoroutine;
        /// <summary>Opaque global handle for the optional look-around coroutine.</summary>
        private Coroutine lookAroundCoroutine;
        /// <summary>Stops global Melon coroutines from touching a disabled/destroyed native inmate.</summary>
        private bool isShuttingDown;

        /// <summary>Initializes the inmate and starts its global scheduled movement loop.</summary>
        void Start()
        {
            isShuttingDown = false;
            Initialize();
        }

        /// <summary>
        /// Resolves native components, configures the NavMesh agent, loads the assigned cell, and starts the
        /// scheduler. The native agent must be on a NavMesh before the global coroutine is allowed to move it.
        /// </summary>
        void Initialize()
        {
            // Get components
            npcComponent = GetComponent<NPC>();
            inmateComponent = BBHelpers.GetComponentSafe<PrisonInmate>(gameObject);
            navAgent = GetComponent<NavMeshAgent>();
            reusablePath = new NavMeshPath();
            hasValidatedPath = false;

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

        /// <summary>
        /// Resolves cell bounds from authored colliders, falling back to an approximate cell volume only for
        /// diagnostics. Cell bounds do not constrain temporary wandering because jail NavMesh/collider edges
        /// are not guaranteed to align.
        /// </summary>
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

        /// <summary>
        /// Runs the global scheduled movement loop. Recreation and cell-return activities use supplied
        /// authored anchors/cell points, while temporary wandering is only the startup-safe fallback; every
        /// candidate must pass NavMesh placement and complete-path validation before assignment.
        /// </summary>
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
                        TrySetValidatedWanderDestination(returnDestination))
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

                    if (foundDestination && TrySetValidatedWanderDestination(destination))
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

        // Legacy point-validation helpers remain below for compatibility; the active scheduler uses the
        // complete-path validation seam instead.

        /// <summary>
        /// Selects a temporary jail-local destination from authored patrol anchors or a local fallback sample.
        /// The fallback remains constrained to a complete jail NavMesh path and is not a substitute for a
        /// lifecycle-selected recreation/cell schedule.
        /// </summary>
        /// <param name="destination">Receives a reachable sampled destination when successful.</param>
        /// <returns>True when a complete-path candidate was found.</returns>
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

        /// <summary>
        /// Selects a sampled destination around one of the lifecycle-provided recreation anchors and rejects
        /// candidates that are too close or lack a complete NavMesh path.
        /// </summary>
        /// <param name="destination">Receives a reachable recreation destination when successful.</param>
        /// <returns>True when a complete-path recreation candidate was found.</returns>
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

        /// <summary>
        /// Selects the first complete-path cell return target, preferring authored spawn points over cell
        /// bounds and finally the cell transform.
        /// </summary>
        /// <param name="destination">Receives a reachable assigned-cell destination when successful.</param>
        /// <returns>True when an assigned-cell candidate has a complete path.</returns>
        private bool TryGetCellReturnDestination(out Vector3 destination)
        {
            destination = Vector3.zero;
            var cell = Core.JailController?.GetCellByIndex(assignedCellNumber);
            if (cell == null)
            {
                return false;
            }

            if (cell.spawnPoints != null)
            {
                for (int index = 0; index < cell.spawnPoints.Count; index++)
                {
                    Transform spawnPoint = cell.spawnPoints[index];
                    if (spawnPoint == null ||
                        !NavMesh.SamplePosition(spawnPoint.position, out NavMeshHit hit, 4f, NavMesh.AllAreas) ||
                        !HasCompletePathTo(hit.position))
                    {
                        continue;
                    }

                    destination = hit.position;
                    return true;
                }
            }

            if (cell.cellBounds != null)
            {
                if (NavMesh.SamplePosition(cell.cellBounds.position, out NavMeshHit boundsHit, 4f, NavMesh.AllAreas) &&
                    HasCompletePathTo(boundsHit.position))
                {
                    destination = boundsHit.position;
                    return true;
                }
            }

            if (cell.cellTransform != null)
            {
                if (NavMesh.SamplePosition(cell.cellTransform.position, out NavMeshHit transformHit, 4f, NavMesh.AllAreas) &&
                    HasCompletePathTo(transformHit.position))
                {
                    destination = transformHit.position;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Ensures the agent is enabled and on a NavMesh, warping to a nearby sampled point when native spawn
        /// placement left it off-mesh. It does not invent a destination or alter the assigned activity.
        /// </summary>
        /// <returns>True when the agent can safely calculate/receive a path.</returns>
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

        /// <summary>
        /// Calculates a complete path into the reusable path buffer and records the exact destination for a
        /// subsequent <see cref="TrySetValidatedWanderDestination"/> call. A path is not assigned here.
        /// </summary>
        /// <param name="destination">Candidate destination to validate.</param>
        /// <returns>True only when the agent is on-mesh and a complete path with at least two corners exists.</returns>
        private bool HasCompletePathTo(Vector3 destination)
        {
            hasValidatedPath = false;
            if (!EnsureAgentOnNavMesh())
            {
                return false;
            }

            if (reusablePath == null)
            {
                reusablePath = new NavMeshPath();
            }

            if (!navAgent.CalculatePath(destination, reusablePath) ||
                reusablePath.status != NavMeshPathStatus.PathComplete ||
                reusablePath.GetCornersNonAlloc(reusablePathCorners) < 2)
            {
                return false;
            }

            validatedPathDestination = destination;
            hasValidatedPath = true;
            return true;
        }

        /// <summary>
        /// Legacy convenience wrapper that validates and immediately assigns a temporary movement path.
        /// Active scheduled movement uses the explicit two-phase validation methods instead.
        /// </summary>
        /// <param name="destination">Candidate destination to validate and assign.</param>
        /// <returns>True when the path was validated and assigned.</returns>
        private bool TrySetWanderDestination(Vector3 destination)
        {
            if (!HasCompletePathTo(destination))
            {
                return false;
            }

            return TrySetValidatedWanderDestination(destination);
        }

        /// <summary>
        /// Assigns the previously validated reusable path only when the destination and agent state still
        /// match. Clearing <see cref="hasValidatedPath"/> after the attempt prevents stale-path reuse.
        /// </summary>
        /// <param name="destination">Destination that must match the validated path.</param>
        /// <returns>True when the reusable path was assigned to the agent.</returns>
        private bool TrySetValidatedWanderDestination(Vector3 destination)
        {
            if (!hasValidatedPath || navAgent == null || !navAgent.enabled || !navAgent.isOnNavMesh ||
                (validatedPathDestination - destination).sqrMagnitude > 0.0001f)
            {
                hasValidatedPath = false;
                return false;
            }

            bool pathAssigned = navAgent.SetPath(reusablePath);
            hasValidatedPath = false;
            return pathAssigned;
        }

        /// <summary>Rate-limits diagnostic warnings for failed placement/path selection.</summary>
        /// <param name="reason">Short explanation of the current navigation failure.</param>
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

        /// <summary>
        /// Legacy cell-bounds/NavMesh point check retained for compatibility. It is not used by the active
        /// scheduled movement loop, which requires a complete path instead of bounds membership alone.
        /// </summary>
        /// <param name="point">World-space point to inspect.</param>
        /// <returns>True when the point is inside resolved bounds and samples onto any NavMesh area.</returns>
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

        /// <summary>
        /// Legacy destination setter retained for compatibility. The active scheduler uses validated path
        /// assignment directly so callers cannot accidentally bypass its destination invariant.
        /// </summary>
        /// <param name="destination">Destination to validate and assign.</param>
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

        /// <summary>
        /// Completes the current scheduler movement. Cell-return transitions to permanent confinement; other
        /// activities perform an idle action and schedule the next movement window.
        /// </summary>
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

        /// <summary>Performs an optional look-around or random facing action after scheduled movement stops.</summary>
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

        /// <summary>
        /// Runs the optional left/right/return rotation sequence and clears its global coroutine handle on
        /// completion or shutdown.
        /// </summary>
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

        /// <summary>Draws resolved cell bounds and the active scheduler destination in the Unity editor.</summary>
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

        /// <summary>Stops global movement/look coroutines before a pooled inmate is disabled.</summary>
        void OnDisable()
        {
            StopBehaviorCoroutines();
        }

        /// <summary>Stops global movement/look coroutines before the native inmate object is destroyed.</summary>
        void OnDestroy()
        {
            StopBehaviorCoroutines();
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        /// <summary>
        /// Marks this behavior shutting down, stops both opaque Melon coroutine handles, and clears any
        /// component-owned Unity coroutines so scene unload cannot resume stale inmate work.
        /// </summary>
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

        /// <summary>Updates the assigned cell index and re-resolves its diagnostic bounds.</summary>
        /// <param name="cellNumber">Assigned jail-cell index, or a negative value when unassigned.</param>
        public void SetCellNumber(int cellNumber)
        {
            assignedCellNumber = cellNumber;
            InitializeCellBounds();
        }

        /// <summary>Chooses the shorter post-arrival wait profile when true.</summary>
        /// <param name="shouldPace">Whether this inmate should use pacing timing.</param>
        public void SetPacingBehavior(bool shouldPace)
        {
            isPacing = shouldPace;
        }

        /// <summary>Clamps and applies the inmate NavMesh speed in world units per second.</summary>
        /// <param name="speed">Requested movement speed; accepted range is 0.5 to 3 world units per second.</param>
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
        /// <summary>
        /// Replaces recreation anchors, switches the scheduler to recreation, and clears the current path.
        /// Null anchors are filtered; this method is hidden from IL2CPP because the generic list is an internal
        /// lifecycle bridge rather than an injected public surface.
        /// </summary>
        /// <param name="recreationAnchors">Candidate recreation anchors supplied by the jail lifecycle.</param>
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

        /// <summary>Clears recreation anchors and schedules a complete-path return to the assigned cell.</summary>
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

        /// <summary>Returns the assigned jail-cell index, or -1 when no assignment is known.</summary>
        public int GetAssignedCellNumber()
        {
            return assignedCellNumber;
        }

        /// <summary>
        /// Reports confinement only after the return-to-cell state has completed and the inmate remains within
        /// 3.5 world units of the assigned cell bounds/transform.
        /// </summary>
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
