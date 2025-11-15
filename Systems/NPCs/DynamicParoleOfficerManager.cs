using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Manages dynamic spawning/despawning of parole officers based on player location and parole status.
    /// Uses event-driven architecture for responsive player movement tracking.
    /// </summary>
    public class DynamicParoleOfficerManager : MonoBehaviour
    {
#if !MONO
        public DynamicParoleOfficerManager(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Singleton

        public static DynamicParoleOfficerManager Instance { get; private set; }

        #endregion

        #region Configuration

        /// <summary>
        /// Distance threshold in meters for spawning patrol officers (200m)
        /// </summary>
        private const float SPAWN_DISTANCE_THRESHOLD = 200f;

        /// <summary>
        /// Distance threshold in meters for despawning patrol officers (250m - hysteresis)
        /// </summary>
        private const float DESPAWN_DISTANCE_THRESHOLD = 250f;

        /// <summary>
        /// Update interval in seconds for spawning checks
        /// </summary>
        private const float UPDATE_INTERVAL = 5f;

        #endregion

        #region State Tracking

        /// <summary>
        /// Dictionary of active officers by assignment
        /// </summary>
        private Dictionary<ParoleOfficerAssignment, ParoleOfficerBehavior> activeOfficers;

        /// <summary>
        /// Set of assignments that are currently spawned
        /// </summary>
        private HashSet<ParoleOfficerAssignment> spawnedAssignments;

        /// <summary>
        /// Current tracked player
        /// </summary>
        private Player currentPlayer;

        /// <summary>
        /// Whether player is currently on parole
        /// </summary>
        private bool isPlayerOnParole;

        /// <summary>
        /// Current player region
        /// </summary>
        private EMapRegion currentPlayerRegion;

        /// <summary>
        /// Last update time for spawning checks
        /// </summary>
        private float lastUpdateTime;

        /// <summary>
        /// Initialization flag
        /// </summary>
        private bool isInitialized = false;

        #endregion

        #region References

        /// <summary>
        /// Reference to location tracker
        /// </summary>
        private PlayerLocationTracker locationTracker;

        /// <summary>
        /// Reference to parole system
        /// </summary>
        private ParoleSystem paroleSystem;

        /// <summary>
        /// Reference to NPC manager for spawning
        /// </summary>
        private PrisonNPCManager npcManager;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Ensure singleton behavior
            if (Instance != null && Instance != this)
            {
                ModLogger.Warn("DynamicParoleOfficerManager: Multiple instances detected, destroying duplicate");
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            Initialize();
        }

        private void Update()
        {
            if (!isInitialized) return;

            // Periodic update for spawning checks
            if (Time.time - lastUpdateTime >= UPDATE_INTERVAL)
            {
                UpdateOfficerSpawning();
                lastUpdateTime = Time.time;
            }
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the dynamic parole officer manager
        /// </summary>
        public void Initialize()
        {
            try
            {
                ModLogger.Debug("DynamicParoleOfficerManager: Initializing...");

                // Initialize state
                activeOfficers = new Dictionary<ParoleOfficerAssignment, ParoleOfficerBehavior>();
                spawnedAssignments = new HashSet<ParoleOfficerAssignment>();
                lastUpdateTime = Time.time;

                // Get references
                npcManager = PrisonNPCManager.Instance;
                if (npcManager == null)
                {
                    ModLogger.Error("DynamicParoleOfficerManager: PrisonNPCManager not found");
                    StartCoroutine(RetryInitialize());
                    return;
                }

                // Initialize route region mapper
                RouteRegionMapper.Initialize();

                // Get or create location tracker
                locationTracker = PlayerLocationTracker.Instance;
                if (locationTracker == null)
                {
                    GameObject trackerObject = new GameObject("PlayerLocationTracker");
                    locationTracker = trackerObject.AddComponent<PlayerLocationTracker>();
                    locationTracker.Initialize();
                }

                // Get parole system
                paroleSystem = Core.Instance?.GetParoleSystem();
                if (paroleSystem == null)
                {
                    ModLogger.Warn("DynamicParoleOfficerManager: ParoleSystem not found, will retry");
                }

                // Get local player
                currentPlayer = GetLocalPlayer();
                if (currentPlayer == null)
                {
                    ModLogger.Debug("DynamicParoleOfficerManager: Local player not found, will retry");
                    StartCoroutine(RetryInitialize());
                    return;
                }

                // Subscribe to events
                SubscribeToEvents();

                // Check initial parole status
                CheckParoleStatus();

                // Get initial region
                if (locationTracker != null)
                {
                    currentPlayerRegion = locationTracker.GetCurrentRegion();
                }

                isInitialized = true;
                ModLogger.Debug("DynamicParoleOfficerManager: Initialized successfully");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error during initialization: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Retry initialization if dependencies not ready
        /// </summary>
        private IEnumerator RetryInitialize()
        {
            int retries = 0;
            const int maxRetries = 10;
            const float retryInterval = 2f;

            while (retries < maxRetries && (!isInitialized))
            {
                yield return new WaitForSeconds(retryInterval);
                
                // Retry getting dependencies
                if (npcManager == null)
                {
                    npcManager = PrisonNPCManager.Instance;
                }
                
                if (currentPlayer == null)
                {
                    currentPlayer = GetLocalPlayer();
                }
                
                if (paroleSystem == null)
                {
                    paroleSystem = Core.Instance?.GetParoleSystem();
                }

                if (npcManager != null && currentPlayer != null)
                {
                    Initialize();
                    yield break;
                }

                retries++;
            }

            if (!isInitialized)
            {
                ModLogger.Error("DynamicParoleOfficerManager: Failed to initialize after retries");
            }
        }

        #endregion

        #region Event Subscription

        /// <summary>
        /// Subscribe to relevant events
        /// </summary>
        private void SubscribeToEvents()
        {
            try
            {
                // Subscribe to location tracker events
                PlayerLocationTracker.OnPlayerRegionChanged += OnPlayerRegionChanged;
                PlayerLocationTracker.OnPlayerSignificantMovement += OnPlayerSignificantMovement;

                // Subscribe to parole system events
                ParoleSystem.OnParoleStarted += OnParoleStarted;
                ParoleSystem.OnParoleEnded += OnParoleEnded;

                ModLogger.Debug("DynamicParoleOfficerManager: Subscribed to events");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error subscribing to events: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribe from events
        /// </summary>
        private void UnsubscribeFromEvents()
        {
            try
            {
                PlayerLocationTracker.OnPlayerRegionChanged -= OnPlayerRegionChanged;
                PlayerLocationTracker.OnPlayerSignificantMovement -= OnPlayerSignificantMovement;
                ParoleSystem.OnParoleStarted -= OnParoleStarted;
                ParoleSystem.OnParoleEnded -= OnParoleEnded;

                ModLogger.Debug("DynamicParoleOfficerManager: Unsubscribed from events");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error unsubscribing from events: {ex.Message}");
            }
        }

        #endregion

        #region Player Access

        /// <summary>
        /// Get the local player instance
        /// </summary>
        private Player GetLocalPlayer()
        {
            try
            {
#if !MONO
                return Il2CppScheduleOne.PlayerScripts.Player.Local;
#else
                return ScheduleOne.PlayerScripts.Player.Local;
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error getting local player: {ex.Message}");
                return null;
            }
        }

        // MULTIPLAYER SUPPORT (commented out for singleplayer focus)
        /*
        /// <summary>
        /// Get all players in multiplayer scenario
        /// </summary>
        private List<Player> GetAllPlayers()
        {
            try
            {
#if !MONO
                return Il2CppScheduleOne.PlayerScripts.Player.AllPlayers;
#else
                return ScheduleOne.PlayerScripts.Player.AllPlayers;
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error getting all players: {ex.Message}");
                return new List<Player>();
            }
        }

        /// <summary>
        /// Handle multiple players on parole
        /// </summary>
        private void HandleMultiplePlayers(List<Player> playersOnParole)
        {
            // For each player on parole, spawn officers based on their location
            foreach (var player in playersOnParole)
            {
                // Spawn supervising officer if not already spawned
                // Spawn patrol officers based on each player's location
                // Merge spawn requirements (union of all needed officers)
            }
        }
        */

        #endregion

        #region Parole Status Monitoring

        /// <summary>
        /// Check current parole status
        /// </summary>
        private void CheckParoleStatus()
        {
            if (currentPlayer == null)
            {
                currentPlayer = GetLocalPlayer();
                if (currentPlayer == null) return;
            }

            try
            {
                // Check via RapSheet
                var rapSheet = RapSheetManager.Instance.GetRapSheet(currentPlayer);
                if (rapSheet != null && rapSheet.CurrentParoleRecord != null)
                {
                    bool wasOnParole = isPlayerOnParole;
                    isPlayerOnParole = rapSheet.CurrentParoleRecord.IsOnParole();

                    if (wasOnParole != isPlayerOnParole)
                    {
                        ModLogger.Debug($"DynamicParoleOfficerManager: Parole status changed to {(isPlayerOnParole ? "ON" : "OFF")} for {currentPlayer.name}");
                        
                        if (isPlayerOnParole)
                        {
                            OnParoleStarted(currentPlayer);
                        }
                        else
                        {
                            OnParoleEnded(currentPlayer);
                        }
                    }
                }
                else
                {
                    bool wasOnParole = isPlayerOnParole;
                    isPlayerOnParole = false;
                    
                    if (wasOnParole && !isPlayerOnParole)
                    {
                        OnParoleEnded(currentPlayer);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error checking parole status: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle parole started event
        /// </summary>
        private void OnParoleStarted(Player player)
        {
            if (player == null || player != currentPlayer) return;

            ModLogger.Debug($"DynamicParoleOfficerManager: Parole started for {player.name}");
            isPlayerOnParole = true;

            // Spawn supervising officer immediately
            SpawnSupervisingOfficer();

            // Update officer spawning based on current location
            UpdateOfficerSpawning();
        }

        /// <summary>
        /// Handle parole ended event
        /// </summary>
        private void OnParoleEnded(Player player)
        {
            if (player == null || player != currentPlayer) return;

            ModLogger.Debug($"DynamicParoleOfficerManager: Parole ended for {player.name}");
            isPlayerOnParole = false;

            // Despawn all officers
            DespawnAllOfficers();
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handle player region changed event
        /// </summary>
        private void OnPlayerRegionChanged(Player player, EMapRegion newRegion)
        {
            if (player == null || player != currentPlayer) return;
            if (!isPlayerOnParole) return;

            ModLogger.Debug($"DynamicParoleOfficerManager: Player region changed to {newRegion}");
            currentPlayerRegion = newRegion;

            // Update officer spawning for new region
            UpdateOfficerSpawning();
        }

        /// <summary>
        /// Handle player significant movement event
        /// </summary>
        private void OnPlayerSignificantMovement(Player player, Vector3 newPosition)
        {
            if (player == null || player != currentPlayer) return;
            if (!isPlayerOnParole) return;

            ModLogger.Debug($"DynamicParoleOfficerManager: Player moved significantly to {newPosition}");
            
            // Update officer spawning based on new position
            UpdateOfficerSpawning();
        }

        #endregion

        #region Spawning Logic

        /// <summary>
        /// Update officer spawning based on current state
        /// </summary>
        private void UpdateOfficerSpawning()
        {
            if (!isInitialized) return;
            if (currentPlayer == null)
            {
                currentPlayer = GetLocalPlayer();
                if (currentPlayer == null) return;
            }

            // Check parole status
            CheckParoleStatus();

            if (!isPlayerOnParole)
            {
                // Ensure all officers are despawned
                DespawnAllOfficers();
                return;
            }

            // Ensure supervising officer is spawned
            EnsureSupervisingOfficer();

            // Update patrol officers based on distance
            UpdatePatrolOfficers();
        }

        /// <summary>
        /// Ensure supervising officer is spawned
        /// </summary>
        private void EnsureSupervisingOfficer()
        {
            var assignment = ParoleOfficerAssignment.PoliceStationSupervisor;

            if (!spawnedAssignments.Contains(assignment))
            {
                SpawnOfficer(assignment);
            }
        }

        /// <summary>
        /// Update patrol officers based on player distance to routes
        /// </summary>
        private void UpdatePatrolOfficers()
        {
            if (currentPlayer == null) return;

            Vector3 playerPosition = currentPlayer.transform.position;

            // Get all patrol assignments
            var patrolAssignments = RouteRegionMapper.GetAllPatrolAssignments();

            foreach (var assignment in patrolAssignments)
            {
                bool isSpawned = spawnedAssignments.Contains(assignment);
                float distance = GetDistanceToRoute(assignment, playerPosition);

                if (!isSpawned && distance < SPAWN_DISTANCE_THRESHOLD)
                {
                    // Should spawn
                    ModLogger.Debug($"DynamicParoleOfficerManager: Spawning {assignment} (distance: {distance:F1}m)");
                    SpawnOfficer(assignment);
                }
                else if (isSpawned && distance > DESPAWN_DISTANCE_THRESHOLD)
                {
                    // Should despawn
                    ModLogger.Debug($"DynamicParoleOfficerManager: Despawning {assignment} (distance: {distance:F1}m)");
                    DespawnOfficer(assignment);
                }
            }
        }

        /// <summary>
        /// Spawn an officer with the given assignment
        /// </summary>
        private void SpawnOfficer(ParoleOfficerAssignment assignment)
        {
            if (spawnedAssignments.Contains(assignment))
            {
                ModLogger.Debug($"DynamicParoleOfficerManager: Officer {assignment} already spawned");
                return;
            }

            if (npcManager == null)
            {
                ModLogger.Error("DynamicParoleOfficerManager: Cannot spawn officer, NPCManager is null");
                return;
            }

            try
            {
                // Get spawn position
                Vector3 spawnPosition = GetSpawnPositionForAssignment(assignment);

                // Get officer name
                string officerName = GetOfficerNameForAssignment(assignment);

                // Generate badge number
                int badgeIndex = (int)assignment;
                string badge = $"HCPO{1000 + badgeIndex}";

                // Spawn via NPC manager
                var paroleOfficer = npcManager.SpawnParoleOfficer(spawnPosition, officerName, badge, assignment);
                
                if (paroleOfficer != null && paroleOfficer.GetComponent<ParoleOfficerBehavior>() != null)
                {
                    var behavior = paroleOfficer.GetComponent<ParoleOfficerBehavior>();
                    activeOfficers[assignment] = behavior;
                    spawnedAssignments.Add(assignment);
                    ModLogger.Debug($"DynamicParoleOfficerManager: ✓ Spawned {assignment} officer {badge}");
                }
                else
                {
                    ModLogger.Error($"DynamicParoleOfficerManager: Failed to spawn {assignment} officer");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error spawning {assignment}: {ex.Message}");
            }
        }

        /// <summary>
        /// Despawn an officer with the given assignment
        /// </summary>
        private void DespawnOfficer(ParoleOfficerAssignment assignment)
        {
            if (!spawnedAssignments.Contains(assignment))
            {
                ModLogger.Debug($"DynamicParoleOfficerManager: Officer {assignment} not spawned");
                return;
            }

            try
            {
                if (activeOfficers.TryGetValue(assignment, out var behavior) && behavior != null)
                {
                    // Destroy the GameObject
                    if (behavior.gameObject != null)
                    {
                        ModLogger.Debug($"DynamicParoleOfficerManager: Despawning {assignment} officer");
                        Destroy(behavior.gameObject);
                    }
                }

                // Clean up tracking
                activeOfficers.Remove(assignment);
                spawnedAssignments.Remove(assignment);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error despawning {assignment}: {ex.Message}");
            }
        }

        /// <summary>
        /// Despawn all officers
        /// </summary>
        private void DespawnAllOfficers()
        {
            var assignmentsToDespawn = new List<ParoleOfficerAssignment>(spawnedAssignments);
            foreach (var assignment in assignmentsToDespawn)
            {
                DespawnOfficer(assignment);
            }
        }

        /// <summary>
        /// Spawn supervising officer (always at police station)
        /// </summary>
        private void SpawnSupervisingOfficer()
        {
            SpawnOfficer(ParoleOfficerAssignment.PoliceStationSupervisor);
        }

        #endregion

        #region Distance Calculations

        /// <summary>
        /// Get distance from player position to a route
        /// </summary>
        private float GetDistanceToRoute(ParoleOfficerAssignment assignment, Vector3 position)
        {
            try
            {
                // Get route name
                string routeName = RouteRegionMapper.GetRouteName(assignment);
                if (string.IsNullOrEmpty(routeName))
                {
                    ModLogger.Debug($"DynamicParoleOfficerManager: No route found for {assignment}");
                    return float.MaxValue;
                }

                // Get route
                var route = PresetParoleOfficerRoutes.GetRoute(routeName);
                if (route == null || route.points == null || route.points.Length == 0)
                {
                    ModLogger.Debug($"DynamicParoleOfficerManager: Route {routeName} not found or empty");
                    return float.MaxValue;
                }

                // Calculate closest distance to route
                return GetClosestDistanceToRoute(route, position);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error calculating distance to route: {ex.Message}");
                return float.MaxValue;
            }
        }

        /// <summary>
        /// Get closest distance from position to route (considers waypoints and line segments)
        /// </summary>
        private float GetClosestDistanceToRoute(ParoleOfficerBehavior.PatrolRoute route, Vector3 position)
        {
            if (route.points == null || route.points.Length == 0)
            {
                return float.MaxValue;
            }

            float minDistance = float.MaxValue;

            // Check distance to each waypoint
            foreach (var waypoint in route.points)
            {
                float distance = Vector3.Distance(position, waypoint);
                minDistance = Mathf.Min(minDistance, distance);
            }

            // Check distance to line segments between waypoints
            for (int i = 0; i < route.points.Length - 1; i++)
            {
                float segmentDistance = DistanceToLineSegment(
                    route.points[i],
                    route.points[i + 1],
                    position
                );
                minDistance = Mathf.Min(minDistance, segmentDistance);
            }

            return minDistance;
        }

        /// <summary>
        /// Calculate distance from point to line segment
        /// </summary>
        private float DistanceToLineSegment(Vector3 lineStart, Vector3 lineEnd, Vector3 point)
        {
            Vector3 line = lineEnd - lineStart;
            float lineLength = line.magnitude;
            
            if (lineLength < 0.001f)
            {
                return Vector3.Distance(point, lineStart);
            }

            Vector3 lineNormalized = line / lineLength;
            Vector3 pointToStart = point - lineStart;
            float projection = Vector3.Dot(pointToStart, lineNormalized);
            
            // Clamp projection to line segment
            projection = Mathf.Clamp(projection, 0f, lineLength);
            
            Vector3 closestPoint = lineStart + lineNormalized * projection;
            return Vector3.Distance(point, closestPoint);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get spawn position for an assignment
        /// </summary>
        private Vector3 GetSpawnPositionForAssignment(ParoleOfficerAssignment assignment)
        {
            // For supervising officer: Use police station route first waypoint
            if (assignment == ParoleOfficerAssignment.PoliceStationSupervisor)
            {
                var route = PresetParoleOfficerRoutes.GetRoute("PoliceStation");
                if (route != null && route.points != null && route.points.Length > 0)
                {
                    return route.points[0];
                }
            }

            // For patrol officers: Use their route's first waypoint
            string routeName = RouteRegionMapper.GetRouteName(assignment);
            if (!string.IsNullOrEmpty(routeName))
            {
                var route = PresetParoleOfficerRoutes.GetRoute(routeName);
                if (route != null && route.points != null && route.points.Length > 0)
                {
                    return route.points[0];
                }
            }

            // Fallback
            ModLogger.Warn($"DynamicParoleOfficerManager: Using fallback spawn position for {assignment}");
            return new Vector3(27.0941f, 1.065f, 45.0492f);
        }

        /// <summary>
        /// Get officer name for assignment
        /// </summary>
        private string GetOfficerNameForAssignment(ParoleOfficerAssignment assignment)
        {
            string[] names = { "Billy", "Kelly", "Johnson", "Martinez", "Thompson", "Garcia" };
            string randomName = names[UnityEngine.Random.Range(0, names.Length)];

            switch (assignment)
            {
                case ParoleOfficerAssignment.PoliceStationSupervisor:
                    return $"Supervising Officer {randomName}";
                case ParoleOfficerAssignment.PoliceStationPatrol:
                    return $"Station Officer {randomName}";
                default:
                    return $"Parole Officer {randomName}";
            }
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Cleanup resources
        /// </summary>
        private void Cleanup()
        {
            UnsubscribeFromEvents();
            DespawnAllOfficers();

            if (Instance == this)
            {
                Instance = null;
            }

            ModLogger.Debug("DynamicParoleOfficerManager: Cleaned up");
        }

        #endregion

        #region Public API

        /// <summary>
        /// Get count of active officers
        /// </summary>
        public int GetActiveOfficerCount()
        {
            return spawnedAssignments.Count;
        }

        /// <summary>
        /// Check if an assignment is currently spawned
        /// </summary>
        public bool IsOfficerSpawned(ParoleOfficerAssignment assignment)
        {
            return spawnedAssignments.Contains(assignment);
        }

        /// <summary>
        /// Force update of officer spawning (useful for testing)
        /// </summary>
        public void ForceUpdate()
        {
            UpdateOfficerSpawning();
        }

        #endregion
    }
}

