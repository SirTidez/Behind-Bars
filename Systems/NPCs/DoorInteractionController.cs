using System;
using System.Collections;
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
    /// Controls all door interactions for guards to ensure consistent positioning and sequencing
    /// </summary>
    public class DoorInteractionController : MonoBehaviour
    {
        // Navigation Settings
        public float positionTolerance = 1.0f;
        public float rotationSpeed = 360f;
        public float navigationTimeout = 10f;
        public float stabilizationDelay = 0.5f;

        // Door Timing
        public float doorAnimationWait = 1f;
        public float prisonerWaitTime = 5f;
        public float transitionTime = 3f;

        // State tracking
        private DoorInteractionState currentState = DoorInteractionState.Idle;
        private JailDoor targetDoor;
        private JailDoor.DoorInteractionType interactionType;
        private Transform approachPoint;
        private Transform exitPoint;
        private Player targetPrisoner;
        private float stateStartTime;
        private int attemptCount = 0;
        private const int maxAttempts = 3;

        // Component references
        private UnityEngine.AI.NavMeshAgent navAgent;
        private Transform guardTransform;

        // Events
        public System.Action<DoorInteractionState> OnStateChanged;
        public System.Action OnInteractionComplete;
        public System.Action<string> OnInteractionFailed;

        public enum DoorInteractionState
        {
            Idle,
            NavigatingToApproachPoint,
            AtApproachPoint,
            OperatingDoor,
            WaitingForPrisoner,
            MovingThrough,
            AtExitPoint,
            ClosingDoor,
            Complete,
            Failed
        }


        void Awake()
        {
            guardTransform = GetCorrectRotationTransform();
            navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent == null)
            {
                ModLogger.Error($"DoorInteractionController: NavMeshAgent not found on {gameObject.name}");
            }
        }

        /// <summary>
        /// Find the correct transform to rotate for this NPC type
        /// </summary>
        private Transform GetCorrectRotationTransform()
        {
            // For NPCs created with DirectNPCBuilder, there might be a character model child
            // Look for common character model transform names
            string[] characterNames = { "Character", "Model", "Body", "Root", "Armature", "Avatar" };
            
            foreach (string name in characterNames)
            {
                var characterTransform = transform.Find(name);
                if (characterTransform != null)
                {
                    ModLogger.Debug($"Found character transform for rotation: {name}");
                    return characterTransform;
                }
            }
            
            // Look for any child that might be the character model (has animator, skinned mesh renderer, etc.)
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child.GetComponent<Animator>() != null || 
                    child.GetComponent<SkinnedMeshRenderer>() != null ||
                    child.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                {
                    ModLogger.Debug($"Found character transform with animator/renderer: {child.name}");
                    return child;
                }
            }
            
            // Fallback to main transform
            ModLogger.Debug("Using main transform for rotation");
            return transform;
        }

        /// <summary>
        /// Start a door interaction sequence
        /// </summary>
        public void StartDoorInteraction(JailDoor door, JailDoor.DoorInteractionType type, Player prisoner = null)
        {
            if (currentState != DoorInteractionState.Idle)
            {
                ModLogger.Warn($"DoorInteractionController: Cannot start new interaction while in state {currentState}");
                return;
            }

            targetDoor = door;
            interactionType = type;
            targetPrisoner = prisoner;
            attemptCount = 0;

            // Determine door points based on interaction type
            if (!SetupDoorPoints())
            {
                FailInteraction("Could not find required door points");
                return;
            }

            ChangeState(DoorInteractionState.NavigatingToApproachPoint);
            MelonCoroutines.Start(ExecuteInteractionSequence());
        }

        /// <summary>
        /// Force stop current interaction
        /// </summary>
        public void StopInteraction()
        {
            if (currentState != DoorInteractionState.Idle)
            {
                ChangeState(DoorInteractionState.Idle);
                StopAllCoroutines();
                ModLogger.Info($"DoorInteractionController: Interaction stopped on {gameObject.name}");
            }
        }

        /// <summary>
        /// Check if controller is currently busy
        /// </summary>
        public bool IsBusy()
        {
            return currentState != DoorInteractionState.Idle;
        }

        private bool SetupDoorPoints()
        {
            if (targetDoor?.doorHolder == null)
            {
                ModLogger.Error("DoorInteractionController: Invalid door or door holder");
                return false;
            }

            if (interactionType == JailDoor.DoorInteractionType.PassThrough)
            {
                // Pass-through doors need both approach and exit points
                approachPoint = GetDoorPoint(targetDoor, GetApproachSide());
                exitPoint = GetDoorPoint(targetDoor, GetExitSide());

                if (approachPoint == null || exitPoint == null)
                {
                    ModLogger.Error($"DoorInteractionController: Missing door points for pass-through door {targetDoor.doorName}");
                    return false;
                }
            }
            else
            {
                // Operation-only doors just need approach point
                approachPoint = GetDoorPoint(targetDoor, GetApproachSide());
                exitPoint = null;

                if (approachPoint == null)
                {
                    ModLogger.Error($"DoorInteractionController: Missing door point for operation door {targetDoor.doorName}");
                    return false;
                }
            }

            return true;
        }

        private string GetApproachSide()
        {
            // Determine which side to approach based on door name and type
            if (targetDoor.doorName != null)
            {
                // Use door name to determine correct approach side
                if (targetDoor.doorName.Contains("Inner"))
                    return "Booking";
                if (targetDoor.doorName.Contains("Entry") || targetDoor.doorName.Contains("Prison"))
                    return "Hall";
                if (targetDoor.doorName.Contains("Guard"))
                    return "Prison";
                if (targetDoor.doorName.Contains("Cell"))
                    return "Corridor";
            }

            // Fallback to door type
            switch (targetDoor.doorType)
            {
                case JailDoor.DoorType.HoldingCellDoor:
                    return "GuardRoom";
                case JailDoor.DoorType.CellDoor:
                    return "Corridor";
                case JailDoor.DoorType.EntryDoor:
                    return "Hall";
                case JailDoor.DoorType.GuardDoor:
                    return "Prison";
                case JailDoor.DoorType.AreaDoor:
                    return "Booking";
                default:
                    return "GuardRoom";
            }
        }

        private string GetExitSide()
        {
            // Determine exit side for pass-through doors based on door name and type
            if (targetDoor.doorName != null)
            {
                // Use door name to determine correct exit side
                if (targetDoor.doorName.Contains("Inner"))
                    return "Hall";
                if (targetDoor.doorName.Contains("Entry") || targetDoor.doorName.Contains("Prison Enter"))
                    return "Prison";
                if (targetDoor.doorName.Contains("Guard") && !targetDoor.doorName.Contains("Room"))
                    return "GuardRoom"; // "Booking Guard Door" should exit to GuardRoom
            }

            // Fallback to door type
            switch (targetDoor.doorType)
            {
                case JailDoor.DoorType.EntryDoor:
                    return "Prison";
                case JailDoor.DoorType.GuardDoor:
                    return "GuardRoom";
                case JailDoor.DoorType.AreaDoor:
                    return "Hall";
                default:
                    return "Hall";
            }
        }

        private Transform GetDoorPoint(JailDoor door, string side)
        {
            if (door?.doorHolder == null) return null;

            // Try specific door point first
            var specificPoint = door.doorHolder.Find($"DoorPoint_{side}");
            if (specificPoint != null)
            {
                ModLogger.Debug($"Found specific DoorPoint_{side} for {door.doorName}");
                return specificPoint;
            }

            // Try alternative naming conventions for cell doors
            if (side == "Corridor" && door.doorType == JailDoor.DoorType.CellDoor)
            {
                var cellPoint = door.doorHolder.Find("DoorPoint_Hall") ?? 
                               door.doorHolder.Find("DoorPoint_Prison") ??
                               door.doorHolder.Find("DoorPoint_PrisonCell") ??
                               door.doorHolder.Find("PrisonCellDoorPoint") ??
                               door.doorHolder.Find("CellPoint");
                if (cellPoint != null)
                {
                    ModLogger.Debug($"Found alternative door point for cell door {door.doorName}: {cellPoint.name}");
                    return cellPoint;
                }
            }

            // Special handling for doors with "PrisonCell" in DoorPoint names
            if (side == "Corridor" || side == "Hall" || side == "Prison")
            {
                var prisonCellPoint = door.doorHolder.Find("PrisonCellDoorPoint");
                if (prisonCellPoint != null)
                {
                    ModLogger.Debug($"Found PrisonCellDoorPoint for {door.doorName}");
                    return prisonCellPoint;
                }
            }

            // Try alternative naming for guard doors
            if (side == "Prison" && door.doorName != null && door.doorName.Contains("Guard"))
            {
                var guardPoint = door.doorHolder.Find("DoorPoint_GuardRoom") ?? 
                                door.doorHolder.Find("DoorPoint_Guard") ??
                                door.doorHolder.Find("GuardPoint");
                if (guardPoint != null)
                {
                    ModLogger.Debug($"Found alternative guard door point for {door.doorName}");
                    return guardPoint;
                }
            }

            // Fallback to generic door point
            var genericPoint = door.doorHolder.Find("DoorPoint");
            if (genericPoint != null)
            {
                ModLogger.Debug($"Using generic DoorPoint for {door.doorName}");
                return genericPoint;
            }

            // Last resort: search all children for any point with "DoorPoint" or "Point" in the name
            for (int i = 0; i < door.doorHolder.childCount; i++)
            {
                var child = door.doorHolder.GetChild(i);
                if (child.name.Contains("DoorPoint") || child.name.Contains("Point"))
                {
                    ModLogger.Debug($"Found fallback door point: {child.name} for {door.doorName}");
                    return child;
                }
            }

            // Debug: List all children to help with troubleshooting
            string childNames = "";
            for (int i = 0; i < door.doorHolder.childCount; i++)
            {
                if (i > 0) childNames += ", ";
                childNames += door.doorHolder.GetChild(i).name;
            }

            ModLogger.Warn($"No DoorPoint found for {door.doorName} from {side} side. Available children: {childNames}");
            return null;
        }

        private IEnumerator ExecuteInteractionSequence()
        {
            while (currentState != DoorInteractionState.Complete && 
                   currentState != DoorInteractionState.Failed && 
                   currentState != DoorInteractionState.Idle)
            {
                // Check for timeout
                if (Time.time - stateStartTime > navigationTimeout)
                {
                    HandleTimeout();
                    yield break;
                }

                switch (currentState)
                {
                    case DoorInteractionState.NavigatingToApproachPoint:
                        yield return HandleNavigationToApproachPoint();
                        break;

                    case DoorInteractionState.AtApproachPoint:
                        yield return HandleAtApproachPoint();
                        break;

                    case DoorInteractionState.OperatingDoor:
                        yield return HandleOperatingDoor();
                        break;

                    case DoorInteractionState.WaitingForPrisoner:
                        yield return HandleWaitingForPrisoner();
                        break;

                    case DoorInteractionState.MovingThrough:
                        yield return HandleMovingThrough();
                        break;

                    case DoorInteractionState.AtExitPoint:
                        yield return HandleAtExitPoint();
                        break;

                    case DoorInteractionState.ClosingDoor:
                        yield return HandleClosingDoor();
                        break;
                }

                yield return null;
            }

            // Cleanup
            if (currentState == DoorInteractionState.Complete)
            {
                ChangeState(DoorInteractionState.Idle);
                OnInteractionComplete?.Invoke();
            }
        }

        private IEnumerator HandleNavigationToApproachPoint()
        {
            if (navAgent == null || approachPoint == null)
            {
                FailInteraction("Missing NavAgent or approach point");
                yield break;
            }

            // Set destination
            navAgent.SetDestination(approachPoint.position);
            ModLogger.Debug($"Guard {gameObject.name} navigating to approach point for {targetDoor.doorName}");

            // Wait for navigation to actually complete - use a more robust approach
            float startTime = Time.time;
            float maxNavigationTime = 20f; // Max 20 seconds to navigate
            
            while (Time.time - startTime < maxNavigationTime)
            {
                float distanceToTarget = Vector3.Distance(guardTransform.position, approachPoint.position);
                
                // Check if we're close enough
                if (distanceToTarget <= 1.0f)
                {
                    ModLogger.Debug($"Guard {gameObject.name} reached approach point. Distance: {distanceToTarget:F2}m");
                    break;
                }
                
                // Check if navigation is stuck (not moving for 3 seconds)
                if (!navAgent.pathPending && navAgent.remainingDistance < 0.1f && distanceToTarget > 2.0f)
                {
                    ModLogger.Warn($"Guard {gameObject.name} navigation stuck. Distance: {distanceToTarget:F2}m. Re-setting destination.");
                    navAgent.SetDestination(approachPoint.position);
                }
                
                yield return new WaitForSeconds(0.2f); // Check every 200ms
            }
            
            // Final position check - only teleport if really necessary
            float finalDistance = Vector3.Distance(guardTransform.position, approachPoint.position);
            if (finalDistance > 2.0f)
            {
                ModLogger.Warn($"Guard {gameObject.name} couldn't reach approach point after {maxNavigationTime}s. Distance: {finalDistance:F2}m. Teleporting.");
                guardTransform.position = approachPoint.position;
            }

            ChangeState(DoorInteractionState.AtApproachPoint);
        }

        private IEnumerator HandleAtApproachPoint()
        {
            // Wait for guard to be completely stationary before any rotation
            yield return WaitForGuardToBeStationary();
            
            // Quick, natural rotation to face the door - ONLY after guard is stationary
            if (approachPoint != null && guardTransform != null)
            {
                Quaternion targetRotation = approachPoint.rotation;
                float angleDifference = Quaternion.Angle(guardTransform.rotation, targetRotation);
                
                // Only rotate if there's a significant difference (more than 15 degrees)
                if (angleDifference > 15f)
                {
                    ModLogger.Debug($"Guard {gameObject.name} stationary rotation to face door (angle difference: {angleDifference:F1}°)");
                    
                    // Quick rotation - should complete in under 0.5 seconds
                    float rotationStartTime = Time.time;
                    float quickRotationSpeed = rotationSpeed * 4f; // 4x faster rotation
                    float maxRotationTime = 0.5f; // Max 0.5 second for rotation
                    
                    while (Quaternion.Angle(guardTransform.rotation, targetRotation) > 5f && 
                           Time.time - rotationStartTime < maxRotationTime)
                    {
                        guardTransform.rotation = Quaternion.RotateTowards(
                            guardTransform.rotation, 
                            targetRotation, 
                            quickRotationSpeed * Time.deltaTime
                        );
                        yield return null;
                    }
                    guardTransform.rotation = targetRotation;
                }
                else
                {
                    ModLogger.Debug($"Guard {gameObject.name} already facing correct direction (angle difference: {angleDifference:F1}°)");
                }
            }

            ModLogger.Info($"Guard {gameObject.name} ready to operate {targetDoor.doorName}");
            ChangeState(DoorInteractionState.OperatingDoor);
        }

        /// <summary>
        /// Wait for the guard to be completely stationary before proceeding
        /// </summary>
        private IEnumerator WaitForGuardToBeStationary()
        {
            Vector3 lastPosition = guardTransform.position;
            float stationaryTime = 0f;
            float requiredStationaryTime = 0.3f; // Guard must be still for 0.3 seconds
            float maxWaitTime = 3f;
            float startWaitTime = Time.time;
            
            while (stationaryTime < requiredStationaryTime && Time.time - startWaitTime < maxWaitTime)
            {
                yield return new WaitForSeconds(0.1f);
                
                float distanceMoved = Vector3.Distance(guardTransform.position, lastPosition);
                
                if (distanceMoved < 0.05f) // Very small movement threshold
                {
                    stationaryTime += 0.1f;
                }
                else
                {
                    stationaryTime = 0f; // Reset if guard moved
                    lastPosition = guardTransform.position;
                }
            }
            
            ModLogger.Debug($"Guard {gameObject.name} is now stationary (waited {stationaryTime:F1}s)");
        }

        private IEnumerator HandleOperatingDoor()
        {
            ModLogger.Info($"Guard {gameObject.name} attempting to operate door {targetDoor.doorName}");
            
            // Check door state before operating
            ModLogger.Debug($"Door {targetDoor.doorName} state - Locked: {targetDoor.IsLocked()}, Closed: {targetDoor.IsClosed()}");
            
            // Unlock and open door
            if (targetDoor.IsLocked())
            {
                ModLogger.Debug($"Unlocking door {targetDoor.doorName}");
                targetDoor.UnlockDoor();
                yield return new WaitForSeconds(0.2f);
            }

            // Try to open the door
            ModLogger.Debug($"Opening door {targetDoor.doorName}");
            try
            {
                targetDoor.OpenDoor();
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error opening door {targetDoor.doorName}: {ex.Message}");
            }
            
            // Check if door actually started opening
            yield return new WaitForSeconds(0.1f);
            if (targetDoor.IsClosed())
            {
                ModLogger.Warn($"Door {targetDoor.doorName} still appears closed after OpenDoor() call");
                // Try again
                try
                {
                    targetDoor.OpenDoor();
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error on second attempt to open door {targetDoor.doorName}: {ex.Message}");
                }
            }
            
            // Disable NavMesh obstacle for pass-through doors
            if (interactionType == JailDoor.DoorInteractionType.PassThrough)
            {
                SetDoorNavMeshObstacle(targetDoor, false);
            }

            // Wait for door animation
            yield return new WaitForSeconds(doorAnimationWait);

            ModLogger.Info($"Door {targetDoor.doorName} operated by guard {gameObject.name}. Final state - Closed: {targetDoor.IsClosed()}");

            // Determine next state based on interaction type
            if (interactionType == JailDoor.DoorInteractionType.PassThrough)
            {
                ChangeState(DoorInteractionState.MovingThrough);
            }
            else
            {
                ChangeState(DoorInteractionState.WaitingForPrisoner);
            }
        }

        private IEnumerator HandleWaitingForPrisoner()
        {
            // For operation-only doors, wait for prisoner to move
            ModLogger.Info($"Guard {gameObject.name} waiting for prisoner movement at {targetDoor.doorName}");
            
            if (targetPrisoner != null)
            {
                // Show instruction to prisoner if available
                // This would integrate with your existing instruction system
            }

            yield return new WaitForSeconds(prisonerWaitTime);

            ChangeState(DoorInteractionState.ClosingDoor);
        }

        private IEnumerator HandleMovingThrough()
        {
            if (exitPoint == null)
            {
                FailInteraction("No exit point defined for pass-through door");
                yield break;
            }

            // Navigate to exit point
            navAgent.SetDestination(exitPoint.position);
            ModLogger.Debug($"Guard {gameObject.name} moving through {targetDoor.doorName}");

            // Wait for navigation to complete - similar robust approach
            float startTime = Time.time;
            float maxNavigationTime = 15f; // Max 15 seconds to move through
            
            while (Time.time - startTime < maxNavigationTime)
            {
                float distanceToTarget = Vector3.Distance(guardTransform.position, exitPoint.position);
                
                // Check if we're close enough
                if (distanceToTarget <= 1.0f)
                {
                    ModLogger.Debug($"Guard {gameObject.name} reached exit point. Distance: {distanceToTarget:F2}m");
                    break;
                }
                
                // Check if navigation is stuck
                if (!navAgent.pathPending && navAgent.remainingDistance < 0.1f && distanceToTarget > 2.0f)
                {
                    ModLogger.Warn($"Guard {gameObject.name} exit navigation stuck. Distance: {distanceToTarget:F2}m. Re-setting destination.");
                    navAgent.SetDestination(exitPoint.position);
                }
                
                yield return new WaitForSeconds(0.2f); // Check every 200ms
            }
            
            // Final position check
            float finalDistance = Vector3.Distance(guardTransform.position, exitPoint.position);
            if (finalDistance > 2.0f)
            {
                ModLogger.Warn($"Guard {gameObject.name} couldn't reach exit point after {maxNavigationTime}s. Distance: {finalDistance:F2}m. Teleporting.");
                guardTransform.position = exitPoint.position;
            }

            ChangeState(DoorInteractionState.AtExitPoint);
        }

        private IEnumerator HandleAtExitPoint()
        {
            // Align rotation to exit point
            if (exitPoint != null)
            {
                Quaternion targetRotation = exitPoint.rotation;
                while (Quaternion.Angle(guardTransform.rotation, targetRotation) > 5f)
                {
                    guardTransform.rotation = Quaternion.RotateTowards(
                        guardTransform.rotation, 
                        targetRotation, 
                        rotationSpeed * Time.deltaTime
                    );
                    yield return null;
                }
                guardTransform.rotation = targetRotation;
            }

            // Brief wait for prisoner to follow through
            yield return new WaitForSeconds(transitionTime);

            ModLogger.Info($"Guard {gameObject.name} at exit point of {targetDoor.doorName}");
            ChangeState(DoorInteractionState.ClosingDoor);
        }

        private IEnumerator HandleClosingDoor()
        {
            // Re-enable NavMesh obstacle
            if (interactionType == JailDoor.DoorInteractionType.PassThrough)
            {
                SetDoorNavMeshObstacle(targetDoor, true);
            }

            // Close and lock door
            targetDoor.CloseDoor();
            yield return new WaitForSeconds(doorAnimationWait);

            targetDoor.LockDoor();

            ModLogger.Info($"Door {targetDoor.doorName} closed and secured by guard {gameObject.name}");
            ChangeState(DoorInteractionState.Complete);
        }

        private void HandleTimeout()
        {
            attemptCount++;
            if (attemptCount < maxAttempts)
            {
                ModLogger.Warn($"Guard {gameObject.name} timeout in state {currentState}. Retrying attempt {attemptCount}/{maxAttempts}");
                
                // Reset to navigation state for retry
                ChangeState(DoorInteractionState.NavigatingToApproachPoint);
                MelonCoroutines.Start(ExecuteInteractionSequence());
            }
            else
            {
                FailInteraction($"Max attempts ({maxAttempts}) reached. Timeout in state {currentState}");
            }
        }

        private void FailInteraction(string reason)
        {
            ModLogger.Error($"DoorInteractionController: {reason} for guard {gameObject.name}");
            ChangeState(DoorInteractionState.Failed);
            OnInteractionFailed?.Invoke(reason);
            
            // Reset to idle after brief delay
            MelonCoroutines.Start(ResetToIdleAfterDelay(2f));
        }

        private IEnumerator ResetToIdleAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            ChangeState(DoorInteractionState.Idle);
        }

        private void ChangeState(DoorInteractionState newState)
        {
            if (currentState != newState)
            {
                ModLogger.Debug($"Guard {gameObject.name} door interaction: {currentState} -> {newState}");
                currentState = newState;
                stateStartTime = Time.time;
                OnStateChanged?.Invoke(newState);
            }
        }

        private void SetDoorNavMeshObstacle(JailDoor door, bool enabled)
        {
            if (door?.doorInstance != null)
            {
                var obstacle = door.doorInstance.GetComponent<UnityEngine.AI.NavMeshObstacle>();
                if (obstacle != null)
                {
                    obstacle.enabled = enabled;
                }
            }
        }

        public string GetCurrentStateInfo()
        {
            return $"State: {currentState}, Door: {targetDoor?.doorName ?? "None"}, Type: {interactionType}";
        }

        /// <summary>
        /// Check if this controller can handle a specific door
        /// Used by trigger handlers to determine if interaction is needed
        /// </summary>
        public bool CanHandleDoor(JailDoor door)
        {
            if (door == null) return false;
            
            // Can't handle if currently busy with a different door
            if (IsBusy() && targetDoor != door)
            {
                return false;
            }
            
            // Can handle if idle or already working on this door
            return currentState == DoorInteractionState.Idle || targetDoor == door;
        }

        /// <summary>
        /// Quick door interaction for trigger-based systems
        /// Simplified version that automatically determines interaction type
        /// </summary>
        public bool TryQuickDoorInteraction(JailDoor door, Player prisoner = null)
        {
            if (!CanHandleDoor(door)) return false;
            
            // Auto-determine interaction type based on door configuration
            var interactionType = door.interactionType;
            if (interactionType == default(JailDoor.DoorInteractionType))
            {
                // Auto-determine from door type
                switch (door.doorType)
                {
                    case JailDoor.DoorType.CellDoor:
                    case JailDoor.DoorType.HoldingCellDoor:
                        interactionType = JailDoor.DoorInteractionType.OperationOnly;
                        break;
                    default:
                        interactionType = JailDoor.DoorInteractionType.PassThrough;
                        break;
                }
            }
            
            StartDoorInteraction(door, interactionType, prisoner);
            return true;
        }

        /// <summary>
        /// Get the door this controller is currently working on
        /// </summary>
        public JailDoor GetCurrentDoor()
        {
            return targetDoor;
        }

        /// <summary>
        /// Check if currently working on a specific door
        /// </summary>
        public bool IsHandling(JailDoor door)
        {
            return targetDoor == door && IsBusy();
        }

        /// <summary>
        /// Force complete current interaction (emergency stop)
        /// </summary>
        public void ForceComplete()
        {
            if (currentState != DoorInteractionState.Idle)
            {
                ModLogger.Info($"Force completing door interaction for {gameObject.name}");
                ChangeState(DoorInteractionState.Complete);
            }
        }
    }
}