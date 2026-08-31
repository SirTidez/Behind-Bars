using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
using BBHelpers = Behind_Bars.Helpers.Helpers;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Helper utility to automatically setup DoorTriggerHandler components on existing door triggers
    /// Run this once to configure all your door triggers with the new system
    /// </summary>
    /// <remarks>
    /// These are in-game scene mutation utilities. They add or reconfigure
    /// components on the active jail hierarchy and provide no undo or cleanup
    /// operation; callers should run them only when the jail controller and its
    /// door references are ready.
    /// </remarks>
    public static class DoorTriggerSetupHelper
    {
        /// <summary>
        /// Automatically find and setup all door triggers in the jail structure
        /// </summary>
        /// <remarks>Configures existing trigger colliders for booking, holding,
        /// and jail-cell doors. Existing handlers are updated as well as missing
        /// handlers being added, so the reported count includes both actions.
        /// The booking entry door is intentionally included in the traversal but
        /// remains documented as being handled directly by the system.</remarks>
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
        /// <param name="door">The jail door whose holder hierarchy is searched.</param>
        /// <param name="doorDisplayName">The diagnostic label used in logs.</param>
        /// <returns>The number of existing handlers updated or new handlers
        /// added; zero when the door/holder is absent or no trigger collider is found.</returns>
        /// <remarks>Every matching trigger gets the supplied door association
        /// and has auto-detection disabled. The hierarchy search includes child
        /// objects recursively and does not filter inactive transforms.</remarks>
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
                    var handler = BBHelpers.AddComponentSafe<DoorTriggerHandler>(trigger.gameObject);
                    
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
        /// <param name="root">The root transform to inspect, including itself.</param>
        /// <returns>A newly allocated list of trigger colliders found recursively.</returns>
        /// <remarks>The caller currently guards a null door holder; this helper
        /// itself does not null-check <paramref name="root"/>.</remarks>
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
        /// <param name="parent">The transform whose descendants are traversed.</param>
        /// <param name="triggers">The list to which trigger colliders are appended.</param>
        /// <remarks>Traversal includes inactive descendants and assumes both
        /// arguments are non-null.</remarks>
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
        /// <remarks>For each configured door without any trigger collider, adds
        /// a child GameObject with a fixed-size trigger BoxCollider and a
        /// manually associated DoorTriggerHandler. Existing triggers are left
        /// unchanged; created objects are not tagged, layered, or tracked for
        /// later removal.</remarks>
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
        /// <param name="door">The jail door whose holder should receive a trigger child.</param>
        /// <param name="doorDisplayName">The diagnostic label used in logs.</param>
        /// <returns><c>1</c> when a trigger is created; otherwise <c>0</c>.</returns>
        /// <remarks>Any existing trigger anywhere in the holder hierarchy
        /// prevents creation. The new BoxCollider is centered at local zero,
        /// sized <c>(3, 3, 1)</c>, and marked as a trigger.</remarks>
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
            var handler = BBHelpers.AddComponentSafe<DoorTriggerHandler>(triggerGO);
            handler.associatedDoor = door;
            handler.autoDetectDoor = false;

            ModLogger.Info($"✓ Created trigger for {doorDisplayName}");
            return 1;
        }

        /// <summary>
        /// Debug method to list all doors and their trigger status
        /// </summary>
        /// <remarks>Read-only with respect to scene objects. Trigger counts are
        /// collected recursively, while Unity's default
        /// <c>GetComponentsInChildren</c> handler query may omit inactive
        /// objects, so the two counts can describe different active scopes.</remarks>
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
        /// <param name="door">The jail door to inspect.</param>
        /// <param name="doorDisplayName">The diagnostic label used in logs.</param>
        /// <remarks>Logs the recursive trigger count and the handler count; it
        /// does not add, update, or remove components.</remarks>
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
