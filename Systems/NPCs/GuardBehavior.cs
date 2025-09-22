using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;

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
            Booking1       // Booking spawn point 1
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

        #endregion

        #region Guard Properties

        public GuardRole role = GuardRole.GuardRoomStationary;
        public GuardAssignment assignment;
        public string badgeNumber = "";
        public int experienceLevel = 1;
        public PatrolRoute patrolRoute = new PatrolRoute();
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

        #endregion

        #region Intake Officer State

        // Intake processing
        private Player currentPrisoner;
        private Dictionary<string, IntakeStationInfo> intakeStations;
        private HashSet<string> completedStations = new HashSet<string>();
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
            doorBehavior = GetComponent<SecurityDoorBehavior>();
            if (doorBehavior == null)
            {
                doorBehavior = gameObject.AddComponent<SecurityDoorBehavior>();
            }

            if (string.IsNullOrEmpty(badgeNumber))
            {
                badgeNumber = GenerateBadgeNumber();
            }

            InitializePatrolPoints();
            InitializeIntakeStations();
            SetupGuardRole();

            // Register with PrisonNPCManager
            if (PrisonNPCManager.Instance != null)
            {
                PrisonNPCManager.Instance.RegisterGuard(this);
            }

            shiftStartTime = Time.time;
            ModLogger.Info($"GuardBehavior initialized: {role} guard {badgeNumber} at {assignment}");
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
                audioController = GetComponent<JailNPCAudioController>();
                if (audioController == null)
                {
                    ModLogger.Warn($"Guard {badgeNumber}: No JailNPCAudioController found");
                }

                // Get dialogue controller
                dialogueController = GetComponent<JailNPCDialogueController>();
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
            if (!patrolInitialized || availablePatrolPoints.Count == 0) return;

            if (Time.time - lastPatrolTime >= patrolRoute.waitTime)
            {
                MoveToNextPatrolPoint();
            }
        }

        public void StartPatrol()
        {
            if (availablePatrolPoints.Count == 0) return;

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
            if (availablePatrolPoints.Count == 0) return;

            var targetPoint = availablePatrolPoints[currentPatrolIndex];
            MoveTo(targetPoint.position);

            currentPatrolIndex = (currentPatrolIndex + 1) % availablePatrolPoints.Count;
            lastPatrolTime = Time.time;

            ModLogger.Debug($"Guard {badgeNumber} patrolling to point {currentPatrolIndex}");
        }

        public void AssignPatrolRoute(Vector3[] points)
        {
            patrolRoute.points = points;
            if (currentActivity == GuardActivity.Patrolling)
            {
                StartPatrol();
            }
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
                intakeStateMachine = GetComponent<IntakeOfficerStateMachine>();
                if (intakeStateMachine == null)
                {
                    intakeStateMachine = gameObject.AddComponent<IntakeOfficerStateMachine>();
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
            // Basic area monitoring - can be expanded
            if (role == GuardRole.IntakeOfficer)
            {
                CheckForNewArrivals();
            }
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

        private void CheckForNewArrivals()
        {
            // This would integrate with the booking system to detect new prisoners
            // For now, it's a placeholder for future expansion
        }

        #endregion

        #region Door Integration

        private void OnTriggerEnter(Collider other)
        {
            // Handle door triggers - delegate to intake state machine if processing intake
            var doorTrigger = other.GetComponent<DoorTriggerHandler>();
            if (doorTrigger != null && doorBehavior != null)
            {
                if (role == GuardRole.IntakeOfficer && intakeStateMachine != null && intakeStateMachine.IsProcessingIntake())
                {
                    // Let intake state machine handle door triggers during intake
                    intakeStateMachine.HandleDoorTrigger(other.name);
                }
                else
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
        public bool IsProcessingIntake() => intakeStateMachine != null ? intakeStateMachine.IsProcessingIntake() : isProcessingIntake;
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

            // Guards have zero tolerance for being attacked
            HandlePlayerAttack(attacker);
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
                // Use the jail system to arrest the player
                var jailSystem = Behind_Bars.Core.Instance?.JailSystem;
                if (jailSystem != null)
                {
                    // Trigger immediate arrest for assault
                    ModLogger.Info($"Guard {badgeNumber}: Initiating immediate arrest for assault by {attacker.name}");

                    // Use the immediate arrest system
                    MelonCoroutines.Start(jailSystem.HandleImmediateArrest(attacker));

                    ModLogger.Info($"Guard {badgeNumber}: Player {attacker.name} arrested for assault on officer");
                }
                else
                {
                    ModLogger.Error($"Guard {badgeNumber}: Could not access jail system for arrest");
                }

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