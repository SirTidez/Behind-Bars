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
    /// Consolidated door handling system for all jail NPCs
    /// Implements smooth, prison-like door transitions with proper security timing
    /// Replaces: DoorTriggerHandler, DoorInteractionController, DynamicDoorNavigator, DoorStateManager
    /// </summary>
    public class SecurityDoorBehavior : MonoBehaviour
    {
#if !MONO
        public SecurityDoorBehavior(System.IntPtr ptr) : base(ptr) { }
#endif

        [System.Serializable]
        public class DoorTransition
        {
            public string entryPointName;
            public string exitPointName;
            public string doorName;
            public Transform entryPoint;
            public Transform exitPoint;
            public JailDoor door;
            public float securityDelay = 1.0f; // Time to wait at each point for security
        }

        [System.Serializable]
        public class SecurityTimingConfig
        {
            public float approachSpeed = 2.0f;          // Slow, deliberate movement
            public float doorPointWaitTime = 1.0f;      // Security check at door point
            public float doorOpenAnimTime = 2.0f;       // Time for door to fully open
            public float escortWaitTime = 8.0f;         // Max time to wait for inmate
            public float doorCloseDelay = 1.5f;         // Time after passing through before closing
            public float positionTolerance = 0.8f;      // How close to get to door points
            public float escortCheckDistance = 3.0f;    // Distance to check for inmate following
        }

        public SecurityTimingConfig timingConfig = new SecurityTimingConfig();

        // Door State Machine
        public enum DoorState
        {
            Idle,
            DetectedTrigger,
            MovingToEntryPoint,
            SecurityCheckAtEntry,
            OpeningDoor,
            WaitingForEscort,
            MovingThroughDoor,
            MovingToExitPoint,
            SecurityCheckAtExit,
            ClosingDoor,
            DoorOperationComplete
        }

        // Current operation state
        private DoorState currentState = DoorState.Idle;
        private DoorTransition currentTransition;
        private bool isEscorting = false;
        private Player escortedInmate;
        private float stateStartTime;
        private Coroutine currentDoorOperation;

        // Component references
        private BaseJailNPC npcController;
        private UnityEngine.AI.NavMeshAgent navAgent;

        // Door mapping - matches the hierarchy structure from the image
        private Dictionary<string, DoorTransition> doorTransitions = new Dictionary<string, DoorTransition>();

        // Events
        public System.Action<DoorState> OnDoorStateChanged;
        public System.Action<string> OnDoorOperationComplete;
        public System.Action<string> OnDoorOperationFailed;

        void Awake()
        {
            npcController = GetComponent<BaseJailNPC>();
            navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        }

        void Start()
        {
            InitializeDoorMappings();

            if (navAgent != null)
            {
                // Override speed for security operations
                navAgent.speed = timingConfig.approachSpeed;
            }

            ModLogger.Info($"SecurityDoorBehavior initialized for {gameObject.name}");
        }

        /// <summary>
        /// Initialize door mappings based on the jail hierarchy structure
        /// Maps triggers to their corresponding door points and doors
        /// </summary>
        private void InitializeDoorMappings()
        {
            // Guard Room ↔ Booking Door
            doorTransitions["GuardRoomDoorTrigger_FromGuardRoom"] = new DoorTransition
            {
                entryPointName = "DoorPoint_GuardRoom",
                exitPointName = "DoorPoint_Booking",
                doorName = "Booking_GuardDoor"
            };

            doorTransitions["GuardRoomDoorTrigger_FromBooking"] = new DoorTransition
            {
                entryPointName = "DoorPoint_Booking",
                exitPointName = "DoorPoint_GuardRoom",
                doorName = "Booking_GuardDoor"
            };

            // Booking ↔ Hall Door
            doorTransitions["BookingDoorTrigger_FromBooking"] = new DoorTransition
            {
                entryPointName = "DoorPoint_Booking",
                exitPointName = "DoorPoint_Hall",
                doorName = "Booking_InnerDoor"
            };

            doorTransitions["BookingDoorTrigger_FromHall"] = new DoorTransition
            {
                entryPointName = "DoorPoint_Hall",
                exitPointName = "DoorPoint_Booking",
                doorName = "Booking_InnerDoor"
            };

            // Prison ↔ Hall Door
            doorTransitions["PrisonDoorTrigger_FromPrison"] = new DoorTransition
            {
                entryPointName = "DoorPoint_Prison",
                exitPointName = "DoorPoint_Hall",
                doorName = "Prison_EnterDoor"
            };

            doorTransitions["PrisonDoorTrigger_FromHall"] = new DoorTransition
            {
                entryPointName = "DoorPoint_Hall",
                exitPointName = "DoorPoint_Prison",
                doorName = "Prison_EnterDoor"
            };

            // Holding Cell Doors
            doorTransitions["HoldingCellDoorTrigger_0"] = new DoorTransition
            {
                entryPointName = "HoldingCell_0_DoorPoint",
                exitPointName = "HoldingCell_0_DoorPoint", // Same point for cell doors
                doorName = "HoldingCell_0_Door",
                securityDelay = 0.5f // Faster for cells
            };

            doorTransitions["HoldingCellDoorTrigger_1"] = new DoorTransition
            {
                entryPointName = "HoldingCell_1_DoorPoint",
                exitPointName = "HoldingCell_1_DoorPoint", // Same point for cell doors
                doorName = "HoldingCell_1_Door",
                securityDelay = 0.5f // Faster for cells
            };

            // Jail Cell Doors (0-11 for first 12 cells)
            for (int i = 0; i < 12; i++)
            {
                doorTransitions[$"JailCellDoorTrigger_{i}"] = new DoorTransition
                {
                    entryPointName = $"JailCell_{i}_DoorPoint",
                    exitPointName = $"JailCell_{i}_DoorPoint", // Same point for cell doors
                    doorName = $"JailCell_{i}_Door",
                    securityDelay = 0.5f // Faster for cells
                };
            }

            // Resolve Transform and JailDoor references
            ResolveDoorReferences();
        }

        /// <summary>
        /// Find and cache Transform and JailDoor references for all transitions using centralized systems
        /// </summary>
        private void ResolveDoorReferences()
        {
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("SecurityDoorBehavior: JailController not found");
                return;
            }

            foreach (var kvp in doorTransitions)
            {
                var transition = kvp.Value;
                string triggerName = kvp.Key;

                // Handle different door types
                if (triggerName.Contains("HoldingCell"))
                {
                    ResolveHoldingCellDoor(transition, jailController);
                }
                else if (triggerName.Contains("JailCell"))
                {
                    ResolveJailCellDoor(transition, jailController);
                }
                else if (jailController.booking != null)
                {
                    // Use centralized BookingArea methods for booking doors
                    transition.entryPoint = jailController.booking.GetDoorPointByName(transition.entryPointName);
                    transition.exitPoint = jailController.booking.GetDoorPointByName(transition.exitPointName);
                    transition.door = jailController.booking.GetDoorByName(transition.doorName);
                }

                if (transition.entryPoint == null)
                    ModLogger.Warn($"Could not find entry point: {transition.entryPointName}");
                if (transition.exitPoint == null)
                    ModLogger.Warn($"Could not find exit point: {transition.exitPointName}");
                if (transition.door == null)
                    ModLogger.Warn($"Could not find door: {transition.doorName}");
                else
                    ModLogger.Info($"✓ SecurityDoor resolved: {transition.doorName} with entry: {transition.entryPoint?.name} exit: {transition.exitPoint?.name}");
            }
        }

        private void ResolveHoldingCellDoor(DoorTransition transition, JailController jailController)
        {
            // Extract holding cell index from trigger name
            if (transition.doorName.Contains("HoldingCell_0"))
            {
                var holdingCell = jailController.GetHoldingCellByIndex(0);
                if (holdingCell?.cellDoor != null)
                {
                    transition.door = holdingCell.cellDoor;
                    transition.entryPoint = holdingCell.cellDoor.doorPoint;
                    transition.exitPoint = holdingCell.cellDoor.doorPoint;
                }
            }
            else if (transition.doorName.Contains("HoldingCell_1"))
            {
                var holdingCell = jailController.GetHoldingCellByIndex(1);
                if (holdingCell?.cellDoor != null)
                {
                    transition.door = holdingCell.cellDoor;
                    transition.entryPoint = holdingCell.cellDoor.doorPoint;
                    transition.exitPoint = holdingCell.cellDoor.doorPoint;
                }
            }
        }

        private void ResolveJailCellDoor(DoorTransition transition, JailController jailController)
        {
            // Extract jail cell index from trigger name
            for (int i = 0; i < 12; i++)
            {
                if (transition.doorName.Contains($"JailCell_{i}"))
                {
                    var cell = jailController.GetCellByIndex(i);
                    if (cell?.cellDoor != null)
                    {
                        transition.door = cell.cellDoor;
                        transition.entryPoint = cell.cellDoor.doorPoint;
                        transition.exitPoint = cell.cellDoor.doorPoint;
                    }
                    break;
                }
            }
        }


        /// <summary>
        /// Check if NPC is actually moving towards this door
        /// </summary>
        private bool IsMovingTowardsDoor(DoorTransition transition)
        {
            if (navAgent == null || !navAgent.hasPath) return true; // Default to true if we can't determine

            Vector3 npcPosition = transform.position;
            Vector3 entryPosition = transition.entryPoint.position;
            Vector3 exitPosition = transition.exitPoint.position;

            // Check if we're closer to entry point than exit point (approaching from correct side)
            float distanceToEntry = Vector3.Distance(npcPosition, entryPosition);
            float distanceToExit = Vector3.Distance(npcPosition, exitPosition);

            return distanceToEntry < distanceToExit;
        }

        /// <summary>
        /// Start the door operation state machine
        /// </summary>
        private void StartDoorOperation()
        {
            if (currentDoorOperation != null)
            {
                StopCoroutine(currentDoorOperation);
            }

            currentDoorOperation = StartCoroutine(ExecuteDoorOperation());
        }

        /// <summary>
        /// Main door operation coroutine - implements the smooth, secure door transition
        /// </summary>
        private IEnumerator ExecuteDoorOperation()
        {
            // 1. Move to entry door point
            ChangeState(DoorState.MovingToEntryPoint);
            yield return MoveToDoorPoint(currentTransition.entryPoint);

            // 2. Security check at entry point (brief pause)
            ChangeState(DoorState.SecurityCheckAtEntry);
            yield return new WaitForSeconds(timingConfig.doorPointWaitTime);

            // 3. Open door
            ChangeState(DoorState.OpeningDoor);
            OpenDoor();
            yield return new WaitForSeconds(timingConfig.doorOpenAnimTime);

            // 4. Optional: Wait for escorted inmate
            if (isEscorting && escortedInmate != null)
            {
                ChangeState(DoorState.WaitingForEscort);
                yield return WaitForEscortedInmate();
            }

            // 5. Move through to exit door point
            ChangeState(DoorState.MovingToExitPoint);
            yield return MoveToDoorPoint(currentTransition.exitPoint);

            // 6. Security check at exit point
            ChangeState(DoorState.SecurityCheckAtExit);
            yield return new WaitForSeconds(timingConfig.doorPointWaitTime);

            // 7. Close door after delay
            yield return new WaitForSeconds(timingConfig.doorCloseDelay);
            ChangeState(DoorState.ClosingDoor);
            CloseDoor();

            // 8. Complete
            ChangeState(DoorState.DoorOperationComplete);
            CompleteDoorOperation();
        }

        /// <summary>
        /// Move to a specific door point with proper positioning
        /// </summary>
        private IEnumerator MoveToDoorPoint(Transform targetPoint)
        {
            if (navAgent == null || targetPoint == null) yield break;

            // Set destination
            navAgent.SetDestination(targetPoint.position);

            // Wait until we reach the point
            float timeout = 15f; // Max 15 seconds to reach door point
            float startTime = Time.time;

            while (Time.time - startTime < timeout)
            {
                float distance = Vector3.Distance(transform.position, targetPoint.position);
                if (distance <= timingConfig.positionTolerance)
                {
                    break;
                }
                yield return new WaitForSeconds(0.1f);
            }

            // Face the door point properly
            Vector3 directionToPoint = (targetPoint.position - transform.position).normalized;
            if (directionToPoint != Vector3.zero)
            {
                transform.rotation = Quaternion.LookRotation(directionToPoint);
            }
        }

        /// <summary>
        /// Wait for escorted inmate to follow through the door
        /// </summary>
        private IEnumerator WaitForEscortedInmate()
        {
            if (escortedInmate == null) yield break;

            float waitStartTime = Time.time;
            float maxWaitTime = timingConfig.escortWaitTime;

            // Send message to inmate
            if (npcController != null)
            {
                npcController.TrySendNPCMessage("Follow me through the door.", 3f);
            }

            while (Time.time - waitStartTime < maxWaitTime)
            {
                float distanceToInmate = Vector3.Distance(transform.position, escortedInmate.transform.position);

                if (distanceToInmate <= timingConfig.escortCheckDistance)
                {
                    // Inmate is close, wait a bit more for them to pass through
                    yield return new WaitForSeconds(2f);
                    break;
                }

                // Remind inmate every few seconds
                if ((Time.time - waitStartTime) % 3f < 0.1f && Time.time - waitStartTime > 3f)
                {
                    if (npcController != null)
                    {
                        npcController.TrySendNPCMessage("Please come through the door.", 2f);
                    }
                }

                yield return new WaitForSeconds(0.5f);
            }

            if (Time.time - waitStartTime >= maxWaitTime)
            {
                ModLogger.Warn($"SecurityDoorBehavior: Timeout waiting for inmate to follow through door");
                if (npcController != null)
                {
                    npcController.TrySendNPCMessage("Proceeding without inmate.", 2f);
                }
            }
        }

        /// <summary>
        /// Open the door
        /// </summary>
        private void OpenDoor()
        {
            if (currentTransition?.door != null)
            {
                currentTransition.door.OpenDoor();
                ModLogger.Debug($"SecurityDoorBehavior: Opened door {currentTransition.doorName}");
            }
        }

        /// <summary>
        /// Close the door
        /// </summary>
        private void CloseDoor()
        {
            if (currentTransition?.door != null)
            {
                currentTransition.door.CloseDoor();
                ModLogger.Debug($"SecurityDoorBehavior: Closed door {currentTransition.doorName}");
            }
        }

        /// <summary>
        /// Complete the door operation and reset state
        /// </summary>
        private void CompleteDoorOperation()
        {
            string doorName = currentTransition?.doorName ?? "Unknown";
            ModLogger.Info($"SecurityDoorBehavior: Completed door operation for {doorName}");

            OnDoorOperationComplete?.Invoke(doorName);

            // Reset state
            ChangeState(DoorState.Idle);
            currentTransition = null;
            isEscorting = false;
            escortedInmate = null;
            currentDoorOperation = null;
        }

        /// <summary>
        /// Change door state and notify listeners
        /// </summary>
        private void ChangeState(DoorState newState)
        {
            if (currentState == newState) return;

            DoorState oldState = currentState;
            currentState = newState;
            stateStartTime = Time.time;

            OnDoorStateChanged?.Invoke(newState);
            ModLogger.Debug($"SecurityDoorBehavior: {gameObject.name} door state: {oldState} → {newState}");
        }

        #region Utility Methods

        /// <summary>
        /// Find transform by name using centralized BookingArea - no more discovery each time
        /// </summary>
        private Transform FindTransformByName(string name)
        {
            var jailController = Core.JailController;
            if (jailController?.booking == null) return null;

            return jailController.booking.GetDoorPointByName(name);
        }

        /// <summary>
        /// Find JailDoor by name using centralized BookingArea - no more discovery each time
        /// </summary>
        private JailDoor FindDoorByName(string doorName)
        {
            var jailController = Core.JailController;
            if (jailController?.booking == null) return null;

            return jailController.booking.GetDoorByName(doorName);
        }

        public bool IsBusy() => currentState != DoorState.Idle;
        public DoorState GetCurrentState() => currentState;

        /// <summary>
        /// Force stop current door operation
        /// </summary>
        public void StopDoorOperation()
        {
            if (currentDoorOperation != null)
            {
                StopCoroutine(currentDoorOperation);
                currentDoorOperation = null;
            }

            ChangeState(DoorState.Idle);
            currentTransition = null;
            isEscorting = false;
            escortedInmate = null;
        }

        /// <summary>
        /// IntakeOfficer integration - Open holding cell door with automated security handling
        /// </summary>
        public bool OpenHoldingCellDoor(int cellIndex, Player prisoner = null)
        {
            string triggerName = $"HoldingCellDoorTrigger_{cellIndex}";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// IntakeOfficer integration - Open jail cell door with automated security handling
        /// </summary>
        public bool OpenJailCellDoor(int cellIndex, Player prisoner = null)
        {
            string triggerName = $"JailCellDoorTrigger_{cellIndex}";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// IntakeOfficer integration - Open booking doors with automated security handling
        /// </summary>
        public bool OpenBookingInnerDoor(Player prisoner = null)
        {
            string triggerName = "BookingDoorTrigger_FromBooking";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// IntakeOfficer integration - Open prison entry door with automated security handling
        /// </summary>
        public bool OpenPrisonEntryDoor(Player prisoner = null)
        {
            string triggerName = "PrisonDoorTrigger_FromHall";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// Enhanced HandleDoorTrigger that returns success/failure for IntakeOfficer integration
        /// </summary>
        public bool HandleDoorTrigger(string triggerName, bool escorting = false, Player inmate = null)
        {
            if (currentState != DoorState.Idle)
            {
                ModLogger.Debug($"SecurityDoorBehavior: Ignoring trigger {triggerName}, already processing door operation");
                return false;
            }

            if (!doorTransitions.ContainsKey(triggerName))
            {
                ModLogger.Warn($"SecurityDoorBehavior: Unknown trigger {triggerName}");
                return false;
            }

            var transition = doorTransitions[triggerName];
            if (transition.entryPoint == null || transition.door == null)
            {
                ModLogger.Error($"SecurityDoorBehavior: Invalid transition setup for {triggerName}");
                return false;
            }

            // For IntakeOfficer integration, skip the movement direction check
            // since we're explicitly triggering door operations
            ModLogger.Info($"SecurityDoorBehavior: Starting automated door operation for {triggerName}");

            currentTransition = transition;
            isEscorting = escorting;
            escortedInmate = inmate;

            // Force door state - ensure it's ready for operation
            if (transition.door.IsClosed())
            {
                // Door is closed, we'll open it as part of the sequence
            }

            StartDoorOperation();
            return true;
        }

        #endregion

        void OnDestroy()
        {
            if (currentDoorOperation != null)
            {
                StopCoroutine(currentDoorOperation);
            }
        }

        // Debug visualization
        void OnDrawGizmos()
        {
            if (currentTransition == null) return;

            // Draw door operation visualization
            if (currentTransition.entryPoint != null)
            {
                Gizmos.color = Color.blue;
                Gizmos.DrawWireCube(currentTransition.entryPoint.position, Vector3.one * 0.5f);
            }

            if (currentTransition.exitPoint != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(currentTransition.exitPoint.position, Vector3.one * 0.5f);
            }

            // Draw state indicator
            Gizmos.color = GetStateColor(currentState);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 3f, 0.4f);
        }

        private Color GetStateColor(DoorState state)
        {
            switch (state)
            {
                case DoorState.Idle: return Color.white;
                case DoorState.DetectedTrigger: return Color.yellow;
                case DoorState.MovingToEntryPoint: return Color.blue;
                case DoorState.SecurityCheckAtEntry: return Color.cyan;
                case DoorState.OpeningDoor: return Color.green;
                case DoorState.WaitingForEscort: return new Color(1f, 0.5f, 0f); // Orange
                case DoorState.MovingThroughDoor: return Color.blue;
                case DoorState.MovingToExitPoint: return Color.blue;
                case DoorState.SecurityCheckAtExit: return Color.cyan;
                case DoorState.ClosingDoor: return Color.red;
                case DoorState.DoorOperationComplete: return Color.magenta;
                default: return Color.gray;
            }
        }
    }
}