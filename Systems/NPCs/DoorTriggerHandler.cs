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
    /// Handles automatic door interactions when guards enter door trigger zones
    /// Attach this to trigger colliders inside doors for automatic detection
    /// </summary>
    public class DoorTriggerHandler : MonoBehaviour
    {
#if !MONO
        public DoorTriggerHandler(System.IntPtr ptr) : base(ptr) { }
#endif

        // Door Settings
        public JailDoor associatedDoor;
        public bool autoDetectDoor = true;
        public float interactionDistance = 2f;
        
        // Trigger Behavior
        public bool requiresApproach = true; // Guard must approach from correct side
        public bool autoOpen = true;
        public bool autoClose = true;
        public float autoCloseDelay = 2f;
        
        // State tracking
        private bool isDoorOpen = false;
        private object autoCloseCoroutine;
        private System.Collections.Generic.HashSet<JailGuardBehavior> nearbyGuards = new System.Collections.Generic.HashSet<JailGuardBehavior>();

        void Start()
        {
            // Auto-detect the associated door if not set
            if (autoDetectDoor && associatedDoor == null)
            {
                DetectAssociatedDoor();
            }
            
            // Ensure we have a trigger collider
            var collider = GetComponent<Collider>();
            if (collider != null && !collider.isTrigger)
            {
                ModLogger.Warn($"DoorTriggerHandler: Collider on {gameObject.name} is not set as trigger!");
            }
            
            ModLogger.Info($"DoorTriggerHandler initialized for door: {associatedDoor?.doorName ?? "Unknown"}");
        }

        private void DetectAssociatedDoor()
        {
            // Look up the hierarchy for door components
            Transform current = transform.parent;
            while (current != null)
            {
                // Check if this transform is a door holder
                var jailController = Core.ActiveJailController;
                if (jailController?.booking != null)
                {
                    // Check booking doors
                    var doors = new JailDoor[] 
                    { 
                        // bookingEntryDoor not available in this system
                        jailController.booking.bookingInnerDoor,
                        jailController.booking.guardDoor
                    };
                    
                    foreach (var door in doors)
                    {
                        if (door?.doorHolder == current)
                        {
                            associatedDoor = door;
                            ModLogger.Info($"Auto-detected door: {door.doorName}");
                            return;
                        }
                    }
                }
                
                // Check jail cells
                if (jailController?.cells != null)
                {
                    foreach (var cell in jailController.cells)
                    {
                        if (cell.cellDoor?.doorHolder == current)
                        {
                            associatedDoor = cell.cellDoor;
                            ModLogger.Info($"Auto-detected cell door: {cell.cellDoor.doorName}");
                            return;
                        }
                    }
                }
                
                // Check holding cells
                if (jailController?.holdingCells != null)
                {
                    foreach (var cell in jailController.holdingCells)
                    {
                        if (cell.cellDoor?.doorHolder == current)
                        {
                            associatedDoor = cell.cellDoor;
                            ModLogger.Info($"Auto-detected holding cell door: {cell.cellDoor.doorName}");
                            return;
                        }
                    }
                }
                
                current = current.parent;
            }
            
            ModLogger.Warn($"DoorTriggerHandler: Could not auto-detect door for trigger {gameObject.name}");
        }

        void OnTriggerEnter(Collider other)
        {
            // Check if it's a guard
            var guard = other.GetComponent<JailGuardBehavior>();
            if (guard == null) return;
            
            // Check if guard has a DoorInteractionController
            var doorController = guard.GetComponent<DoorInteractionController>();
            if (doorController == null)
            {
                ModLogger.Debug($"Guard {guard.badgeNumber} doesn't have DoorInteractionController - adding one");
                doorController = guard.gameObject.AddComponent<DoorInteractionController>();
            }
            
            nearbyGuards.Add(guard);
            ModLogger.Debug($"Guard {guard.badgeNumber} entered door trigger for {associatedDoor?.doorName}");
            
            // Auto-open door if configured
            if (autoOpen && associatedDoor != null && !isDoorOpen)
            {
                HandleGuardApproach(guard, doorController);
            }
        }

        void OnTriggerExit(Collider other)
        {
            var guard = other.GetComponent<JailGuardBehavior>();
            if (guard == null) return;
            
            nearbyGuards.Remove(guard);
            ModLogger.Debug($"Guard {guard.badgeNumber} exited door trigger for {associatedDoor?.doorName}");
            
            // Auto-close door if no guards nearby
            if (autoClose && nearbyGuards.Count == 0 && isDoorOpen)
            {
                StartAutoCloseTimer();
            }
        }

        private void HandleGuardApproach(JailGuardBehavior guard, DoorInteractionController doorController)
        {
            if (associatedDoor == null) return;
            
            // Check if door needs interaction
            if (associatedDoor.IsClosed() && !doorController.IsBusy())
            {
                ModLogger.Info($"Auto-triggering door interaction for guard {guard.badgeNumber} at {associatedDoor.doorName}");
                
                // Determine interaction type based on door setup
                var interactionType = DetermineInteractionType();
                
                // Start door interaction - let the DoorInteractionController handle the details
                doorController.StartDoorInteraction(associatedDoor, interactionType, null);
                isDoorOpen = true;
                
                // Cancel any pending auto-close
                if (autoCloseCoroutine != null)
                {
                    MelonCoroutines.Stop(autoCloseCoroutine);
                    autoCloseCoroutine = null;
                }
            }
        }

        private JailDoor.DoorInteractionType DetermineInteractionType()
        {
            if (associatedDoor == null) return JailDoor.DoorInteractionType.OperationOnly;
            
            // Use the door's configured interaction type, or determine from door type
            if (associatedDoor.interactionType != default(JailDoor.DoorInteractionType))
            {
                return associatedDoor.interactionType;
            }
            
            // Auto-determine based on door type
            switch (associatedDoor.doorType)
            {
                case JailDoor.DoorType.CellDoor:
                case JailDoor.DoorType.HoldingCellDoor:
                    return JailDoor.DoorInteractionType.OperationOnly;
                    
                case JailDoor.DoorType.EntryDoor:
                case JailDoor.DoorType.GuardDoor:
                case JailDoor.DoorType.AreaDoor:
                    return JailDoor.DoorInteractionType.PassThrough;
                    
                default:
                    return JailDoor.DoorInteractionType.PassThrough;
            }
        }

        private void StartAutoCloseTimer()
        {
            if (autoCloseCoroutine != null)
            {
                MelonCoroutines.Stop(autoCloseCoroutine);
            }
            
            autoCloseCoroutine = MelonCoroutines.Start(AutoCloseAfterDelay());
        }

        private IEnumerator AutoCloseAfterDelay()
        {
            yield return new WaitForSeconds(autoCloseDelay);
            
            // Double-check no guards are nearby
            if (nearbyGuards.Count == 0 && associatedDoor != null && isDoorOpen)
            {
                ModLogger.Info($"Auto-closing door {associatedDoor.doorName} after delay");
                associatedDoor.CloseDoor();
                isDoorOpen = false;
            }
            
            autoCloseCoroutine = null;
        }

        /// <summary>
        /// Force close the door (called by external systems)
        /// </summary>
        public void ForceDoorClose()
        {
            if (associatedDoor != null && isDoorOpen)
            {
                associatedDoor.CloseDoor();
                isDoorOpen = false;
                
                if (autoCloseCoroutine != null)
                {
                    MelonCoroutines.Stop(autoCloseCoroutine);
                    autoCloseCoroutine = null;
                }
            }
        }

        /// <summary>
        /// Check if any guards are currently in this trigger zone
        /// </summary>
        public bool HasGuardsNearby()
        {
            return nearbyGuards.Count > 0;
        }

        /// <summary>
        /// Get all guards currently in this trigger zone
        /// </summary>
        public System.Collections.Generic.HashSet<JailGuardBehavior> GetNearbyGuards()
        {
            return new System.Collections.Generic.HashSet<JailGuardBehavior>(nearbyGuards);
        }

        void OnDestroy()
        {
            if (autoCloseCoroutine != null)
            {
                MelonCoroutines.Stop(autoCloseCoroutine);
            }
        }

        // Debug visualization
        void OnDrawGizmos()
        {
            if (associatedDoor != null)
            {
                Gizmos.color = isDoorOpen ? Color.green : Color.red;
                Gizmos.DrawWireCube(transform.position, GetComponent<Collider>()?.bounds.size ?? Vector3.one);
                
                // Draw line to associated door
                if (associatedDoor.doorHolder != null)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawLine(transform.position, associatedDoor.doorHolder.position);
                }
            }
        }
    }
}