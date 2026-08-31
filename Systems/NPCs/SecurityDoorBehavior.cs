using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using BBHelpers = Behind_Bars.Helpers.Helpers;

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

        /// <summary>
        /// Describes one directed transit through a jail door.  Holding-cell and
        /// jail-cell transitions intentionally use the same physical point for
        /// entry and exit; the resolved references are populated at startup.
        /// </summary>
        [System.Serializable]
        public class DoorTransition
        {
            /// <summary>Hierarchy name used to resolve the approach point.</summary>
            public string entryPointName;
            /// <summary>Hierarchy name used to resolve the departure point.</summary>
            public string exitPointName;
            /// <summary>Centralized JailController/BookingArea door name.</summary>
            public string doorName;
            /// <summary>Resolved approach point; null when hierarchy lookup failed.</summary>
            public Transform entryPoint;
            /// <summary>Resolved departure point; null when hierarchy lookup failed.</summary>
            public Transform exitPoint;
            /// <summary>Resolved native door used for open/close operations.</summary>
            public JailDoor door;
            /// <summary>Legacy per-transition pause value; the current sequence uses the shared timing config.</summary>
            public float securityDelay = 1.0f; // Time to wait at each point for security
        }

        /// <summary>
        /// Tunable real-time delays and distances for an automated door transit.
        /// The values are applied to the NavMesh/animation sequence at startup.
        /// </summary>
        [System.Serializable]
        public class SecurityTimingConfig
        {
            /// <summary>NavMesh approach speed in world units per second.</summary>
            public float approachSpeed = 3.0f;          // Faster movement
            /// <summary>Real-time pause at each authored door point.</summary>
            public float doorPointWaitTime = 0.3f;      // Quick security check at door point
            /// <summary>Maximum real-time wait for an escorted player to clear the doorway.</summary>
            public float escortWaitTime = 4.0f;         // Reduced wait time for inmate
            /// <summary>Real-time pause between transit and the close request.</summary>
            public float doorCloseDelay = 0.5f;         // Quick close after passing through
            /// <summary>Horizontal arrival tolerance for authored door points, in world units.</summary>
            public float positionTolerance = 0.8f;      // How close to get to door points
            /// <summary>Legacy following-distance setting; doorway clearance uses the authored plane.</summary>
            public float escortCheckDistance = 0.8f;    // Distance to check for inmate following
        }

        /// <summary>Runtime timing configuration used by this door component.</summary>
        public SecurityTimingConfig timingConfig = new SecurityTimingConfig();

        // Door State Machine
        /// <summary>
        /// Phases of one directed door operation.  Completion is driven by
        /// JailDoor animation events; the watchdog is recovery-only.
        /// </summary>
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

        // Current operation state.  currentTransition, escortedInmate, and the
        // success flags belong to one operation and are cleared together by both
        // completion and failure paths.
        private DoorState currentState = DoorState.Idle;
        private DoorTransition currentTransition;
        private bool isEscorting = false;
        private Player escortedInmate;
        private float stateStartTime;
        private Coroutine currentDoorOperation;
        private bool lastDoorPointMoveSucceeded;
        private bool lastEscortWaitSucceeded;
        private bool lastDoorOpenSucceeded;
        private bool lastDoorCloseSucceeded;
        private JailDoor observedDoor;

        // Door operations advance from JailDoor's completed-animation events.
        // This is only a recovery bound for a malformed or missing animation,
        // never the normal sequencing mechanism.
        private const float DoorAnimationWatchdogSeconds = 6f;

        // Component references
        private BaseJailNPC npcController;
        private UnityEngine.AI.NavMeshAgent navAgent;

        // Door mapping mirrors the authored jail hierarchy and is resolved through
        // JailController/BookingArea; it is not a runtime scene-wide discovery map.
        private Dictionary<string, DoorTransition> doorTransitions = new Dictionary<string, DoorTransition>();

        // Managed-only callbacks must remain off the IL2CPP-injected type surface.
        // The public subscription helpers below are hidden from IL2CPP and are only
        // called by other managed Behind Bars systems.
        private System.Action<DoorState> onDoorStateChanged;
        private System.Action<string> onDoorOperationComplete;
        private System.Action<string> onDoorOperationFailed;

        /// <summary>
        /// Adds a managed listener for state changes.  This helper is hidden from
        /// IL2CPP because its Action signature is not injection-safe; callers must
        /// retain the same delegate instance to remove it later.
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void AddDoorStateChangedListener(System.Action<DoorState> listener)
        {
            onDoorStateChanged += listener;
        }

        /// <summary>Removes a previously added managed state-change listener.</summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void RemoveDoorStateChangedListener(System.Action<DoorState> listener)
        {
            onDoorStateChanged -= listener;
        }

        /// <summary>
        /// Adds a managed listener invoked with the transition door name after a
        /// successful operation; the delegate surface remains hidden from IL2CPP.
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void AddDoorOperationCompleteListener(System.Action<string> listener)
        {
            onDoorOperationComplete += listener;
        }

        /// <summary>Removes a previously added managed completion listener.</summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void RemoveDoorOperationCompleteListener(System.Action<string> listener)
        {
            onDoorOperationComplete -= listener;
        }

        /// <summary>
        /// Adds a managed listener invoked with a formatted failure reason when a
        /// transit cannot complete; the delegate surface remains hidden from IL2CPP.
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void AddDoorOperationFailedListener(System.Action<string> listener)
        {
            onDoorOperationFailed += listener;
        }

        /// <summary>Removes a previously added managed failure listener.</summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void RemoveDoorOperationFailedListener(System.Action<string> listener)
        {
            onDoorOperationFailed -= listener;
        }

        /// <summary>
        /// Caches the NPC controller and native NavMeshAgent before mappings are
        /// resolved.  Missing components are handled by the later operation
        /// guards rather than by injecting replacement behavior here.
        /// </summary>
        void Awake()
        {
            npcController = BBHelpers.GetComponentSafe<BaseJailNPC>(gameObject);
            navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        }

        /// <summary>
        /// Builds the directed door map, resolves native door references, and
        /// applies the configured approach speed to the NavMeshAgent.
        /// </summary>
        void Start()
        {
            InitializeDoorMappings();

            if (navAgent != null)
            {
                // Override speed for security operations
                navAgent.speed = timingConfig.approachSpeed;
            }

            ModLogger.Debug($"SecurityDoorBehavior initialized for {gameObject.name}");
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
                    // First find the door by name
                    transition.door = jailController.booking.GetDoorByName(transition.doorName);

                    // Then find entry/exit points within that specific door's hierarchy
                    if (transition.door?.doorHolder != null)
                    {
                        transition.entryPoint = FindChildByName(transition.door.doorHolder, transition.entryPointName);
                        transition.exitPoint = FindChildByName(transition.door.doorHolder, transition.exitPointName);
                    }
                    else
                    {
                        // Fallback to global search if door holder not found
                        transition.entryPoint = jailController.booking.GetDoorPointByName(transition.entryPointName);
                        transition.exitPoint = jailController.booking.GetDoorPointByName(transition.exitPointName);
                    }
                }

                if (transition.entryPoint == null)
                    ModLogger.Warn($"Could not find entry point: {transition.entryPointName}");
                if (transition.exitPoint == null)
                    ModLogger.Warn($"Could not find exit point: {transition.exitPointName}");
                if (transition.door == null)
                    ModLogger.Warn($"Could not find door: {transition.doorName}");
                else
                    ModLogger.Debug($"✓ SecurityDoor resolved: {transition.doorName} with entry: {transition.entryPoint?.name} exit: {transition.exitPoint?.name}");
            }
        }

        /// <summary>
        /// Resolves a holding-cell transition through JailController rather than
        /// searching the scene.  Both points intentionally resolve to the native
        /// cell door point because the cell door has no separate corridor waypoint.
        /// </summary>
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

        /// <summary>
        /// Resolves one of the authored jail-cell doors by parsing its transition
        /// name and asking JailController for the corresponding cell index.
        /// </summary>
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
        /// Checks whether the NPC is approaching the transition from its entry
        /// side.  Explicit integrations currently bypass this heuristic, so it
        /// should not be treated as the authorization gate for a triggered door.
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
                MelonCoroutines.Stop(currentDoorOperation);
                UnsubscribeFromDoorEvents();
            }

            currentDoorOperation = (Coroutine)MelonCoroutines.Start(ExecuteDoorOperation());
            //currentDoorOperation = StartCoroutine(ExecuteDoorOperation());
        }

        /// <summary>
        /// Runs the directed door sequence: approach, entry pause, open event,
        /// optional escort clearance, exit traversal, exit pause, close event,
        /// and completion.  Any failed step routes through the shared failure
        /// cleanup so the owning escort can recover control.
        /// </summary>
        private IEnumerator ExecuteDoorOperation()
        {
            if (currentTransition == null)
            {
                FailDoorOperation("No active door transition");
                yield break;
            }

            // 1. Move to entry door point
            ChangeState(DoorState.MovingToEntryPoint);
            yield return MoveToDoorPoint(currentTransition.entryPoint);
            if (!lastDoorPointMoveSucceeded)
            {
                FailDoorOperation("Could not reach entry door point");
                yield break;
            }

            // 2. Security check at entry point (brief pause)
            ChangeState(DoorState.SecurityCheckAtEntry);
            yield return new WaitForSeconds(timingConfig.doorPointWaitTime);

            // 3. Open door
            ChangeState(DoorState.OpeningDoor);
            yield return OpenDoorAndWaitForEvent();
            if (!lastDoorOpenSucceeded)
            {
                FailDoorOperation("Door did not open");
                yield break;
            }

            // 4. Optional: Wait for escorted inmate
            if (isEscorting && escortedInmate != null)
            {
                ChangeState(DoorState.WaitingForEscort);
                yield return WaitForEscortedInmate();
                if (!lastEscortWaitSucceeded)
                {
                    FailDoorOperation("Escorted prisoner did not clear the door in time");
                    yield break;
                }
            }

            // 5. Move through to exit door point
            ChangeState(DoorState.MovingToExitPoint);
            yield return MoveToDoorPoint(currentTransition.exitPoint);
            if (!lastDoorPointMoveSucceeded)
            {
                FailDoorOperation("Could not reach exit door point");
                yield break;
            }

            // 6. Security check at exit point
            ChangeState(DoorState.SecurityCheckAtExit);
            yield return new WaitForSeconds(timingConfig.doorPointWaitTime);

            // 7. Wait before closing door
            yield return new WaitForSeconds(timingConfig.doorCloseDelay);

            // 8. Close door after ensuring clearance
            ChangeState(DoorState.ClosingDoor);
            yield return CloseDoorAndWaitForEvent();
            if (!lastDoorCloseSucceeded)
            {
                ModLogger.Warn($"SecurityDoorBehavior: Door {currentTransition.doorName} did not report closed before the animation watchdog elapsed");
            }

            // 9. Complete
            ChangeState(DoorState.DoorOperationComplete);
            CompleteDoorOperation();
        }

        /// <summary>
        /// Moves to an authored door point using only its horizontal coordinates,
        /// waits up to fifteen real seconds for the configured tolerance, and
        /// leaves the success flag false when NavMesh movement cannot be started.
        /// </summary>
        private IEnumerator MoveToDoorPoint(Transform targetPoint)
        {
            lastDoorPointMoveSucceeded = false;
            if (navAgent == null || targetPoint == null || !navAgent.enabled || !navAgent.isOnNavMesh)
            {
                ModLogger.Warn("SecurityDoorBehavior: Cannot move to door point; NavMesh agent or target is unavailable");
                yield break;
            }

            // Only use X and Z from door point - let NavMesh control Y position
            Vector3 destination = new Vector3(targetPoint.position.x, transform.position.y, targetPoint.position.z);
            if (!navAgent.SetDestination(destination))
            {
                ModLogger.Warn($"SecurityDoorBehavior: NavMesh rejected door destination {destination}");
                yield break;
            }

            // Wait until we reach the point
            float timeout = 15f; // Max 15 seconds to reach door point
            float startTime = Time.time;

            while (Time.time - startTime < timeout && navAgent.enabled && navAgent.isOnNavMesh)
            {
                // Calculate distance ignoring Y axis
                Vector3 npcPos2D = new Vector3(transform.position.x, 0, transform.position.z);
                Vector3 targetPos2D = new Vector3(targetPoint.position.x, 0, targetPoint.position.z);
                float distance = Vector3.Distance(npcPos2D, targetPos2D);

                if (distance <= timingConfig.positionTolerance)
                {
                    lastDoorPointMoveSucceeded = true;
                    break;
                }
                yield return new WaitForSeconds(0.1f);
            }

            if (!lastDoorPointMoveSucceeded)
            {
                ModLogger.Warn($"SecurityDoorBehavior: Timed out reaching door point '{targetPoint.name}'");
                yield break;
            }

            // Face the door point properly (only horizontal rotation)
            Vector3 directionToPoint = (targetPoint.position - transform.position);
            directionToPoint.y = 0; // Keep rotation horizontal only
            directionToPoint.Normalize();

            if (directionToPoint != Vector3.zero)
            {
                transform.rotation = Quaternion.LookRotation(directionToPoint);
            }
        }


        /// <summary>
        /// Wait for the escorted prisoner to cross the physical doorway.
        /// The authored exit point may be a navigation waypoint farther down the corridor, so it
        /// must not be used as the clearance boundary: doing so makes the officer wait until the
        /// player reaches the far wall instead of merely passing through the open door.
        /// </summary>
        private IEnumerator WaitForEscortedInmate()
        {
            lastEscortWaitSucceeded = false;
            if (escortedInmate == null || currentTransition?.exitPoint == null || currentTransition?.door == null)
            {
                yield break;
            }

            // Keep the architectural holder in the diagnostic only. In the jail
            // prefab its origin is offset from the actual passage, so it cannot be
            // the player-clearance plane.
            Transform doorwayFrame = currentTransition.door.doorHolder
                ?? currentTransition.door.doorInstance?.transform;
            string doorwaySource = doorwayFrame == currentTransition.door.doorHolder
                ? "door holder diagnostic"
                : doorwayFrame != null
                    ? "door instance fallback"
                    : "entry/exit midpoint";
            const string clearanceSource = "fixed entry/exit midpoint plane";

            ModLogger.Info(
                $"SecurityDoor: Awaiting escorted prisoner at {currentTransition.doorName} doorway " +
                $"(frame={doorwaySource}, entry={currentTransition.entryPoint.position}, " +
                $"exit={currentTransition.exitPoint.position}, framePosition=" +
                $"{(doorwayFrame != null ? doorwayFrame.position.ToString() : "<none>")}, " +
                $"clearance={clearanceSource})");

            float lastReminderTime = Time.time;
            float timeoutAt = Time.time + Mathf.Max(10f, timingConfig.escortWaitTime);

            // Send initial message to inmate
            if (npcController != null)
            {
                npcController.TrySendNPCMessage("Go through the door.", 3f);
            }

            // The old indefinite wait could leave an officer repeatedly saying
            // “Go through the door” after a route or door failure. Bound the
            // wait and surface a recoverable failure to the owning escort.
            while (Time.time < timeoutAt)
            {
                bool crossedDoorway = HasEscortedInmateCrossedDoorway(
                    out float signedDoorwayDistance,
                    out float lateralDoorwayDistance);

                ModLogger.Debug(
                    $"SecurityDoor: Doorway clearance for {currentTransition.doorName}: " +
                    $"signed={signedDoorwayDistance:F2}m, lateral={lateralDoorwayDistance:F2}m, crossed={crossedDoorway}");

                if (crossedDoorway)
                {
                    ModLogger.Info(
                        $"SecurityDoor: Prisoner crossed {currentTransition.doorName} doorway " +
                        $"(signed={signedDoorwayDistance:F2}m); continuing escort transit");
                    lastEscortWaitSucceeded = true;
                    break;
                }

                // Remind inmate every 5 seconds
                if (Time.time - lastReminderTime >= 5f)
                {
                    if (npcController != null)
                    {
                        npcController.TrySendNPCMessage("Go through the door.", 2f);
                    }
                    lastReminderTime = Time.time;
                }

                yield return new WaitForSeconds(0.5f);
            }

            if (!lastEscortWaitSucceeded)
            {
                ModLogger.Warn("SecurityDoorBehavior: Escort wait timed out before the prisoner cleared the door");
            }
        }

        /// <summary>
        /// Determines whether the escorted player has crossed from this transition's entry side
        /// to its exit side at the physical door, independent of distant navigation waypoints.
        /// </summary>
        private bool HasEscortedInmateCrossedDoorway(out float signedDoorwayDistance, out float lateralDoorwayDistance)
        {
            signedDoorwayDistance = 0f;
            lateralDoorwayDistance = float.MaxValue;

            if (escortedInmate == null || currentTransition?.entryPoint == null || currentTransition.exitPoint == null)
            {
                return false;
            }

            // The apparent "side" trigger colliders in the jail prefab are children
            // of the animated door, so they move with the door swing and cannot
            // represent a stable clearance boundary. The authored entry/exit pair
            // straddles the actual opening and is static, so its midpoint defines
            // the physical doorway plane without waiting for a distant nav waypoint.
            Vector3 doorwayMidpoint = (currentTransition.entryPoint.position + currentTransition.exitPoint.position) * 0.5f;
            return HasCrossedDoorwayPlane(doorwayMidpoint, out signedDoorwayDistance, out lateralDoorwayDistance);
        }

        /// <summary>
        /// Evaluates the fixed doorway plane and lateral opening width used to
        /// protect the player from a premature close.  The direction comes from
        /// the authored transition, not the animated door transform.
        /// </summary>
        private bool HasCrossedDoorwayPlane(
            Vector3 doorwayPosition,
            out float signedDoorwayDistance,
            out float lateralDoorwayDistance)
        {
            signedDoorwayDistance = 0f;
            lateralDoorwayDistance = float.MaxValue;

            // Direction belongs to the authored transition, not the moving visual door.
            // It remains correct even when the exit navigation point is farther down the
            // corridor than the physical threshold.
            Vector3 routeToExit = currentTransition.exitPoint.position - currentTransition.entryPoint.position;
            routeToExit.y = 0f;
            if (routeToExit.sqrMagnitude < 0.01f)
            {
                routeToExit = currentTransition.exitPoint.position - doorwayPosition;
                routeToExit.y = 0f;
            }

            if (routeToExit.sqrMagnitude < 0.01f)
            {
                return false;
            }

            routeToExit.Normalize();

            Vector3 doorwayToPrisoner = escortedInmate.transform.position - doorwayPosition;
            doorwayToPrisoner.y = 0f;
            signedDoorwayDistance = Vector3.Dot(doorwayToPrisoner, routeToExit);
            lateralDoorwayDistance = (doorwayToPrisoner - routeToExit * signedDoorwayDistance).magnitude;

            // Cross the plane by a small margin so a player standing in the doorway never lets
            // the guard close it on them.  The generous lateral width reflects the physical
            // door opening plus controller movement variance, not the full corridor.
            const float clearancePastDoorwayMeters = 0.10f;
            const float maximumDoorwayLateralMeters = 2.25f;
            return signedDoorwayDistance >= clearancePastDoorwayMeters &&
                   lateralDoorwayDistance <= maximumDoorwayLateralMeters;
        }

        /// <summary>
        /// Requests the mapped door to unlock/open and waits for its native opened
        /// event.  The animation watchdog only bounds malformed/missing events;
        /// it is not the normal sequencing signal.
        /// </summary>
        private IEnumerator OpenDoorAndWaitForEvent()
        {
            lastDoorOpenSucceeded = false;
            JailDoor door = currentTransition?.door;
            if (door == null)
            {
                yield break;
            }

            SubscribeToDoorEvents(door);
            if (door.IsOpen())
            {
                lastDoorOpenSucceeded = true;
                yield break;
            }

            // Shared transit doors can legitimately remain locked after a
            // lockdown or a prior custody transition. This state machine is the
            // authorized guard operation, so it may unlock its own shared door.
            if (door.IsLocked())
            {
                door.UnlockDoor();
                ModLogger.Debug($"SecurityDoorBehavior: Unlocked door {currentTransition.doorName} for authorized transit");
            }

            door.OpenDoor();
            ModLogger.Debug($"SecurityDoorBehavior: Requested open for {currentTransition.doorName}; awaiting completed-open event");

            float watchdogAt = Time.time + DoorAnimationWatchdogSeconds;
            while (!lastDoorOpenSucceeded && Time.time < watchdogAt && observedDoor == door)
            {
                yield return null;
            }

            if (!lastDoorOpenSucceeded)
            {
                ModLogger.Warn(
                    $"SecurityDoorBehavior: Door {currentTransition?.doorName ?? door.doorName} did not raise opened " +
                    $"before the animation watchdog (state={door.currentState}, locked={door.IsLocked()}, " +
                    $"animating={door.IsAnimating()}, hinge={(door.doorHinge != null)})");
            }
        }

        /// <summary>
        /// Requests a door close and waits for the native closed event, retaining a
        /// failure flag if the animation watchdog expires.
        /// </summary>
        private IEnumerator CloseDoorAndWaitForEvent()
        {
            lastDoorCloseSucceeded = false;
            JailDoor door = currentTransition?.door;
            if (door == null)
            {
                yield break;
            }

            SubscribeToDoorEvents(door);
            if (door.IsClosed())
            {
                lastDoorCloseSucceeded = true;
                yield break;
            }

            door.CloseDoor();
            ModLogger.Debug($"SecurityDoorBehavior: Requested close for {currentTransition.doorName}; awaiting completed-closed event");

            float watchdogAt = Time.time + DoorAnimationWatchdogSeconds;
            while (!lastDoorCloseSucceeded && Time.time < watchdogAt && observedDoor == door)
            {
                yield return null;
            }

            if (!lastDoorCloseSucceeded)
            {
                ModLogger.Warn(
                    $"SecurityDoorBehavior: Door {currentTransition?.doorName ?? door.doorName} did not raise closed " +
                    $"before the animation watchdog (state={door.currentState}, locked={door.IsLocked()}, " +
                    $"animating={door.IsAnimating()}, hinge={(door.doorHinge != null)})");
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
        /// Subscribes to the currently operated native door, first removing any
        /// prior door subscription so an operation has one event source only.
        /// </summary>
        private void SubscribeToDoorEvents(JailDoor door)
        {
            if (observedDoor == door)
            {
                return;
            }

            UnsubscribeFromDoorEvents();
            observedDoor = door;
            observedDoor.Opened += HandleDoorOpened;
            observedDoor.Closed += HandleDoorClosed;
        }

        /// <summary>
        /// Removes native door listeners and clears the observed-door guard used
        /// to reject late events from a previous operation.
        /// </summary>
        private void UnsubscribeFromDoorEvents()
        {
            if (observedDoor == null)
            {
                return;
            }

            observedDoor.Opened -= HandleDoorOpened;
            observedDoor.Closed -= HandleDoorClosed;
            observedDoor = null;
        }

        /// <summary>
        /// Accepts an opened event only from the currently observed door and marks
        /// the open wait as complete.
        /// </summary>
        private void HandleDoorOpened(JailDoor door)
        {
            if (door != observedDoor)
            {
                return;
            }

            lastDoorOpenSucceeded = true;
            ModLogger.Debug($"SecurityDoorBehavior: Received opened event for {currentTransition?.doorName ?? door.doorName}");
        }

        /// <summary>
        /// Accepts a closed event only from the currently observed door and marks
        /// the close wait as complete.
        /// </summary>
        private void HandleDoorClosed(JailDoor door)
        {
            if (door != observedDoor)
            {
                return;
            }

            lastDoorCloseSucceeded = true;
            ModLogger.Debug($"SecurityDoorBehavior: Received closed event for {currentTransition?.doorName ?? door.doorName}");
        }

        /// <summary>
        /// Completes the active operation, notifies managed listeners, unsubscribes
        /// from the native door, and clears all operation-owned state.
        /// </summary>
        private void CompleteDoorOperation()
        {
            string doorName = currentTransition?.doorName ?? "Unknown";
            ModLogger.Info($"SecurityDoorBehavior: Completed door operation for {doorName}");

            UnsubscribeFromDoorEvents();
            onDoorOperationComplete?.Invoke(doorName);

            // Reset state
            ChangeState(DoorState.Idle);
            currentTransition = null;
            isEscorting = false;
            escortedInmate = null;
            currentDoorOperation = null;
        }

        /// <summary>
        /// Fails the active operation, attempts to close the door, notifies managed
        /// listeners, and clears operation state so the owning escort can retry or
        /// choose its fallback path.
        /// </summary>
        private void FailDoorOperation(string reason)
        {
            string doorName = currentTransition?.doorName ?? "Unknown";
            ModLogger.Warn($"SecurityDoorBehavior: Door operation failed for {doorName}: {reason}");

            // A failed route must not leave a door open while the owning
            // intake/release state machine regains control.
            CloseDoor();
            UnsubscribeFromDoorEvents();
            onDoorOperationFailed?.Invoke($"{doorName}: {reason}");

            ChangeState(DoorState.Idle);
            currentTransition = null;
            isEscorting = false;
            escortedInmate = null;
            currentDoorOperation = null;
        }

        /// <summary>
        /// Changes the observable door phase, timestamps it with real Unity time,
        /// and notifies managed listeners.  It does not start or stop the coroutine.
        /// </summary>
        private void ChangeState(DoorState newState)
        {
            if (currentState == newState) return;

            DoorState oldState = currentState;
            currentState = newState;
            stateStartTime = Time.time;

            onDoorStateChanged?.Invoke(newState);
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

        /// <summary>Returns whether a door operation is currently active.</summary>
        public bool IsBusy() => currentState != DoorState.Idle;

        /// <summary>Returns the current phase of the door operation state machine.</summary>
        public DoorState GetCurrentState() => currentState;

        /// <summary>
        /// Force stop current door operation
        /// </summary>
        public void StopDoorOperation()
        {
            if (currentDoorOperation != null)
            {
                MelonCoroutines.Stop(currentDoorOperation);
                currentDoorOperation = null;
            }

            UnsubscribeFromDoorEvents();

            ChangeState(DoorState.Idle);
            currentTransition = null;
            isEscorting = false;
            escortedInmate = null;
        }

        /// <summary>
        /// Requests the automated security sequence for a holding-cell door.
        /// </summary>
        /// <param name="cellIndex">The authored holding-cell index.</param>
        /// <param name="prisoner">Optional player whose doorway clearance should be awaited.</param>
        /// <returns>True when the transition was accepted while the component was idle.</returns>
        public bool OpenHoldingCellDoor(int cellIndex, Player prisoner = null)
        {
            string triggerName = $"HoldingCellDoorTrigger_{cellIndex}";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// Requests the automated security sequence for a jail-cell door.
        /// </summary>
        /// <param name="cellIndex">The authored jail-cell index.</param>
        /// <param name="prisoner">Optional player whose doorway clearance should be awaited.</param>
        /// <returns>True when the transition was accepted while the component was idle.</returns>
        public bool OpenJailCellDoor(int cellIndex, Player prisoner = null)
        {
            string triggerName = $"JailCellDoorTrigger_{cellIndex}";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// Requests the automated security sequence for the booking inner door.
        /// </summary>
        /// <param name="prisoner">Optional player whose doorway clearance should be awaited.</param>
        /// <returns>True when the transition was accepted while the component was idle.</returns>
        public bool OpenBookingInnerDoor(Player prisoner = null)
        {
            string triggerName = "BookingDoorTrigger_FromBooking";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// Requests the automated security sequence for the prison-entry door.
        /// </summary>
        /// <param name="prisoner">Optional player whose doorway clearance should be awaited.</param>
        /// <returns>True when the transition was accepted while the component was idle.</returns>
        public bool OpenPrisonEntryDoor(Player prisoner = null)
        {
            string triggerName = "PrisonDoorTrigger_FromHall";
            return HandleDoorTrigger(triggerName, prisoner != null, prisoner);
        }

        /// <summary>
        /// Accepts a named authored transition and starts the shared automated
        /// door sequence.  Release and intake escorts both use this entry point;
        /// a busy component, unknown trigger, or unresolved door rejects it.
        /// </summary>
        /// <param name="triggerName">The authored trigger/transition key.</param>
        /// <param name="escorting">Whether the sequence must await a player's doorway clearance.</param>
        /// <param name="inmate">The player to track when <paramref name="escorting"/> is true.</param>
        /// <returns>True when the operation was accepted and started.</returns>
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

        /// <summary>
        /// Stops any active coroutine and removes native door listeners so late
        /// animation events cannot reach a destroyed NPC component.
        /// </summary>
        void OnDestroy()
        {
            if (currentDoorOperation != null)
            {
                MelonCoroutines.Stop(currentDoorOperation);
                currentDoorOperation = null;
            }

            UnsubscribeFromDoorEvents();
        }

        // Debug visualization only; it has no bearing on door authorization or
        // operation sequencing.
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

        /// <summary>
        /// Find child Transform by name within a specific parent hierarchy
        /// This ensures we find the correct door point within the specific door object
        /// </summary>
        private Transform FindChildByName(Transform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName)) return null;

            // Check direct children first
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == childName)
                {
                    return child;
                }
            }

            // If not found in direct children, search recursively
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                Transform found = FindChildByName(child, childName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
