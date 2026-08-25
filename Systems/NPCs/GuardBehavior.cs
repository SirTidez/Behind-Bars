using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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

        public enum GuardRole
        {
            GuardRoomStationary,    // Guards stationed in guard room
            BookingStationary,      // Guards stationed in booking area
            IntakeOfficer,          // Dedicated intake processing guard
            PatrolGuard,            // Guards doing patrol routes
            ResponseGuard           // Responds to incidents
        }

        public enum GuardAssignment
        {
            GuardRoom0,    // Guard room spawn point 0
            GuardRoom1,    // Guard room spawn point 1
            Booking0,      // Booking spawn point 0 (usually intake officer)
            Booking1,      // Booking spawn point 1
            DayRoomPatrol  // Dedicated officer for the cell-block/day-room circuit
        }

        public enum GuardActivity
        {
            Idle,
            Patrolling,
            ProcessingIntake,
            EscortingPrisoner,
            MonitoringArea,
            RespondingToIncident
        }

#if MONO
        [System.Serializable]
        public class PatrolRoute
        {
            public Vector3[] points;
            public float speed = 2.5f;
            public float waitTime = 3f;
            public bool isActive = true;
        }

        [System.Serializable]
        public class IntakeStationInfo
        {
            public string stationName;
            public Transform stationTransform;
            public Transform guardPoint;
            public bool requiresPrisoner = true;
            public float processingTime = 5f;
        }
#endif

        #endregion

        #region Guard Properties

        public GuardRole role = GuardRole.GuardRoomStationary;
        public GuardAssignment assignment;
        public string badgeNumber = "";
        public int experienceLevel = 1;
#if MONO
        public PatrolRoute patrolRoute = new PatrolRoute();
#else
        private float patrolWaitTime = 3f;
        private Vector3[] patrolRoutePoints = Array.Empty<Vector3>();
#endif
        public float shiftStartTime = 0f;
        public float shiftDuration = 480f; // 8 minutes default

        // Runtime state
        private GuardActivity currentActivity = GuardActivity.Idle;
        private SecurityDoorBehavior doorBehavior;
        private JailNPCAudioController audioController;
        private JailNPCDialogueController dialogueController;
        private Transform assignedSpawnPoint;
        private int currentPatrolIndex = 0;
        private float lastPatrolTime = 0f;
        private bool isOnDuty = true;
        private Vector3 dayRoomInspectionTarget;
        private bool hasDayRoomInspectionTarget;
        private Vector3[] dayRoomInspectionTargets = Array.Empty<Vector3>();
        private bool dayRoomPatrolBatonEquipped;
        private bool dayRoomNativeMovementLogged;

        // Deliberately below the game's ordinary walking pace so the day-room guard
        // reads as an observant patrol rather than a response/escort movement.
        private const float DayRoomPatrolSpeed = 0.25f;
        private const int DayRoomPatrolSpeedPriority = 100;
        private const string DayRoomPatrolSpeedControlId = "BehindBars.DayRoomPatrol";
        private const float DayRoomPatrolWaitTime = 2.5f;
        private const float DayRoomLookTurnSpeed = 360f;
        private const string DayRoomPatrolBatonResourcePath = "Avatar/Equippables/Baton";
        private const string EmptyEquippableResourcePath = "";

        #endregion

        #region Intake Officer State

        // Intake processing
        private Player currentPrisoner;
#if MONO
        private Dictionary<string, IntakeStationInfo> intakeStations;
        private HashSet<string> completedStations = new HashSet<string>();
