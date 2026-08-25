using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppInterop.Runtime.Attributes;
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
        /// Private coordinator for supervising-officer interaction ownership.
        /// Tracks intake and check-in sessions without exposing a new global singleton.
        /// </summary>
        private readonly SupervisingOfficerInteractionCoordinator supervisingOfficerInteractionCoordinator = new SupervisingOfficerInteractionCoordinator();

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

        /// <summary>
        /// Guards against overlapping initialization attempts from Start, direct manager bootstrap,
        /// and retry paths.
        /// </summary>
        private bool isInitializing = false;

        // A release begins before the parole record becomes active. Retain the
        // supervising officer only for that narrow bridge so they can walk to the
        // police-station release point instead of appearing after the summary closes.
        private Player preparedReleaseParolee;
        private Vector3 preparedReleaseMeetingPoint;
        private Coroutine preparedReleaseMeetingCoroutine;

        #endregion

        #region References

        /// <summary>
        /// Reference to location tracker
        /// </summary>
        private PlayerLocationTracker locationTracker;

        /// <summary>
        /// Reference to parole system
        /// </summary>
        /// <summary>
        /// Reference to NPC manager for spawning
        /// </summary>
        private NpcManager npcManager;

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

            supervisingOfficerInteractionCoordinator.Poll();

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
            if (Instance == this)
            {
                Instance = null;
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the dynamic parole officer manager
        /// </summary>
        public void Initialize()
        {
            if (isInitialized || isInitializing)
            {
                return;
            }

            isInitializing = true;

            try
            {
                ModLogger.Debug("DynamicParoleOfficerManager: Initializing...");

                // Initialize state
                activeOfficers = new Dictionary<ParoleOfficerAssignment, ParoleOfficerBehavior>();
                spawnedAssignments = new HashSet<ParoleOfficerAssignment>();
                lastUpdateTime = Time.time;

                // Get references
                npcManager = Core.Instance?.NpcManager;
                if (npcManager == null)
                {
                    ModLogger.Error("DynamicParoleOfficerManager: NpcManager not found");
                    MelonCoroutines.Start(RetryInitialize());
                    return;
                }

                // Initialize route region mapper
                RouteRegionMapper.Initialize();

                // Get or create location tracker
                locationTracker = PlayerLocationTracker.Instance;
                if (locationTracker == null)
                {
                    GameObject trackerObject = new GameObject("PlayerLocationTracker");
                    locationTracker = BBHelpers.AddComponentSafe<PlayerLocationTracker>(trackerObject);
                    if (locationTracker != null)
                    {
                        locationTracker.Initialize();
                    }
                    else
                    {
                        ModLogger.Warn("DynamicParoleOfficerManager: PlayerLocationTracker could not be created - continuing without tracker events");
                    }
                }

                // Get parole manager
                var paroleManager = Core.Instance?.ParoleManager;
                if (paroleManager == null)
                {
                    ModLogger.Warn("DynamicParoleOfficerManager: ParoleManager not found, will retry");
                }

                // Get local player
                currentPlayer = GetLocalPlayer();
                if (currentPlayer == null)
                {
                    ModLogger.Debug("DynamicParoleOfficerManager: Local player not found, will retry");
                    MelonCoroutines.Start(RetryInitialize());
                    return;
                }

                // Subscribe to events
                SubscribeToEvents();

                // Check initial parole status without firing transition handlers.
                // Loaded saves may already be on parole; we only want to sync state here.
                CheckParoleStatus(false);

                if (isPlayerOnParole)
                {
                    paroleManager?.EnsureRuntimeParoleTrackingForLoadedPlayer(currentPlayer);
                }

                // Get initial region
                if (locationTracker != null)
                {
                    currentPlayerRegion = locationTracker.GetCurrentRegion();
                }

                isInitialized = true;
                ModLogger.Info($"DynamicParoleOfficerManager: Initialized successfully (player={currentPlayer.name}, onParole={isPlayerOnParole})");

                // Run an immediate spawn pass once initialization succeeds.
                UpdateOfficerSpawning();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error during initialization: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
            finally
            {
                isInitializing = false;
            }
        }

        /// <summary>
        /// Retry initialization if dependencies not ready
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
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
                    npcManager = Core.Instance?.NpcManager;
                }
                
                if (currentPlayer == null)
                {
                    currentPlayer = GetLocalPlayer();
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
        /// Subscribe to relevant scene-local events.
        /// Parole lifecycle notifications are forwarded through <see cref="BehindBarsSystemManager"/>
        /// so this manager does not subscribe directly to <see cref="ParoleSystem"/> statics.
        /// </summary>
        private void SubscribeToEvents()
        {
            try
            {
#if MONO
                // Subscribe to location tracker events
                PlayerLocationTracker.OnPlayerRegionChanged += OnPlayerRegionChanged;
                PlayerLocationTracker.OnPlayerSignificantMovement += OnPlayerSignificantMovement;
#endif

                ModLogger.Info("DynamicParoleOfficerManager: Subscribed to scene-local parole/location events");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error subscribing to events: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribe from directly owned scene-local events.
        /// </summary>
        private void UnsubscribeFromEvents()
        {
            try
            {
#if MONO
                PlayerLocationTracker.OnPlayerRegionChanged -= OnPlayerRegionChanged;
                PlayerLocationTracker.OnPlayerSignificantMovement -= OnPlayerSignificantMovement;
#endif

                ModLogger.Info("DynamicParoleOfficerManager: Unsubscribed from scene-local parole/location events");
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
        private void CheckParoleStatus(bool notifyTransitions = true)
        {
            if (currentPlayer == null)
            {
                currentPlayer = GetLocalPlayer();
                if (currentPlayer == null) return;
            }

            try
            {
                // Check via RapSheet
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(currentPlayer);
                if (rapSheet != null && rapSheet.CurrentParoleRecord != null)
                {
                    bool wasOnParole = isPlayerOnParole;
                    isPlayerOnParole = rapSheet.CurrentParoleRecord.IsOnParole();

                    if (isPlayerOnParole)
                    {
                        Core.Instance?.ParoleManager?.EnsureRuntimeParoleTrackingForLoadedPlayer(currentPlayer);
                    }

                    if (notifyTransitions && wasOnParole != isPlayerOnParole)
                    {
                        ModLogger.Debug($"DynamicParoleOfficerManager: Parole status changed to {(isPlayerOnParole ? "ON" : "OFF")} for {currentPlayer.name}");
                        
                        if (isPlayerOnParole)
                        {
                            HandleParoleStarted(currentPlayer);
                        }
                        else
                        {
                            HandleParoleEnded(currentPlayer);
                        }
                    }
                }
                else
                {
                    bool wasOnParole = isPlayerOnParole;
                    isPlayerOnParole = false;
                    
                    if (notifyTransitions && wasOnParole && !isPlayerOnParole)
                    {
                        HandleParoleEnded(currentPlayer);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Error checking parole status: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle a parole-start lifecycle notification forwarded through the manager graph.
        /// </summary>
        internal void HandleParoleStarted(Player player)
        {
            if (player == null || player != currentPlayer) return;

            ModLogger.Info($"DynamicParoleOfficerManager: Parole started for {player.name}");
            isPlayerOnParole = true;

            // Update officer spawning based on current location
            // This will ensure supervising officer is spawned via EnsureSupervisingOfficer()
            UpdateOfficerSpawning();

            // Queue the initial intake handoff exactly once.
            TryQueueInitialIntakeStart(player);
        }

        /// <summary>
        /// Queue the initial intake notification to allow the supervising officer to spawn.
        /// The queue is idempotent so repeated parole-start signals do not duplicate intake.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool TryQueueInitialIntakeStart(Player player)
        {
            if (player == null)
            {
                return false;
            }

            if (!supervisingOfficerInteractionCoordinator.TryQueueInitialIntake(player))
            {
                ModLogger.Debug($"DynamicParoleOfficerManager: Initial intake already queued for {player.name}");
                return false;
            }

            MelonCoroutines.Start(DelayedIntakeNotification(player));
            return true;
        }

        /// <summary>
        /// Delayed intake handoff that retries until the supervising officer can actually take the player.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator DelayedIntakeNotification(Player player)
        {
            try
            {
                const float retryIntervalSeconds = 2f;
                const float maxWaitSeconds = 30f;
                float elapsed = 0f;

                while (elapsed < maxWaitSeconds && isPlayerOnParole && player != null)
                {
                    yield return new WaitForSeconds(retryIntervalSeconds);
                    elapsed += retryIntervalSeconds;

                    var supervisingOfficer = npcManager?.GetSupervisingOfficer();
                    if (supervisingOfficer == null)
                    {
                        ModLogger.Debug($"DynamicParoleOfficerManager: Supervising officer not available for intake yet ({player.name})");
                        continue;
                    }

                    if (!TryBeginSupervisingOfficerInteraction(player, supervisingOfficer, SupervisingOfficerInteractionKind.Intake))
                    {
                        if (supervisingOfficer.IsHandlingIntakeFor(player))
                        {
                            ModLogger.Debug($"DynamicParoleOfficerManager: Intake already active for {player.name}");
                            yield break;
                        }

                        if (supervisingOfficer.IsIntakeProcessingActive())
                        {
                            ModLogger.Debug($"DynamicParoleOfficerManager: Supervising officer is busy with another intake while waiting for {player.name}");
                            continue;
                        }

                        ModLogger.Debug($"DynamicParoleOfficerManager: Intake handoff blocked for {player.name}, retrying");
                        continue;
                    }

                    supervisingOfficer.HandleParoleIntake(player);

                    if (supervisingOfficer.IsHandlingIntakeFor(player))
                    {
                        supervisingOfficerInteractionCoordinator.MarkIntakeStarted(player, supervisingOfficer);
                        ModLogger.Debug($"DynamicParoleOfficerManager: Triggered intake for {player.name}");
                        yield break;
                    }

                    ModLogger.Debug($"DynamicParoleOfficerManager: Intake handoff not yet accepted for {player.name}, retrying");
                }

                if (player != null && isPlayerOnParole)
                {
                    ModLogger.Warn($"DynamicParoleOfficerManager: Timed out waiting to hand off intake for {player.name}");
                }
            }
            finally
            {
                if (player != null)
                {
                    supervisingOfficerInteractionCoordinator.ClearPendingIntake(player);
                }
            }
        }

        /// <summary>
        /// Handle a parole-end lifecycle notification forwarded through the manager graph.
        /// </summary>
        internal void HandleParoleEnded(Player player)
        {
            if (player == null || player != currentPlayer) return;

            ModLogger.Info($"DynamicParoleOfficerManager: Parole ended for {player.name}");
            isPlayerOnParole = false;
            CancelPreparedSupervisingOfficerForRelease(player);
            supervisingOfficerInteractionCoordinator.ClearPlayer(player);

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
                if (HasPreparedReleaseMeeting())
                {
                    EnsureSupervisingOfficer();
                    return;
                }

                // Ensure all officers are despawned when there is no active or pending
                // parole supervision.
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

            if (activeOfficers.TryGetValue(assignment, out var trackedOfficer) && trackedOfficer != null)
            {
                spawnedAssignments.Add(assignment);
                DeduplicateSupervisingOfficers(trackedOfficer);
                return;
            }

            var existingSupervisor = FindExistingSupervisingOfficer();
            if (existingSupervisor != null)
            {
                activeOfficers[assignment] = existingSupervisor;
                spawnedAssignments.Add(assignment);
                return;
            }

            if (!spawnedAssignments.Contains(assignment))
            {
                SpawnOfficer(assignment);
            }
        }

        /// <summary>
        /// Starts the supervising officer's walk to a pending release point before parole
        /// becomes active. The canonical intake state machine remains responsible for the
        /// greeting and escort once the release summary is dismissed.
        /// </summary>
        internal void PrepareSupervisingOfficerForRelease(Player player, Vector3 meetingPoint)
        {
            if (!isInitialized || player == null)
            {
                ModLogger.Warn("DynamicParoleOfficerManager: Cannot pre-position supervising officer before initialization");
                return;
            }

            preparedReleaseParolee = player;
            preparedReleaseMeetingPoint = meetingPoint;
            EnsureSupervisingOfficer();

            if (preparedReleaseMeetingCoroutine == null)
            {
                preparedReleaseMeetingCoroutine = MelonCoroutines.Start(PrepareSupervisingOfficerForReleaseCoroutine()) as Coroutine;
            }

            ModLogger.Info($"DynamicParoleOfficerManager: Preparing supervising officer to meet {player.name} at release point {meetingPoint}");
        }

        internal void CancelPreparedSupervisingOfficerForRelease(Player player)
        {
            if (preparedReleaseParolee == null || (player != null && preparedReleaseParolee != player))
            {
                return;
            }

            if (preparedReleaseMeetingCoroutine != null)
            {
                MelonCoroutines.Stop(preparedReleaseMeetingCoroutine);
                preparedReleaseMeetingCoroutine = null;
            }

            if (activeOfficers.TryGetValue(ParoleOfficerAssignment.PoliceStationSupervisor, out var supervisingOfficer) && supervisingOfficer != null)
            {
                BBHelpers.GetComponentSafe<ParoleIntakeStateMachine>(supervisingOfficer.gameObject)?.StopIntakeProcess();
            }

            preparedReleaseParolee = null;
            ModLogger.Info("DynamicParoleOfficerManager: Cancelled pending supervising-officer release meeting");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator PrepareSupervisingOfficerForReleaseCoroutine()
        {
            const float timeoutSeconds = 90f;
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;

            try
            {
                while (Time.realtimeSinceStartup < deadline && HasPreparedReleaseMeeting())
                {
                    EnsureSupervisingOfficer();

                    if (activeOfficers.TryGetValue(ParoleOfficerAssignment.PoliceStationSupervisor, out var supervisingOfficer) && supervisingOfficer != null)
                    {
                        var intakeStateMachine = BBHelpers.GetComponentSafe<ParoleIntakeStateMachine>(supervisingOfficer.gameObject);
                        if (intakeStateMachine != null)
                        {
                            intakeStateMachine.PrepareForReleaseMeeting(preparedReleaseParolee, preparedReleaseMeetingPoint);
                            ModLogger.Info($"DynamicParoleOfficerManager: Supervising officer is walking to the police-station release point for {preparedReleaseParolee.name}");
                            yield break;
                        }
                    }

                    yield return new WaitForSeconds(0.5f);
                }

                if (HasPreparedReleaseMeeting())
                {
                    ModLogger.Warn("DynamicParoleOfficerManager: Timed out preparing supervising officer for pending release");
                }
            }
            finally
            {
                preparedReleaseMeetingCoroutine = null;
            }
        }

        private bool HasPreparedReleaseMeeting()
        {
            return preparedReleaseParolee != null && preparedReleaseParolee.gameObject != null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private ParoleOfficerBehavior FindExistingSupervisingOfficer()
        {
            var allParoleOfficers = BBHelpers.FindObjectsOfTypeSafe<ParoleOfficerBehavior>();
            if (allParoleOfficers == null || allParoleOfficers.Length == 0)
            {
                return null;
            }

            ParoleOfficerBehavior firstSupervisor = null;

            foreach (var officer in allParoleOfficers)
            {
                if (officer == null)
                {
                    continue;
                }

                try
                {
                    if (officer.GetRole() != ParoleOfficerBehavior.ParoleOfficerRole.SupervisingOfficer)
                    {
                        continue;
                    }

                    if (firstSupervisor == null)
                    {
                        firstSupervisor = officer;
                    }
                }
                catch
                {
                    // Ignore partially initialized officers.
                }
            }

            if (firstSupervisor != null)
            {
                DeduplicateSupervisingOfficers(firstSupervisor);
            }

            return firstSupervisor;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void DeduplicateSupervisingOfficers(ParoleOfficerBehavior keeper)
        {
            if (keeper == null)
            {
                return;
            }

            var allParoleOfficers = BBHelpers.FindObjectsOfTypeSafe<ParoleOfficerBehavior>();
            if (allParoleOfficers == null || allParoleOfficers.Length == 0)
            {
                return;
            }

            int removedCount = 0;

            foreach (var officer in allParoleOfficers)
            {
                if (officer == null || officer == keeper)
                {
                    continue;
                }

                try
                {
                    if (officer.GetRole() != ParoleOfficerBehavior.ParoleOfficerRole.SupervisingOfficer)
                    {
                        continue;
                    }

                    if (officer.gameObject != null)
                    {
                        Destroy(officer.gameObject);
                        removedCount++;
                    }
                }
                catch
                {
                    // Ignore transient object state while deduplicating.
                }
            }

            if (removedCount > 0)
            {
                ModLogger.Warn($"DynamicParoleOfficerManager: Removed {removedCount} duplicate supervising officer(s)");
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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

                // Spawn via the manager-owned NPC seam.
                var paroleOfficer = npcManager.SpawnParoleOfficer(spawnPosition, officerName, badge, assignment);
                
                if (paroleOfficer != null && BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(paroleOfficer.gameObject) != null)
                {
                    var behavior = BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(paroleOfficer.gameObject);
                    activeOfficers[assignment] = behavior;
                    spawnedAssignments.Add(assignment);
                    ModLogger.Info($"DynamicParoleOfficerManager: Spawned {assignment} officer {badge} at {spawnPosition}");
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SpawnSupervisingOfficer()
        {
            SpawnOfficer(ParoleOfficerAssignment.PoliceStationSupervisor);
        }

        #endregion

        #region Distance Calculations

        /// <summary>
        /// Get distance from player position to a route
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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
#if !MONO
        [HideFromIl2Cpp]
#endif
        private Vector3 GetSpawnPositionForAssignment(ParoleOfficerAssignment assignment)
        {
            // The supervising officer is permanently posted at the courthouse.
            if (assignment == ParoleOfficerAssignment.PoliceStationSupervisor)
            {
                return PresetParoleOfficerRoutes.GetSupervisingOfficerStation();
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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
            CancelPreparedSupervisingOfficerForRelease(preparedReleaseParolee);
            DespawnAllOfficers();
            isInitialized = false;
            isInitializing = false;

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
#if !MONO
        [HideFromIl2Cpp]
#endif
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

        /// <summary>
        /// Gate a supervising-officer interaction through the private coordinator.
        /// Used by intake handoff and check-in initiation to prevent duplicate controller starts.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool TryBeginSupervisingOfficerInteraction(Player parolee, ParoleOfficerBehavior officer, SupervisingOfficerInteractionKind interactionKind)
        {
            return supervisingOfficerInteractionCoordinator.TryBeginInteraction(parolee, officer, interactionKind);
        }

        /// <summary>
        /// Reserve a supervising-officer check-in session before the downstream controller accepts it.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal bool TryReserveCheckIn(Player parolee, ParoleOfficerBehavior officer)
        {
            return supervisingOfficerInteractionCoordinator.TryReserveCheckIn(parolee, officer);
        }

        /// <summary>
        /// Mark a supervising-officer interaction as actively started after the downstream controller accepts it.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void MarkSupervisingOfficerInteractionStarted(Player parolee, ParoleOfficerBehavior officer, SupervisingOfficerInteractionKind interactionKind)
        {
            switch (interactionKind)
            {
                case SupervisingOfficerInteractionKind.Intake:
                    supervisingOfficerInteractionCoordinator.MarkIntakeStarted(parolee, officer);
                    break;
                case SupervisingOfficerInteractionKind.CheckIn:
                    supervisingOfficerInteractionCoordinator.MarkCheckInStarted(parolee, officer);
                    break;
            }
        }

        /// <summary>
        /// End a supervising-officer interaction through the private coordinator.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void EndSupervisingOfficerInteraction(Player parolee, ParoleOfficerBehavior officer, SupervisingOfficerInteractionKind interactionKind)
        {
            supervisingOfficerInteractionCoordinator.EndInteraction(parolee, officer, interactionKind);
        }

        /// <summary>
        /// Complete a supervising-officer check-in session through the private coordinator.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void CompleteSupervisingOfficerCheckIn(Player parolee, ParoleOfficerBehavior officer)
        {
            supervisingOfficerInteractionCoordinator.CompleteCheckIn(parolee, officer);
        }

        /// <summary>
        /// Commit a supervising-officer check-in after the parole system has admitted the parolee.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void StartCheckIn(Player parolee, ParoleOfficerBehavior officer)
        {
            supervisingOfficerInteractionCoordinator.StartCheckIn(parolee, officer);
        }

        /// <summary>
        /// Cancel a supervising-officer intake reservation through the private coordinator.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void CancelSupervisingOfficerIntake(Player parolee, ParoleOfficerBehavior officer)
        {
            supervisingOfficerInteractionCoordinator.CancelIntake(parolee, officer);
        }

        /// <summary>
        /// Cancel a supervising-officer check-in reservation through the private coordinator.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void CancelCheckIn(Player parolee, ParoleOfficerBehavior officer)
        {
            supervisingOfficerInteractionCoordinator.CompleteCheckIn(parolee, officer);
        }

        #endregion
    }
}

