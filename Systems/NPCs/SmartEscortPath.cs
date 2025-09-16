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
    /// Handles smart pathfinding for prisoner escorts with automatic door detection
    /// Replaces static waypoint system with dynamic navigation
    /// </summary>
    public class SmartEscortPath : MonoBehaviour
    {
#if !MONO
        public SmartEscortPath(System.IntPtr ptr) : base(ptr) { }
#endif

        // Escort Settings
        public float stationWaitTime = 3f;
        public float prisonerCheckDistance = 10f;
        public float escortSpeed = 2f;
        public bool waitForPrisoner = true;

        // References
        private JailGuardBehavior guardBehavior;
        private DynamicDoorNavigator doorNavigator;
        private UnityEngine.AI.NavMeshAgent navAgent;

        // Escort state
        private bool isEscortActive = false;
        private Player currentPrisoner;
        private EscortStation currentStation = EscortStation.None;
        private List<EscortStation> completedStations = new List<EscortStation>();
        private Vector3 lastPrisonerPosition;
        private float prisonerStationaryTime = 0f;

        // Station definitions
        private Dictionary<EscortStation, EscortStationInfo> stationInfo;

        public enum EscortStation
        {
            None,
            HoldingCell,
            MugshotStation,
            FingerprintScanner,
            StorageDropOff,
            PrisonCell
        }

        [System.Serializable]
        public class EscortStationInfo
        {
            public EscortStation station;
            public string stationName;
            public string guardPointName;
            public Vector3 guardPosition;
            public bool requiresPrisoner;
            public float waitTime;
            public System.Action<Player> stationAction;

            public EscortStationInfo(EscortStation station, string stationName, string guardPointName, 
                                   bool requiresPrisoner = true, float waitTime = 3f)
            {
                this.station = station;
                this.stationName = stationName;
                this.guardPointName = guardPointName;
                this.requiresPrisoner = requiresPrisoner;
                this.waitTime = waitTime;
            }
        }

        // Events
        public System.Action<EscortStation> OnStationReached;
        public System.Action<EscortStation> OnStationCompleted;
        public System.Action OnEscortCompleted;
        public System.Action<string> OnEscortFailed;

        void Awake()
        {
            guardBehavior = GetComponent<JailGuardBehavior>();
            doorNavigator = GetComponent<DynamicDoorNavigator>();
            navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();

            if (doorNavigator == null)
            {
                doorNavigator = gameObject.AddComponent<DynamicDoorNavigator>();
            }

            InitializeStationInfo();
        }

        void Start()
        {
            if (navAgent != null && escortSpeed > 0)
            {
                navAgent.speed = escortSpeed;
            }

            // Subscribe to door navigator events
            if (doorNavigator != null)
            {
                doorNavigator.OnDestinationReached += OnStationPositionReached;
                doorNavigator.OnDoorDetected += OnDoorDetectedDuringEscort;
                doorNavigator.OnNavigationBlocked += OnNavigationBlocked;
            }
        }

        /// <summary>
        /// Initialize information for all escort stations
        /// </summary>
        private void InitializeStationInfo()
        {
            stationInfo = new Dictionary<EscortStation, EscortStationInfo>
            {
                [EscortStation.MugshotStation] = new EscortStationInfo(
                    EscortStation.MugshotStation, "Mugshot Station", "MugshotStation", true, 5f),
                    
                [EscortStation.FingerprintScanner] = new EscortStationInfo(
                    EscortStation.FingerprintScanner, "Fingerprint Scanner", "ScannerStation", true, 4f),
                    
                [EscortStation.StorageDropOff] = new EscortStationInfo(
                    EscortStation.StorageDropOff, "Storage Drop-Off", "Storage", true, 3f)
            };

            // Update station positions from actual guard points
            UpdateStationPositions();
        }

        /// <summary>
        /// Update station positions based on current jail setup
        /// </summary>
        private void UpdateStationPositions()
        {
            var jailController = Core.ActiveJailController;
            if (jailController == null) return;

            foreach (var kvp in stationInfo)
            {
                var station = kvp.Value;
                var guardPoint = FindGuardPoint(station.guardPointName);
                if (guardPoint != null)
                {
                    station.guardPosition = guardPoint.position;
                }
                else
                {
                    ModLogger.Warn($"Could not find guard point for station: {station.stationName}");
                }
            }
        }

        /// <summary>
        /// Start smart escort for a prisoner through all stations
        /// </summary>
        public void StartSmartEscort(Player prisoner)
        {
            if (isEscortActive)
            {
                ModLogger.Warn($"Smart escort already active for guard {guardBehavior?.badgeNumber}");
                return;
            }

            currentPrisoner = prisoner;
            isEscortActive = true;
            currentStation = EscortStation.None;
            completedStations.Clear();

            ModLogger.Info($"Starting smart escort for prisoner: {prisoner?.name}");

            // Start escort coroutine
            MelonCoroutines.Start(ExecuteSmartEscort());
        }

        /// <summary>
        /// Stop current escort
        /// </summary>
        public void StopEscort(bool completed = false)
        {
            if (!isEscortActive) return;

            isEscortActive = false;
            
            if (doorNavigator != null)
            {
                doorNavigator.StopDynamicNavigation();
            }

            ModLogger.Info($"Smart escort stopped. Completed: {completed}");

            if (completed)
            {
                OnEscortCompleted?.Invoke();
            }
        }

        /// <summary>
        /// Main escort execution coroutine
        /// </summary>
        private IEnumerator ExecuteSmartEscort()
        {
            // Define escort sequence (prisoner starts at holding cell, ends at prison cell via different logic)
            var escortSequence = new List<EscortStation>
            {
                EscortStation.MugshotStation,
                EscortStation.FingerprintScanner,
                EscortStation.StorageDropOff
            };

            foreach (var station in escortSequence)
            {
                if (!isEscortActive) yield break;

                yield return ExecuteStationEscort(station);
            }

            // Escort completed
            ModLogger.Info("Smart escort completed successfully");
            StopEscort(true);
        }

        /// <summary>
        /// Execute escort to a specific station
        /// </summary>
        private IEnumerator ExecuteStationEscort(EscortStation station)
        {
            if (!stationInfo.ContainsKey(station))
            {
                ModLogger.Error($"Unknown station: {station}");
                yield break;
            }

            var info = stationInfo[station];
            currentStation = station;

            ModLogger.Info($"Escorting to station: {info.stationName}");

            // Navigate to station using dynamic door navigator
            if (doorNavigator != null && info.guardPosition != Vector3.zero)
            {
                doorNavigator.NavigateToDestination(info.guardPosition);

                // Wait for navigation to complete
                yield return new WaitUntil(() => !doorNavigator.IsNavigating() || !isEscortActive);

                if (!isEscortActive) yield break;

                OnStationReached?.Invoke(station);

                // Wait at station
                if (info.requiresPrisoner)
                {
                    yield return WaitForPrisonerAtStation(info);
                }
                else
                {
                    yield return new WaitForSeconds(info.waitTime);
                }

                // Execute station-specific actions
                if (info.stationAction != null && currentPrisoner != null)
                {
                    info.stationAction.Invoke(currentPrisoner);
                }

                completedStations.Add(station);
                OnStationCompleted?.Invoke(station);

                ModLogger.Info($"Completed station: {info.stationName}");
            }
            else
            {
                ModLogger.Error($"Cannot navigate to station {info.stationName} - missing navigator or position");
            }
        }

        /// <summary>
        /// Wait for prisoner to reach station and complete interaction
        /// </summary>
        private IEnumerator WaitForPrisonerAtStation(EscortStationInfo stationInfo)
        {
            if (!waitForPrisoner || currentPrisoner == null)
            {
                yield return new WaitForSeconds(stationInfo.waitTime);
                yield break;
            }

            float waitStartTime = Time.time;
            float maxWaitTime = 30f; // Max 30 seconds to wait for prisoner
            bool prisonerReachedStation = false;

            ModLogger.Info($"Waiting for prisoner at {stationInfo.stationName}");

            while (Time.time - waitStartTime < maxWaitTime && isEscortActive)
            {
                // Check if prisoner is near the station
                float distanceToPrisoner = Vector3.Distance(transform.position, currentPrisoner.transform.position);
                
                if (distanceToPrisoner <= prisonerCheckDistance)
                {
                    prisonerReachedStation = true;
                    
                    // Check if prisoner is stationary (interacting with station)
                    float prisonerMovement = Vector3.Distance(currentPrisoner.transform.position, lastPrisonerPosition);
                    
                    if (prisonerMovement < 0.5f)
                    {
                        prisonerStationaryTime += Time.deltaTime;
                        
                        // If prisoner has been stationary for a while, assume interaction is happening
                        if (prisonerStationaryTime >= stationInfo.waitTime)
                        {
                            ModLogger.Info($"Prisoner appears to be using {stationInfo.stationName}");
                            yield return new WaitForSeconds(stationInfo.waitTime);
                            break;
                        }
                    }
                    else
                    {
                        prisonerStationaryTime = 0f;
                    }
                    
                    lastPrisonerPosition = currentPrisoner.transform.position;
                }
                else
                {
                    prisonerStationaryTime = 0f;
                    
                    // If prisoner is far away, guide them
                    if (Time.time - waitStartTime > 10f) // After waiting 10 seconds
                    {
                        GuideToStation(stationInfo);
                    }
                }

                yield return new WaitForSeconds(0.5f);
            }

            if (!prisonerReachedStation)
            {
                ModLogger.Warn($"Prisoner did not reach {stationInfo.stationName} within time limit");
            }

            // Reset for next station
            prisonerStationaryTime = 0f;
        }

        /// <summary>
        /// Guide prisoner to current station
        /// </summary>
        private void GuideToStation(EscortStationInfo stationInfo)
        {
            if (guardBehavior != null)
            {
                // Use existing message system if available
                if (guardBehavior.TrySendNPCMessage($"Please proceed to the {stationInfo.stationName}."))
                {
                    ModLogger.Debug($"Guided prisoner to {stationInfo.stationName}");
                }
            }
        }

        /// <summary>
        /// Find guard point for a station
        /// </summary>
        private Transform FindGuardPoint(string pointName)
        {
            var jailController = Core.ActiveJailController;
            if (jailController == null) return null;

            // Search for guard points in jail hierarchy
            Transform[] allTransforms = jailController.GetComponentsInChildren<Transform>();
            foreach (Transform t in allTransforms)
            {
                if (t.name.Equals(pointName, StringComparison.OrdinalIgnoreCase) ||
                    t.name.Contains(pointName))
                {
                    return t;
                }
            }

            ModLogger.Warn($"Guard point not found: {pointName}");
            return null;
        }

        /// <summary>
        /// Event handler for when guard reaches station position
        /// </summary>
        private void OnStationPositionReached(Vector3 position)
        {
            ModLogger.Debug($"Guard reached position: {position}");
        }

        /// <summary>
        /// Event handler for door detection during escort
        /// </summary>
        private void OnDoorDetectedDuringEscort(JailDoor door)
        {
            ModLogger.Info($"Door detected during escort: {door.doorName}");
            
            // Additional logic for prisoner guidance through doors
            if (waitForPrisoner && currentPrisoner != null)
            {
                StartCoroutine(GuidePrisonerThroughDoor(door));
            }
        }

        /// <summary>
        /// Guide prisoner through a door
        /// </summary>
        private IEnumerator GuidePrisonerThroughDoor(JailDoor door)
        {
            float guidanceStartTime = Time.time;
            float maxGuidanceTime = 15f;

            while (Time.time - guidanceStartTime < maxGuidanceTime)
            {
                float distanceToDoor = Vector3.Distance(currentPrisoner.transform.position, door.doorHolder.position);
                
                if (distanceToDoor > 5f)
                {
                    // Prisoner is far from door, guide them
                    if (guardBehavior != null && Time.time - guidanceStartTime > 3f)
                    {
                        guardBehavior.TrySendNPCMessage("Follow me through the door.");
                    }
                }
                else if (distanceToDoor < 2f)
                {
                    // Prisoner is close, they should go through soon
                    break;
                }

                yield return new WaitForSeconds(1f);
            }
        }

        /// <summary>
        /// Handle navigation blocked events
        /// </summary>
        private void OnNavigationBlocked()
        {
            ModLogger.Warn($"Navigation blocked during escort. Attempting to resolve...");
            
            // Try to resolve by recalculating path to current station
            if (currentStation != EscortStation.None && stationInfo.ContainsKey(currentStation))
            {
                var info = stationInfo[currentStation];
                if (doorNavigator != null && info.guardPosition != Vector3.zero)
                {
                    doorNavigator.NavigateToDestination(info.guardPosition);
                }
            }
        }

        /// <summary>
        /// Get current escort progress
        /// </summary>
        public float GetEscortProgress()
        {
            if (!isEscortActive) return 0f;
            
            float totalStations = System.Enum.GetValues(typeof(EscortStation)).Length - 1; // Exclude None
            return completedStations.Count / totalStations;
        }

        /// <summary>
        /// Get current station being escorted to
        /// </summary>
        public EscortStation GetCurrentStation()
        {
            return currentStation;
        }

        /// <summary>
        /// Check if escort is currently active
        /// </summary>
        public bool IsEscortActive()
        {
            return isEscortActive;
        }

        /// <summary>
        /// Get list of completed stations
        /// </summary>
        public List<EscortStation> GetCompletedStations()
        {
            return new List<EscortStation>(completedStations);
        }

        /// <summary>
        /// Add custom station action
        /// </summary>
        public void SetStationAction(EscortStation station, System.Action<Player> action)
        {
            if (stationInfo.ContainsKey(station))
            {
                stationInfo[station].stationAction = action;
            }
        }

        void OnDestroy()
        {
            if (doorNavigator != null)
            {
                doorNavigator.OnDestinationReached -= OnStationPositionReached;
                doorNavigator.OnDoorDetected -= OnDoorDetectedDuringEscort;
                doorNavigator.OnNavigationBlocked -= OnNavigationBlocked;
            }
        }

        // Debug visualization
        void OnDrawGizmos()
        {
            if (!isEscortActive || stationInfo == null) return;

            // Draw stations
            foreach (var kvp in stationInfo)
            {
                var info = kvp.Value;
                if (info.guardPosition != Vector3.zero)
                {
                    Color stationColor = completedStations.Contains(kvp.Key) ? Color.green : 
                                       (kvp.Key == currentStation) ? Color.yellow : Color.white;
                    
                    Gizmos.color = stationColor;
                    Gizmos.DrawWireCube(info.guardPosition, Vector3.one * 0.5f);
                    
                    // Draw connection to current station
                    if (kvp.Key == currentStation)
                    {
                        Gizmos.color = Color.cyan;
                        Gizmos.DrawLine(transform.position, info.guardPosition);
                    }
                }
            }

            // Draw prisoner connection
            if (currentPrisoner != null)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(transform.position, currentPrisoner.transform.position);
            }
        }
    }
}