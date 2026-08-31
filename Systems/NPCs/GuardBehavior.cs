using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Consolidated guard behavior - replaces JailGuardBehavior, IntakeOfficerStateMachine, SmartEscortPath, PatrolSystem
    /// Inherits from BaseJailNPC for core functionality, uses SecurityDoorBehavior for door operations
    /// </summary>
    public class GuardBehavior : BaseJailNPC
    {
#if !MONO
        public GuardBehavior(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Guard Configuration

        /// <summary>Role selected for this guard's normal jail duties.</summary>
        public enum GuardRole
        {
            /// <summary>Remains stationed in the guard room.</summary>
            GuardRoomStationary,    // Guards stationed in guard room
            /// <summary>Remains stationed in the booking area.</summary>
            BookingStationary,      // Guards stationed in booking area
            /// <summary>Owns prisoner booking through <see cref="IntakeOfficerStateMachine"/>.</summary>
            IntakeOfficer,          // Dedicated intake processing guard
            /// <summary>Follows the configured patrol route.</summary>
            PatrolGuard,            // Guards doing patrol routes
            /// <summary>Responds to incidents and lockdown assignments.</summary>
            ResponseGuard           // Responds to incidents
        }

        /// <summary>Spawn/post assignment that determines role-specific scene ownership.</summary>
        public enum GuardAssignment
        {
            /// <summary>First guard-room post.</summary>
            GuardRoom0,    // Guard room spawn point 0
            /// <summary>Second guard-room post.</summary>
            GuardRoom1,    // Guard room spawn point 1
            /// <summary>Primary booking post, normally occupied by the intake officer.</summary>
            Booking0,      // Booking spawn point 0 (usually intake officer)
            /// <summary>Secondary booking post.</summary>
            Booking1,      // Booking spawn point 1
            /// <summary>Dedicated cell-block/day-room patrol assignment.</summary>
            DayRoomPatrol  // Dedicated officer for the cell-block/day-room circuit
        }

        /// <summary>Current activity layered over the base NPC state.</summary>
        public enum GuardActivity
        {
            /// <summary>No active patrol, intake, escort, or incident response.</summary>
            Idle,
            /// <summary>Following a patrol route.</summary>
            Patrolling,
            /// <summary>Delegating prisoner processing to the intake state machine.</summary>
            ProcessingIntake,
            /// <summary>Monitoring a prisoner escort and compliance distance.</summary>
            EscortingPrisoner,
            /// <summary>Holding a post while another system owns orchestration.</summary>
            MonitoringArea,
            /// <summary>Moving to or handling an incident.</summary>
            RespondingToIncident
        }

#if MONO
        [System.Serializable]
        public class PatrolRoute
        {
            /// <summary>World-space patrol points in traversal order.</summary>
            public Vector3[] points;
            /// <summary>Patrol movement speed in world units per second.</summary>
            public float speed = 2.5f;
            /// <summary>Idle time at each patrol point, in Unity seconds.</summary>
            public float waitTime = 3f;
            /// <summary>Whether the authored route is available for use.</summary>
            public bool isActive = true;
        }

        [System.Serializable]
        public class IntakeStationInfo
        {
            /// <summary>Stable station key used by the MONO intake compatibility surface.</summary>
            public string stationName;
            /// <summary>Scene transform representing the station.</summary>
            public Transform stationTransform;
            /// <summary>Guard-facing point used for station interaction.</summary>
            public Transform guardPoint;
            /// <summary>Whether this station expects a prisoner.</summary>
            public bool requiresPrisoner = true;
            /// <summary>Station processing duration, in Unity seconds.</summary>
            public float processingTime = 5f;
        }
#endif

        #endregion

        #region Guard Properties

        /// <summary>Role behavior selected for this guard.</summary>
        public GuardRole role = GuardRole.GuardRoomStationary;
        /// <summary>Spawn/post assignment used to resolve role and route ownership.</summary>
        public GuardAssignment assignment;
        /// <summary>Display/diagnostic badge identifier; generated when empty.</summary>
        public string badgeNumber = "";
        /// <summary>Configured experience level used by role data; currently informational.</summary>
        public int experienceLevel = 1;
#if MONO
        /// <summary>MONO-authored patrol route; IL2CPP stores equivalent points in a private array.</summary>
        public PatrolRoute patrolRoute = new PatrolRoute();
#else
        /// <summary>IL2CPP patrol wait time copied from the native-compatible route configuration.</summary>
        private float patrolWaitTime = 3f;
        /// <summary>IL2CPP patrol points supplied through the hidden route bridge.</summary>
        private Vector3[] patrolRoutePoints = Array.Empty<Vector3>();
#endif
        /// <summary>Unity-time start of the current shift.</summary>
        public float shiftStartTime = 0f;
        /// <summary>Configured shift length, in Unity seconds.</summary>
        public float shiftDuration = 480f; // 8 minutes default

        // Runtime state
        /// <summary>Activity layered over the inherited coarse NPC state.</summary>
        private GuardActivity currentActivity = GuardActivity.Idle;
        /// <summary>Security-door owner for non-intake escort operations.</summary>
        private SecurityDoorBehavior doorBehavior;
        /// <summary>Optional native voice-command controller.</summary>
        private JailNPCAudioController audioController;
        /// <summary>Optional dialogue/UI controller for guard commands.</summary>
        private JailNPCDialogueController dialogueController;
        /// <summary>Resolved spawn/post transform for the assignment.</summary>
        private Transform assignedSpawnPoint;
        /// <summary>Index of the current patrol waypoint.</summary>
        private int currentPatrolIndex = 0;
        /// <summary>Unity-time arrival timestamp for the current patrol waypoint.</summary>
        private float lastPatrolTime = 0f;
        /// <summary>Whether a patrol waypoint has been dispatched and remains active.</summary>
        private bool hasActivePatrolDestination;
        /// <summary>Whether patrol arrival has passed the completion test.</summary>
        private bool patrolArrivalConfirmed;
        /// <summary>Number of recovery attempts made for the current patrol waypoint.</summary>
        private int patrolRetryCount;
        /// <summary>Unity-time sample used to detect a stalled patrol route.</summary>
        private float lastPatrolProgressTime;
        /// <summary>Last position used for patrol progress comparison.</summary>
        private Vector3 lastPatrolProgressPosition;
        /// <summary>Prevents repeated retry-limit warnings for one waypoint.</summary>
        private bool patrolRetryLimitLogged;
        /// <summary>Whether the guard is available for normal duty.</summary>
        private bool isOnDuty = true;
        /// <summary>Cell-facing target used by day-room inspection rotation.</summary>
        private Vector3 dayRoomInspectionTarget;
        /// <summary>Whether <see cref="dayRoomInspectionTarget"/> is valid.</summary>
        private bool hasDayRoomInspectionTarget;
        /// <summary>Optional authored cell-facing targets paired with day-room patrol points.</summary>
        private Vector3[] dayRoomInspectionTargets = Array.Empty<Vector3>();
        /// <summary>Whether the day-room guard's native baton is currently equipped.</summary>
        private bool dayRoomPatrolBatonEquipped;
        /// <summary>Prevents repeated native-movement ownership diagnostics.</summary>
        private bool dayRoomNativeMovementLogged;

        // Deliberately below the game's ordinary walking pace so the day-room guard
        // reads as an observant patrol rather than a response/escort movement.
        private const float DayRoomPatrolSpeed = 0.25f;
        private const int DayRoomPatrolSpeedPriority = 100;
        private const string DayRoomPatrolSpeedControlId = "BehindBars.DayRoomPatrol";
        private const float DayRoomPatrolWaitTime = 2.5f;
        private const float DayRoomLookTurnSpeed = 360f;
        private const float PatrolRetryDelay = 2f;
        private const float PatrolStallTimeout = 8f;
        private const float PatrolProgressDistance = 0.1f;
        private const int MaxPatrolRetries = 2;
        private const string DayRoomPatrolBatonResourcePath = "Avatar/Equippables/Baton";
        private const string EmptyEquippableResourcePath = "";

        #endregion

        #region Intake Officer State

        // Intake processing
        /// <summary>Prisoner currently owned by intake or escort behavior, if any.</summary>
        private Player currentPrisoner;
#if MONO
        /// <summary>MONO station definitions retained for the legacy intake compatibility path.</summary>
        private Dictionary<string, IntakeStationInfo> intakeStations;
        /// <summary>MONO set of completed legacy station keys.</summary>
        private HashSet<string> completedStations = new HashSet<string>();
#endif
        /// <summary>Current station key used by intake diagnostics.</summary>
        private string currentTargetStation = "";
        /// <summary>Whether this guard has delegated an intake workflow.</summary>
        private bool isProcessingIntake = false;

        // Prisoner compliance system
        /// <summary>Current escort patience value, clamped to the 0–100 range.</summary>
        private float guardPatience = 100f;
        /// <summary>Unity-time timestamp of the last compliance warning.</summary>
        private float lastComplianceWarningTime = 0f;
        /// <summary>Number of compliance violations observed during the current escort.</summary>
        private int complianceViolationCount = 0;
        /// <summary>Last sampled prisoner position used for diagnostics.</summary>
        private Vector3 lastKnownPrisonerPosition;

        // Compliance thresholds are world-space meters; rates/cooldowns use Unity seconds.
        /// <summary>Distance at or below which the prisoner is considered perfectly compliant.</summary>
        private const float COMPLIANCE_PERFECT = 2f;      // 0-2m: Perfect compliance
        /// <summary>Distance at or above which a warning may be issued.</summary>
        private const float COMPLIANCE_WARNING = 3f;      // 2-3m: Warning zone
        /// <summary>Distance at or above which active intervention messaging is used.</summary>
        private const float COMPLIANCE_VIOLATION = 5f;    // 3-5m: Active intervention
        /// <summary>Distance at or above which the escort is treated as an escape attempt.</summary>
        private const float COMPLIANCE_ESCAPE = 8f;       // 5m+: Escape attempt
        /// <summary>Patience loss rate per Unity second while outside the perfect range.</summary>
        private const float PATIENCE_LOSS_RATE = 2f;
        /// <summary>Patience recovery rate per Unity second while compliant.</summary>
        private const float PATIENCE_GAIN_RATE = 3f;
        /// <summary>Minimum Unity seconds between compliance warnings.</summary>
        private const float WARNING_COOLDOWN = 5f;

        #endregion

        #region Patrol System

        /// <summary>Filtered patrol points resolved from the jail controller.</summary>
        private List<Transform> availablePatrolPoints = new List<Transform>();
        /// <summary>Whether patrol-point resolution has completed for this guard.</summary>
        private bool patrolInitialized = false;

        #endregion

        #region Initialization

        /// <summary>
        /// Resolves shared door/audio/dialogue surfaces, initializes patrol/intake data, applies the role
        /// profile, and registers this guard with the NPC manager. Registration is paired with
        /// <see cref="OnDestroy"/> so destroyed guards cannot be selected for later work.
        /// </summary>
        protected override void InitializeNPC()
        {
            doorBehavior = BBHelpers.GetComponentSafe<SecurityDoorBehavior>(gameObject);
            if (doorBehavior == null)
            {
                doorBehavior = BBHelpers.AddComponentSafe<SecurityDoorBehavior>(gameObject);
            }

            if (string.IsNullOrEmpty(badgeNumber))
            {
                badgeNumber = GenerateBadgeNumber();
            }

            InitializePatrolPoints();
            InitializeIntakeStations();
            SetupGuardRole();
            EnsureDayRoomPatrolBaton();

            // Register with PrisonNPCManager
            var npcManager = Core.Instance?.NpcManager;
            if (npcManager != null)
            {
                npcManager.RegisterGuard(this);
            }

            shiftStartTime = Time.time;
            ModLogger.Debug($"GuardBehavior initialized: {role} guard {badgeNumber} at {assignment}");
        }

        /// <summary>Unregisters the guard before releasing base resources and native presentation state.</summary>
        protected override void OnDestroy()
        {
            // Guard registration outlives Unity object destruction unless it is removed
            // explicitly. Leaving the stale behavior in the manager can make later scene
            // sessions pick a destroyed guard for an escort or lockdown response.
            Core.Instance?.NpcManager?.UnregisterGuard(this);
            base.OnDestroy();
        }

        /// <summary>
        /// Applies an assignment and badge, derives the role, resolves its spawn point, and initializes audio.
        /// The shared Unity lifecycle still owns full component initialization.
        /// </summary>
        /// <param name="guardAssignment">Spawn/post assignment for the guard.</param>
        /// <param name="badge">Optional stable badge identifier; generated when empty.</param>
        public void Initialize(GuardAssignment guardAssignment, string badge = "")
        {
            assignment = guardAssignment;
            badgeNumber = string.IsNullOrEmpty(badge) ? GenerateBadgeNumber() : badge;

            // Set role based on assignment
            switch (assignment)
            {
                case GuardAssignment.GuardRoom0:
                case GuardAssignment.GuardRoom1:
                    role = GuardRole.GuardRoomStationary;
                    break;
                case GuardAssignment.Booking0:
                    role = GuardRole.IntakeOfficer;
                    break;
                case GuardAssignment.Booking1:
                    role = GuardRole.BookingStationary;
                    break;
                case GuardAssignment.DayRoomPatrol:
                    role = GuardRole.PatrolGuard;
                    break;
            }

            SetAssignedSpawnPoint();
            InitializeAudioComponents();
        }

        /// <summary>
        /// Resolves optional audio and dialogue controllers from the current native NPC object. The canonical
        /// template/spawner path is expected to provide these components; missing controllers only disable
        /// their presentation paths and do not create a fallback NPC graph.
        /// </summary>
        private void InitializeAudioComponents()
        {
            try
            {
                // The canonical native template/spawner path provides this component; DirectNPCBuilder is legacy.
                audioController = BBHelpers.GetComponentSafe<JailNPCAudioController>(gameObject);
                if (audioController == null)
                {
                    ModLogger.Warn($"Guard {badgeNumber}: No JailNPCAudioController found");
                }

                // Get dialogue controller
                dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(gameObject);
                if (dialogueController == null)
                {
                    ModLogger.Warn($"Guard {badgeNumber}: No JailNPCDialogueController found");
                }

                ModLogger.Debug($"Guard {badgeNumber}: Audio components initialized");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing audio components for guard {badgeNumber}: {e.Message}");
            }
        }

        /// <summary>
        /// Helper method to play guard voice commands during various activities
        /// </summary>
        /// <param name="commandType">Type of command to play</param>
        /// <param name="textMessage">Optional text message to display</param>
        /// <param name="useRadio">Whether to use radio effect</param>
        public void PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType commandType, string textMessage = null, bool useRadio = true)
        {
            try
            {
                if (dialogueController != null)
                {
                    dialogueController.SendGuardCommand(commandType, textMessage, useRadio);
                }
                else if (!string.IsNullOrEmpty(textMessage))
                {
                    // Fallback to text message only
                    TrySendNPCMessage(textMessage, 3f);
                }

                ModLogger.Debug($"Guard {badgeNumber}: Played voice command {commandType}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error playing voice command for guard {badgeNumber}: {e.Message}");
            }
        }

        /// <summary>
        /// Play appropriate voice command based on guard activity
        /// </summary>
        public void PlayActivityVoiceCommand()
        {
            switch (currentActivity)
            {
                case GuardActivity.Patrolling:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.CellCheck, "Cell check in progress.");
                    break;

                case GuardActivity.ProcessingIntake:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Follow, "Follow me for processing.");
                    break;

                case GuardActivity.EscortingPrisoner:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Move, "Keep moving.");
                    break;

                case GuardActivity.RespondingToIncident:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Alert, "Responding to incident.");
                    break;

                case GuardActivity.MonitoringArea:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.AllClear, "Area secure.");
                    break;

                default:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Greeting, "Guard on duty.");
                    break;
            }
        }

        /// <summary>Maps the configured role to its activity and starts patrol setup when required.</summary>
        private void SetupGuardRole()
        {
            ConfigureDayRoomPatrolProfile();

            switch (role)
            {
                case GuardRole.IntakeOfficer:
                    currentActivity = GuardActivity.MonitoringArea;
                    break;
                case GuardRole.PatrolGuard:
                    currentActivity = GuardActivity.Patrolling;
                    StartPatrol();
                    break;
                default:
                    currentActivity = GuardActivity.MonitoringArea;
                    break;
            }
        }

        /// <summary>Copies non-null jail patrol points into the guard's route cache.</summary>
        private void InitializePatrolPoints()
        {
            availablePatrolPoints.Clear();

            var jailController = Core.JailController;
            if (jailController != null)
            {
                foreach (var point in jailController.patrolPoints)
                {
                    if (point != null)
                    {
                        availablePatrolPoints.Add(point);
                    }
                }
            }

            patrolInitialized = true;
            ModLogger.Debug($"Guard {badgeNumber} initialized with {availablePatrolPoints.Count} patrol points");
        }

        /// <summary>Builds the MONO compatibility station table from canonical jail transforms.</summary>
        private void InitializeIntakeStations()
        {
#if MONO
            intakeStations = new Dictionary<string, IntakeStationInfo>();

            // Define standard intake stations
            var stationConfigs = new[]
            {
                new { name = "MugshotStation", processing = 5f },
                new { name = "ScannerStation", processing = 4f },
                new { name = "Storage", processing = 3f }
            };

            foreach (var config in stationConfigs)
            {
                var stationInfo = new IntakeStationInfo
                {
                    stationName = config.name,
                    processingTime = config.processing,
                    stationTransform = FindStationTransform(config.name),
                    guardPoint = FindGuardPoint(config.name)
                };

                intakeStations[config.name] = stationInfo;
            }
#endif
        }

        /// <summary>Generates a four-digit diagnostic badge suffix for an unconfigured guard.</summary>
        private string GenerateBadgeNumber()
        {
            return $"G{UnityEngine.Random.Range(1000, 9999)}";
        }

        /// <summary>Resolves the assignment-specific spawn transform from the jail hierarchy.</summary>
        private void SetAssignedSpawnPoint()
        {
            assignedSpawnPoint = FindSpawnPoint(assignment.ToString());
        }

        #endregion

        #region State Management (Override BaseJailNPC)

        /// <summary>
        /// Dispatches idle activity. Patrol guards continue waypoint/inspection work, monitoring guards remain
        /// under their external orchestration owner, and intake processing is driven by its state machine.
        /// </summary>
        protected override void HandleIdleState()
        {
            switch (currentActivity)
            {
                case GuardActivity.Patrolling:
                    MaintainDayRoomCellInspection();
                    HandlePatrolLogic();
                    break;
                case GuardActivity.MonitoringArea:
                    HandleMonitoringLogic();
                    break;
                case GuardActivity.ProcessingIntake:
                    // Intake processing is handled by coroutines
                    break;
            }
        }

        /// <summary>
        /// Runs base movement completion, then advances patrol recovery or prisoner-compliance checks. Patrol
        /// activity depends on the base destination result unless the native day-room movement path is active.
        /// </summary>
        protected override void HandleMovingState()
        {
            base.HandleMovingState();

            if (currentActivity == GuardActivity.Patrolling)
            {
                if (currentState == NPCState.Idle)
                {
                    HandlePatrolLogic();
                }
                else
                {
                    HandlePatrolMovementRecovery();
                }
            }

            // Check for prisoner compliance if escorting
            if (currentActivity == GuardActivity.EscortingPrisoner && currentPrisoner != null)
            {
                CheckPrisonerCompliance();
            }
        }

        /// <summary>Dispatches work-state activity to intake/escort owners without duplicating their workflows.</summary>
        protected override void HandleWorkingState()
        {
            switch (currentActivity)
            {
                case GuardActivity.ProcessingIntake:
                    // Intake processing is handled by coroutines
                    break;
                case GuardActivity.EscortingPrisoner:
                    HandleEscortLogic();
                    break;
            }
        }

        #endregion

        #region Patrol Logic

        /// <summary>
        /// Maintains the current patrol waypoint, confirms arrival, waits at the point, and dispatches the
        /// next waypoint. Movement failure is delegated to the bounded recovery path.
        /// </summary>
        private void HandlePatrolLogic()
        {
            if (!patrolInitialized || GetPatrolPointCount() == 0) return;

            if (!hasActivePatrolDestination)
            {
                DispatchCurrentPatrolPoint(true);
                return;
            }

            if (!patrolArrivalConfirmed)
            {
                if (HasReachedDestination())
                {
                    patrolArrivalConfirmed = true;
                    lastPatrolTime = Time.time;
                    patrolRetryCount = 0;
                    patrolRetryLimitLogged = false;
                    ModLogger.Debug($"Guard {badgeNumber} reached patrol point {currentPatrolIndex}");
                }
                else
                {
                    HandlePatrolMovementRecovery();
                }

                return;
            }

            float waitTime =
#if MONO
                patrolRoute.waitTime;
#else
                patrolWaitTime;
#endif

            if (Time.time - lastPatrolTime >= waitTime)
            {
                currentPatrolIndex = (currentPatrolIndex + 1) % GetPatrolPointCount();
                DispatchCurrentPatrolPoint(true);
            }
        }

        /// <summary>
        /// Resets waypoint/retry state, equips the day-room patrol baton when applicable, and dispatches the
        /// first configured waypoint. A patrol with no points remains inactive.
        /// </summary>
        public void StartPatrol()
        {
            if (GetPatrolPointCount() == 0) return;

            EnsureDayRoomPatrolBaton();
            currentActivity = GuardActivity.Patrolling;
            currentPatrolIndex = 0;
            hasActivePatrolDestination = false;
            patrolArrivalConfirmed = false;
            patrolRetryCount = 0;
            patrolRetryLimitLogged = false;

            // Play patrol start announcement
            if (dialogueController != null)
            {
                dialogueController.SendGuardCommand(JailNPCAudioController.GuardCommandType.CellCheck,
                    "Beginning patrol.", true);
            }

            DispatchCurrentPatrolPoint(true);
        }

        /// <summary>
        /// Records the current waypoint as active, updates day-room inspection/speed state, and requests the
        /// route. Retry recovery calls this without clearing the retry count.
        /// </summary>
        /// <param name="resetRetryCount">Whether this is a fresh waypoint dispatch.</param>
        private void DispatchCurrentPatrolPoint(bool resetRetryCount)
        {
            int patrolPointCount = GetPatrolPointCount();
            if (patrolPointCount == 0) return;

            int targetIndex = currentPatrolIndex;
            Vector3 targetPosition = GetPatrolPointPosition(targetIndex);
            SetDayRoomInspectionTarget(targetPosition, targetIndex);
            ApplyDayRoomPatrolSpeedControl();
            hasActivePatrolDestination = true;
            patrolArrivalConfirmed = false;
            if (resetRetryCount)
            {
                patrolRetryCount = 0;
                patrolRetryLimitLogged = false;
            }

            lastPatrolProgressPosition = transform.position;
            lastPatrolProgressTime = Time.time;

            if (MoveTo(targetPosition))
            {
                ModLogger.Debug($"Guard {badgeNumber} patrolling to point {targetIndex}");
            }
            else
            {
                ModLogger.Warn($"Guard {badgeNumber} could not start path to patrol point {targetIndex}");
            }
        }

        /// <summary>
        /// Reissues a stalled or failed patrol destination after the retry delay, up to the bounded retry
        /// limit. Exhaustion retains the waypoint until a restart or eventual arrival.
        /// </summary>
        private void HandlePatrolMovementRecovery()
        {
            if (!hasActivePatrolDestination || patrolArrivalConfirmed || patrolRetryCount >= MaxPatrolRetries)
            {
                if (patrolRetryCount >= MaxPatrolRetries && !patrolRetryLimitLogged)
                {
                    patrolRetryLimitLogged = true;
                    ModLogger.Warn($"Guard {badgeNumber} exhausted patrol recovery attempts at point {currentPatrolIndex}; retaining the waypoint until arrival or patrol restart");
                }

                return;
            }

            Vector3 progressOffset = transform.position - lastPatrolProgressPosition;
            if (progressOffset.sqrMagnitude >= PatrolProgressDistance * PatrolProgressDistance)
            {
                lastPatrolProgressPosition = transform.position;
                lastPatrolProgressTime = Time.time;
                return;
            }

            bool pathFailed = navAgent != null && navAgent.enabled && navAgent.isOnNavMesh &&
                !navAgent.pathPending && navAgent.pathStatus != NavMeshPathStatus.PathComplete;
            bool pathStalled = Time.time - lastPatrolProgressTime >= PatrolStallTimeout;
            if (!pathFailed && !pathStalled)
            {
                return;
            }

            if (Time.time - lastDestinationTime < PatrolRetryDelay)
            {
                return;
            }

            patrolRetryCount++;
            ModLogger.Warn($"Guard {badgeNumber} retrying patrol point {currentPatrolIndex} after {(pathFailed ? "path failure" : "movement stall")} ({patrolRetryCount}/{MaxPatrolRetries})");
            DispatchCurrentPatrolPoint(false);
        }

        /// <summary>
        /// Routes day-room patrol movement through the native NPC movement owner so its SpeedController keeps
        /// authority over effective speed. Other assignments use <see cref="BaseJailNPC.MoveTo"/> directly.
        /// </summary>
        /// <param name="destination">World-space destination.</param>
        /// <param name="tolerance">Optional completion tolerance in world units.</param>
        /// <returns>True when the selected movement owner accepted the route.</returns>
        public override bool MoveTo(Vector3 destination, float tolerance = -1f)
        {
            // BaseJailNPC's direct NavMeshAgent path is kept for every other guard role.
            // The day-room guard must use the native NPC movement owner so its native
            // NPCSpeedController has authority over the effective walking speed.
            if (assignment != GuardAssignment.DayRoomPatrol || npcComponent?.Movement == null || !npcComponent.Movement.CanMove())
            {
                return base.MoveTo(destination, tolerance);
            }

            if (tolerance > 0)
            {
                positionTolerance = tolerance;
            }

            ApplyDayRoomPatrolSpeedControl();
            currentDestination = destination;
            hasReachedDestination = false;
            lastDestinationTime = Time.time;
            npcComponent.Movement.SetDestination(destination);
            ChangeState(NPCState.Moving);

            if (!dayRoomNativeMovementLogged)
            {
                dayRoomNativeMovementLogged = true;
                ModLogger.Info($"Day-room guard {badgeNumber} is using native NPC movement at {DayRoomPatrolSpeed:F2} speed multiplier");
            }

            return true;
        }

        /// <summary>
        /// Applies the slow day-room patrol profile and leaves ordinary guard assignments on their native
        /// defaults. The NavMesh value alone is insufficient because native movement may overwrite it.
        /// </summary>
        private void ConfigureDayRoomPatrolProfile()
        {
            if (assignment != GuardAssignment.DayRoomPatrol)
            {
                if (navAgent != null)
                {
                    navAgent.updateRotation = true;
                }

                return;
            }

            if (navAgent != null)
            {
                navAgent.speed = DayRoomPatrolSpeed;
                navAgent.acceleration = Mathf.Min(navAgent.acceleration, 4f);
                // Let the native NavMesh agent own the walking-facing direction.  Forcing
                // the guard to face a cell while travelling made the agent walk sideways or
                // backwards when a route segment ran parallel to the cell block.
                navAgent.updateRotation = true;
            }

            ApplyDayRoomPatrolSpeedControl();

#if MONO
            patrolRoute.speed = DayRoomPatrolSpeed;
            patrolRoute.waitTime = DayRoomPatrolWaitTime;
#else
            patrolWaitTime = DayRoomPatrolWaitTime;
#endif
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Replaces the named native speed control for day-room patrols. This is IL2CPP-hidden because it is
        /// an internal native bridge, not a public injected API.
        /// </summary>
        private void ApplyDayRoomPatrolSpeedControl()
        {
            if (assignment != GuardAssignment.DayRoomPatrol || npcComponent?.Movement?.SpeedController == null)
            {
                return;
            }

            try
            {
                // The native NPC movement system owns the NavMeshAgent's effective speed
                // after a destination is set.  Apply a named native control rather than
                // relying on the agent value alone, which native movement later replaces.
                var speedController = npcComponent.Movement.SpeedController;
                speedController.RemoveSpeedControl(DayRoomPatrolSpeedControlId);
                speedController.AddSpeedControl(new NPCSpeedController.SpeedControl(
                    DayRoomPatrolSpeedControlId,
                    DayRoomPatrolSpeedPriority,
                    DayRoomPatrolSpeed));
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Day-room guard {badgeNumber} could not apply native patrol speed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pairs a patrol waypoint with an authored inspection target, falling back to the nearest cell
        /// interior when no paired target was supplied.
        /// </summary>
        /// <param name="patrolPoint">Waypoint used for nearest-cell fallback.</param>
        /// <param name="patrolIndex">Index into the optional paired target array.</param>
        private void SetDayRoomInspectionTarget(Vector3 patrolPoint, int patrolIndex)
        {
            hasDayRoomInspectionTarget = false;
            if (assignment != GuardAssignment.DayRoomPatrol)
            {
                return;
            }

            // The route builder pairs every authored circulation marker with the nearest
            // cell interior.  Use that recorded target so a stop always faces the cell row
            // beside that part of the patrol circuit.
            if (patrolIndex >= 0 && patrolIndex < dayRoomInspectionTargets.Length)
            {
                dayRoomInspectionTarget = dayRoomInspectionTargets[patrolIndex];
                hasDayRoomInspectionTarget = true;
                return;
            }

            var jailController = Core.JailController;
            if (jailController?.cells == null)
            {
                return;
            }

            float closestDistanceSquared = float.MaxValue;
            foreach (var cell in jailController.cells)
            {
                Transform doorPoint = cell?.cellDoor?.doorPoint;
                Transform interiorTarget = cell?.cellBounds ?? cell?.cellTransform;
                if (doorPoint == null || interiorTarget == null)
                {
                    continue;
                }

                float distanceSquared = (doorPoint.position - patrolPoint).sqrMagnitude;
                if (distanceSquared < closestDistanceSquared)
                {
                    closestDistanceSquared = distanceSquared;
                    dayRoomInspectionTarget = interiorTarget.position;
                    hasDayRoomInspectionTarget = true;
                }
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Requests the native baton equippable once for the day-room assignment. No wrapper component is
        /// created; the native NPC slot remains the source of truth.
        /// </summary>
        private void EnsureDayRoomPatrolBaton()
        {
            if (assignment != GuardAssignment.DayRoomPatrol || dayRoomPatrolBatonEquipped || npcComponent == null)
            {
                return;
            }

            try
            {
                // Native NPC API only. This is the game's persistent AvatarEquippable seam;
                // no S1API component, wrapper, or dependency is involved.
                npcComponent.SetEquippable_Return(DayRoomPatrolBatonResourcePath);
                dayRoomPatrolBatonEquipped = true;
                ModLogger.Debug($"Day-room guard {badgeNumber} equipped police baton");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Day-room guard {badgeNumber} could not equip police baton: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the native equippable state used during a jail emergency. The game's NPC
        /// avatar exposes one active equippable slot, so the responding guard draws the Taser
        /// while the remaining guards retain their visible police batons.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Enters emergency response activity and equips the native Taser or baton assignment.</summary>
        /// <param name="isPrimaryResponder">Whether this guard is the primary Taser responder.</param>
        public void EnterEmergencyLockdown(bool isPrimaryResponder)
        {
            StopMovement();
            currentActivity = GuardActivity.RespondingToIncident;

            try
            {
                npcComponent?.SetEquippable_Return(isPrimaryResponder
                    ? "Avatar/Equippables/Taser"
                    : "Avatar/Equippables/Baton");
                ModLogger.Debug($"Guard {badgeNumber} entered lockdown with {(isPrimaryResponder ? "Taser" : "baton")} active");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Guard {badgeNumber} could not set lockdown equipment: {ex.Message}");
            }
        }

        /// <summary>
        /// Marks the visible point at which the responding guard has reached and subdued the
        /// prisoner. The lockdown manager owns the blackout and transfer immediately after it.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Stops movement and emits the visible subdual instruction; the lockdown manager owns transfer.</summary>
        public void PerformLockdownSubdual()
        {
            StopMovement();
            currentActivity = GuardActivity.RespondingToIncident;
            TrySendNPCMessage("Get down!", 1.5f);
            ModLogger.Info($"Guard {badgeNumber} reached the prisoner and delivered the lockdown subdual");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Clears the emergency native equippable and restores normal monitoring or day-room patrol behavior.
        /// </summary>
        public void ExitEmergencyLockdown()
        {
            // Each guard has a single native equippable slot. Clear the response
            // Taser/baton before restoring its ordinary assignment so the emergency
            // weapon never leaks into normal jail behavior.
            ClearEmergencyEquippable();

            if (assignment == GuardAssignment.DayRoomPatrol)
            {
                dayRoomPatrolBatonEquipped = false;
                EnsureDayRoomPatrolBaton();
                StartPatrol();
                return;
            }

            currentActivity = GuardActivity.MonitoringArea;
            StopMovement();
            TrySendNPCMessage("Area secure.", 1.5f);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Clears the single native equippable slot used for emergency response.</summary>
        private void ClearEmergencyEquippable()
        {
            if (npcComponent == null)
            {
                return;
            }

            try
            {
                npcComponent.SetEquippable_Return(EmptyEquippableResourcePath);
                ModLogger.Debug($"Guard {badgeNumber} cleared emergency equippable state");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Guard {badgeNumber} could not clear emergency equipment: {ex.Message}");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Returns the native NPC component for internal IL2CPP-safe coordination bridges.</summary>
        public NPC GetNativeNpc()
        {
            return npcComponent;
        }

        /// <summary>Turns a day-room patrol guard toward its paired cell inspection target while stopped.</summary>
        private void MaintainDayRoomCellInspection()
        {
            if (assignment != GuardAssignment.DayRoomPatrol || !hasDayRoomInspectionTarget)
            {
                return;
            }

            Vector3 towardCell = dayRoomInspectionTarget - transform.position;
            towardCell.y = 0f;
            if (towardCell.sqrMagnitude < 0.01f)
            {
                return;
            }

            Quaternion desiredRotation = Quaternion.LookRotation(towardCell.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, desiredRotation, DayRoomLookTurnSpeed * Time.deltaTime);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Replaces the patrol route and restarts patrol when already active. The vector-array bridge is hidden
        /// from IL2CPP callers because the native route is consumed internally.
        /// </summary>
        /// <param name="points">World-space patrol points in traversal order.</param>
        public void AssignPatrolRoute(Vector3[] points)
        {
#if MONO
            patrolRoute.points = points?.ToArray() ?? Array.Empty<Vector3>();
#else
            patrolRoutePoints = points?.ToArray() ?? Array.Empty<Vector3>();
#endif
            if (currentActivity == GuardActivity.Patrolling)
            {
                StartPatrol();
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Assigns a day-room route and its optional cell-facing inspection targets.</summary>
        /// <param name="points">World-space patrol points in traversal order.</param>
        /// <param name="inspectionTargets">Optional world-space cell targets paired by index.</param>
        public void AssignDayRoomPatrolRoute(Vector3[] points, Vector3[] inspectionTargets)
        {
            dayRoomInspectionTargets = inspectionTargets?.ToArray() ?? Array.Empty<Vector3>();
            AssignPatrolRoute(points);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Returns the configured route length, falling back to resolved jail patrol points.</summary>
        private int GetPatrolPointCount()
        {
#if MONO
            if (patrolRoute?.points != null && patrolRoute.points.Length > 0)
            {
                return patrolRoute.points.Length;
            }
#else
            if (patrolRoutePoints != null && patrolRoutePoints.Length > 0)
            {
                return patrolRoutePoints.Length;
            }
#endif
            return availablePatrolPoints.Count;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>Returns a patrol point by wrapped index from configured or resolved route data.</summary>
        /// <param name="index">Logical patrol index.</param>
        private Vector3 GetPatrolPointPosition(int index)
        {
#if MONO
            if (patrolRoute?.points != null && patrolRoute.points.Length > 0)
            {
                return patrolRoute.points[index % patrolRoute.points.Length];
            }
#else
            if (patrolRoutePoints != null && patrolRoutePoints.Length > 0)
            {
                return patrolRoutePoints[index % patrolRoutePoints.Length];
            }
#endif
            return availablePatrolPoints[index % availablePatrolPoints.Count].position;
        }

        #endregion

        #region Intake Officer Logic

        /// <summary>Detailed intake state machine owned by an intake-role guard.</summary>
        private IntakeOfficerStateMachine intakeStateMachine;

        /// <summary>
        /// Ensures an intake state machine exists and delegates prisoner booking to it. Guard activity remains
        /// a summary/coordination layer; detailed doors, stations, and completion belong to the delegate.
        /// </summary>
        /// <param name="prisoner">Prisoner to process.</param>
        public void StartIntakeProcess(Player prisoner)
        {
            if (role != GuardRole.IntakeOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not an intake officer");
                return;
            }

            // Play intake command
            if (dialogueController != null)
            {
                dialogueController.SendGuardCommand(JailNPCAudioController.GuardCommandType.Follow,
                    "Follow me for processing.", true);
            }

            // Initialize intake state machine if not already present
            if (intakeStateMachine == null)
            {
                intakeStateMachine = BBHelpers.GetComponentSafe<IntakeOfficerStateMachine>(gameObject);
                if (intakeStateMachine == null)
                {
                    intakeStateMachine = BBHelpers.AddComponentSafe<IntakeOfficerStateMachine>(gameObject);
                }
            }

            // Delegate to intake state machine
            if (intakeStateMachine != null)
            {
                intakeStateMachine.ForceStartIntake(prisoner);
                isProcessingIntake = true;
                currentActivity = GuardActivity.ProcessingIntake;
                currentPrisoner = prisoner;
                ModLogger.Info($"Guard {badgeNumber} delegating intake process to state machine for {prisoner.name}");
            }
            else
            {
                ModLogger.Error($"Failed to create IntakeOfficerStateMachine for guard {badgeNumber}");
            }
        }

        /// <summary>Returns whether the delegated intake state machine is processing a prisoner.</summary>
        public bool IsIntakeProcessingActive()
        {
            return intakeStateMachine != null && intakeStateMachine.IsProcessingIntake();
        }

        /// <summary>
        /// Forwards an intake door trigger to the detailed state machine. That receiver currently logs the
        /// compatibility callback while SecurityDoorBehavior owns real operations.
        /// </summary>
        /// <param name="triggerName">Door trigger identifier.</param>
        public void HandleIntakeDoorTrigger(string triggerName)
        {
            if (intakeStateMachine != null && role == GuardRole.IntakeOfficer)
            {
                intakeStateMachine.HandleDoorTrigger(triggerName);
            }
        }

        #endregion

        #region Prisoner Compliance

        /// <summary>Samples escort distance and forwards it to the patience/violation state update.</summary>
        private void CheckPrisonerCompliance()
        {
            if (currentPrisoner == null) return;

            float distance = Vector3.Distance(transform.position, currentPrisoner.transform.position);
            UpdatePrisonerCompliance(distance);
        }

        /// <summary>
        /// Updates patience and warning cooldown from world-space escort distance. Thresholds are evaluated in
        /// order from perfect compliance to escape response; this method only emits response messaging.
        /// </summary>
        /// <param name="distance">Current guard-to-prisoner distance in world units.</param>
        private void UpdatePrisonerCompliance(float distance)
        {
            bool isCompliant = distance <= COMPLIANCE_PERFECT;

            if (isCompliant)
            {
                // Gain patience when compliant
                guardPatience = Mathf.Min(100f, guardPatience + PATIENCE_GAIN_RATE * Time.deltaTime);
            }
            else
            {
                // Lose patience when non-compliant
                guardPatience = Mathf.Max(0f, guardPatience - PATIENCE_LOSS_RATE * Time.deltaTime);

                if (distance >= COMPLIANCE_WARNING && Time.time - lastComplianceWarningTime >= WARNING_COOLDOWN)
                {
                    HandleComplianceViolation(distance);
                    lastComplianceWarningTime = Time.time;
                }
            }

            // Store last known position
            lastKnownPrisonerPosition = currentPrisoner.transform.position;
        }

        /// <summary>Emits the response instruction appropriate for the current compliance-distance band.</summary>
        /// <param name="distance">Current guard-to-prisoner distance in world units.</param>
        private void HandleComplianceViolation(float distance)
        {
            complianceViolationCount++;

            if (distance >= COMPLIANCE_ESCAPE)
            {
                TrySendNPCMessage("Stop! Return immediately!", 3f);
                // Could trigger additional security response here
            }
            else if (distance >= COMPLIANCE_VIOLATION)
            {
                TrySendNPCMessage("You're too far away. Stay close.", 3f);
            }
            else if (distance >= COMPLIANCE_WARNING)
            {
                TrySendNPCMessage("Please stay closer.", 2f);
            }
        }

        #endregion

        #region Monitoring and Response

        /// <summary>
        /// Intentionally empty for monitoring guards. Intake orchestration is owned by
        /// <see cref="IntakeOfficerStateMachine"/> and <see cref="BookingProcess"/> rather than an independent
        /// arrival-polling path here.
        /// </summary>
        private void HandleMonitoringLogic()
        {
            // Intake orchestration is owned by IntakeOfficerStateMachine/BookingProcess.
            // Monitoring guards deliberately have no independent arrival polling path.
        }

        /// <summary>Maintains active escort compliance or returns to monitoring when its prisoner disappears.</summary>
        private void HandleEscortLogic()
        {
            if (currentPrisoner == null)
            {
                currentActivity = GuardActivity.MonitoringArea;
                ChangeState(NPCState.Idle);
                return;
            }

            CheckPrisonerCompliance();
        }

        #endregion

        #region Door Integration

        /// <summary>
        /// Gives intake officers first opportunity to receive a door trigger; all other escort operations go
        /// to the guard's SecurityDoorBehavior. The intake receiver currently preserves a compatibility no-op.
        /// </summary>
        /// <param name="other">Collider that entered this guard's trigger volume.</param>
        private void OnTriggerEnter(Collider other)
        {
            // Handle door triggers - delegate to intake state machine if processing intake
            var doorTrigger = BBHelpers.GetComponentSafe<DoorTriggerHandler>(other.gameObject);
            if (doorTrigger != null && doorBehavior != null)
            {
                bool handledByIntakeStateMachine = false;
                if (role == GuardRole.IntakeOfficer && intakeStateMachine != null && intakeStateMachine.IsProcessingIntake())
                {
                    // Let intake state machine handle door triggers during intake
                    intakeStateMachine.HandleDoorTrigger(other.name);
                    handledByIntakeStateMachine = true;
                }

                if (!handledByIntakeStateMachine)
                {
                    // Standard door behavior for non-intake operations
                    bool escorting = currentActivity == GuardActivity.EscortingPrisoner;
                    doorBehavior.HandleDoorTrigger(other.name, escorting, currentPrisoner);
                }
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>Finds the first jail-hierarchy transform whose name contains the supplied station key.</summary>
        /// <param name="stationName">Case-insensitive station name fragment.</param>
        /// <returns>Matching transform, or null when the jail controller/scene has no match.</returns>
        private Transform FindStationTransform(string stationName)
        {
            var jailController = Core.JailController;
            if (jailController == null) return null;

            Transform[] allTransforms = jailController.GetComponentsInChildren<Transform>();
            return allTransforms.FirstOrDefault(t =>
                t.name.Contains(stationName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Resolves a guard point using the two supported station naming conventions.</summary>
        /// <param name="stationName">Station name fragment.</param>
        /// <returns>Matching guard point, or null when absent.</returns>
        private Transform FindGuardPoint(string stationName)
        {
            return FindStationTransform($"GuardPoint_{stationName}") ??
                   FindStationTransform($"{stationName}_GuardPoint");
        }

        /// <summary>Finds the assignment-specific spawn transform in the jail hierarchy.</summary>
        /// <param name="assignmentName">Assignment enum name fragment.</param>
        /// <returns>Matching spawn transform, or null when absent.</returns>
        private Transform FindSpawnPoint(string assignmentName)
        {
            var jailController = Core.JailController;
            if (jailController == null) return null;

            // Look for spawn points based on assignment
            Transform[] allTransforms = jailController.GetComponentsInChildren<Transform>();
            return allTransforms.FirstOrDefault(t =>
                t.name.Contains(assignmentName, StringComparison.OrdinalIgnoreCase) &&
                t.name.Contains("Spawn", StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region Public Interface

        /// <summary>Returns the configured guard role.</summary>
        public GuardRole GetRole() => role;
        /// <summary>Returns the configured spawn/post assignment.</summary>
        public GuardAssignment GetAssignment() => assignment;
        /// <summary>Returns the current activity layered over the base NPC state.</summary>
        public GuardActivity GetCurrentActivity() => currentActivity;
        /// <summary>Returns the display/diagnostic badge identifier.</summary>
        public string GetBadgeNumber() => badgeNumber;
        /// <summary>Returns whether the guard is available for normal duty.</summary>
        public bool IsOnDuty() => isOnDuty;
        /// <summary>Returns delegated intake activity when available, otherwise the cached guard flag.</summary>
        public bool IsProcessingIntake()
        {
            return intakeStateMachine != null ? intakeStateMachine.IsProcessingIntake() : isProcessingIntake;
        }
        /// <summary>Returns the prisoner currently owned by escort/intake behavior, or null.</summary>
        public Player GetCurrentPrisoner() => currentPrisoner;
        /// <summary>Returns current escort patience in the 0–100 range.</summary>
        public float GetGuardPatience() => guardPatience;

        /// <summary>Updates duty availability and stops movement when taking the guard off duty.</summary>
        /// <param name="onDuty">Whether this guard should remain available for normal work.</param>
        public void SetOnDuty(bool onDuty)
        {
            isOnDuty = onDuty;
            if (!onDuty)
            {
                StopMovement();
                currentActivity = GuardActivity.Idle;
            }
        }

        /// <summary>Changes role and reapplies the corresponding activity profile.</summary>
        /// <param name="newRole">Role to assign.</param>
        public void AssignToRole(GuardRole newRole)
        {
            role = newRole;
            SetupGuardRole();
        }

        /// <summary>
        /// Sends this guard to an incident unless it is currently escorting a prisoner, preserving escort
        /// ownership over incident response.
        /// </summary>
        /// <param name="location">World-space incident location.</param>
        public void RespondToIncident(Vector3 location)
        {
            if (currentActivity != GuardActivity.EscortingPrisoner) // Don't abandon escorting
            {
                currentActivity = GuardActivity.RespondingToIncident;
                MoveTo(location);
                TrySendNPCMessage("Responding to incident.", 2f);

                // Play alert voice command
                if (dialogueController != null)
                {
                    dialogueController.SendGuardCommand(JailNPCAudioController.GuardCommandType.Alert,
                        "Responding to incident.", true);
                }
            }
        }

        /// <summary>
        /// Forwards attacks to the central jail-guard assault coordinator. That coordinator owns lockdown,
        /// custody transfer, and duplicate suppression for both runtime targets.
        /// </summary>
        /// <param name="attacker">Player who attacked this guard.</param>
        public override void OnAttackedByPlayer(Player attacker)
        {
            base.OnAttackedByPlayer(attacker);

            if (attacker == null) return;

            ModLogger.Info($"Guard {badgeNumber}: Attacked by player {attacker.name}");

            // The central manager owns lockdown state, custody transfer, and duplicate
            // suppression. This callback can precede the health postfix on some runtimes.
            Harmony.HarmonyPatches.TryBeginJailGuardAssault(this, attacker);
        }

        /// <summary>
        /// Legacy duplicate assault path retained for compatibility. The active attack entry point is
        /// <see cref="OnAttackedByPlayer"/> and the central assault coordinator; this method is not the parity path.
        /// </summary>
        /// <param name="attacker">Player who attacked this guard.</param>
        private void HandlePlayerAttack(Player attacker)
        {
            // Stop current activity
            StopMovement();

            // Send warning message with voice command
            TrySendNPCMessage("You just assaulted a correctional officer! You're under arrest!", 4f);

            // Play arrest command with voice
            if (dialogueController != null)
            {
                dialogueController.SendGuardCommand(JailNPCAudioController.GuardCommandType.Stop,
                    "You're under arrest!", true);
            }

            // Initiate arrest procedure
            try
            {
                Harmony.HarmonyPatches.TryBeginJailGuardAssault(this, attacker);

                // If intake officer, interrupt intake process
                if (role == GuardRole.IntakeOfficer && intakeStateMachine != null)
                {
                    intakeStateMachine.StopIntakeProcess();
                    ModLogger.Info($"Intake Officer {badgeNumber}: Intake process interrupted due to attack");
                }

                // Return to alert state
                currentActivity = GuardActivity.RespondingToIncident;
                guardPatience = 0f; // No patience left
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Guard {badgeNumber}: Error handling player attack: {ex.Message}");
            }
        }

        #endregion

        #region Debug and Visualization

        /// <summary>Draws base destination/state gizmos plus guard activity, prisoner, and patrol overlays.</summary>
        protected override void OnDrawGizmos()
        {
            base.OnDrawGizmos();

            // Draw activity indicator
            Gizmos.color = GetActivityColor(currentActivity);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 2.5f, 0.2f);

            // Draw prisoner connection if escorting
            if (currentPrisoner != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(transform.position, currentPrisoner.transform.position);
            }

            // Draw patrol points if patrolling
            if (currentActivity == GuardActivity.Patrolling && availablePatrolPoints.Count > 0)
            {
                Gizmos.color = Color.blue;
                for (int i = 0; i < availablePatrolPoints.Count; i++)
                {
                    var point = availablePatrolPoints[i];
                    if (point != null)
                    {
                        Gizmos.DrawWireCube(point.position, Vector3.one * 0.3f);

                        if (i == currentPatrolIndex)
                        {
                            Gizmos.color = Color.green;
                            Gizmos.DrawLine(transform.position, point.position);
                            Gizmos.color = Color.blue;
                        }
                    }
                }
            }
        }

        private Color GetActivityColor(GuardActivity activity)
        {
            switch (activity)
            {
                case GuardActivity.Idle: return Color.white;
                case GuardActivity.Patrolling: return Color.blue;
                case GuardActivity.ProcessingIntake: return Color.green;
                case GuardActivity.EscortingPrisoner: return Color.yellow;
                case GuardActivity.MonitoringArea: return Color.cyan;
                case GuardActivity.RespondingToIncident: return Color.red;
                default: return Color.gray;
            }
        }

        #endregion
    }
}
