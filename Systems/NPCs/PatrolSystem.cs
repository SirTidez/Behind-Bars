using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// System for managing patrol points and door triggers for guard NPCs
    /// </summary>
    public static class PatrolSystem
    {
        private static List<Transform> availablePatrolPoints = new List<Transform>();
        private static List<Transform> assignedPatrolPoints = new List<Transform>();
        private static Dictionary<Transform, Transform> guardPatrolAssignments = new Dictionary<Transform, Transform>();
        private static Dictionary<string, DoorTriggerInfo> doorTriggers = new Dictionary<string, DoorTriggerInfo>();
        
        /// <summary>
        /// Initialize the patrol system using JailController's registered patrol points
        /// </summary>
        public static void Initialize()
        {
            ModLogger.Info("Initializing Patrol System...");
            
            RegisterPatrolPointsFromJailController();
            RegisterDoorTriggers();
            
            ModLogger.Info($"✓ Patrol System initialized: {availablePatrolPoints.Count} patrol points, {doorTriggers.Count} door triggers");
        }
        
        /// <summary>
        /// Register patrol points from JailController - static and explicit
        /// </summary>
        private static void RegisterPatrolPointsFromJailController()
        {
            availablePatrolPoints.Clear();
            assignedPatrolPoints.Clear();
            
            // Find the JailController in the scene
            var jailController = UnityEngine.Object.FindObjectOfType<JailController>();
            if (jailController == null)
            {
                ModLogger.Warn("No JailController found - cannot register patrol points");
                return;
            }
            
            // Use the JailController's registered patrol points
            foreach (var patrolPoint in jailController.patrolPoints)
            {
                if (patrolPoint != null)
                {
                    availablePatrolPoints.Add(patrolPoint);
                    ModLogger.Debug($"Registered patrol point from JailController: {patrolPoint.name} at {patrolPoint.position}");
                }
            }
            
            ModLogger.Info($"Registered {availablePatrolPoints.Count} patrol points from JailController");
        }
        
        /// <summary>
        /// Find and register all door triggers, linking them to their corresponding doors
        /// </summary>
        private static void RegisterDoorTriggers()
        {
            doorTriggers.Clear();
            
            // Find door triggers by name pattern
            var allObjects = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var obj in allObjects)
            {
                if (obj.name.Contains("DoorTrigger"))
                {
                    var doorInfo = CreateDoorTriggerInfo(obj);
                    if (doorInfo != null)
                    {
                        doorTriggers[obj.name] = doorInfo;
                        ModLogger.Debug($"Registered door trigger: {obj.name} -> Door: {doorInfo.doorName}");
                    }
                }
            }
            
            ModLogger.Info($"Found {doorTriggers.Count} door triggers in scene");
        }
        
        /// <summary>
        /// Create door trigger info by finding the corresponding door
        /// </summary>
        private static DoorTriggerInfo CreateDoorTriggerInfo(Transform trigger)
        {
            string triggerName = trigger.name;
            string expectedDoorName = "";
            
            // Map trigger names to door names based on your naming convention
            if (triggerName.Contains("BookingDoorTrigger"))
            {
                expectedDoorName = "BookingDoor"; // Adjust based on actual door names
            }
            else if (triggerName.Contains("PrisonDoorTrigger"))
            {
                expectedDoorName = "PrisonDoor"; // Adjust based on actual door names  
            }
            else if (triggerName.Contains("GuardRoomDoorTrigger"))
            {
                expectedDoorName = "GuardRoomDoor"; // Adjust based on actual door names
            }
            
            if (string.IsNullOrEmpty(expectedDoorName))
            {
                ModLogger.Warn($"Could not determine door name for trigger: {triggerName}");
                return null;
            }
            
            // Find the door in the Booking area or nearby
            Transform door = FindDoorByName(expectedDoorName);
            if (door == null)
            {
                ModLogger.Warn($"Could not find door '{expectedDoorName}' for trigger: {triggerName}");
                return null;
            }
            
            return new DoorTriggerInfo
            {
                trigger = trigger,
                door = door,
                doorName = expectedDoorName,
                isOpen = false
            };
        }
        
        /// <summary>
        /// Find a door by name in the scene, searching in likely locations
        /// </summary>
        private static Transform FindDoorByName(string doorName)
        {
            // Search in the entire scene first
            var allObjects = UnityEngine.Object.FindObjectsOfType<Transform>();
            
            // Try exact match first
            var exactMatch = allObjects.FirstOrDefault(obj => obj.name.Equals(doorName));
            if (exactMatch != null) return exactMatch;
            
            // Try partial match
            var partialMatch = allObjects.FirstOrDefault(obj => obj.name.Contains(doorName.Replace("Door", "")));
            if (partialMatch != null) return partialMatch;
            
            // Search in Booking area specifically
            var bookingArea = allObjects.FirstOrDefault(obj => obj.name.Contains("Booking"));
            if (bookingArea != null)
            {
                var doorInBooking = bookingArea.GetComponentsInChildren<Transform>()
                    .FirstOrDefault(child => child.name.Contains(doorName.Replace("Door", "")));
                if (doorInBooking != null) return doorInBooking;
            }
            
            return null;
        }
        
        /// <summary>
        /// Assign a unique patrol point to a guard
        /// </summary>
        public static Transform AssignPatrolPointToGuard(Transform guard)
        {
            if (availablePatrolPoints.Count == 0)
            {
                ModLogger.Warn($"No available patrol points for guard: {guard.name}");
                return null;
            }
            
            // Check if guard already has assignment
            if (guardPatrolAssignments.ContainsKey(guard))
            {
                ModLogger.Debug($"Guard {guard.name} already has patrol point: {guardPatrolAssignments[guard].name}");
                return guardPatrolAssignments[guard];
            }
            
            // Find closest available patrol point
            Transform closestPoint = null;
            float closestDistance = float.MaxValue;
            
            foreach (var point in availablePatrolPoints)
            {
                if (!assignedPatrolPoints.Contains(point))
                {
                    float distance = Vector3.Distance(guard.position, point.position);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closestPoint = point;
                    }
                }
            }
            
            if (closestPoint != null)
            {
                // Assign the patrol point
                guardPatrolAssignments[guard] = closestPoint;
                assignedPatrolPoints.Add(closestPoint);
                availablePatrolPoints.Remove(closestPoint);
                
                ModLogger.Info($"✓ Assigned patrol point '{closestPoint.name}' to guard '{guard.name}' (distance: {closestDistance:F1})");
                return closestPoint;
            }
            
            ModLogger.Warn($"No available patrol points remaining for guard: {guard.name}");
            return null;
        }
        
        /// <summary>
        /// Release a patrol point assignment when a guard is destroyed/removed
        /// </summary>
        public static void ReleasePatrolPointAssignment(Transform guard)
        {
            if (guardPatrolAssignments.TryGetValue(guard, out Transform assignedPoint))
            {
                guardPatrolAssignments.Remove(guard);
                assignedPatrolPoints.Remove(assignedPoint);
                availablePatrolPoints.Add(assignedPoint);
                
                ModLogger.Info($"✓ Released patrol point '{assignedPoint.name}' from guard '{guard.name}'");
            }
        }
        
        /// <summary>
        /// Get all door triggers in the scene
        /// </summary>
        public static Dictionary<string, DoorTriggerInfo> GetDoorTriggers()
        {
            return new Dictionary<string, DoorTriggerInfo>(doorTriggers);
        }
        
        /// <summary>
        /// Get door trigger info by trigger name
        /// </summary>
        public static DoorTriggerInfo GetDoorTrigger(string triggerName)
        {
            doorTriggers.TryGetValue(triggerName, out DoorTriggerInfo info);
            return info;
        }
        
        /// <summary>
        /// Open a door via its trigger
        /// </summary>
        public static void OpenDoor(string triggerName, Transform npc)
        {
            if (doorTriggers.TryGetValue(triggerName, out DoorTriggerInfo doorInfo))
            {
                if (!doorInfo.isOpen)
                {
                    doorInfo.isOpen = true;
                    ModLogger.Info($"✓ {npc.name} opened door: {doorInfo.doorName}");
                    
                    // TODO: Add actual door opening animation/logic here
                    // For now, we'll just log the action
                }
            }
        }
        
        /// <summary>
        /// Close a door via its trigger
        /// </summary>
        public static void CloseDoor(string triggerName, Transform npc)
        {
            if (doorTriggers.TryGetValue(triggerName, out DoorTriggerInfo doorInfo))
            {
                if (doorInfo.isOpen)
                {
                    doorInfo.isOpen = false;
                    ModLogger.Info($"✓ {npc.name} closed door: {doorInfo.doorName}");
                    
                    // TODO: Add actual door closing animation/logic here
                    // For now, we'll just log the action
                }
            }
        }
        
        /// <summary>
        /// Get assigned patrol points for debugging
        /// </summary>
        public static List<Transform> GetAssignedPatrolPoints()
        {
            return new List<Transform>(assignedPatrolPoints);
        }
        
        /// <summary>
        /// Get available patrol points for debugging
        /// </summary>
        public static List<Transform> GetAvailablePatrolPoints()
        {
            return new List<Transform>(availablePatrolPoints);
        }
    }
    
    /// <summary>
    /// Information about a door trigger and its associated door
    /// </summary>
    public class DoorTriggerInfo
    {
        public Transform trigger;
        public Transform door;
        public string doorName;
        public bool isOpen;
        
        public Vector3 TriggerPosition => trigger != null ? trigger.position : Vector3.zero;
        public Vector3 DoorPosition => door != null ? door.position : Vector3.zero;
    }
}