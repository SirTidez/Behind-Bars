using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Controls inmate NPC behavior within their assigned cells
    /// Makes them walk around randomly as if going insane from confinement
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

        // Cell bounds
        private Bounds cellBounds;
        private int assignedCellNumber = -1;
        private bool hasCellBounds = false;

        // Movement state
        private NavMeshAgent navAgent;
        private bool isMoving = false;
        private float nextMoveTime = 0f;
        private Vector3 currentDestination;

        // Animation variations
        private float animationVariation = 0f;
        private bool isPacing = false;
        private int paceDirection = 1;

        // References
        private NPC npcComponent;
        private PrisonInmate inmateComponent;

        void Start()
        {
            Initialize();
        }

        void Initialize()
        {
            // Get components
            npcComponent = GetComponent<NPC>();
            inmateComponent = GetComponent<PrisonInmate>();
            navAgent = GetComponent<NavMeshAgent>();

            // Ensure NavMeshAgent is present and configured
            if (navAgent == null)
            {
                ModLogger.Error($"InmateBehavior: No NavMeshAgent found on {gameObject.name}");
                enabled = false;
                return;
            }

            // Configure nav agent for cell movement
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

            // Add some variation to each inmate's behavior
            animationVariation = UnityEngine.Random.Range(0f, 1f);

            // Some inmates pace more, others wander more randomly
            isPacing = UnityEngine.Random.Range(0f, 1f) > 0.6f; // 40% chance to be a pacer

            // Start behavior using MelonCoroutines
            MelonCoroutines.Start(InmateCellBehavior());
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

        IEnumerator InmateCellBehavior()
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(0.5f, 2f)); // Initial random delay

            while (enabled)
            {
                if (!hasCellBounds || navAgent == null || !navAgent.enabled)
                {
                    yield return new WaitForSeconds(5f);
                    continue;
                }

                // Check if it's time to move
                if (Time.time >= nextMoveTime && !isMoving)
                {
                    // Get a random destination in the cell
                    Vector3 destination = GetRandomPointInCell();

                    // Use NavMeshAgent to move there
                    if (navAgent.SetDestination(destination))
                    {
                        isMoving = true;
                        currentDestination = destination;
                    }
                    else
                    {
                        ModLogger.Warn($"Inmate {gameObject.name} failed to set destination");
                        nextMoveTime = Time.time + 1f; // Try again in 1 second
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

        Vector3 GetRandomPointInCell()
        {
            // Generate a random point within cell bounds - using full bounds for realistic movement
            float x = cellBounds.center.x + UnityEngine.Random.Range(-cellBounds.size.x * 0.45f, cellBounds.size.x * 0.45f);
            float z = cellBounds.center.z + UnityEngine.Random.Range(-cellBounds.size.z * 0.45f, cellBounds.size.z * 0.45f);
            float y = transform.position.y; // Keep at current floor level

            Vector3 randomPoint = new Vector3(x, y, z);

            // Sample the NavMesh to get a valid position
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(randomPoint, out hit, 2.0f, UnityEngine.AI.NavMesh.AllAreas))
            {
                return hit.position; // Return the valid NavMesh position
            }

            // If no valid position found, return current position
            return transform.position;
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
            if (navAgent != null && navAgent.enabled)
            {
                currentDestination = destination;
                navAgent.SetDestination(destination);
                isMoving = true;

                // Vary movement speed slightly for each movement
                navAgent.speed = moveSpeed * UnityEngine.Random.Range(0.8f, 1.2f);
            }
        }

        void OnReachedDestination()
        {
            isMoving = false;

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
            // Random idle actions when stopped
            float rand = UnityEngine.Random.Range(0f, 1f);
            if (rand < 0.3f)
            {
                // Look around
                MelonCoroutines.Start(LookAround());
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
            // Look left and right
            float startRotation = transform.eulerAngles.y;

            // Look left
            float targetRotation = startRotation - 45f;
            float elapsedTime = 0f;
            while (elapsedTime < 0.5f)
            {
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
                float currentY = transform.eulerAngles.y;
                transform.rotation = Quaternion.Euler(0, Mathf.Lerp(currentY, startRotation, elapsedTime / 0.5f), 0);
                elapsedTime += Time.deltaTime;
                yield return null;
            }
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

        void OnDestroy()
        {
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
    }
}