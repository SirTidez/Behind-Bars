using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Provides dynamic door detection and handling for guard navigation
    /// Automatically detects doors along the path and handles interactions
    /// </summary>
    public class DynamicDoorNavigator : MonoBehaviour
    {
#if !MONO
        public DynamicDoorNavigator(System.IntPtr ptr) : base(ptr) { }
#endif

        // Detection Settings
        public float detectionRange = 3f;
        public float doorCheckDistance = 5f;
        public LayerMask doorDetectionLayer = -1;
        public float pathCheckInterval = 0.5f;

        // Navigation
        public float pauseDistance = 2f;
        public float resumeDistance = 1f;
        public float waitAfterDoorOpen = 1f;
        public float maxDoorWaitTime = 10f;

        // Component references
        private UnityEngine.AI.NavMeshAgent navAgent;
        private JailGuardBehavior guardBehavior;
        private DoorInteractionController doorController;

        // State tracking
        private bool isDynamicNavigationActive = false;
        private Vector3 currentDestination;
        private Queue<Vector3> queuedDestinations = new Queue<Vector3>();
        private JailDoor currentDoorBlocking = null;
        private bool waitingForDoor = false;
        private float doorWaitStartTime = 0f;
        private List<JailDoor> doorsPassedThrough = new List<JailDoor>();

        // Path monitoring
        private Coroutine pathMonitoringCoroutine;
        private Vector3 lastPosition;
        private float stuckCheckTime = 0f;
        private const float stuckThreshold = 10f; // Increased from 3f to 10f to reduce false positives
        private const float minMovementDistance = 0.5f; // Increased from 0.1f to 0.5f for better detection

        // Events
        public System.Action<JailDoor> OnDoorDetected;
        public System.Action<JailDoor> OnDoorPassed;
        public System.Action<Vector3> OnDestinationReached;
        public System.Action OnNavigationBlocked;

        void Awake()
        {
            navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            guardBehavior = GetComponent<JailGuardBehavior>();
            doorController = GetComponent<DoorInteractionController>();
            
            if (doorController == null)
            {
                doorController = gameObject.AddComponent<DoorInteractionController>();
            }

            lastPosition = transform.position;
        }

        void Start()
        {
            if (navAgent == null)
            {
                ModLogger.Error($"DynamicDoorNavigator: NavMeshAgent not found on {gameObject.name}");
                enabled = false;
                return;
            }

            ModLogger.Info($"DynamicDoorNavigator initialized on {gameObject.name}");
        }

        /// <summary>
        /// Start dynamic navigation to a destination with automatic door handling
        /// </summary>
        public void NavigateToDestination(Vector3 destination)
        {
            if (!enabled || navAgent == null) return;

            currentDestination = destination;
            isDynamicNavigationActive = true;
            waitingForDoor = false;
            currentDoorBlocking = null;
            doorsPassedThrough.Clear();

            // Set initial navigation
            navAgent.SetDestination(destination);
            
            // Start path monitoring
            if (pathMonitoringCoroutine != null)
            {
                StopCoroutine(pathMonitoringCoroutine);
            }
            pathMonitoringCoroutine = StartCoroutine(MonitorPathForDoors());

            ModLogger.Info($"Dynamic navigation started to destination: {destination}");
        }

        /// <summary>
        /// Navigate through multiple waypoints with automatic door handling
        /// </summary>
        public void NavigateWithWaypoints(Queue<Vector3> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0) return;

            queuedDestinations = new Queue<Vector3>(waypoints);
            
            // Start with first waypoint
            if (queuedDestinations.Count > 0)
            {
                Vector3 firstDestination = queuedDestinations.Dequeue();
                NavigateToDestination(firstDestination);
            }
        }

        /// <summary>
        /// Stop dynamic navigation and return control to standard NavMeshAgent
        /// </summary>
        public void StopDynamicNavigation()
        {
            isDynamicNavigationActive = false;
            waitingForDoor = false;
            currentDoorBlocking = null;
            
            if (pathMonitoringCoroutine != null)
            {
                StopCoroutine(pathMonitoringCoroutine);
                pathMonitoringCoroutine = null;
            }

            ModLogger.Info($"Dynamic navigation stopped on {gameObject.name}");
        }

        /// <summary>
        /// Main coroutine that monitors the path for doors and handles interactions
        /// </summary>
        private IEnumerator MonitorPathForDoors()
        {
            while (isDynamicNavigationActive && navAgent != null)
            {
                // Check if we've reached destination
                if (!waitingForDoor && HasReachedDestination())
                {
                    OnDestinationReached?.Invoke(currentDestination);
                    
                    // Check for queued destinations
                    if (queuedDestinations.Count > 0)
                    {
                        Vector3 nextDestination = queuedDestinations.Dequeue();
                        NavigateToDestination(nextDestination);
                    }
                    else
                    {
                        StopDynamicNavigation();
                        break;
                    }
                }

                // Check for stuck navigation
                CheckForStuckMovement();

                // Look for doors in the path ahead
                if (!waitingForDoor)
                {
                    JailDoor doorAhead = DetectDoorInPath();
                    if (doorAhead != null && !doorsPassedThrough.Contains(doorAhead))
                    {
                        yield return HandleDoorInteraction(doorAhead);
                    }
                }
                
                // Check if we're waiting too long for a door
                if (waitingForDoor && Time.time - doorWaitStartTime > maxDoorWaitTime)
                {
                    ModLogger.Warn($"Waited too long for door {currentDoorBlocking?.doorName}. Resuming navigation.");
                    ResumePausedNavigation();
                }

                yield return new WaitForSeconds(pathCheckInterval);
            }
        }

        /// <summary>
        /// Detect doors in the navigation path using raycasting
        /// </summary>
        private JailDoor DetectDoorInPath()
        {
            // Cast ahead in the direction of movement
            Vector3 forward = navAgent.velocity.normalized;
            if (forward == Vector3.zero)
            {
                // If not moving, use direction to destination
                forward = (currentDestination - transform.position).normalized;
            }

            // Perform sphere cast to detect door triggers
            RaycastHit[] hits = Physics.SphereCastAll(
                transform.position + Vector3.up * 0.5f,
                0.5f,
                forward,
                doorCheckDistance,
                doorDetectionLayer
            );

            float closestDistance = float.MaxValue;
            JailDoor closestDoor = null;

            foreach (RaycastHit hit in hits)
            {
                // Check if this is a door trigger
                var doorTrigger = hit.collider.GetComponent<DoorTriggerHandler>();
                if (doorTrigger != null && doorTrigger.associatedDoor != null)
                {
                    float distance = Vector3.Distance(transform.position, hit.point);
                    if (distance < closestDistance && distance < detectionRange)
                    {
                        closestDistance = distance;
                        closestDoor = doorTrigger.associatedDoor;
                    }
                }
                
                // Also check for direct JailDoor components
                var jailController = Core.ActiveJailController;
                if (jailController != null)
                {
                    JailDoor foundDoor = FindDoorFromCollider(hit.collider);
                    if (foundDoor != null)
                    {
                        float distance = Vector3.Distance(transform.position, hit.point);
                        if (distance < closestDistance && distance < detectionRange)
                        {
                            closestDistance = distance;
                            closestDoor = foundDoor;
                        }
                    }
                }
            }

            return closestDoor;
        }

        /// <summary>
        /// Find JailDoor associated with a collider
        /// </summary>
        private JailDoor FindDoorFromCollider(Collider collider)
        {
            var jailController = Core.ActiveJailController;
            if (jailController == null) return null;

            // Check booking doors
            if (jailController.booking != null)
            {
                var doors = new JailDoor[] 
                { 
                    jailController.booking.bookingInnerDoor,
                    jailController.booking.guardDoor
                };
                
                foreach (var door in doors)
                {
                    if (door?.doorHolder != null && IsChildOf(collider.transform, door.doorHolder))
                    {
                        return door;
                    }
                }
            }

            // Check cell doors
            if (jailController.cells != null)
            {
                foreach (var cell in jailController.cells)
                {
                    if (cell.cellDoor?.doorHolder != null && 
                        IsChildOf(collider.transform, cell.cellDoor.doorHolder))
                    {
                        return cell.cellDoor;
                    }
                }
            }

            // Check holding cell doors
            if (jailController.holdingCells != null)
            {
                foreach (var cell in jailController.holdingCells)
                {
                    if (cell.cellDoor?.doorHolder != null && 
                        IsChildOf(collider.transform, cell.cellDoor.doorHolder))
                    {
                        return cell.cellDoor;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Check if a transform is a child of another transform
        /// </summary>
        private bool IsChildOf(Transform child, Transform parent)
        {
            Transform current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = current.parent;
            }
            return false;
        }

        /// <summary>
        /// Handle door interaction when one is detected
        /// </summary>
        private IEnumerator HandleDoorInteraction(JailDoor door)
        {
            ModLogger.Info($"Dynamic navigator detected door: {door.doorName} for guard {gameObject.name}");
            
            OnDoorDetected?.Invoke(door);
            
            // Check if we're close enough to need to pause
            float distanceToDoor = GetDistanceToDoor(door);
            if (distanceToDoor <= pauseDistance)
            {
                // Pause navigation
                PauseNavigationForDoor(door);
                
                // Wait for door interaction to complete
                yield return StartCoroutine(WaitForDoorInteraction(door));
                
                // Mark door as passed through
                doorsPassedThrough.Add(door);
                OnDoorPassed?.Invoke(door);
                
                // Resume navigation
                ResumePausedNavigation();
            }
        }

        /// <summary>
        /// Pause navigation when approaching a door
        /// </summary>
        private void PauseNavigationForDoor(JailDoor door)
        {
            waitingForDoor = true;
            currentDoorBlocking = door;
            doorWaitStartTime = Time.time;
            
            // Stop the NavMeshAgent
            navAgent.isStopped = true;
            
            ModLogger.Debug($"Navigation paused for door: {door.doorName}");
        }

        /// <summary>
        /// Resume navigation after door interaction
        /// </summary>
        private void ResumePausedNavigation()
        {
            waitingForDoor = false;
            currentDoorBlocking = null;
            
            // Resume navigation
            navAgent.isStopped = false;
            navAgent.SetDestination(currentDestination);
            
            ModLogger.Debug($"Navigation resumed for guard {gameObject.name}");
        }

        /// <summary>
        /// Wait for door interaction to complete
        /// </summary>
        private IEnumerator WaitForDoorInteraction(JailDoor door)
        {
            // Try to trigger door interaction through existing system
            var doorTrigger = FindDoorTriggerHandler(door);
            if (doorTrigger != null)
            {
                // Let the existing trigger system handle it
                ModLogger.Debug($"Using existing trigger system for door {door.doorName}");
            }
            else if (doorController != null && doorController.CanHandleDoor(door))
            {
                // Use door controller directly
                bool success = doorController.TryQuickDoorInteraction(door);
                if (success)
                {
                    ModLogger.Debug($"Started door interaction via controller for {door.doorName}");
                    
                    // Wait for interaction to complete
                    float startTime = Time.time;
                    while (doorController.IsBusy() && Time.time - startTime < maxDoorWaitTime)
                    {
                        yield return new WaitForSeconds(0.1f);
                    }
                }
            }
            
            // Additional wait for door animation
            yield return new WaitForSeconds(waitAfterDoorOpen);
        }

        /// <summary>
        /// Find door trigger handler for a specific door
        /// </summary>
        private DoorTriggerHandler FindDoorTriggerHandler(JailDoor door)
        {
            if (door?.doorHolder == null) return null;
            
            // Look for trigger handlers in door hierarchy
            var triggers = door.doorHolder.GetComponentsInChildren<DoorTriggerHandler>();
            foreach (var trigger in triggers)
            {
                if (trigger.associatedDoor == door)
                {
                    return trigger;
                }
            }
            
            return null;
        }

        /// <summary>
        /// Get distance to a door
        /// </summary>
        private float GetDistanceToDoor(JailDoor door)
        {
            if (door?.doorHolder == null) return float.MaxValue;
            return Vector3.Distance(transform.position, door.doorHolder.position);
        }

        /// <summary>
        /// Check if guard has reached the current destination
        /// </summary>
        private bool HasReachedDestination()
        {
            if (!navAgent.pathPending && navAgent.remainingDistance < 1.0f)
            {
                float distanceToDestination = Vector3.Distance(transform.position, currentDestination);
                return distanceToDestination <= 1.5f;
            }
            return false;
        }

        /// <summary>
        /// Check if the guard is stuck and hasn't moved
        /// </summary>
        private void CheckForStuckMovement()
        {
            float distanceMoved = Vector3.Distance(transform.position, lastPosition);
            
            if (distanceMoved < minMovementDistance)
            {
                stuckCheckTime += pathCheckInterval;
                
                if (stuckCheckTime >= stuckThreshold)
                {
                    ModLogger.Warn($"Guard {gameObject.name} appears stuck. Distance moved: {distanceMoved:F2}m");
                    OnNavigationBlocked?.Invoke();
                    
                    // Try to resolve by re-setting destination
                    if (!waitingForDoor)
                    {
                        navAgent.SetDestination(currentDestination);
                    }
                    
                    stuckCheckTime = 0f;
                }
            }
            else
            {
                stuckCheckTime = 0f;
            }
            
            lastPosition = transform.position;
        }

        /// <summary>
        /// Get list of doors that have been passed through
        /// </summary>
        public List<JailDoor> GetDoorsPassedThrough()
        {
            return new List<JailDoor>(doorsPassedThrough);
        }

        /// <summary>
        /// Check if navigation is currently active
        /// </summary>
        public bool IsNavigating()
        {
            return isDynamicNavigationActive;
        }

        /// <summary>
        /// Check if currently waiting for door interaction
        /// </summary>
        public bool IsWaitingForDoor()
        {
            return waitingForDoor;
        }

        /// <summary>
        /// Get the door currently blocking navigation
        /// </summary>
        public JailDoor GetBlockingDoor()
        {
            return currentDoorBlocking;
        }

        void OnDestroy()
        {
            if (pathMonitoringCoroutine != null)
            {
                StopCoroutine(pathMonitoringCoroutine);
            }
        }

        // Debug visualization
        void OnDrawGizmos()
        {
            if (!enabled) return;

            // Draw detection range
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, detectionRange);

            // Draw door check distance
            Gizmos.color = Color.blue;
            Vector3 forward = navAgent != null && navAgent.velocity != Vector3.zero 
                ? navAgent.velocity.normalized 
                : transform.forward;
            Gizmos.DrawRay(transform.position, forward * doorCheckDistance);

            // Draw current destination
            if (isDynamicNavigationActive)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(currentDestination, Vector3.one * 0.5f);
                Gizmos.DrawLine(transform.position, currentDestination);
            }

            // Draw doors passed through
            Gizmos.color = Color.red;
            foreach (var door in doorsPassedThrough)
            {
                if (door?.doorHolder != null)
                {
                    Gizmos.DrawWireCube(door.doorHolder.position, Vector3.one * 0.3f);
                }
            }
        }
    }
}