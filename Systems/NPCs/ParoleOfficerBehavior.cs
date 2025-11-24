using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Jail;
using Behind_Bars.UI;
using ScheduleOne.VoiceOver;

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
        private StationaryBehavior stationaryBehavior;
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
            ModLogger.Debug($"ParoleOfficerBehavior initialized: {role} officer {badgeNumber} at {assignment}");
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
                else
                {
                    // Initialize parole-specific dialogue states for supervising officer
                    if (role == ParoleOfficerRole.SupervisingOfficer)
                    {
                        InitializeParoleDialogueStates();
                    }
                }

                ModLogger.Debug($"Guard {badgeNumber}: Audio components initialized");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing audio components for guard {badgeNumber}: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize stationary behavior for supervising officer
        /// </summary>
        private void InitializeStationaryBehavior()
        {
            try
            {
                // Get or add StationaryBehavior component
                stationaryBehavior = GetComponent<StationaryBehavior>();
                if (stationaryBehavior == null)
                {
                    stationaryBehavior = gameObject.AddComponent<StationaryBehavior>();
                }

                // Set stationary position to police station entrance (first waypoint of PoliceStation route)
                var policeStationRoute = PresetParoleOfficerRoutes.GetRoute("PoliceStation");
                if (policeStationRoute != null && policeStationRoute.points != null && policeStationRoute.points.Length > 0)
                {
                    Vector3 entrancePosition = policeStationRoute.points[0];
                    stationaryBehavior.SetStationaryPosition(entrancePosition);
                    ModLogger.Debug($"Supervising Officer {badgeNumber}: Set stationary position to police station entrance: {entrancePosition}");
                }
                else
                {
                    // Fallback to current position
                    stationaryBehavior.SetStationaryPosition(transform.position);
                    ModLogger.Warn($"Supervising Officer {badgeNumber}: Could not find PoliceStation route, using current position as stationary");
                }

                // Initialize check-in system for supervising officer
                if (checkInSystem == null)
                {
                    checkInSystem = GetComponent<ParoleCheckInSystem>();
                    if (checkInSystem == null)
                    {
                        checkInSystem = gameObject.AddComponent<ParoleCheckInSystem>();
                        ModLogger.Debug($"Supervising Officer {badgeNumber}: Added ParoleCheckInSystem component");
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing stationary behavior for supervising officer {badgeNumber}: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize parole-specific dialogue states for supervising officer
        /// </summary>
        private void InitializeParoleDialogueStates()
        {
            if (dialogueController == null) return;

            try
            {
                // Parole intake states
                dialogueController.AddStateDialogue("Idle", "Standing by for parole intake.",
                    new[] { "Waiting for parolees.", "On duty.", "Ready for processing." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("DetectingParolee", "I see you're starting parole.",
                    new[] { "Welcome to parole supervision.", "Let's get you processed." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("GreetingParolee", "Welcome. I'm your supervising officer. Let's get you processed.",
                    new[] { "Follow me.", "This way.", "Let's begin." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("ReviewingConditions", "Let me review your parole conditions with you.",
                    new[] { "Here are your conditions.", "Pay attention.", "These are important." }, true, EVOLineType.Command);

                dialogueController.AddStateDialogue("IssuingParoleCard", "Here's your parole card. Keep it with you at all times.",
                    new[] { "Don't lose this.", "Keep it safe.", "You'll need this." }, true, EVOLineType.Command);

                dialogueController.AddStateDialogue("FinalizingIntake", "You're all set. Remember to check in regularly.",
                    new[] { "Stay compliant.", "See you at check-ins.", "Good luck." }, true, EVOLineType.Greeting);

                // Check-in states
                dialogueController.AddStateDialogue("CheckInGreeting", "Good to see you. Let's do your check-in.",
                    new[] { "Time for your check-in.", "Let's review your status." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInReviewing", "Let me review your compliance record.",
                    new[] { "Checking your record.", "Reviewing your status." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInCompliant", "You're doing well. Keep it up.",
                    new[] { "Good job.", "Stay compliant.", "Keep up the good work." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInWarning", "I've noticed some concerns. Stay compliant.",
                    new[] { "Be careful.", "Don't slip up.", "Watch yourself." }, true, EVOLineType.Angry);

                dialogueController.AddStateDialogue("CheckInComplete", "Check-in complete. See you next time.",
                    new[] { "Until next time.", "Stay out of trouble." }, true, EVOLineType.Acknowledge);

                // Violation states
                dialogueController.AddStateDialogue("ViolationDetected", "I need to speak with you about a violation.",
                    new[] { "We have a problem.", "This is serious." }, true, EVOLineType.Alerted);

                dialogueController.AddStateDialogue("ViolationExplaining", "You violated your parole conditions.",
                    new[] { "This is unacceptable.", "You know the rules." }, true, EVOLineType.Angry);

                dialogueController.AddStateDialogue("ViolationWarning", "This is a warning. Don't let it happen again.",
                    new[] { "One more strike.", "Be careful." }, true, EVOLineType.Angry);

                dialogueController.AddStateDialogue("ViolationEscalating", "This is serious. Your parole may be revoked.",
                    new[] { "This is your last chance.", "One more violation and you're done." }, true, EVOLineType.Angry);

                dialogueController.AddStateDialogue("ViolationComplete", "Violation recorded. Stay compliant.",
                    new[] { "Don't let it happen again.", "Watch yourself." }, true, EVOLineType.Command);

                // Conditions review states
                dialogueController.AddStateDialogue("ConditionsRequest", "You want to review your conditions?",
                    new[] { "Sure, let's go over them.", "Of course." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("ConditionsExplaining", "Here are your parole conditions...",
                    new[] { "Pay attention.", "These are important." }, true, EVOLineType.Command);

                dialogueController.AddStateDialogue("ConditionsComplete", "Any questions about your conditions?",
                    new[] { "Need clarification?", "Understood?" }, true, EVOLineType.Greeting);

                ModLogger.Debug($"Supervising Officer {badgeNumber}: Initialized parole dialogue states");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing parole dialogue states for supervising officer {badgeNumber}: {e.Message}");
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
                    // Initialize stationary behavior for supervising officer
                    InitializeStationaryBehavior();
                    ChangeParoleActivity(ParoleOfficerActivity.MonitoringArea);
                    ModLogger.Debug($"Guard {badgeNumber} set as supervising officer at {assignment}");
                    break;
                case ParoleOfficerRole.PatrolOfficer:
                    ChangeParoleActivity(ParoleOfficerActivity.Patrolling);
                    string routeName = AssignmentToRouteMap.ContainsKey(assignment) ? AssignmentToRouteMap[assignment] : "unknown";
                    ModLogger.Debug($"Guard {badgeNumber} assigned to patrol {assignment} on route {routeName}");
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
                        ModLogger.Debug($"Guard {badgeNumber} assigned to patrol route: {routeName} with {presetRoute.points.Length} waypoints");
                        
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

                    // CRITICAL: Freeze player movement immediately when search is initiated
                    try
                    {
#if MONO
                        var playerMovement = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                        if (playerMovement != null)
                        {
                            playerMovement.CanMove = false;
                            ModLogger.Debug($"Froze player {player.name} movement immediately for parole search");
                        }
#else
                        var playerMovement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                        if (playerMovement != null)
                        {
                            playerMovement.canMove = false;
                            ModLogger.Debug($"Froze player {player.name} movement immediately for parole search");
                        }
#endif
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Error freezing player movement immediately: {ex.Message}");
                    }

                    // Set search activity (DO NOT call StopMovement() here - we need the officer to walk to the player)
                    // The search coroutine will handle movement to the player
                    ChangeParoleActivity(ParoleOfficerActivity.SearchingParolee);
                    currentParolee = player;

                    // Show initial search notification
                    ShowSearchNotification("Parole compliance check - stay where you are", false);

                    // Start search coroutine (which will handle movement to player and restore movement when done)
                    MelonCoroutines.Start(ParoleSearchSystem.Instance.PerformParoleSearch(this, player));

                    // Only search one player at a time
                    break;
                }
            }
        }

        #endregion

        #region Parole Intake Logic

        private ParoleIntakeStateMachine paroleIntakeStateMachine;

        /// <summary>
        /// Start parole intake process for a parolee
        /// </summary>
        public void HandleParoleIntake(Player parolee)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not a supervising officer");
                return;
            }

            // Initialize parole intake state machine if not already present
            if (paroleIntakeStateMachine == null)
            {
                paroleIntakeStateMachine = GetComponent<ParoleIntakeStateMachine>();
                if (paroleIntakeStateMachine == null)
                {
                    paroleIntakeStateMachine = gameObject.AddComponent<ParoleIntakeStateMachine>();
                }
            }

            // Delegate to parole intake state machine
            if (paroleIntakeStateMachine != null)
            {
                paroleIntakeStateMachine.StartParoleIntake(parolee);
                isProcessingIntake = true;
                ChangeParoleActivity(ParoleOfficerActivity.ProcessingIntake);
                currentParolee = parolee;
                ModLogger.Info($"Supervising Officer {badgeNumber} starting parole intake for {parolee.name}");
            }
            else
            {
                ModLogger.Error($"Failed to create ParoleIntakeStateMachine for supervising officer {badgeNumber}");
            }
        }

        /// <summary>
        /// Start intake process (legacy method - redirects to HandleParoleIntake)
        /// </summary>
        public void StartIntakeProcess(Player parolee)
        {
            HandleParoleIntake(parolee);
        }

        /// <summary>
        /// Check if intake processing is active (delegates to state machine)
        /// </summary>
        public bool IsIntakeProcessingActive()
        {
            return paroleIntakeStateMachine != null && paroleIntakeStateMachine.IsProcessingIntake();
        }

        #endregion

        #region Parolee Compliance

        private void CheckParoleeCompliance()
        {
            if (currentParolee == null) return;

            float distance = Vector3.Distance(transform.position, currentParolee.transform.position);
            UpdateParoleeCompliance(distance);
        }

        /// <summary>
        /// Get compliance score for a parolee
        /// </summary>
        public float GetComplianceScore(Player parolee)
        {
            if (parolee == null) return 0f;

            var rapSheet = RapSheetManager.Instance.GetRapSheet(parolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                return rapSheet.CurrentParoleRecord.GetComplianceScore();
            }

            return 0f;
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

            ModLogger.Debug($"ParoleOfficer {badgeNumber}: {oldActivity} → {newActivity}");

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
            // Don't show notifications for intake processing - ParoleIntakeStateMachine handles those
            if (activity == ParoleOfficerActivity.ProcessingIntake && paroleIntakeStateMachine != null)
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

        #region Supervising Officer Methods

        private ParoleCheckInSystem checkInSystem;

        /// <summary>
        /// Handle check-in for a parolee
        /// </summary>
        public void HandleCheckIn(Player parolee)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not a supervising officer");
                return;
            }

            // Get or add check-in system
            if (checkInSystem == null)
            {
                checkInSystem = GetComponent<ParoleCheckInSystem>();
                if (checkInSystem == null)
                {
                    checkInSystem = gameObject.AddComponent<ParoleCheckInSystem>();
                }
            }

            if (checkInSystem != null)
            {
                checkInSystem.InitiateCheckIn(parolee);
                ModLogger.Info($"Supervising Officer {badgeNumber} handling check-in for {parolee.name}");
            }
        }

        /// <summary>
        /// Handle violation for a parolee
        /// </summary>
        public void HandleViolation(Player parolee, string violationType)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not a supervising officer");
                return;
            }

            if (parolee == null)
            {
                ModLogger.Warn("ParoleOfficerBehavior: Cannot handle violation, parolee is null");
                return;
            }

            ModLogger.Info($"Supervising Officer {badgeNumber} handling violation '{violationType}' for {parolee.name}");

            // Start violation dialogue
            StartCoroutine(ProcessViolation(parolee, violationType));
        }

        /// <summary>
        /// Process violation with dialogue
        /// </summary>
        private IEnumerator ProcessViolation(Player parolee, string violationType)
        {
            // Update dialogue to violation detected
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("ViolationDetected");
                dialogueController.SendContextualMessage("greeting");
            }

            // Face the parolee
            if (parolee != null)
            {
                LookAt(parolee.transform.position);
            }

            yield return new WaitForSeconds(2f);

            // Explain violation
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("ViolationExplaining");
                dialogueController.SendContextualMessage("interaction");
            }

            yield return new WaitForSeconds(2f);

            // Get parole record
            var rapSheet = RapSheetManager.Instance.GetRapSheet(parolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                var paroleRecord = rapSheet.CurrentParoleRecord;
                int violationCount = paroleRecord.GetViolationCount();
                float complianceScore = paroleRecord.GetComplianceScore();

                // Determine severity and response
                string responseState;
                if (violationCount >= 3 || complianceScore < 30f)
                {
                    responseState = "ViolationEscalating";
                }
                else
                {
                    responseState = "ViolationWarning";
                }

                // Update dialogue
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState(responseState);
                    dialogueController.SendContextualMessage("interaction");
                }

                // Adjust compliance score (violation already added to record by ParoleSystem)
                paroleRecord.AdjustComplianceScore(-10f); // Deduct 10 points for violation
                RapSheetManager.Instance.MarkRapSheetChanged(parolee);

                yield return new WaitForSeconds(2f);

                // Complete violation processing
                if (dialogueController != null)
                {
                    dialogueController.UpdateGreetingForState("ViolationComplete");
                }

                yield return new WaitForSeconds(2f);
            }

            // Return to idle
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("Idle");
            }

            // Return to entrance position if stationary
            if (stationaryBehavior != null)
            {
                stationaryBehavior.ReturnToPosition();
            }
        }

        /// <summary>
        /// Review conditions with a parolee
        /// </summary>
        public void ReviewConditions(Player parolee)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not a supervising officer");
                return;
            }

            if (parolee == null)
            {
                ModLogger.Warn("ParoleOfficerBehavior: Cannot review conditions, parolee is null");
                return;
            }

            ModLogger.Info($"Supervising Officer {badgeNumber} reviewing conditions with {parolee.name}");

            // Start conditions review dialogue
            StartCoroutine(ProcessConditionsReview(parolee));
        }

        /// <summary>
        /// Process conditions review with dialogue
        /// </summary>
        private IEnumerator ProcessConditionsReview(Player parolee)
        {
            // Update dialogue to conditions request
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("ConditionsRequest");
                dialogueController.SendContextualMessage("greeting");
            }

            // Face the parolee
            if (parolee != null)
            {
                LookAt(parolee.transform.position);
            }

            yield return new WaitForSeconds(2f);

            // Explain conditions
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("ConditionsExplaining");
                dialogueController.SendContextualMessage("interaction");
            }

            // Get conditions summary
            var rapSheet = RapSheetManager.Instance.GetRapSheet(parolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                string conditionsSummary = rapSheet.CurrentParoleRecord.GetConditionsSummary();
                ModLogger.Info($"Conditions for {parolee.name}: {conditionsSummary}");

                // Could display conditions in UI here if needed
            }

            yield return new WaitForSeconds(3f);

            // Complete conditions review
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("ConditionsComplete");
            }

            yield return new WaitForSeconds(2f);

            // Return to idle
            if (dialogueController != null)
            {
                dialogueController.UpdateGreetingForState("Idle");
            }

            // Return to entrance position if stationary
            if (stationaryBehavior != null)
            {
                stationaryBehavior.ReturnToPosition();
            }
        }

        #endregion

        #region Door Integration

        private void OnTriggerEnter(Collider other)
        {
            // Handle door triggers - delegate to intake state machine if processing intake
            var doorTrigger = other.GetComponent<DoorTriggerHandler>();
            if (doorTrigger != null && doorBehavior != null)
            {
                if (role == ParoleOfficerRole.SupervisingOfficer && paroleIntakeStateMachine != null && paroleIntakeStateMachine.IsProcessingIntake())
                {
                    // Parole intake doesn't typically need door handling, but handle if needed
                    // For now, use standard door behavior
                    bool escorting = currentActivity == ParoleOfficerActivity.EscortingParolee;
                    doorBehavior.HandleDoorTrigger(other.name, escorting, currentParolee);
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
        public bool IsProcessingIntake() => paroleIntakeStateMachine != null ? paroleIntakeStateMachine.IsProcessingIntake() : isProcessingIntake;
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
                if (role == ParoleOfficerRole.SupervisingOfficer && paroleIntakeStateMachine != null)
                {
                    paroleIntakeStateMachine.StopIntakeProcess();
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