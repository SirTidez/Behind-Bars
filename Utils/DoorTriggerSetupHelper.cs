using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Helper utility to automatically setup DoorTriggerHandler components on existing door triggers
    /// Run this once to configure all your door triggers with the new system
    /// </summary>
    public static class DoorTriggerSetupHelper
    {
        /// <summary>
        /// Automatically find and setup all door triggers in the jail structure
        /// </summary>
        public static void SetupAllDoorTriggers()
        {
            ModLogger.Info("=== DOOR TRIGGER SETUP HELPER ===");
            
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("No active jail controller found!");
                return;
            }

            int setupCount = 0;

            // Setup triggers for booking doors
            if (jailController.booking != null)
            {
                // Note: Entry door is handled by system directly, not needed for triggers
                setupCount += SetupTriggersForDoor(jailController.booking.bookingInnerDoor, "Inner Door");
                setupCount += SetupTriggersForDoor(jailController.booking.guardDoor, "Guard Door");
                setupCount += SetupTriggersForDoor(jailController.booking.prisonEntryDoor, "Prison Entry Door");
            }

            // Setup triggers for holding cell doors
            if (jailController.holdingCells != null)
            {
                foreach (var cell in jailController.holdingCells)
                {
                    setupCount += SetupTriggersForDoor(cell.cellDoor, $"Holding Cell {cell.cellIndex}");
                }
            }

            // Setup triggers for jail cell doors
            if (jailController.cells != null)
            {
                foreach (var cell in jailController.cells)
                {
                    setupCount += SetupTriggersForDoor(cell.cellDoor, $"Jail Cell {cell.cellIndex}");
                }
            }

            ModLogger.Info($"Door trigger setup complete! Configured {setupCount} triggers.");
            ModLogger.Info("=== END DOOR TRIGGER SETUP ===");
        }

        /// <summary>
        /// Setup triggers for a specific door
        /// </summary>
        private static int SetupTriggersForDoor(JailDoor door, string doorDisplayName)
        {
            if (door?.doorHolder == null) return 0;

            ModLogger.Info($"Setting up triggers for {doorDisplayName}");

            // Find all trigger colliders under this door
            var triggers = FindTriggersInHierarchy(door.doorHolder);
            int setupCount = 0;

            foreach (var trigger in triggers)
            {
                // Check if it already has a DoorTriggerHandler
                var existingHandler = trigger.GetComponent<DoorTriggerHandler>();
                if (existingHandler == null)
                {
                    // Add the handler
                    var handler = trigger.gameObject.AddComponent<DoorTriggerHandler>();
                    
                    // Configure it
                    handler.associatedDoor = door;
                    handler.autoDetectDoor = false; // We're manually assigning it
                    
                    ModLogger.Info($"  ✓ Added DoorTriggerHandler to {trigger.name}");
                    setupCount++;
                }
                else
                {
                    // Update existing handler
                    existingHandler.associatedDoor = door;
                    ModLogger.Info($"  ✓ Updated existing DoorTriggerHandler on {trigger.name}");
                    setupCount++;
                }
            }

            if (setupCount == 0)
            {
                ModLogger.Warn($"  ⚠ No trigger colliders found for {doorDisplayName}");
                
                // Log children for debugging
                ModLogger.Debug($"    Children of {door.doorHolder.name}:");
                for (int i = 0; i < door.doorHolder.childCount; i++)
                {
                    var child = door.doorHolder.GetChild(i);
                    var collider = child.GetComponent<Collider>();
                    ModLogger.Debug($"      - {child.name} (Collider: {collider != null}, Trigger: {collider?.isTrigger == true})");
                }
            }

            return setupCount;
        }

        /// <summary>
        /// Find all trigger colliders in a hierarchy
        /// </summary>
        private static System.Collections.Generic.List<Collider> FindTriggersInHierarchy(Transform root)
        {
            var triggers = new System.Collections.Generic.List<Collider>();
            
            // Check root
            var rootCollider = root.GetComponent<Collider>();
            if (rootCollider != null && rootCollider.isTrigger)
            {
                triggers.Add(rootCollider);
            }
            
            // Check all children recursively
            FindTriggersRecursive(root, triggers);
            
            return triggers;
        }

        /// <summary>
        /// Recursively find trigger colliders
        /// </summary>
        private static void FindTriggersRecursive(Transform parent, System.Collections.Generic.List<Collider> triggers)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                
                // Check if this child has a trigger collider
                var collider = child.GetComponent<Collider>();
                if (collider != null && collider.isTrigger)
                {
                    triggers.Add(collider);
                }
                
                // Check children of this child
                FindTriggersRecursive(child, triggers);
            }
        }

        /// <summary>
        /// Create trigger colliders for doors that don't have them
        /// </summary>
        public static void CreateMissingTriggers()
        {
            ModLogger.Info("=== CREATING MISSING DOOR TRIGGERS ===");
            
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("No active jail controller found!");
                return;
            }

            int createdCount = 0;

            // Check booking doors
            if (jailController.booking != null)
            {
                // Note: Entry door is handled by system directly, not needed for triggers
                createdCount += CreateTriggerIfMissing(jailController.booking.bookingInnerDoor, "Inner Door");
                createdCount += CreateTriggerIfMissing(jailController.booking.guardDoor, "Guard Door");
                createdCount += CreateTriggerIfMissing(jailController.booking.prisonEntryDoor, "Prison Entry Door");
            }

            // Check cell doors
            if (jailController.holdingCells != null)
            {
                foreach (var cell in jailController.holdingCells)
                {
                    createdCount += CreateTriggerIfMissing(cell.cellDoor, $"Holding Cell {cell.cellIndex}");
                }
            }

            if (jailController.cells != null)
            {
                foreach (var cell in jailController.cells)
                {
                    createdCount += CreateTriggerIfMissing(cell.cellDoor, $"Jail Cell {cell.cellIndex}");
                }
            }

            ModLogger.Info($"Created {createdCount} missing door triggers.");
            ModLogger.Info("=== END CREATING MISSING TRIGGERS ===");
        }

        /// <summary>
        /// Create a trigger for a door if it doesn't have one
        /// </summary>
        private static int CreateTriggerIfMissing(JailDoor door, string doorDisplayName)
        {
            if (door?.doorHolder == null) return 0;

            // Check if door already has triggers
            var existingTriggers = FindTriggersInHierarchy(door.doorHolder);
            if (existingTriggers.Count > 0)
            {
                ModLogger.Debug($"{doorDisplayName} already has {existingTriggers.Count} trigger(s)");
                return 0;
            }

            // Create a new trigger GameObject
            var triggerGO = new GameObject($"{door.doorName}_Trigger");
            triggerGO.transform.SetParent(door.doorHolder);
            triggerGO.transform.localPosition = Vector3.zero;

            // Add a box collider set as trigger
            var boxCollider = triggerGO.AddComponent<BoxCollider>();
            boxCollider.isTrigger = true;
            boxCollider.size = new Vector3(3f, 3f, 1f); // Reasonable size for a door trigger

            // Add and configure the DoorTriggerHandler
            var handler = triggerGO.AddComponent<DoorTriggerHandler>();
            handler.associatedDoor = door;
            handler.autoDetectDoor = false;

            ModLogger.Info($"✓ Created trigger for {doorDisplayName}");
            return 1;
        }

        /// <summary>
        /// Debug method to list all doors and their trigger status
        /// </summary>
        public static void DebugListAllDoors()
        {
            ModLogger.Info("=== DOOR TRIGGER STATUS REPORT ===");
            
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("No active jail controller found!");
                return;
            }

            // Check booking doors
            if (jailController.booking != null)
            {
                // Note: Entry door is handled by system directly, not needed for triggers
                DebugDoorStatus(jailController.booking.bookingInnerDoor, "Inner Door");
                DebugDoorStatus(jailController.booking.guardDoor, "Guard Door");
                DebugDoorStatus(jailController.booking.prisonEntryDoor, "Prison Entry Door");
            }

            // Check cell doors
            if (jailController.holdingCells != null)
            {
                foreach (var cell in jailController.holdingCells)
                {
                    DebugDoorStatus(cell.cellDoor, $"Holding Cell {cell.cellIndex}");
                }
            }

            if (jailController.cells != null)
            {
                foreach (var cell in jailController.cells)
                {
                    DebugDoorStatus(cell.cellDoor, $"Jail Cell {cell.cellIndex}");
                }
            }

            ModLogger.Info("=== END DOOR STATUS REPORT ===");
        }

        /// <summary>
        /// Debug a single door's trigger status
        /// </summary>
        private static void DebugDoorStatus(JailDoor door, string doorDisplayName)
        {
            if (door?.doorHolder == null)
            {
                ModLogger.Info($"❌ {doorDisplayName}: MISSING DOOR");
                return;
            }

            var triggers = FindTriggersInHierarchy(door.doorHolder);
            var handlers = door.doorHolder.GetComponentsInChildren<DoorTriggerHandler>();

            string status = triggers.Count > 0 ? "✓" : "❌";
            string handlerStatus = handlers.Length > 0 ? "✓" : "❌";

            ModLogger.Info($"{status} {doorDisplayName}: {triggers.Count} trigger(s), {handlers.Length} handler(s)");
        }
    }
}