using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Manual setup helper for adding DoorTriggerHandler to specific door triggers by name
    /// Use this in-game to add the script to your existing trigger colliders
    /// </summary>
    public static class ManualDoorTriggerSetup
    {
        /// <summary>
        /// Find and setup a specific door trigger by its GameObject name
        /// </summary>
        public static bool SetupDoorTriggerByName(string triggerName, string doorName = null)
        {
            ModLogger.Info($"Looking for door trigger: {triggerName}");
            
            // Find the trigger GameObject by name
            var triggerGO = GameObject.Find(triggerName);
            if (triggerGO == null)
            {
                ModLogger.Error($"Could not find GameObject with name: {triggerName}");
                return false;
            }
            
            // Check if it has a collider set as trigger
            var collider = triggerGO.GetComponent<Collider>();
            if (collider == null || !collider.isTrigger)
            {
                ModLogger.Error($"GameObject {triggerName} does not have a trigger collider!");
                return false;
            }
            
            // Check if it already has the handler
            var existingHandler = triggerGO.GetComponent<DoorTriggerHandler>();
            if (existingHandler != null)
            {
                ModLogger.Info($"GameObject {triggerName} already has DoorTriggerHandler");
                return true;
            }
            
            // Add the DoorTriggerHandler component
            var handler = triggerGO.AddComponent<DoorTriggerHandler>();
            
            // Try to auto-detect the door or manually assign if doorName provided
            if (!string.IsNullOrEmpty(doorName))
            {
                var door = FindDoorByName(doorName);
                if (door != null)
                {
                    handler.associatedDoor = door;
                    handler.autoDetectDoor = false; // We manually assigned it
                    ModLogger.Info($"✓ Added DoorTriggerHandler to {triggerName} with manually assigned door: {doorName}");
                }
                else
                {
                    ModLogger.Warn($"Could not find door: {doorName}, will use auto-detection");
                    handler.autoDetectDoor = true;
                }
            }
            else
            {
                handler.autoDetectDoor = true; // Let it auto-detect
                ModLogger.Info($"✓ Added DoorTriggerHandler to {triggerName} with auto-detection enabled");
            }
            
            return true;
        }
        
        /// <summary>
        /// Setup the specific jail door triggers under PatrolPoints
        /// </summary>
        public static void SetupJailDoorTriggers()
        {
            ModLogger.Info("=== SETTING UP JAIL DOOR TRIGGERS ===");
            
            // Find the PatrolPoints parent first
            var patrolPoints = GameObject.Find("PatrolPoints");
            if (patrolPoints == null)
            {
                ModLogger.Error("PatrolPoints GameObject not found! Cannot setup door triggers.");
                return;
            }
            
            // Specific door trigger names under PatrolPoints
            string[] triggerNames = {
                "GuardRoomDoorTrigger",
                "BookingDoorTrigger", 
                "PrisonDoorTrigger"
            };
            
            // Door name mappings for each trigger
            string[] doorNames = {
                "Booking Guard Door",    // GuardRoomDoorTrigger -> Guard Door
                "Booking Inner Door",    // BookingDoorTrigger -> Inner Door  
                "Prison Enter Door"      // PrisonDoorTrigger -> Prison Entry Door
            };
            
            int setupCount = 0;
            for (int i = 0; i < triggerNames.Length; i++)
            {
                var triggerTransform = patrolPoints.transform.Find(triggerNames[i]);
                if (triggerTransform != null)
                {
                    if (SetupSpecificTrigger(triggerTransform.gameObject, doorNames[i]))
                    {
                        setupCount++;
                    }
                }
                else
                {
                    ModLogger.Warn($"Trigger not found: PatrolPoints/{triggerNames[i]}");
                }
            }
            
            ModLogger.Info($"Setup complete! Configured {setupCount} jail door triggers.");
            ModLogger.Info("=== END JAIL DOOR TRIGGER SETUP ===");
        }
        
        /// <summary>
        /// Setup a specific trigger GameObject with door association
        /// </summary>
        private static bool SetupSpecificTrigger(GameObject triggerGO, string doorName)
        {
            if (triggerGO == null) return false;
            
            ModLogger.Info($"Setting up trigger: {triggerGO.name}");
            
            // Check if it has a collider set as trigger
            var collider = triggerGO.GetComponent<Collider>();
            if (collider == null || !collider.isTrigger)
            {
                ModLogger.Error($"GameObject {triggerGO.name} does not have a trigger collider!");
                return false;
            }
            
            // Check if it already has the handler
            var existingHandler = triggerGO.GetComponent<DoorTriggerHandler>();
            if (existingHandler != null)
            {
                ModLogger.Info($"GameObject {triggerGO.name} already has DoorTriggerHandler");
                return true;
            }
            
            // Add the DoorTriggerHandler component
            var handler = triggerGO.AddComponent<DoorTriggerHandler>();
            
            // Try to find and assign the specific door
            var door = FindDoorByName(doorName);
            if (door != null)
            {
                handler.associatedDoor = door;
                handler.autoDetectDoor = false; // We manually assigned it
                ModLogger.Info($"✓ Added DoorTriggerHandler to {triggerGO.name} with door: {doorName}");
            }
            else
            {
                ModLogger.Warn($"Could not find door: {doorName}, using auto-detection for {triggerGO.name}");
                handler.autoDetectDoor = true;
            }
            
            return true;
        }
        
        /// <summary>
        /// Find all GameObjects with "DoorTrigger" in their name and set them up
        /// </summary>
        public static void SetupAllDoorTriggersInScene()
        {
            ModLogger.Info("=== FINDING ALL DOOR TRIGGERS IN SCENE ===");
            
            // Find all GameObjects in the scene
            var allObjects = GameObject.FindObjectsOfType<GameObject>();
            int setupCount = 0;
            
            foreach (var go in allObjects)
            {
                // Look for objects with "Trigger" in the name that have trigger colliders
                if (go.name.Contains("Trigger") || go.name.Contains("trigger"))
                {
                    var collider = go.GetComponent<Collider>();
                    if (collider != null && collider.isTrigger)
                    {
                        // Check if it doesn't already have the handler
                        if (go.GetComponent<DoorTriggerHandler>() == null)
                        {
                            ModLogger.Info($"Found potential door trigger: {go.name}");
                            
                            // Add the handler
                            var handler = go.AddComponent<DoorTriggerHandler>();
                            handler.autoDetectDoor = true;
                            
                            setupCount++;
                            ModLogger.Info($"✓ Added DoorTriggerHandler to {go.name}");
                        }
                    }
                }
            }
            
            ModLogger.Info($"Auto-setup complete! Found and configured {setupCount} door triggers.");
            ModLogger.Info("=== END AUTO DOOR TRIGGER SETUP ===");
        }
        
        /// <summary>
        /// Find a JailDoor by name
        /// </summary>
        private static JailDoor FindDoorByName(string doorName)
        {
            var jailController = Core.ActiveJailController;
            if (jailController == null) return null;
            
            // Check booking doors
            if (jailController.booking != null)
            {
                var doors = new[] {
                    jailController.booking.bookingInnerDoor,
                    jailController.booking.guardDoor,
                    jailController.booking.prisonEntryDoor
                };
                
                foreach (var door in doors)
                {
                    if (door?.doorName == doorName)
                        return door;
                }
            }
            
            // Check cell doors
            if (jailController.holdingCells != null)
            {
                foreach (var cell in jailController.holdingCells)
                {
                    if (cell.cellDoor?.doorName == doorName)
                        return cell.cellDoor;
                }
            }
            
            if (jailController.cells != null)
            {
                foreach (var cell in jailController.cells)
                {
                    if (cell.cellDoor?.doorName == doorName)
                        return cell.cellDoor;
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Debug method to list all potential door triggers in the scene
        /// </summary>
        public static void ListAllPotentialDoorTriggers()
        {
            ModLogger.Info("=== LISTING ALL POTENTIAL DOOR TRIGGERS ===");
            
            var allObjects = GameObject.FindObjectsOfType<GameObject>();
            int foundCount = 0;
            
            foreach (var go in allObjects)
            {
                var collider = go.GetComponent<Collider>();
                if (collider != null && collider.isTrigger)
                {
                    var hasHandler = go.GetComponent<DoorTriggerHandler>() != null;
                    string status = hasHandler ? "✓ HAS HANDLER" : "❌ NEEDS HANDLER";
                    
                    ModLogger.Info($"Trigger: {go.name} | Parent: {go.transform.parent?.name ?? "None"} | {status}");
                    foundCount++;
                }
            }
            
            ModLogger.Info($"Found {foundCount} trigger colliders in the scene.");
            ModLogger.Info("=== END TRIGGER LISTING ===");
        }
    }
}