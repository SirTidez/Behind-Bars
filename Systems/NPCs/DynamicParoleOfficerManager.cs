using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems;
using Behind_Bars.Utils;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Schedules;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.Map;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Schedules;
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
        /// Officers currently owned by the native courthouse StayInBuilding action.  This
        /// state prevents the roster pump from resetting an in-progress doorway transition.
        /// </summary>
        private HashSet<ParoleOfficerAssignment> officersAtCourthouse;

        private NPCEnterableBuilding courthouseHomeBuilding;
        private bool loggedCourthouseHomeLookupFailure;

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
        // MelonCoroutines returns an opaque handle on IL2CPP.  Treating it as a
        // Unity Coroutine made the cast return null, so overlapping release-prep
        // coroutines could be started and the supervisor was not reliably staged.
        private object preparedReleaseMeetingCoroutine;
        private Coroutine retryInitializeCoroutine;
        private Coroutine delayedIntakeNotificationCoroutine;

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
                officersAtCourthouse = new HashSet<ParoleOfficerAssignment>();
                lastUpdateTime = Time.time;

                // Get references
                npcManager = Core.Instance?.NpcManager;
                if (npcManager == null)
                {
                    ModLogger.Error("DynamicParoleOfficerManager: NpcManager not found");
                    StartInitializationRetry();
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
                    StartInitializationRetry();
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

                // Do not lose a release request that arrived while this component was
                // waiting for scene-local manager/player references.
                if (HasPreparedReleaseMeeting() && preparedReleaseMeetingCoroutine == null)
                {
                    preparedReleaseMeetingCoroutine = MelonCoroutines.Start(PrepareSupervisingOfficerForReleaseCoroutine());
                    ModLogger.Info("DynamicParoleOfficerManager: Starting queued supervising-officer release meeting after initialization");
                }
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

            retryInitializeCoroutine = null;
        }

        private void StartInitializationRetry()
        {
            if (retryInitializeCoroutine == null)
            {
                retryInitializeCoroutine = MelonCoroutines.Start(RetryInitialize()) as Coroutine;
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

            // A release can have already dispatched this supervisor from the courthouse
            // before the parole record becomes active. Keep that pre-positioning intact;
            // a second generic intake request would otherwise replace the police-station
            // meeting point with the player's current location.
            if (HasPreparedReleaseMeetingFor(player))
            {
                ModLogger.Info($"DynamicParoleOfficerManager: Retaining prepared police-station meeting for {player.name} after parole activation");
                return;
            }

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

            // The manager normally keeps the supervisor inside the courthouse.  Initial
            // intake is an explicit exception: recall/spawn them before the delayed handoff
            // begins so the canonical state machine has an officer to own the interaction.
            EnsureSupervisingOfficer();
            RecallOfficerFromCourthouse(ParoleOfficerAssignment.PoliceStationSupervisor, GetActiveSupervisingOfficer(), returnToAssignedPost: true);

            if (delayedIntakeNotificationCoroutine == null)
            {
                delayedIntakeNotificationCoroutine = MelonCoroutines.Start(DelayedIntakeNotification(player)) as Coroutine;
            }
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

                    var supervisingOfficer = GetActiveSupervisingOfficer();
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
                delayedIntakeNotificationCoroutine = null;

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

            UpdateSupervisingOfficerRoster();

            // Update patrol officers according to the small rotating field roster.
            UpdatePatrolOfficers();
        }

        /// <summary>
        /// Keeps the supervisor inside the courthouse unless the release bridge, an active
        /// supervising interaction, a queued initial intake, or a parolee approaching the
        /// valid report point requires a visible officer at the existing front-apron station.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void UpdateSupervisingOfficerRoster()
        {
            var supervisingOfficer = GetActiveSupervisingOfficer();
            bool requiresExteriorPresence = HasPreparedReleaseMeeting() ||
                                            supervisingOfficerInteractionCoordinator.HasPendingIntake(currentPlayer) ||
                                            supervisingOfficerInteractionCoordinator.HasActiveSession(currentPlayer) ||
                                            IsPlayerApproachingCheckIn(currentPlayer);

            if (supervisingOfficer != null && supervisingOfficer.IsProcessingIntake())
            {
                requiresExteriorPresence = true;
            }

            if (requiresExteriorPresence)
            {
                EnsureSupervisingOfficer();
                supervisingOfficer = GetActiveSupervisingOfficer();
                RecallOfficerFromCourthouse(
                    ParoleOfficerAssignment.PoliceStationSupervisor,
                    supervisingOfficer,
                    returnToAssignedPost: !HasPreparedReleaseMeeting());
                return;
            }

            if (supervisingOfficer != null)
            {
                SendOfficerToCourthouse(ParoleOfficerAssignment.PoliceStationSupervisor, supervisingOfficer);
            }
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
            if (player == null)
            {
                return;
            }

            preparedReleaseParolee = player;
            preparedReleaseMeetingPoint = meetingPoint;

            // Release requests may arrive while this scene component is still completing
            // its first-frame initialization. Persist the request so Initialize can begin
            // the walk immediately instead of dropping the only early-dispatch signal.
            if (!isInitialized)
            {
                ModLogger.Info($"DynamicParoleOfficerManager: Queued supervising-officer release meeting for {player.name} until initialization completes");
                return;
            }

            EnsureSupervisingOfficer();

            if (preparedReleaseMeetingCoroutine == null)
            {
                preparedReleaseMeetingCoroutine = MelonCoroutines.Start(PrepareSupervisingOfficerForReleaseCoroutine());
            }

            ModLogger.Info($"DynamicParoleOfficerManager: Preparing supervising officer to meet {player.name} at release point {meetingPoint}");
        }

        /// <summary>
        /// Resolves the supervisor owned by this dynamic manager. The legacy prison-NPC
        /// manager does not own dynamically spawned parole staff.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal ParoleOfficerBehavior GetActiveSupervisingOfficer()
        {
            if (activeOfficers != null &&
                activeOfficers.TryGetValue(ParoleOfficerAssignment.PoliceStationSupervisor, out var supervisingOfficer) &&
                supervisingOfficer != null)
            {
                return supervisingOfficer;
            }

            return FindExistingSupervisingOfficer();
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
                        // Release staging occurs before a parole record exists, so the
                        // state machine has not necessarily been lazily created by a
                        // normal check-in yet.  Ask the canonical officer behavior to
                        // create/own it rather than silently polling a missing component.
                        var intakeStateMachine = supervisingOfficer.EnsureParoleIntakeStateMachine();
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
        private bool HasPreparedReleaseMeetingFor(Player player)
        {
            return player != null && preparedReleaseParolee == player && HasPreparedReleaseMeeting();
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
                bool isRosterActive = ParoleOfficerRosterSchedule.IsPatrolActive(assignment);

                if (isRosterActive && !isSpawned && distance < SPAWN_DISTANCE_THRESHOLD)
                {
                    ModLogger.Debug($"DynamicParoleOfficerManager: Spawning roster patrol {assignment} for {ParoleOfficerRosterSchedule.GetCurrentShiftLabel()} (distance: {distance:F1}m)");
                    SpawnOfficer(assignment);
                }
                else if (isRosterActive && isSpawned)
                {
                    if (activeOfficers.TryGetValue(assignment, out var officer) && officer != null)
                    {
                        RecallOfficerFromCourthouse(assignment, officer, returnToAssignedPost: false);
                        officer.ResumeScheduledPatrol();
                    }
                }
                else if (!isRosterActive && isSpawned)
                {
                    if (activeOfficers.TryGetValue(assignment, out var officer) && officer != null)
                    {
                        SendOfficerToCourthouse(assignment, officer);
                    }
                }
            }
        }

        /// <summary>
        /// Puts an otherwise idle officer inside the native courthouse building event.
        /// The action is pre-created on the registered NPC template and is never added to
        /// a live network object.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SendOfficerToCourthouse(ParoleOfficerAssignment assignment, ParoleOfficerBehavior officer)
        {
            if (officer == null || officer.IsProcessingIntake())
            {
                return;
            }

            if (officersAtCourthouse.Contains(assignment))
            {
                // Keep the officer fully live while resident. NPCEnterableBuilding owns
                // courthouse occupancy and player knock/entry access; disabling either the
                // native root or this injected behavior makes that interaction graph flaky.
                return;
            }

            if (!TryResolveCourthouseHomeAction(officer, out var scheduleManager, out var homeAction))
            {
                return;
            }

            try
            {
                homeAction.SetStartTime(0);
                homeAction.Duration = 1439;
                ReflectionUtils.TrySetFieldOrProperty(homeAction, "EndTime", 2359);
                ReflectionUtils.TrySetFieldOrProperty(homeAction, "Building", courthouseHomeBuilding);
                ReflectionUtils.TrySetFieldOrProperty(homeAction, "Door", null);
                homeAction.gameObject.SetActive(true);
                homeAction.enabled = true;
                scheduleManager.InitializeActions();
                scheduleManager.EnableSchedule();
                // EnforceState is the native public schedule transition. NPCEvent_StayInBuilding
                // does not expose a callable Begin method in the current game build.
                scheduleManager.EnforceState(true);
                officer.BeginCourthouseHomeStay();
                officersAtCourthouse.Add(assignment);
                ModLogger.Info($"DynamicParoleOfficerManager: {assignment} is returning inside the courthouse home base");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: Failed to send {assignment} to courthouse home base: {ex.Message}");
            }
        }

        /// <summary>
        /// Ends the native home event before this officer resumes an exterior task.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void RecallOfficerFromCourthouse(ParoleOfficerAssignment assignment, ParoleOfficerBehavior officer, bool returnToAssignedPost)
        {
            if (officer == null)
            {
                return;
            }

            bool wasAtCourthouse = officersAtCourthouse.Contains(assignment);

            if (wasAtCourthouse &&
                TryResolveCourthouseHomeAction(officer, out var scheduleManager, out var homeAction))
            {
                try
                {
                    homeAction.End();
                    homeAction.enabled = false;
                    homeAction.gameObject.SetActive(false);
                    scheduleManager.InitializeActions();
                    officersAtCourthouse.Remove(assignment);
                    ModLogger.Info($"DynamicParoleOfficerManager: {assignment} is leaving the courthouse home base");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"DynamicParoleOfficerManager: Failed to recall {assignment} from courthouse home base: {ex.Message}");
                    return;
                }
            }

            officer.SetOnDuty(true);
            // Avoid continuously resetting a supervisor that is already outside: a
            // check-in dialogue or release handoff owns its current movement target.
            if (returnToAssignedPost && wasAtCourthouse && !officer.IsProcessingIntake())
            {
                officer.ReturnToAssignedPost(PresetParoleOfficerRoutes.GetSupervisingOfficerStation());
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool TryResolveCourthouseHomeAction(
            ParoleOfficerBehavior officer,
            out NPCScheduleManager scheduleManager,
            out NPCEvent_StayInBuilding homeAction)
        {
            scheduleManager = null;
            homeAction = null;
            if (officer == null || officer.gameObject == null)
            {
                return false;
            }

            if (!TryResolveCourthouseHomeBuilding(out var building))
            {
                return false;
            }

            scheduleManager = officer.GetComponentInChildren<NPCScheduleManager>(true);
            if (scheduleManager == null)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: {officer.name} is missing the pre-registered native NPCScheduleManager");
                return false;
            }

            var actions = scheduleManager.GetComponentsInChildren<NPCEvent_StayInBuilding>(true);
            foreach (var candidate in actions)
            {
                if (candidate != null && candidate.gameObject != null &&
                    string.Equals(candidate.gameObject.name, JailNpcPrefabLifecycle.CourthouseHomeScheduleActionName, StringComparison.Ordinal))
                {
                    homeAction = candidate;
                    break;
                }
            }

            if (homeAction == null)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: {officer.name} is missing the pre-registered courthouse home action");
                return false;
            }

            var nativeNpc = officer.GetComponentInChildren<NPC>(true);
            if (nativeNpc == null)
            {
                ModLogger.Error($"DynamicParoleOfficerManager: {officer.name} has no native NPC surface for courthouse schedule");
                return false;
            }

            ReflectionUtils.TrySetFieldOrProperty(homeAction, "npc", nativeNpc);
            ReflectionUtils.TrySetFieldOrProperty(homeAction, "schedule", scheduleManager);
            courthouseHomeBuilding = building;
            return true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool TryResolveCourthouseHomeBuilding(out NPCEnterableBuilding building)
        {
            building = courthouseHomeBuilding;
            if (building != null && building.gameObject != null)
            {
                return true;
            }

            var buildings = BBHelpers.FindObjectsOfTypeSafe<NPCEnterableBuilding>();
            if (buildings != null)
            {
                foreach (var candidate in buildings)
                {
                    if (candidate?.gameObject != null &&
                        candidate.gameObject.name.IndexOf("Courthouse", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        building = candidate;
                        courthouseHomeBuilding = candidate;
                        loggedCourthouseHomeLookupFailure = false;
                        ModLogger.Info($"DynamicParoleOfficerManager: Resolved native courthouse home base at {candidate.transform.position}");
                        return true;
                    }
                }
            }

            if (!loggedCourthouseHomeLookupFailure)
            {
                loggedCourthouseHomeLookupFailure = true;
                ModLogger.Error("DynamicParoleOfficerManager: Native Courthouse NPCEnterableBuilding was not found; parole officers will remain at their current exterior locations");
            }

            return false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool IsCheckInWindowOpen(Player player)
        {
            if (player == null)
            {
                return false;
            }

            var paroleManager = Core.ResolveParoleManager();
            return paroleManager != null &&
                   paroleManager.GetDailyCheckInStatus(player, out _, applyConsequences: false) == ParoleManager.CheckInStatus.Allowed;
        }

        /// <summary>
        /// A scheduled check-in makes the supervisor available only when the parolee is
        /// actually approaching the report point.  Keeping an officer outside for the
        /// entire window defeated the courthouse home schedule and added needless NPC cost.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool IsPlayerApproachingCheckIn(Player player)
        {
            if (!IsCheckInWindowOpen(player))
            {
                return false;
            }

            const float checkInRecallDistance = 22f;
            Vector3 reportPoint = PresetParoleOfficerRoutes.GetSupervisingOfficerStation();
            return Vector3.Distance(player.transform.position, reportPoint) <= checkInRecallDistance;
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
                officersAtCourthouse?.Remove(assignment);
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
            // The supervisor is spawned at the validated exterior meeting point before
            // transitioning into the courthouse home schedule when off duty.
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
            if (retryInitializeCoroutine != null)
            {
                MelonCoroutines.Stop(retryInitializeCoroutine);
                retryInitializeCoroutine = null;
            }

            if (delayedIntakeNotificationCoroutine != null)
            {
                MelonCoroutines.Stop(delayedIntakeNotificationCoroutine);
                delayedIntakeNotificationCoroutine = null;
            }

            UnsubscribeFromEvents();
            CancelPreparedSupervisingOfficerForRelease(preparedReleaseParolee);
            DespawnAllOfficers();
            officersAtCourthouse?.Clear();
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
            ReturnSupervisingOfficerToCourthouse(officer, "check-in");
        }

        /// <summary>
        /// Completes the release/intake handoff and returns the supervisor to the native
        /// courthouse home action.  This is intentionally separate from a daily check-in:
        /// the initial location explanation is not itself a reporting appointment.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void CompleteSupervisingOfficerIntake(Player parolee, ParoleOfficerBehavior officer)
        {
            supervisingOfficerInteractionCoordinator.CancelIntake(parolee, officer);
            CompletePreparedSupervisingOfficerReleaseMeeting(parolee);
            ReturnSupervisingOfficerToCourthouse(officer, "initial intake");
        }

        /// <summary>
        /// Marks the release-door staging request as consumed once its canonical intake
        /// handoff completes.  This is intentionally not the cancellation path: cancelling
        /// here would ask the intake state machine to stop while it is completing and can
        /// leave the supervisor at the exterior meeting point.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void CompletePreparedSupervisingOfficerReleaseMeeting(Player parolee)
        {
            if (!HasPreparedReleaseMeetingFor(parolee))
            {
                return;
            }

            if (preparedReleaseMeetingCoroutine != null)
            {
                MelonCoroutines.Stop(preparedReleaseMeetingCoroutine);
                preparedReleaseMeetingCoroutine = null;
            }

            preparedReleaseParolee = null;
            ModLogger.Info("DynamicParoleOfficerManager: Consumed supervising-officer release meeting after initial intake");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void ReturnSupervisingOfficerToCourthouse(ParoleOfficerBehavior officer, string completedWorkflow)
        {
            if (officer == null ||
                officer.GetAssignment() != ParoleOfficerAssignment.PoliceStationSupervisor)
            {
                return;
            }

            SendOfficerToCourthouse(ParoleOfficerAssignment.PoliceStationSupervisor, officer);
            ModLogger.Info($"DynamicParoleOfficerManager: Supervisor returning to courthouse after {completedWorkflow}");
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

