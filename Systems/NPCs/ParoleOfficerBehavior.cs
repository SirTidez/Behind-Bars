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
    /// Consolidated parole officer behavior - replaces JailGuardBehavior, IntakeOfficerStateMachine, SmartEscortPath, PatrolSystem
    /// Inherits from BaseJailNPC for core functionality, uses SecurityDoorBehavior for door operations
    /// </summary>
    public class ParoleOfficerBehavior : BaseJailNPC
    {
#if !MONO
        public ParoleOfficerBehavior(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Parole Officer Configuration

        public enum ParoleOfficerRole
        {
            GuardRoomStationary,      // Guards stationed in guard room
            PoliceStationStationary,  // Guards stationed in booking area
            SupervisingOfficer,            // Dedicated supervisor for processing new parolees
            PatrolOfficer,            // Officers doing patrol routes
            ResponseOfficer,          // Responds to incidents
            RandomSearchOfficer       // Conducts random searches
        }

        public enum ParoleOfficerAssignment
        {
            PoliceStation0, // Police station spawn point 0 (usually supervising officer)
            DowntownPatrol, // Patrols downtown area
            UptownPatrol,   // Patrols uptown area
            WestsidePatrol,  // Patrols westside area
            DocksPatrol,     // Patrols docks area
            NorthtownPatrol,   // Patrols northtown area
            SuburbiaPatrol   // Patrols suburbia area
        }

        public enum ParoleOfficerActivity
        {
            Idle,
            Patrolling,
            ProcessingIntake,
            EscortingParolee,
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

        //[System.Serializable]
        //public class IntakeStationInfo
        //{
        //    public string stationName;
        //    public Transform stationTransform;
        //    public Transform guardPoint;
        //    public bool requiresPrisoner = true;
        //    public float processingTime = 5f;
        //}

        #endregion

        #region Guard Properties

        public ParoleOfficerRole role = ParoleOfficerRole.GuardRoomStationary;
        public ParoleOfficerAssignment assignment;
        public string badgeNumber = "";
        public int experienceLevel = 1;
        public PatrolRoute patrolRoute = new PatrolRoute();
        public float shiftStartTime = 0f;
        public float shiftDuration = 480f; // 8 minutes default

        // Runtime state
        private ParoleOfficerActivity currentActivity = ParoleOfficerActivity.Idle;
        private SecurityDoorBehavior doorBehavior;
        private JailNPCAudioController audioController;
        private JailNPCDialogueController dialogueController;
        private Transform assignedSpawnPoint;
        private int currentPatrolIndex = 0;
        private float lastPatrolTime = 0f;
        private bool isOnDuty = true;

        #endregion

        #region Supervising Officer State

        // Intake processing
        private Player currentParolee;
        //private Dictionary<string, IntakeStationInfo> intakeStations;
        //private HashSet<string> completedStations = new HashSet<string>();
        //private string currentTargetStation = "";
        private bool isProcessingIntake = false;

        // Parolee compliance system
        private float officerPatience = 100f;
        private float lastComplianceWarningTime = 0f;
        private int complianceViolationCount = 0;
        private Vector3 lastKnownParoleePosition;

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
            //InitializeIntakeStations();
            SetupOfficerRole();

            // Register with PrisonNPCManager
            if (PrisonNPCManager.Instance != null)
            {
                PrisonNPCManager.Instance.RegisterParoleOfficer(this);
            }

            shiftStartTime = Time.time;
            ModLogger.Info($"ParoleOfficerBehavior initialized: {role} officer {badgeNumber} at {assignment}");
        }

        public void Initialize(ParoleOfficerBehavior.ParoleOfficerAssignment guardAssignment, string badge = "")
        {
            assignment = guardAssignment;
            badgeNumber = string.IsNullOrEmpty(badge) ? GenerateBadgeNumber() : badge;

            // Set role based on assignment
            switch (assignment)
            {
                case ParoleOfficerAssignment.PoliceStation0:
                    role = ParoleOfficerRole.SupervisingOfficer;
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
                case ParoleOfficerActivity.Patrolling:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.CellCheck, "Cell check in progress.");
                    break;

                case ParoleOfficerActivity.ProcessingIntake:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Follow, "Follow me for processing.");
                    break;

                case ParoleOfficerActivity.EscortingParolee:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Move, "Keep moving.");
                    break;

                case ParoleOfficerActivity.RespondingToIncident:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Alert, "Responding to incident.");
                    break;

                case ParoleOfficerActivity.MonitoringArea:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.AllClear, "Area secure.");
                    break;

                default:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Greeting, "Guard on duty.");
                    break;
            }
        }

        private void SetupOfficerRole()
        {
            switch (role)
            {
                case ParoleOfficerRole.SupervisingOfficer:
                    currentActivity = ParoleOfficerActivity.MonitoringArea;
                    break;
                case ParoleOfficerRole.PatrolOfficer:
                    currentActivity = ParoleOfficerActivity.Patrolling;
                    StartPatrol();
                    break;
                default:
                    currentActivity = ParoleOfficerActivity.MonitoringArea;
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

        //private void InitializeIntakeStations()
        //{
        //    intakeStations = new Dictionary<string, IntakeStationInfo>();

        //    // Define standard intake stations
        //    var stationConfigs = new[]
        //    {
        //        new { name = "MugshotStation", processing = 5f },
        //        new { name = "ScannerStation", processing = 4f },
        //        new { name = "Storage", processing = 3f }
        //    };

        //    foreach (var config in stationConfigs)
        //    {
        //        var stationInfo = new IntakeStationInfo
        //        {
        //            stationName = config.name,
        //            processingTime = config.processing,
        //            stationTransform = FindStationTransform(config.name),
        //            guardPoint = FindGuardPoint(config.name)
        //        };

        //        intakeStations[config.name] = stationInfo;
        //    }
        //}

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
                case ParoleOfficerActivity.Patrolling:
                    HandlePatrolLogic();
                    break;
                case ParoleOfficerActivity.MonitoringArea:
                    HandleMonitoringLogic();
                    break;
                case ParoleOfficerActivity.ProcessingIntake:
                    // Intake processing is handled by coroutines
                    break;
            }
        }

        protected override void HandleMovingState()
        {
            base.HandleMovingState();

            // Check for parolee compliance if escorting
            if (currentActivity == ParoleOfficerActivity.EscortingParolee && currentParolee != null)
            {
                CheckParoleeCompliance();
            }
        }

        protected override void HandleWorkingState()
        {
            switch (currentActivity)
            {
                case ParoleOfficerActivity.ProcessingIntake:
                    // Intake processing is handled by coroutines
                    break;
                // Escort the new parolee to the intake processing area
                case ParoleOfficerActivity.EscortingParolee:
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

            currentActivity = ParoleOfficerActivity.Patrolling;
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
            if (currentActivity == ParoleOfficerActivity.Patrolling)
            {
                StartPatrol();
            }
        }

        #endregion

        #region Intake Officer Logic

        private IntakeOfficerStateMachine intakeStateMachine;

        public void StartIntakeProcess(Player parolee)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not a supervising officer");
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
                intakeStateMachine.ForceStartIntake(parolee);
                isProcessingIntake = true;
                currentActivity = ParoleOfficerActivity.ProcessingIntake;
                currentParolee = parolee;
                ModLogger.Info($"Guard {badgeNumber} delegating intake process to state machine for {parolee.name}");
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
            if (intakeStateMachine != null && role == ParoleOfficerRole.SupervisingOfficer)
            {
                intakeStateMachine.HandleDoorTrigger(triggerName);
            }
        }

        #endregion

        #region Parolee Compliance

        private void CheckParoleeCompliance()
        {
            if (currentParolee == null) return;

            float distance = Vector3.Distance(transform.position, currentParolee.transform.position);
            UpdateParoleeCompliance(distance);
        }

        private void UpdateParoleeCompliance(float distance)
        {
            bool isCompliant = distance <= COMPLIANCE_PERFECT;

            if (isCompliant)
            {
                // Gain patience when compliant
                officerPatience = Mathf.Min(100f, officerPatience + PATIENCE_GAIN_RATE * Time.deltaTime);
            }
            else
            {
                // Lose patience when non-compliant
                officerPatience = Mathf.Max(0f, officerPatience - PATIENCE_LOSS_RATE * Time.deltaTime);

                if (distance >= COMPLIANCE_WARNING && Time.time - lastComplianceWarningTime >= WARNING_COOLDOWN)
                {
                    HandleComplianceViolation(distance);
                    lastComplianceWarningTime = Time.time;
                }
            }

            // Store last known position
            lastKnownParoleePosition = currentParolee.transform.position;
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
            if (role == ParoleOfficerRole.SupervisingOfficer)
            {
                CheckForNewArrivals();
            }
        }

        private void HandleEscortLogic()
        {
            if (currentParolee == null)
            {
                currentActivity = ParoleOfficerActivity.MonitoringArea;
                ChangeState(NPCState.Idle);
                return;
            }

            CheckParoleeCompliance();
        }

        private void CheckForNewArrivals()
        {
            // This would integrate with the booking system to detect new parolees
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
                if (role == ParoleOfficerRole.SupervisingOfficer && intakeStateMachine != null && intakeStateMachine.IsProcessingIntake())
                {
                    // Let intake state machine handle door triggers during intake
                    intakeStateMachine.HandleDoorTrigger(other.name);
                }
                else
                {
                    // Standard door behavior for non-intake operations
                    bool escorting = currentActivity == ParoleOfficerActivity.EscortingParolee;
                    doorBehavior.HandleDoorTrigger(other.name, escorting, currentParolee);
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

        public ParoleOfficerRole GetRole() => role;
        public ParoleOfficerAssignment GetAssignment() => assignment;
        public ParoleOfficerActivity GetCurrentActivity() => currentActivity;
        public string GetBadgeNumber() => badgeNumber;
        public bool IsOnDuty() => isOnDuty;
        public bool IsProcessingIntake() => intakeStateMachine != null ? intakeStateMachine.IsProcessingIntake() : isProcessingIntake;
        public Player GetCurrentParolee() => currentParolee;
        public float GetOfficerPatience() => officerPatience;

        public void SetOnDuty(bool onDuty)
        {
            isOnDuty = onDuty;
            if (!onDuty)
            {
                StopMovement();
                currentActivity = ParoleOfficerActivity.Idle;
            }
        }

        public void AssignToRole(ParoleOfficerRole newRole)
        {
            role = newRole;
            SetupOfficerRole();
        }

        public void RespondToIncident(Vector3 location)
        {
            if (currentActivity != ParoleOfficerActivity.EscortingParolee) // Don't abandon escorting
            {
                currentActivity = ParoleOfficerActivity.RespondingToIncident;
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

                // If supervising officer, interrupt intake process
                if (role == ParoleOfficerRole.SupervisingOfficer && intakeStateMachine != null)
                {
                    intakeStateMachine.StopIntakeProcess();
                    ModLogger.Info($"Supervising Officer {badgeNumber}: Intake process interrupted due to attack");
                }

                // Return to alert state
                currentActivity = ParoleOfficerActivity.RespondingToIncident;
                officerPatience = 0f; // No patience left
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

            // Draw parolee connection if escorting
            if (currentParolee != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(transform.position, currentParolee.transform.position);
            }

            // Draw patrol points if patrolling
            if (currentActivity == ParoleOfficerActivity.Patrolling && availablePatrolPoints.Count > 0)
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

        private Color GetActivityColor(ParoleOfficerActivity activity)
        {
            switch (activity)
            {
                case ParoleOfficerActivity.Idle: return Color.white;
                case ParoleOfficerActivity.Patrolling: return Color.blue;
                case ParoleOfficerActivity.ProcessingIntake: return Color.green;
                case ParoleOfficerActivity.EscortingParolee: return Color.yellow;
                case ParoleOfficerActivity.MonitoringArea: return Color.cyan;
                case ParoleOfficerActivity.RespondingToIncident: return Color.red;
                default: return Color.gray;
            }
        }

        #endregion
    }
}