#endif
        private string currentTargetStation = "";
        private bool isProcessingIntake = false;

        // Prisoner compliance system
        private float guardPatience = 100f;
        private float lastComplianceWarningTime = 0f;
        private int complianceViolationCount = 0;
        private Vector3 lastKnownPrisonerPosition;

        // Compliance constants
        private const float COMPLIANCE_PERFECT = 2f;      // 0-2m: Perfect compliance
        private const float COMPLIANCE_WARNING = 3f;      // 2-3m: Warning zone
        private const float COMPLIANCE_VIOLATION = 5f;    // 3-5m: Active intervention
        private const float COMPLIANCE_ESCAPE = 8f;       // 5m+: Escape attempt
        private const float PATIENCE_LOSS_RATE = 2f;
        private const float PATIENCE_GAIN_RATE = 3f;
        private const float WARNING_COOLDOWN = 5f;

        #endregion

        #region Patrol System

        private List<Transform> availablePatrolPoints = new List<Transform>();
        private bool patrolInitialized = false;

        #endregion

        #region Initialization

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

        protected override void OnDestroy()
        {
            // Guard registration outlives Unity object destruction unless it is removed
            // explicitly. Leaving the stale behavior in the manager can make later scene
            // sessions pick a destroyed guard for an escort or lockdown response.
            Core.Instance?.NpcManager?.UnregisterGuard(this);
            base.OnDestroy();
        }

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
        /// Initialize audio and dialogue components for voice commands
        /// </summary>
        private void InitializeAudioComponents()
        {
            try
            {
                // Get audio controller (should be added by DirectNPCBuilder)
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

        private string GenerateBadgeNumber()
        {
            return $"G{UnityEngine.Random.Range(1000, 9999)}";
        }

        private void SetAssignedSpawnPoint()
        {
            assignedSpawnPoint = FindSpawnPoint(assignment.ToString());
        }

        #endregion

        #region State Management (Override BaseJailNPC)

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

        protected override void HandleMovingState()
        {
            base.HandleMovingState();

            // Check for prisoner compliance if escorting
            if (currentActivity == GuardActivity.EscortingPrisoner && currentPrisoner != null)
            {
                CheckPrisonerCompliance();
            }
        }

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

        private void HandlePatrolLogic()
        {
            if (!patrolInitialized || GetPatrolPointCount() == 0) return;

            float waitTime =
#if MONO
                patrolRoute.waitTime;
#else
                patrolWaitTime;
#endif

            if (Time.time - lastPatrolTime >= waitTime)
            {
                MoveToNextPatrolPoint();
            }
        }

        public void StartPatrol()
        {
            if (GetPatrolPointCount() == 0) return;

            EnsureDayRoomPatrolBaton();
            currentActivity = GuardActivity.Patrolling;
            currentPatrolIndex = 0;

            // Play patrol start announcement
            if (dialogueController != null)
            {
                dialogueController.SendGuardCommand(JailNPCAudioController.GuardCommandType.CellCheck,
                    "Beginning patrol.", true);
            }

            MoveToNextPatrolPoint();
        }

        private void MoveToNextPatrolPoint()
        {
            int patrolPointCount = GetPatrolPointCount();
            if (patrolPointCount == 0) return;

            int targetIndex = currentPatrolIndex;
            Vector3 targetPosition = GetPatrolPointPosition(targetIndex);
            SetDayRoomInspectionTarget(targetPosition, targetIndex);
            ApplyDayRoomPatrolSpeedControl();
            MoveTo(targetPosition);

            currentPatrolIndex = (currentPatrolIndex + 1) % patrolPointCount;
            lastPatrolTime = Time.time;

            ModLogger.Debug($"Guard {badgeNumber} patrolling to point {currentPatrolIndex}");
        }

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
        public NPC GetNativeNpc()
        {
            return npcComponent;
        }

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
        public void AssignDayRoomPatrolRoute(Vector3[] points, Vector3[] inspectionTargets)
        {
            dayRoomInspectionTargets = inspectionTargets?.ToArray() ?? Array.Empty<Vector3>();
            AssignPatrolRoute(points);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
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

        private IntakeOfficerStateMachine intakeStateMachine;

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

        /// <summary>
        /// Check if intake processing is active (delegates to state machine)
        /// </summary>
        public bool IsIntakeProcessingActive()
        {
            return intakeStateMachine != null && intakeStateMachine.IsProcessingIntake();
        }

        /// <summary>
        /// Handle door triggers during intake escort (delegates to state machine)
        /// </summary>
        public void HandleIntakeDoorTrigger(string triggerName)
        {
            if (intakeStateMachine != null && role == GuardRole.IntakeOfficer)
            {
                intakeStateMachine.HandleDoorTrigger(triggerName);
            }
        }

        #endregion

        #region Prisoner Compliance

        private void CheckPrisonerCompliance()
        {
            if (currentPrisoner == null) return;

            float distance = Vector3.Distance(transform.position, currentPrisoner.transform.position);
            UpdatePrisonerCompliance(distance);
        }

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

        private void HandleMonitoringLogic()
        {
            // Intake orchestration is owned by IntakeOfficerStateMachine/BookingProcess.
            // Monitoring guards deliberately have no independent arrival polling path.
        }

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

        private Transform FindStationTransform(string stationName)
        {
            var jailController = Core.JailController;
            if (jailController == null) return null;

            Transform[] allTransforms = jailController.GetComponentsInChildren<Transform>();
            return allTransforms.FirstOrDefault(t =>
                t.name.Contains(stationName, StringComparison.OrdinalIgnoreCase));
        }

        private Transform FindGuardPoint(string stationName)
        {
            return FindStationTransform($"GuardPoint_{stationName}") ??
                   FindStationTransform($"{stationName}_GuardPoint");
        }

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

        public GuardRole GetRole() => role;
        public GuardAssignment GetAssignment() => assignment;
        public GuardActivity GetCurrentActivity() => currentActivity;
        public string GetBadgeNumber() => badgeNumber;
        public bool IsOnDuty() => isOnDuty;
        public bool IsProcessingIntake()
        {
            return intakeStateMachine != null ? intakeStateMachine.IsProcessingIntake() : isProcessingIntake;
        }
        public Player GetCurrentPrisoner() => currentPrisoner;
        public float GetGuardPatience() => guardPatience;

        public void SetOnDuty(bool onDuty)
        {
            isOnDuty = onDuty;
            if (!onDuty)
            {
                StopMovement();
                currentActivity = GuardActivity.Idle;
            }
        }

        public void AssignToRole(GuardRole newRole)
        {
            role = newRole;
            SetupGuardRole();
        }

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
        /// Override BaseJailNPC attack handling for guard-specific responses
        /// </summary>
        public override void OnAttackedByPlayer(Player attacker)
        {
            base.OnAttackedByPlayer(attacker);

            if (attacker == null) return;

            ModLogger.Info($"Guard {badgeNumber}: Attacked by player {attacker.name}");

            // The central manager owns lockdown state, custody transfer, and duplicate
            // suppression. This callback can precede the health postfix on some runtimes.
            Harmony.HarmonyPatches.TryBeginJailGuardAssault(this, attacker);
        }

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
