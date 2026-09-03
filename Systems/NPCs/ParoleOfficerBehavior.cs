using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.Parole;
using Behind_Bars.Systems.Parole.Conditions;
using Behind_Bars.UI;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.VoiceOver;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.VoiceOver;
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Consolidated parole officer behavior for patrol, escort, search, and
    /// incident response.  Supervising intake is delegated to the canonical
    /// ParoleIntakeStateMachine; this component mirrors its activity for shared
    /// officer/UI integration.
    /// Inherits from BaseJailNPC for core functionality, uses SecurityDoorBehavior for door operations
    /// </summary>
    public class ParoleOfficerBehavior : BaseJailNPC
    {
#if !MONO
        public ParoleOfficerBehavior(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Parole Officer Configuration

        /// <summary>Role of the officer, including the dedicated supervising role.</summary>
        public enum ParoleOfficerRole
        {
            SupervisingOfficer,            // Dedicated supervisor for processing new parolees
            PatrolOfficer,            // Officers doing patrol routes
            RandomSearchOfficer       // Conducts random searches
        }

        /// <summary>Roster assignment that determines route, role, and station.</summary>
        public enum ParoleOfficerAssignment
        {
            PoliceStationSupervisor, // Supervisor; exterior only for release, intake, and check-in work
            PoliceStationPatrol,     // Police station patrol route officer
            UptownPatrol,            // Patrols uptown area
            WestsidePatrol,          // Patrols westside area
            DocksPatrol,             // Patrols docks area
            NorthtownPatrol          // Patrols northtown area
        }

        /// <summary>Activity mirrored to dialogue, command notifications, and patrol logic.</summary>
        public enum ParoleOfficerActivity
        {
            Idle,
            Patrolling,
            ProcessingIntake,
            EscortingParolee,
            MonitoringArea,
            RespondingToIncident,
            SearchingParolee,
            ConductingHomeVisit
        }

        /// <summary>
        /// Serializable patrol route definition.  Points are world-space
        /// waypoints, while speed and wait time are runtime navigation values.
        /// </summary>
        [System.Serializable]
        public class PatrolRoute
        {
            /// <summary>Registry name used to resolve this route.</summary>
            public string routeName = "DefaultRoute";
            /// <summary>Ordered world-space patrol waypoints.</summary>
            public Vector3[] points;
            /// <summary>NavMesh movement speed in world units per second.</summary>
            public float speed = 2.5f;
            /// <summary>Real-time wait at each waypoint.</summary>
            public float waitTime = 3f;
            /// <summary>Whether patrol logic may use this route.</summary>
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

        /// <summary>Configured role used by intake, search, and incident routing.</summary>
        public ParoleOfficerRole role = ParoleOfficerRole.PatrolOfficer;
        /// <summary>Roster assignment used to select the route and spawn point.</summary>
        public ParoleOfficerAssignment assignment;
        /// <summary>Stable display/coordination identifier for this officer.</summary>
        public string badgeNumber = "";
        /// <summary>Reserved for future experience-based behavior tuning.</summary>
        public int experienceLevel = 1;
        /// <summary>Route selected for patrol officers; supervisors normally have no route.</summary>
        public PatrolRoute patrolRoute = new PatrolRoute();
        /// <summary>Initial Unity-time marker for this officer's schedule.</summary>
        public float shiftStartTime = 0f;
        /// <summary>Reserved shift-duration value in seconds; roster scheduling currently owns duty decisions.</summary>
        public float shiftDuration = 480f; // 8 minutes default

        // Runtime state: currentActivity is mirrored to command/dialogue surfaces;
        // the intake state machine owns the authoritative supervising workflow.
        private ParoleOfficerActivity currentActivity = ParoleOfficerActivity.Idle;
        private SecurityDoorBehavior doorBehavior;
        private JailNPCAudioController audioController;
        private JailNPCDialogueController dialogueController;
        private StationaryBehavior stationaryBehavior;
        private Transform assignedSpawnPoint;
        private int currentPatrolIndex = 0;
        private float lastPatrolTime = 0f;
        private bool isOnDuty = true;

        // Search system integration.  The patrol scheduler invokes searches only
        // when this officer is not consumed by intake or escort work.
        private float lastSearchCheckTime = 0f;
        private const float SEARCH_CHECK_INTERVAL = 5f; // Check for search opportunities every 5 seconds

        #endregion

        #region Supervising Officer State

        // Intake processing.  currentParolee is the exact player retained by the
        // canonical ParoleIntakeStateMachine; do not substitute a nearby player.
        private Player currentParolee;
        //private Dictionary<string, IntakeStationInfo> intakeStations;
        //private HashSet<string> completedStations = new HashSet<string>();
        //private string currentTargetStation = "";
        private bool isProcessingIntake = false;

        // Parolee compliance system.  Patience and warning counters are escort-
        // local state and are reset/escalated by the distance thresholds below.
        private float officerPatience = 100f;
        private float lastComplianceWarningTime = 0f;
        private int complianceViolationCount = 0;
        private Vector3 lastKnownParoleePosition;

        // Compliance constants
        private const float COMPLIANCE_PERFECT = 3f;      // 0-3m: target intake-escort distance
        private const float COMPLIANCE_WARNING = 3.5f;    // 3-3.5m: warning zone
        private const float COMPLIANCE_VIOLATION = 5f;    // 3.5-5m: active intervention
        private const float COMPLIANCE_ESCAPE = 8f;       // 5m+: Escape attempt
        private const float PATIENCE_LOSS_RATE = 2f;
        private const float PATIENCE_GAIN_RATE = 3f;
        private const float WARNING_COOLDOWN = 5f;

        #endregion

        #region Patrol System

        private List<Transform> availablePatrolPoints = new List<Transform>();
        private bool patrolInitialized = false;

        // Mapping between assignment and route names.  The supervisor deliberately
        // maps to null because its post is stationary rather than route-based.
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

        /// <summary>
        /// Resolves or creates the security-door seam, initializes route/audio
        /// helpers, and registers this officer with PrisonNPCManager.  If the
        /// injected door component cannot be resolved or created, later
        /// door-dependent paths remain unavailable; this method does not install
        /// a static-guard replacement.
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
            //InitializeIntakeStations();
            SetupOfficerRole();

            // Register with PrisonNPCManager
            var npcManager = Core.Instance?.NpcManager;
            if (npcManager != null)
            {
                npcManager.RegisterParoleOfficer(this);
            }

            shiftStartTime = Time.time;
            ModLogger.Debug($"ParoleOfficerBehavior initialized: {role} officer {badgeNumber} at {assignment}");
        }

        /// <summary>
        /// Removes the officer from PrisonNPCManager and delegates base look-
        /// controller cleanup before the native NPC object is destroyed.  It does
        /// not itself clear the intake state machine or coordinator session.
        /// </summary>
        protected override void OnDestroy()
        {
            // Dynamic parole officers are spawned/despawned with the Main scene. Pair their
            // registration so a stale Unity object cannot remain eligible for a later search,
            // check-in, or release-intake assignment.
            Core.Instance?.NpcManager?.UnregisterParoleOfficer(this);
            base.OnDestroy();
        }

        /// <summary>
        /// Applies an assignment and optional badge, then initializes role-specific
        /// patrol/interaction state.  The parameter name is retained for API
        /// compatibility even though it represents a parole-officer assignment.
        /// </summary>
        /// <param name="guardAssignment">Assignment used to select the officer role and route.</param>
        /// <param name="badge">Optional stable badge identifier; generated when empty.</param>
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
                stationaryBehavior = BBHelpers.GetComponentSafe<StationaryBehavior>(gameObject);
                if (stationaryBehavior == null)
                {
                    stationaryBehavior = BBHelpers.AddComponentSafe<StationaryBehavior>(gameObject);
                }

                Vector3 stationPosition = PresetParoleOfficerRoutes.GetSupervisingOfficerStation();
                stationaryBehavior.SetStationaryPosition(stationPosition);
                ModLogger.Debug($"Supervising Officer {badgeNumber}: Set stationary position to courthouse check-in post: {stationPosition}");
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

                // Rapport-tiered check-in greetings
                dialogueController.AddStateDialogue("CheckInGreetingHostile", "You again. Let's make this quick.",
                    new[] { "Don't waste my time.", "I've got my eye on you." }, true, EVOLineType.Angry);

                dialogueController.AddStateDialogue("CheckInGreetingFriendly", "Good to see you staying on track.",
                    new[] { "How's it going? Let's do your check-in.", "You're doing well. Quick check-in time." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInGreetingTrusted", "Hey, just the usual. You're doing well.",
                    new[] { "This should be quick. You've been great.", "Just a formality at this point." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInReviewing", "Let me review your compliance record.",
                    new[] { "Checking your record.", "Reviewing your status." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInCompliant", "You're doing well. Keep it up.",
                    new[] { "Good job.", "Stay compliant.", "Keep up the good work." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInWarning", "I've noticed some concerns. Stay compliant.",
                    new[] { "Be careful.", "Don't slip up.", "Watch yourself." }, true, EVOLineType.Angry);

                dialogueController.AddStateDialogue("CheckInTooEarly", "You're early. Return during your scheduled appointment window.",
                    new[] { "Come back during your assigned time.", "You're not on the clock yet." }, true, EVOLineType.Command);

                dialogueController.AddStateDialogue("CheckInMissedWindow", "You missed your appointment window.",
                    new[] { "You're out of compliance.", "A missed report will be recorded." }, true, EVOLineType.Angry);

                dialogueController.AddStateDialogue("CheckInNoSchedule", "You do not have an active check-in appointment right now.",
                    new[] { "Wait for your next check-in text.", "No appointment is active at this time." }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("CheckInComplete", "Check-in complete. See you next time.",
                    new[] { "Until next time.", "Stay out of trouble." }, true, EVOLineType.Acknowledge);

                // Drug test states
                dialogueController.AddStateDialogue("DrugTestAnnounce", "I need to conduct a random drug test.",
                    new[] { "Standard procedure. Let me check.", "Time for a drug screening." }, true, EVOLineType.Command);

                dialogueController.AddStateDialogue("DrugTestPass", "Test is clean. Good.",
                    new[] { "All clear.", "No issues detected." }, true, EVOLineType.Acknowledge);

                dialogueController.AddStateDialogue("DrugTestFail", "You tested positive. This is a serious violation.",
                    new[] { "This will be reported.", "You've violated your conditions." }, true, EVOLineType.Angry);

                // Employment check states
                dialogueController.AddStateDialogue("EmploymentCheck", "Let's review your employment status.",
                    new[] { "Are you maintaining employment?", "How's the job situation?" }, true, EVOLineType.Greeting);

                dialogueController.AddStateDialogue("EmploymentVerified", "Employment verified. Good work.",
                    new[] { "Keep it up.", "Glad to see you're working." }, true, EVOLineType.Acknowledge);

                dialogueController.AddStateDialogue("EmploymentWarning", "You need to find employment. This is a warning.",
                    new[] { "Get a job or there will be consequences.", "Employment is a condition of your parole." }, true, EVOLineType.Angry);

                // Fee payment states
                dialogueController.AddStateDialogue("FeePaymentDue", "You have an outstanding supervision fee.",
                    new[] { "Payment is due.", "Let's handle your fee." }, true, EVOLineType.Command);

                dialogueController.AddStateDialogue("FeePaymentReceived", "Payment received. Thank you.",
                    new[] { "All settled.", "Noted." }, true, EVOLineType.Acknowledge);

                dialogueController.AddStateDialogue("FeePaymentFailed", "You don't have enough to pay. This will be noted.",
                    new[] { "Missed payment recorded.", "You need to pay at your next check-in." }, true, EVOLineType.Angry);

                // Home visit states
                dialogueController.AddStateDialogue("HomeVisitArrival", "Parole compliance check. I'm here for a home visit.",
                    new[] { "Routine home inspection.", "Just checking in on you." }, true, EVOLineType.Command);

                dialogueController.AddStateDialogue("HomeVisitComplete", "Everything looks fine. Carry on.",
                    new[] { "Home visit complete.", "All clear here." }, true, EVOLineType.Acknowledge);

                dialogueController.AddStateDialogue("HomeVisitAbsent", "You weren't home for your scheduled visit. This has been noted.",
                    new[] { "Missed home visit recorded.", "You need to be available." }, true, EVOLineType.Angry);

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

        /// <summary>
        /// Applies assignment-to-role rules and configures stationary versus
        /// patrol behavior.  Supervisors remain post-based; route officers use
        /// the preset route registry and patrol scheduler.
        /// </summary>
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

        /// <summary>
        /// Resolves the route named by the assignment and caches its waypoints.
        /// Missing routes leave patrol unavailable rather than inventing points.
        /// </summary>
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

        /// <summary>Generates a display badge identifier for an officer with no supplied badge.</summary>
        private string GenerateBadgeNumber()
        {
            return $"G{UnityEngine.Random.Range(1000, 9999)}";
        }

        /// <summary>Resolves and caches the authored spawn/post transform for this assignment.</summary>
        private void SetAssignedSpawnPoint()
        {
            assignedSpawnPoint = FindSpawnPoint(assignment.ToString());
        }

        #endregion

        #region State Management (Override BaseJailNPC)

        /// <summary>Handles idle duty without starting an independent intake/search workflow.</summary>
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

        /// <summary>Maintains base navigation while preserving escort/intake activity ownership.</summary>
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

        /// <summary>Dispatches working activity logic; supervising intake remains state-machine-owned.</summary>
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

        /// <summary>
        /// Advances the assigned patrol route, waits at points, and periodically
        /// evaluates search/condition opportunities for nearby parolees.
        /// </summary>
        private void HandlePatrolLogic()
        {
            if (!patrolInitialized || availablePatrolPoints.Count == 0) return;

            // Performance: Only move to next point if arrived at current point AND wait time has passed
            bool hasReachedDestination = HasReachedDestination();
            bool waitTimeElapsed = (Time.time - lastPatrolTime >= patrolRoute.waitTime);

            if (hasReachedDestination && waitTimeElapsed)
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

        /// <summary>
        /// Starts patrol activity when a valid route is initialized.  The existing
        /// patrol index is retained so a resumed officer continues its route.
        /// </summary>
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

        /// <summary>Moves toward the current route point and advances the index on arrival.</summary>
        private void MoveToNextPatrolPoint()
        {
            if (availablePatrolPoints.Count == 0) return;

            var targetPoint = availablePatrolPoints[currentPatrolIndex];
            MoveTo(targetPoint.position);

            currentPatrolIndex = (currentPatrolIndex + 1) % availablePatrolPoints.Count;
            lastPatrolTime = Time.time;

            ModLogger.Debug($"Guard {badgeNumber} patrolling to point {currentPatrolIndex}");
        }

        /// <summary>
        /// Replaces the active patrol waypoints and resets route traversal.  This
        /// is a managed/test integration point and does not alter roster assignment.
        /// </summary>
        /// <param name="points">Ordered world-space waypoints, or null to disable the route.</param>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void AssignPatrolRoute(Vector3[] points)
        {
            patrolRoute.points = points;
            if (currentActivity == ParoleOfficerActivity.Patrolling)
            {
                StartPatrol();
            }
        }

        /// <summary>
        /// Finds eligible nearby parolees and starts the shared search coroutine.
        /// This method changes activity to SearchingParolee before the coroutine
        /// freezes the player, preventing patrol logic from issuing competing work.
        /// </summary>
        private void CheckForSearchOpportunities()
        {
            // Only patrol officers perform random searches
            if (role != ParoleOfficerRole.PatrolOfficer) return;

            // Get all players in range
            var players = GameObject.FindObjectsOfType<Player>();
            if (players == null || players.Length == 0) return;

            // Check for parole condition violations (curfew, restricted zones) during patrol
            CheckPatrolConditionViolations(players);

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
                            playerMovement.CanMove = false;
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

        /// <summary>
        /// Evaluates curfew/restricted-area conditions for nearby parolees using
        /// the game clock and records violations through their rap sheets.
        /// </summary>
        private void CheckPatrolConditionViolations(Player[] players)
        {
            foreach (var player in players)
            {
                if (player == null) continue;

                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole()) continue;

                float distance = Vector3.Distance(transform.position, player.transform.position);
                float detectionRange = ParoleSearchSystem.Instance.GetDetectionRange(rapSheet.LSILevel);
                if (distance > detectionRange) continue;

                // Curfew check (officer-proximity detection for all LSI levels)
                if (rapSheet.CurrentParoleRecord.IsConditionActive("curfew"))
                {
                    int currentMinuteOfDay = (int)(GameTimeManager.Instance.GetCurrentGameTimeInMinutes() % 1440f);

                    if (CurfewCondition.IsPastCurfew(rapSheet.LSILevel, currentMinuteOfDay) &&
                        !PlayerHomeDetector.IsPlayerAtHome(player))
                    {
                        // Throttle: don't repeatedly flag the same player
                        float lastInteraction = rapSheet.CurrentParoleRecord.GetLastInteractionGameTime();
                        float currentTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
                        if (currentTime - lastInteraction < 30f) continue; // 30 game min cooldown

                        Core.ResolveParoleManager()?.ReportCurfewViolation(
                            player,
                            rapSheet,
                            $"Officer {badgeNumber} patrol observation");
                    }
                }

                // Restricted zone check
                if (rapSheet.CurrentParoleRecord.IsConditionActive("restricted_zones"))
                {
                    var (isRestricted, zoneName) = RestrictedZoneCondition.IsInRestrictedZone(
                        player.transform.position, rapSheet);

                    if (isRestricted)
                    {
                        float lastInteraction = rapSheet.CurrentParoleRecord.GetLastInteractionGameTime();
                        float currentTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
                        if (currentTime - lastInteraction < 30f) continue;

                        rapSheet.CurrentParoleRecord.AdjustComplianceScore(-8f);
                        rapSheet.CurrentParoleRecord.AdjustRapport(-10f);
                        rapSheet.CurrentParoleRecord.RecordInteraction();

                        var violation = new ViolationRecord(ViolationType.RestrictedAreaViolation,
                            $"Detected in restricted zone: {zoneName}", 2.0f);
                        rapSheet.AddParoleViolation(violation);

                        Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                            $"Officer {badgeNumber} found you in restricted area ({zoneName}). Violation recorded.");

                        Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
                        ModLogger.Info($"[PATROL] Officer {badgeNumber} detected restricted zone violation for {player.name} in {zoneName}");
                    }
                }
            }
        }

        #endregion

        #region Parole Intake Logic

        private ParoleIntakeStateMachine paroleIntakeStateMachine;

        /// <summary>
        /// Gets or injects the canonical supervising-intake state machine.  This
        /// helper is hidden from the IL2CPP public surface and refuses to replace
        /// a failed injection with a static/fallback behavior.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        internal ParoleIntakeStateMachine EnsureParoleIntakeStateMachine()
        {
            if (paroleIntakeStateMachine == null)
            {
                paroleIntakeStateMachine = BBHelpers.GetComponentSafe<ParoleIntakeStateMachine>(gameObject)
                    ?? BBHelpers.AddComponentSafe<ParoleIntakeStateMachine>(gameObject);

                if (paroleIntakeStateMachine != null)
                {
                    ModLogger.Info($"Supervising Officer {badgeNumber}: Parole intake state machine is ready");
                }
            }

            return paroleIntakeStateMachine;
        }

        /// <summary>
        /// Hands a new parolee to the canonical intake state machine and mirrors
        /// the processing activity on this officer.  Intake ownership and player
        /// cleanup belong to the state machine/coordinator, not this wrapper.
        /// </summary>
        /// <param name="parolee">Exact player to retain for intake.</param>
        public void HandleParoleIntake(Player parolee)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not a supervising officer");
                return;
            }

            if (parolee == null)
            {
                ModLogger.Warn($"Supervising Officer {badgeNumber}: Cannot start parole intake for null parolee");
                return;
            }

            // The supervising officer owns this state machine.  It must also be
            // available for release staging before an active parole record exists.
            EnsureParoleIntakeStateMachine();

            if (paroleIntakeStateMachine != null && paroleIntakeStateMachine.IsProcessingIntake())
            {
                if (currentParolee == parolee)
                {
                    ModLogger.Debug($"Supervising Officer {badgeNumber}: Intake already active for {parolee.name}");
                }
                else
                {
                    ModLogger.Warn($"Supervising Officer {badgeNumber}: Cannot start intake for {parolee.name}, already processing {currentParolee?.name ?? "another parolee"}");
                }

                return;
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
        /// Compatibility shim that forwards legacy intake callers to the canonical
        /// parole intake state machine.
        /// </summary>
        /// <param name="parolee">Player to process through canonical intake.</param>
        public void StartIntakeProcess(Player parolee)
        {
            HandleParoleIntake(parolee);
        }

        /// <summary>Returns whether canonical or mirrored intake state is active.</summary>
        public bool IsIntakeProcessingActive()
        {
            return paroleIntakeStateMachine != null && paroleIntakeStateMachine.IsProcessingIntake();
        }

        /// <summary>
        /// Marks the officer as escorting the exact intake player and begins the
        /// navigation/activity mirror used by command notifications.
        /// </summary>
        /// <param name="parolee">Player retained by the intake state machine.</param>
        public void BeginIntakeEscort(Player parolee)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer || parolee == null)
            {
                return;
            }

            currentParolee = parolee;
            ChangeParoleActivity(ParoleOfficerActivity.EscortingParolee);
        }

        /// <summary>
        /// Clears the intake escort mirror and returns activity ownership to the
        /// caller/state machine; it does not record intake completion itself.
        /// </summary>
        public void CompleteIntakeEscort()
        {
            currentParolee = null;
            if (role == ParoleOfficerRole.SupervisingOfficer)
            {
                ChangeParoleActivity(ParoleOfficerActivity.MonitoringArea);
            }
        }

        /// <summary>Checks whether this officer's canonical intake owns the exact player.</summary>
        /// <param name="parolee">Player identity to compare.</param>
        /// <returns>True only when the intake state machine is active for that player.</returns>
        public bool IsHandlingIntakeFor(Player parolee)
        {
            return role == ParoleOfficerRole.SupervisingOfficer &&
                   currentParolee == parolee &&
                   IsIntakeProcessingActive();
        }

        #endregion

        #region Parolee Compliance

        // Compliance thresholds below are measured in world-space metres.  The
        // officer's patience changes with frame delta, while warning cooldowns
        // use real Unity time.
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

            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(parolee);
            if (rapSheet?.CurrentParoleRecord != null)
            {
                return rapSheet.CurrentParoleRecord.GetComplianceScore();
            }

            return 0f;
        }

        /// <summary>
        /// Applies distance-based patience changes and throttled warnings for the
        /// exact escorted parolee.  This does not change the officer's activity or
        /// start an arrest on its own.
        /// </summary>
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

        /// <summary>
        /// Increments the local warning count and sends the message associated with
        /// the current warning, intervention, or escape-distance band.
        /// </summary>
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
        /// Leaves arrival/intake polling to DynamicParoleOfficerManager and the
        /// canonical ParoleIntakeStateMachine; monitoring officers have no local
        /// arrival poller to avoid duplicate ownership.
        /// </summary>
        private void HandleMonitoringLogic()
        {
            // DynamicParoleOfficerManager and ParoleIntakeStateMachine own arrival/intake work.
            // Monitoring officers deliberately have no independent arrival polling path.
        }

        /// <summary>
        /// Changes the mirrored activity and updates the officer-command surface.
        /// The command surface is authoritative over passive HUD status: intake
        /// processing is delegated to the canonical state machine, and a tier UI
        /// must defer whenever an officer command is active.
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
        /// Publishes activity command data unless canonical intake owns the message.
        /// This is an officer-command producer, not a general-purpose HUD status
        /// update.
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
                    Core.ResolveUIManager().UpdateOfficerCommand(commandData);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error updating command notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Determines whether the current activity has a player-facing officer
        /// command.  Only escort and search activities currently qualify.
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
        /// Builds the command payload for escort/search activity.  A null result
        /// means no command should be published; intake remains state-machine-owned.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
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
        /// Clears the officer-command surface after this officer no longer owns it.
        /// Passive HUD status may resume only after this handoff.
        /// </summary>
        private void HideOfficerCommandNotification()
        {
            try
            {
                Core.ResolveUIManager().HideOfficerCommand();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error hiding command notification: {ex.Message}");
            }
        }

        #region Search Notification Methods

        // Search state tracking.  These fields feed the officer-command surface;
        // they are intentionally separate from passive tier-status HUD state.
        private string currentSearchMessage = "";
        private bool searchInProgress = false;
        private bool searchContrabandFound = false;
        private int contrabandItemCount = 0;

        /// <summary>
        /// Shows the current search instruction through the officer-command surface
        /// and records whether the search is still active.
        /// </summary>
        /// <param name="message">Instruction or result text shown to the player.</param>
        /// <param name="isSearching">Whether the search workflow remains active.</param>
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

                Core.ResolveUIManager().UpdateOfficerCommand(commandData);
                ModLogger.Debug($"ParoleOfficer {badgeNumber}: Showing search notification: {message}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error showing search notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the current search instruction without changing ownership or
        /// search activity state.
        /// </summary>
        /// <param name="message">Replacement instruction text.</param>
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

                Core.ResolveUIManager().UpdateOfficerCommand(commandData);
                ModLogger.Debug($"ParoleOfficer {badgeNumber}: Updating search notification: {message}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: Error updating search notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the final search result through the officer-command surface and
        /// records the detected-item summary for later message queries.
        /// </summary>
        /// <param name="contrabandFound">Whether the search found contraband.</param>
        /// <param name="itemCount">Number of detected items when applicable.</param>
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

                Core.ResolveUIManager().UpdateOfficerCommand(commandData);
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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

        /// <summary>
        /// Maintains compliance for the active escort and returns the officer to
        /// monitoring when its exact parolee reference is gone.
        /// </summary>
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

        #endregion

        #region Supervising Officer Methods

        private ParoleCheckInSystem checkInSystem;

        /// <summary>Resolves and caches the supervising officer's check-in controller.</summary>
        private ParoleCheckInSystem GetCheckInSystem()
        {
            if (checkInSystem != null)
            {
                return checkInSystem;
            }

            checkInSystem = BBHelpers.GetComponentSafe<ParoleCheckInSystem>(gameObject);

            return checkInSystem;
        }

        /// <summary>
        /// Compatibility shim for older callers. ParoleCheckInSystem owns check-in interaction flow.
        /// </summary>
        /// <param name="parolee">Player forwarded to the check-in controller.</param>
        public void HandleCheckIn(Player parolee)
        {
            if (role != ParoleOfficerRole.SupervisingOfficer)
            {
                ModLogger.Warn($"Guard {badgeNumber} is not a supervising officer");
                return;
            }

            var activeCheckInSystem = GetCheckInSystem();

            if (activeCheckInSystem != null)
            {
                activeCheckInSystem.InitiateCheckIn(parolee);
                ModLogger.Info($"Supervising Officer {badgeNumber}: Forwarded check-in request for {parolee.name} to ParoleCheckInSystem");
            }
            else
            {
                ModLogger.Warn($"Supervising Officer {badgeNumber}: No ParoleCheckInSystem available to handle check-in for {parolee.name}");
            }
        }

        /// <summary>
        /// Check whether the dedicated check-in controller is already processing this parolee.
        /// </summary>
        public bool IsHandlingCheckInFor(Player parolee)
        {
            var activeCheckInSystem = GetCheckInSystem();
            return activeCheckInSystem != null &&
                   activeCheckInSystem.IsProcessingCheckIn() &&
                   activeCheckInSystem.GetCurrentCheckInParolee() == parolee;
        }

        /// <summary>
        /// Check whether this officer has a check-in controller attached.
        /// </summary>
        public bool HasCheckInController()
        {
            return GetCheckInSystem() != null;
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
            LookAt(parolee.transform.position);
            if (GetCheckInSystem()?.BeginViolationDialogue(parolee, violationType) != true)
            {
                ModLogger.Warn($"Supervising Officer {badgeNumber}: Could not start violation dialogue for {parolee.name}");
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
            LookAt(parolee.transform.position);
            if (GetCheckInSystem()?.BeginConditionsReviewDialogue(parolee) != true)
            {
                ModLogger.Warn($"Supervising Officer {badgeNumber}: Could not start conditions dialogue for {parolee.name}");
            }
        }

        #endregion

        #region Door Integration

        private void OnTriggerEnter(Collider other)
        {
            // Handle door triggers - delegate to intake state machine if processing intake
            var doorTrigger = BBHelpers.GetComponentSafe<DoorTriggerHandler>(other.gameObject);
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

        /// <summary>
        /// Resumes this officer's configured patrol after an off-duty courthouse stay.
        /// The patrol index is retained so a returning officer continues the assigned route
        /// instead of visibly restarting every roster change.
        /// </summary>
        public void ResumeScheduledPatrol()
        {
            if (role != ParoleOfficerRole.PatrolOfficer || IsProcessingIntake())
            {
                return;
            }

            isOnDuty = true;

            if (!patrolInitialized)
            {
                InitializePatrolPoints();
            }

            if (availablePatrolPoints.Count == 0)
            {
                ModLogger.Warn($"ParoleOfficer {badgeNumber}: cannot resume scheduled patrol because no patrol points are available");
                return;
            }

            if (currentActivity == ParoleOfficerActivity.Patrolling)
            {
                return;
            }

            ChangeParoleActivity(ParoleOfficerActivity.Patrolling);
            MoveToNextPatrolPoint();
            ModLogger.Info($"ParoleOfficer {badgeNumber}: resumed scheduled patrol for {assignment}");
        }

        /// <summary>
        /// Marks this officer off duty while the native schedule action moves them inside
        /// the courthouse.  The native action owns the actual building transition.
        /// </summary>
        public void BeginCourthouseHomeStay()
        {
            if (IsProcessingIntake())
            {
                return;
            }

            SetOnDuty(false);
        }

        /// <summary>
        /// Keeps the canonical officer active while walking to the courthouse entrance.
        /// The roster manager hands ownership to the native building action only after arrival.
        /// </summary>
        public bool BeginCourthouseReturn(Vector3 exteriorApproach)
        {
            if (IsProcessingIntake())
            {
                return false;
            }

            isOnDuty = true;
            ChangeParoleActivity(ParoleOfficerActivity.RespondingToIncident);
            return MoveTo(exteriorApproach, 1.25f);
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

        public void ReturnToAssignedPost(Vector3 fallbackPosition)
        {
            try
            {
                if (stationaryBehavior != null)
                {
                    stationaryBehavior.ReturnToPosition();
                    ChangeParoleActivity(ParoleOfficerActivity.Idle);
                    return;
                }

                Vector3 destination = assignedSpawnPoint != null ? assignedSpawnPoint.position : fallbackPosition;
                MoveTo(destination);
                ChangeParoleActivity(ParoleOfficerActivity.RespondingToIncident);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleOfficer {badgeNumber}: failed to return to assigned post: {ex.Message}");
            }
        }

        /// <summary>
        /// Routes an attack through the parole officer's immediate-arrest response
        /// and interrupts supervising intake when this officer owns one.
        /// </summary>
        /// <param name="attacker">Player who attacked the officer.</param>
        public override void OnAttackedByPlayer(Player attacker)
        {
            base.OnAttackedByPlayer(attacker);

            if (attacker == null) return;

            ModLogger.Info($"Guard {badgeNumber}: Attacked by player {attacker.name}");

            // Guards have zero tolerance for being attacked
            HandlePlayerAttack(attacker);
        }

        /// <summary>
        /// Stops current navigation, warns the attacker, starts the jail-manager
        /// arrest coroutine, and cancels an active supervising intake.  Failure to
        /// resolve the jail manager is logged; no local fallback arrest is invented.
        /// </summary>
        /// <param name="attacker">Player to pass to the immediate-arrest flow.</param>
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
                // Route the arrest through the jail manager seam.
                var jailManager = Core.Instance?.JailManager;
                if (jailManager != null)
                {
                    // Trigger immediate arrest for assault
                    ModLogger.Info($"Guard {badgeNumber}: Initiating immediate arrest for assault by {attacker.name}");

                    // Use the immediate arrest system
                    MelonCoroutines.Start(jailManager.HandleImmediateArrest(attacker));

                    ModLogger.Info($"Guard {badgeNumber}: Player {attacker.name} arrested for assault on officer");
                }
                else
                {
                    ModLogger.Error($"Guard {badgeNumber}: Could not access jail manager for arrest");
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
