using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Manages the state of doors during guard operations and escort missions
    /// Ensures proper door closure and security during prisoner transfers
    /// </summary>
    public class DoorStateManager : MonoBehaviour
    {
#if !MONO
        public DoorStateManager(System.IntPtr ptr) : base(ptr) { }
#endif

        // Door State Settings
        public float autoCloseDelay = 3f;
        public bool trackAllDoors = true;
        public bool ensureSecurityDoors = true;

        // Door tracking
        private Dictionary<JailDoor, DoorState> doorStates = new Dictionary<JailDoor, DoorState>();
        private List<JailDoor> doorsToSecure = new List<JailDoor>();
        private Dictionary<JailDoor, Coroutine> autoCloseCoroutines = new Dictionary<JailDoor, Coroutine>();

        // References
        private JailGuardBehavior guardBehavior;

        [System.Serializable]
        public class DoorState
        {
            public JailDoor door;
            public bool wasOpenedByGuard;
            public bool needsSecuring;
            public float openTime;
            public Vector3 guardPositionWhenOpened;
            public bool prisonerPassedThrough;
            public int accessCount;

            public DoorState(JailDoor door)
            {
                this.door = door;
                this.wasOpenedByGuard = false;
                this.needsSecuring = false;
                this.openTime = 0f;
                this.guardPositionWhenOpened = Vector3.zero;
                this.prisonerPassedThrough = false;
                this.accessCount = 0;
            }
        }

        // Events
        public System.Action<JailDoor> OnDoorOpened;
        public System.Action<JailDoor> OnDoorSecured;
        public System.Action<JailDoor> OnDoorSecurityBreach;

        void Awake()
        {
            guardBehavior = GetComponent<JailGuardBehavior>();
        }

        void Start()
        {
            // Subscribe to door events from other components
            var dynamicNavigator = GetComponent<DynamicDoorNavigator>();
            if (dynamicNavigator != null)
            {
                dynamicNavigator.OnDoorDetected += OnDoorDetected;
                dynamicNavigator.OnDoorPassed += OnDoorPassed;
            }

            ModLogger.Info($"DoorStateManager initialized for guard {guardBehavior?.badgeNumber}");
        }

        /// <summary>
        /// Register a door that the guard is about to interact with
        /// </summary>
        public void RegisterDoorInteraction(JailDoor door, bool willOpen = true)
        {
            if (door == null) return;

            if (!doorStates.ContainsKey(door))
            {
                doorStates[door] = new DoorState(door);
            }

            var state = doorStates[door];
            if (willOpen)
            {
                state.wasOpenedByGuard = true;
                state.openTime = Time.time;
                state.guardPositionWhenOpened = transform.position;
                state.accessCount++;

                // Determine if this door needs securing based on type
                state.needsSecuring = DetermineIfDoorNeedsSecuring(door);

                if (state.needsSecuring && !doorsToSecure.Contains(door))
                {
                    doorsToSecure.Add(door);
                }

                OnDoorOpened?.Invoke(door);
                ModLogger.Debug($"Door {door.doorName} registered as opened by guard {guardBehavior?.badgeNumber}");
            }
        }

        /// <summary>
        /// Mark that a prisoner has passed through a door
        /// </summary>
        public void RegisterPrisonerPassage(JailDoor door)
        {
            if (door == null || !doorStates.ContainsKey(door)) return;

            var state = doorStates[door];
            state.prisonerPassedThrough = true;

            // If this is a security door and prisoner has passed, it definitely needs securing
            if (IsSecurityDoor(door))
            {
                state.needsSecuring = true;
                if (!doorsToSecure.Contains(door))
                {
                    doorsToSecure.Add(door);
                }
            }

            ModLogger.Debug($"Prisoner passage registered for door {door.doorName}");
        }

        /// <summary>
        /// Event handler for when door is detected by navigator
        /// </summary>
        private void OnDoorDetected(JailDoor door)
        {
            RegisterDoorInteraction(door, true);
        }

        /// <summary>
        /// Event handler for when guard has passed through door
        /// </summary>
        private void OnDoorPassed(JailDoor door)
        {
            if (door == null) return;

            // Start auto-close timer for this door
            StartAutoCloseTimer(door);

            // Check if this door should be secured immediately
            if (doorStates.ContainsKey(door))
            {
                var state = doorStates[door];
                if (state.needsSecuring && IsGuardPastDoor(door))
                {
                    // Schedule door securing after delay
                    MelonCoroutines.Start(SecureDoorAfterDelay(door, autoCloseDelay));
                }
            }
        }

        /// <summary>
        /// Secure all doors that were opened during the escort
        /// </summary>
        public void SecureAllOpenDoors()
        {
            foreach (var door in doorsToSecure.ToArray())
            {
                SecureDoor(door);
            }
        }

        /// <summary>
        /// Secure a specific door
        /// </summary>
        public void SecureDoor(JailDoor door)
        {
            if (door == null) return;

            try
            {
                // Close door if open
                if (!door.IsClosed())
                {
                    door.CloseDoor();
                    ModLogger.Debug($"Closed door {door.doorName}");
                }

                // Lock door if it should be locked
                if (ShouldLockDoor(door) && !door.IsLocked())
                {
                    door.LockDoor();
                    ModLogger.Debug($"Locked door {door.doorName}");
                }

                // Remove from doors to secure list
                doorsToSecure.Remove(door);

                // Cancel auto-close coroutine if running
                if (autoCloseCoroutines.ContainsKey(door))
                {
                    if (autoCloseCoroutines[door] != null)
                    {
                        MelonCoroutines.Stop(autoCloseCoroutines[door]);
                    }
                    autoCloseCoroutines.Remove(door);
                }

                OnDoorSecured?.Invoke(door);
                ModLogger.Info($"Door {door.doorName} secured by guard {guardBehavior?.badgeNumber}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error securing door {door.doorName}: {e.Message}");
            }
        }

        /// <summary>
        /// Start auto-close timer for a door
        /// </summary>
        private void StartAutoCloseTimer(JailDoor door)
        {
            if (door == null) return;

            // Cancel existing timer
            if (autoCloseCoroutines.ContainsKey(door) && autoCloseCoroutines[door] != null)
            {
                MelonCoroutines.Stop(autoCloseCoroutines[door]);
            }

            // Start new timer
            var coroutine = MelonCoroutines.Start(AutoCloseDoor(door));
            autoCloseCoroutines[door] = (Coroutine)coroutine;
        }

        /// <summary>
        /// Auto-close door after delay
        /// </summary>
        private System.Collections.IEnumerator AutoCloseDoor(JailDoor door)
        {
            yield return new WaitForSeconds(autoCloseDelay);

            if (door != null && !door.IsClosed())
            {
                door.CloseDoor();
                ModLogger.Debug($"Auto-closed door {door.doorName}");
            }

            autoCloseCoroutines.Remove(door);
        }

        /// <summary>
        /// Secure door after delay
        /// </summary>
        private System.Collections.IEnumerator SecureDoorAfterDelay(JailDoor door, float delay)
        {
            yield return new WaitForSeconds(delay);
            SecureDoor(door);
        }

        /// <summary>
        /// Determine if a door needs securing based on its type and context
        /// </summary>
        private bool DetermineIfDoorNeedsSecuring(JailDoor door)
        {
            if (door == null) return false;

            // Security doors always need securing
            if (IsSecurityDoor(door)) return true;

            // Cell doors need securing when prisoners are involved
            if (door.doorType == JailDoor.DoorType.CellDoor || 
                door.doorType == JailDoor.DoorType.HoldingCellDoor)
            {
                return true;
            }

            // Entry and guard doors need securing during escorts
            if (door.doorType == JailDoor.DoorType.EntryDoor ||
                door.doorType == JailDoor.DoorType.GuardDoor)
            {
                return guardBehavior != null && guardBehavior.GetComponent<SmartEscortPath>()?.IsEscortActive() == true;
            }

            return false;
        }

        /// <summary>
        /// Check if a door is a security door
        /// </summary>
        private bool IsSecurityDoor(JailDoor door)
        {
            if (door == null) return false;

            return door.doorType == JailDoor.DoorType.CellDoor ||
                   door.doorType == JailDoor.DoorType.HoldingCellDoor ||
                   door.doorType == JailDoor.DoorType.EntryDoor ||
                   (door.doorType == JailDoor.DoorType.GuardDoor && 
                    door.doorName != null && door.doorName.Contains("Prison"));
        }

        /// <summary>
        /// Check if door should be locked after closure
        /// </summary>
        private bool ShouldLockDoor(JailDoor door)
        {
            if (door == null) return false;

            // Security doors should generally be locked
            return IsSecurityDoor(door);
        }

        /// <summary>
        /// Check if guard is past a door (used to determine when to secure)
        /// </summary>
        private bool IsGuardPastDoor(JailDoor door)
        {
            if (door?.doorHolder == null) return false;

            float distanceToDoor = Vector3.Distance(transform.position, door.doorHolder.position);
            return distanceToDoor > 3f; // Guard is far enough from door
        }

        /// <summary>
        /// Get current door states for debugging
        /// </summary>
        public Dictionary<JailDoor, DoorState> GetDoorStates()
        {
            return new Dictionary<JailDoor, DoorState>(doorStates);
        }

        /// <summary>
        /// Get list of doors that still need securing
        /// </summary>
        public List<JailDoor> GetDoorsToSecure()
        {
            return new List<JailDoor>(doorsToSecure);
        }

        /// <summary>
        /// Check for potential security breaches (doors left open too long)
        /// </summary>
        public void CheckForSecurityBreaches()
        {
            float currentTime = Time.time;
            float maxOpenTime = 30f; // 30 seconds max for security doors

            foreach (var kvp in doorStates)
            {
                var door = kvp.Key;
                var state = kvp.Value;

                if (IsSecurityDoor(door) && state.wasOpenedByGuard && 
                    !door.IsClosed() && (currentTime - state.openTime) > maxOpenTime)
                {
                    ModLogger.Warn($"Security breach detected: Door {door.doorName} has been open for {currentTime - state.openTime:F1} seconds");
                    OnDoorSecurityBreach?.Invoke(door);

                    // Auto-secure the door
                    SecureDoor(door);
                }
            }
        }

        /// <summary>
        /// Reset door state manager (call when escort ends)
        /// </summary>
        public void Reset()
        {
            // Secure all remaining doors
            SecureAllOpenDoors();

            // Clear state tracking
            doorStates.Clear();
            doorsToSecure.Clear();

            // Stop all auto-close coroutines
            foreach (var coroutine in autoCloseCoroutines.Values)
            {
                if (coroutine != null)
                {
                    MelonCoroutines.Stop(coroutine);
                }
            }
            autoCloseCoroutines.Clear();

            ModLogger.Debug($"DoorStateManager reset for guard {guardBehavior?.badgeNumber}");
        }

        void Update()
        {
            // Periodically check for security breaches
            if (Time.frameCount % 300 == 0) // Check every 5 seconds (at 60fps)
            {
                CheckForSecurityBreaches();
            }
        }

        void OnDestroy()
        {
            Reset();
        }

        // Debug visualization
        void OnDrawGizmos()
        {
            if (doorStates == null || doorStates.Count == 0) return;

            foreach (var kvp in doorStates)
            {
                var door = kvp.Key;
                var state = kvp.Value;

                if (door?.doorHolder == null) continue;

                // Color coding for door states
                Color gizmoColor = Color.white;
                if (state.needsSecuring)
                {
                    gizmoColor = door.IsClosed() ? Color.yellow : Color.red;
                }
                else if (state.wasOpenedByGuard)
                {
                    gizmoColor = Color.green;
                }

                Gizmos.color = gizmoColor;
                Gizmos.DrawWireCube(door.doorHolder.position + Vector3.up * 2f, Vector3.one * 0.3f);

                // Draw connection to guard
                if (state.wasOpenedByGuard)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawLine(transform.position, door.doorHolder.position);
                }
            }
        }
    }
}