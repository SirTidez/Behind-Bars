using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using Behind_Bars.UI;

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
            SupervisingOfficer,            // Dedicated supervisor for processing new parolees
            PatrolOfficer,            // Officers doing patrol routes
            RandomSearchOfficer       // Conducts random searches
        }

        public enum ParoleOfficerAssignment
        {
            PoliceStationSupervisor, // Police station supervising officer (stationary)
            PoliceStationPatrol,     // Police station patrol route officer
            UptownPatrol,            // Patrols uptown area
            WestsidePatrol,          // Patrols westside area
            DocksPatrol,             // Patrols docks area
            NorthtownPatrol          // Patrols northtown area
        }

        public enum ParoleOfficerActivity
        {
            Idle,
            Patrolling,
            ProcessingIntake,
            EscortingParolee,
            MonitoringArea,
            RespondingToIncident,
            SearchingParolee
        }

        [System.Serializable]
        public class PatrolRoute
        {
            public string routeName = "DefaultRoute";
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

        #region Parole Officer Properties

        public ParoleOfficerRole role = ParoleOfficerRole.PatrolOfficer;
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

        // Search system integration
        private float lastSearchCheckTime = 0f;
        private const float SEARCH_CHECK_INTERVAL = 5f; // Check for search opportunities every 5 seconds

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

        // Mapping between assignment and route names
        public static readonly Dictionary<ParoleOfficerAssignment, string> AssignmentToRouteMap = new Dictionary<ParoleOfficerAssignment, string>
        {
            { ParoleOfficerAssignment.PoliceStationSupervisor, null }, // Supervising officer, no route
            { ParoleOfficerAssignment.PoliceStationPatrol, "PoliceStation" }, // Police station patrol route
            { ParoleOfficerAssignment.UptownPatrol, "East" },
            { ParoleOfficerAssignment.WestsidePatrol, "West" },
            { ParoleOfficerAssignment.DocksPatrol, "Canal" },
            { ParoleOfficerAssignment.NorthtownPatrol, "North" }
        };

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
                case ParoleOfficerAssignment.PoliceStationSupervisor:
                    role = ParoleOfficerRole.SupervisingOfficer;
                    break;
                case ParoleOfficerAssignment.PoliceStationPatrol:
                    role = ParoleOfficerRole.PatrolOfficer;
                    break;
                case ParoleOfficerAssignment.UptownPatrol:
                    role = ParoleOfficerRole.PatrolOfficer;
                    break;
                case ParoleOfficerAssignment.WestsidePatrol:
                    role = ParoleOfficerRole.PatrolOfficer;
                    break;
                case ParoleOfficerAssignment.DocksPatrol:
                    role = ParoleOfficerRole.PatrolOfficer;
                    break;
                case ParoleOfficerAssignment.NorthtownPatrol:
                    role = ParoleOfficerRole.PatrolOfficer;
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
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.CellCheck, "Move along.");
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

                case ParoleOfficerActivity.SearchingParolee:
                    PlayGuardVoiceCommand(JailNPCAudioController.GuardCommandType.Stop, "Parole compliance check.");
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
                    // TODO: Implement police station enter/exit logic for supervising officer. Officer should remain at station entrance and handle intake processing.
                    ChangeParoleActivity(ParoleOfficerActivity.MonitoringArea);
                    ModLogger.Info($"Guard {badgeNumber} set as supervising officer at {assignment}");
                    break;
                case ParoleOfficerRole.PatrolOfficer:
                    ChangeParoleActivity(ParoleOfficerActivity.Patrolling);
                    string routeName = AssignmentToRouteMap.ContainsKey(assignment) ? AssignmentToRouteMap[assignment] : "unknown";
                    ModLogger.Info($"Guard {badgeNumber} assigned to patrol {assignment} on route {routeName}");
                    StartPatrol();
                    break;
                default:
                    ChangeParoleActivity(ParoleOfficerActivity.MonitoringArea);
                    break;
            }
        }

        private void InitializePatrolPoints()
        {
            availablePatrolPoints.Clear();

            // If this is a patrol officer, assign a route from PresetParoleOfficerRoutes
            if (role == ParoleOfficerRole.PatrolOfficer && AssignmentToRouteMap.ContainsKey(assignment))
            {
                string routeName = AssignmentToRouteMap[assignment];
                if (!string.IsNullOrEmpty(routeName))
                {
                    var presetRoute = PresetParoleOfficerRoutes.GetRoute(routeName);
                    if (presetRoute != null && presetRoute.points != null && presetRoute.points.Length > 0)
                    {
                        // Assign the preset route
                        patrolRoute = presetRoute;
                        ModLogger.Info($"Guard {badgeNumber} assigned to patrol route: {routeName} with {presetRoute.points.Length} waypoints");
                        
                        // Convert Vector3[] to Transform list for existing patrol logic
                        // Create temporary GameObjects with Transform components
                        foreach (var point in presetRoute.points)
                        {
                            GameObject tempPoint = new GameObject($"PatrolPoint_{availablePatrolPoints.Count}");
                            tempPoint.transform.position = point;
                            availablePatrolPoints.Add(tempPoint.transform);
                        }
                    }
                    else
                    {
                        ModLogger.Warn($"Guard {badgeNumber}: Route {routeName} not found or has no waypoints");
                    }
                }
            }
            else
            {
                // Fallback to jail controller patrol points for supervising officer or if no route assigned
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
                case ParoleOfficerActivity.SearchingParolee:
                    // Search processing is handled by coroutines
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

            // Update command notification during movement
            UpdateOfficerCommandNotification(currentActivity);
        }

        protected override void HandleWorkingState()
        {
            switch (currentActivity)
            {
                case ParoleOfficerActivity.ProcessingIntake:
                    // Intake processing is handled by coroutines
                    // Notifications are handled by IntakeOfficerStateMachine
                    break;
                // Escort the new parolee to the intake processing area
                case ParoleOfficerActivity.EscortingParolee:
                    HandleEscortLogic();
                    UpdateOfficerCommandNotification(currentActivity);
                    break;
            }
        }

        #endregion

        #region Patrol Logic

        private void HandlePatrolLogic()
        {
            if (!patrolInitialized || availablePatrolPoints.Count == 0) return;

            // Continue patrol movement
            if (Time.time - lastPatrolTime >= patrolRoute.waitTime)
            {
                MoveToNextPatrolPoint();
            }

            // Check for search opportunities while patrolling
            if (Time.time - lastSearchCheckTime >= SEARCH_CHECK_INTERVAL)
            {
                CheckForSearchOpportunities();
                lastSearchCheckTime = Time.time;
            }
        }

        public void StartPatrol()
        {
            // TODO: For officers spawned at police station entrance, add initial pathfinding to route start point before beginning patrol loop
            if (availablePatrolPoints.Count == 0) return;

            ChangeParoleActivity(ParoleOfficerActivity.Patrolling);
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

        /// <summary>
        /// Check if any nearby players should be searched
        /// Called periodically during patrol
        /// </summary>
        private void CheckForSearchOpportunities()
        {
            // Only patrol officers perform random searches
            if (role != ParoleOfficerRole.PatrolOfficer) return;

            // Get all players in range
            var players = GameObject.FindObjectsOfType<Player>();
            if (players == null || players.Length == 0) return;

            foreach (var player in players)
            {
                if (player == null) continue;

                // Check if search should be initiated
                if (ParoleSearchSystem.Instance.ShouldInitiateSearch(this, player))
                {
                    // Initiate search
                    ModLogger.Info($"Officer {badgeNumber}: Initiating random search on {player.name}");

                    // Stop patrol temporarily and set search activity
                    StopMovement();
                    ChangeParoleActivity(ParoleOfficerActivity.SearchingParolee);
                    currentParolee = player;

                    // Show initial search notification
                    ShowSearchNotification("Parole compliance check - stay where you are", false);

                    // Start search coroutine
                    MelonCoroutines.Start(ParoleSearchSystem.Instance.PerformParoleSearch(this, player));

                    // Only search one player at a time
                    break;
                }
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
                ChangeParoleActivity(ParoleOfficerActivity.ProcessingIntake);
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

        /// <summary>
        /// Change parole officer activity and update notifications
        /// </summary>
        private void ChangeParoleActivity(ParoleOfficerActivity newActivity)
        {
            if (currentActivity == newActivity) return;

            ParoleOfficerActivity oldActivity = currentActivity;
            currentActivity = newActivity;

            ModLogger.Info($"ParoleOfficer {badgeNumber}: {oldActivity} → {newActivity}");

            // Update officer command notification
            UpdateOfficerCommandNotification(newActivity);

            // Hide notification if activity doesn't require it
            if (!ShouldShowCommandNotification(newActivity))
            {
                HideOfficerCommandNotification();
            }
        }

        /// <summary>
        /// Update officer command notification based on current activity
        /// </summary>
        private void UpdateOfficerCommandNotification(ParoleOfficerActivity activity)
        {
            // Don't show notifications for intake processing - IntakeOfficerStateMachine handles those
            if (activity == ParoleOfficerActivity.ProcessingIntake && intakeStateMachine != null)
            {
                return;
            }

            if (!ShouldShowCommandNotification(activity))
            {
                return;
            }

            try
            {
                var commandData = GetCommandDataForActivity(activity);
                if (commandData != null)
                {
                    BehindBarsUIManager.Instance?.UpdateOfficerCommand(commandData);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error updating command notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Determine if this activity should display a command notification
        /// </summary>
        private bool ShouldShowCommandNotification(ParoleOfficerActivity activity)
        {
            return activity switch
            {
                ParoleOfficerActivity.EscortingParolee => true,
                ParoleOfficerActivity.SearchingParolee => true,
                _ => false
            };
        }

        /// <summary>
        /// Get command data for the current activity
        /// </summary>
        private OfficerCommandData? GetCommandDataForActivity(ParoleOfficerActivity activity)
        {
            bool isEscorting = IsCurrentlyEscortingParolee();

            return activity switch
            {
                ParoleOfficerActivity.EscortingParolee => new OfficerCommandData(
                    "PAROLE OFFICER",
                    isEscorting ? "Follow me" : "Stay close",
                    1, 1, isEscorting),

                ParoleOfficerActivity.SearchingParolee => new OfficerCommandData(
                    "PAROLE OFFICER",
                    GetCurrentSearchMessage(),
                    1, 1, false),

                _ => null
            };
        }

        /// <summary>
        /// Check if currently escorting a parolee (officer is moving)
        /// </summary>
        private bool IsCurrentlyEscortingParolee()
        {
            if (currentParolee == null) return false;
            if (currentState != NPCState.Moving) return false;

            // Check if officer is moving toward the parolee or a destination
            float distanceToParolee = Vector3.Distance(transform.position, currentParolee.transform.position);
            return distanceToParolee > 3f; // If more than 3 units away, show "Follow me"
        }

        /// <summary>
        /// Hide officer command notification
        /// </summary>
        private void HideOfficerCommandNotification()
        {
            try
            {
                BehindBarsUIManager.Instance?.HideOfficerCommand();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error hiding command notification: {ex.Message}");
            }
        }

        #region Search Notification Methods

        // Search state tracking
        private string currentSearchMessage = "";
        private bool searchInProgress = false;
        private bool searchContrabandFound = false;
        private int contrabandItemCount = 0;

        /// <summary>
        /// Show search notification - called when search starts
        /// </summary>
        public void ShowSearchNotification(string message, bool isSearching)
        {
            currentSearchMessage = message;
            searchInProgress = isSearching;
            
            try
            {
                var commandData = new OfficerCommandData(
                    "PAROLE OFFICER",
                    message,
                    1, 1, false);

                BehindBarsUIManager.Instance?.UpdateOfficerCommand(commandData);
                ModLogger.Debug($"ParoleOfficer {badgeNumber}: Showing search notification: {message}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error showing search notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Update search notification during search process
        /// </summary>
        public void UpdateSearchNotification(string message)
        {
            currentSearchMessage = message;
            searchInProgress = true;

            try
            {
                var commandData = new OfficerCommandData(
                    "PAROLE OFFICER",
                    message,
                    1, 1, false);

                BehindBarsUIManager.Instance?.UpdateOfficerCommand(commandData);
                ModLogger.Debug($"ParoleOfficer {badgeNumber}: Updating search notification: {message}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error updating search notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Show search results notification
        /// </summary>
        public void ShowSearchResults(bool contrabandFound, int itemCount = 0)
        {
            searchContrabandFound = contrabandFound;
            contrabandItemCount = itemCount;
            searchInProgress = false;

            string resultMessage;
            if (contrabandFound)
            {
                if (itemCount > 0)
                {
                    resultMessage = $"Contraband found! ({itemCount} item{(itemCount > 1 ? "s" : "")}) - Parole violation!";
                }
                else
                {
                    resultMessage = "Contraband found! - Parole violation!";
                }
            }
            else
            {
                resultMessage = "Search complete - you're clean";
            }

            currentSearchMessage = resultMessage;

            try
            {
                var commandData = new OfficerCommandData(
                    "PAROLE OFFICER",
                    resultMessage,
                    1, 1, false);

                BehindBarsUIManager.Instance?.UpdateOfficerCommand(commandData);
                ModLogger.Debug($"ParoleOfficer {badgeNumber}: Showing search results: {resultMessage}");

                // Hide notification after a delay
                MelonCoroutines.Start(HideSearchNotificationAfterDelay(contrabandFound ? 5f : 3f));
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error showing search results: {ex.Message}");
            }
        }

        /// <summary>
        /// Get current search message for notification
        /// </summary>
        private string GetCurrentSearchMessage()
        {
            if (!string.IsNullOrEmpty(currentSearchMessage))
            {
                return currentSearchMessage;
            }

            if (searchInProgress)
            {
                return "Searching inventory - don't move";
            }

            return "Parole compliance check";
        }

        /// <summary>
        /// Hide search notification after delay
        /// </summary>
        private IEnumerator HideSearchNotificationAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            
            // Only hide if we're still in search activity
            if (currentActivity == ParoleOfficerActivity.SearchingParolee)
            {
                ChangeParoleActivity(ParoleOfficerActivity.Patrolling);
                currentParolee = null;
                currentSearchMessage = "";
                searchInProgress = false;
                searchContrabandFound = false;
                contrabandItemCount = 0;
            }
        }

        #endregion

        private void HandleEscortLogic()
        {
            if (currentParolee == null)
            {
                ChangeParoleActivity(ParoleOfficerActivity.MonitoringArea);
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
                ChangeParoleActivity(ParoleOfficerActivity.Idle);
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
                ChangeParoleActivity(ParoleOfficerActivity.RespondingToIncident);
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
                ChangeParoleActivity(ParoleOfficerActivity.RespondingToIncident);
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
                case ParoleOfficerActivity.SearchingParolee: return Color.magenta;
                default: return Color.gray;
            }
        }

        #endregion
    }
}