using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems.Jail;
using MelonLoader;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
#endif

#if !MONO
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.AvatarFramework.Animation;
using Il2CppSystem.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
#else
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Messaging;
using ScheduleOne.AvatarFramework;
using ScheduleOne.AvatarFramework.Animation;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Information about a station or guard point for intake management
    /// </summary>
    public class StationInfo
    {
        public Transform StationTransform { get; set; }
        public string StationType { get; set; } // "InteractionStation" or "GuardPoint"
        public string Area { get; set; } // "Booking", "Storage", "Guard"
        public bool IsEnabled { get; set; }
    }

    /// <summary>
    /// Jail-specific behavior for guard NPCs with patrol routes and prisoner monitoring
    /// Enhanced version with proper 4-guard system and coordinated patrols
    /// </summary>
    public class JailGuardBehavior : MonoBehaviour
    {
        public enum GuardRole
        {
            GuardRoomStationary,    // Guards stationed in guard room (2 guards)
            BookingStationary,      // Guards stationed in booking area (1 guard)
            IntakeOfficer,          // Dedicated intake processing guard (1 guard)
            CoordinatedPatrol,      // Guards doing coordinated patrol together
            ResponseGuard           // Responds to incidents
        }

        public enum GuardAssignment
        {
            GuardRoom0,    // Guard room spawn point 0
            GuardRoom1,    // Guard room spawn point 1  
            Booking0,      // Booking spawn point 0
            Booking1       // Booking spawn point 1
        }

        [System.Serializable]
        public class PatrolData
        {
            public Vector3[] points;
            public float speed = 2.5f; // Default patrol speed for stair climbing
            public float waitTime = 3f;
        }

        // Saveable fields for persistence across game sessions
        [SaveableField("guard_role")]
        public GuardRole role = GuardRole.GuardRoomStationary;

        [SaveableField("guard_patrol_route")]
        public PatrolData patrolRoute = new PatrolData();

        [SaveableField("guard_shift_start_time")]
        public float shiftStartTime = 0f;

        [SaveableField("guard_shift_duration")]
        public float shiftDuration = 480f; // 8 minutes default

        [SaveableField("guard_experience_level")]
        public int experienceLevel = 1;

        [SaveableField("guard_badge_number")]
        public string badgeNumber = "";

        // Enhanced runtime state
        private UnityEngine.AI.NavMeshAgent navAgent;
        private DoorInteractionController doorController;
        private int currentPatrolIndex = 0;
        private float lastPatrolTime = 0f;
        private bool isOnDuty = true;
        private GameObject assignedArea;
        private bool hasReachedDestination = true;
        private Vector3 currentDestination;
        private float lastDestinationTime = 0f;

        // Guard assignment and coordination
        public GuardAssignment assignment;
        private Transform assignedSpawnPoint;
        private JailGuardBehavior patrolPartner; // For coordinated patrols
        private bool isPatrolLeader = false;
        private float patrolStartDelay = 0f;

        // IL2CPP-safe coordination - managed by PrisonNPCManager instead of static
        private bool isIntakeActive = false;
        private GameObject currentEscortTarget = null;
        private System.Collections.Generic.Queue<Vector3> escortWaypoints = new System.Collections.Generic.Queue<Vector3>();
        private readonly float PATROL_COOLDOWN = 300f; // 5 minutes between coordinated patrols
        
        // Simplified escort state for IL2CPP compatibility
        private EscortState currentEscortState = EscortState.Idle;
        private Vector3 currentEscortDestination;
        private float escortTimer = 0f;
        private bool isMovingToDestination = false;
        
        // LookController support for natural facing behavior
#if !MONO
        private Il2CppScheduleOne.AvatarFramework.Avatar guardAvatar;
        private Il2CppScheduleOne.AvatarFramework.Animation.AvatarLookController lookController;
#else
        private ScheduleOne.AvatarFramework.Avatar guardAvatar;
        private ScheduleOne.AvatarFramework.Animation.AvatarLookController lookController;
#endif
        private bool lookControllerAvailable = false;
        
        
        private enum EscortState
        {
            Idle,
            MovingToHoldingCell,
            OpeningHoldingCell,
            WaitingForPrisoner,
            EscortingToMugshot,
            WaitingAtMugshot,
            EscortingToScanner,
            WaitingAtScanner,
            EscortingToStorage,
            WaitingAtStorage,
            EscortComplete
        }

#if !MONO
        private Il2CppScheduleOne.NPCs.NPC npcComponent;

        public JailGuardBehavior(System.IntPtr ptr) : base(ptr) { }
#else
        private ScheduleOne.NPCs.NPC npcComponent;
#endif

        public void Initialize(GuardAssignment guardAssignment, string badge = "")
        {
            assignment = guardAssignment;
            badgeNumber = string.IsNullOrEmpty(badge) ? GenerateBadgeNumber() : badge;
            shiftStartTime = Time.time;

            // Set role based on assignment
            switch (assignment)
            {
                case GuardAssignment.GuardRoom0:
                case GuardAssignment.GuardRoom1:
                    role = GuardRole.GuardRoomStationary;
                    break;
                case GuardAssignment.Booking0:
                    role = GuardRole.IntakeOfficer; // Booking0 is the intake officer
                    break;
                case GuardAssignment.Booking1:
                    role = GuardRole.BookingStationary; // Booking1 is stationary booking guard
                    break;
            }

            // Find and set assigned spawn point
            SetAssignedSpawnPoint();

            // Register this guard with PrisonNPCManager instead of static list
            if (PrisonNPCManager.Instance != null)
            {
                PrisonNPCManager.Instance.RegisterGuard(this);
            }

            ModLogger.Info($"Initializing {role} guard with badge {badgeNumber} at assignment {assignment}");
        }

        private void Start()
        {
            try
            {
                navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
                doorController = GetComponent<DoorInteractionController>();
                if (doorController == null)
                {
                    doorController = gameObject.AddComponent<DoorInteractionController>();
                    ModLogger.Debug($"Added DoorInteractionController to guard {gameObject.name}");
                }

#if !MONO
                npcComponent = GetComponent<Il2CppScheduleOne.NPCs.NPC>();
#else
                npcComponent = GetComponent<ScheduleOne.NPCs.NPC>();
#endif

                if (navAgent == null)
                {
                    ModLogger.Error($"NavMeshAgent not found on guard {gameObject.name}");
                    return;
                }

                if (npcComponent == null)
                {
                    ModLogger.Error($"NPC component not found on guard {gameObject.name}");
                    return;
                }

                // Initialize LookController for natural facing behavior
                InitializeLookController();

                SetupGuardBehavior();
                StartCoroutine("GuardBehaviorLoop");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error starting JailGuardBehavior: {e.Message}");
            }
        }

        /// <summary>
        /// Ensure NPC component is initialized (called when needed if Start() hasn't run yet)
        /// </summary>
        private void EnsureNPCComponentInitialized()
        {
            if (npcComponent != null) return;

            try
            {
#if !MONO
                npcComponent = GetComponent<Il2CppScheduleOne.NPCs.NPC>();
#else
                npcComponent = GetComponent<ScheduleOne.NPCs.NPC>();
#endif

                if (npcComponent != null)
                {
                    ModLogger.Debug($"NPC component initialized for guard {badgeNumber}");
                }
                else
                {
                    ModLogger.Error($"Failed to get NPC component for guard {gameObject.name}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing NPC component for guard {gameObject.name}: {e.Message}");
            }
        }

        /// <summary>
        /// Safely attempt to send a text message via NPC component
        /// Returns true if message was sent successfully, false otherwise
        /// </summary>
        public bool TrySendNPCMessage(string message)
        {
            try
            {
                if (npcComponent == null)
                {
                    EnsureNPCComponentInitialized();
                }

                if (npcComponent != null &&
                    npcComponent.gameObject != null &&
                    npcComponent.enabled &&
                    npcComponent.gameObject.activeInHierarchy)
                {
                    npcComponent.SendTextMessage(message);
                    return true;
                }
            }
            catch (Exception ex)
            {
                // Log once per guard but don't spam - text messaging is optional
                ModLogger.Warn($"Guard {badgeNumber} text messaging unavailable: {ex.Message}");
            }

            return false;
        }

        private void SetupGuardBehavior()
        {
            switch (role)
            {
                case GuardRole.GuardRoomStationary:
                    SetupStationaryPosition();
                    break;
                case GuardRole.BookingStationary:
                    SetupStationaryPosition();
                    break;
                case GuardRole.IntakeOfficer:
                    SetupIntakePosition();
                    break;
                case GuardRole.CoordinatedPatrol:
                    SetupPatrolRoute();
                    break;
                case GuardRole.ResponseGuard:
                    SetupResponsePosition();
                    break;
            }
        }

        /// <summary>
        /// Set assigned spawn point based on guard assignment
        /// </summary>
        private void SetAssignedSpawnPoint()
        {
            var jailController = Core.ActiveJailController;
            if (jailController == null) return;

            switch (assignment)
            {
                case GuardAssignment.GuardRoom0:
                    if (jailController.guardRoom.guardSpawns.Count > 0)
                        assignedSpawnPoint = jailController.guardRoom.guardSpawns[0];
                    break;
                case GuardAssignment.GuardRoom1:
                    if (jailController.guardRoom.guardSpawns.Count > 1)
                        assignedSpawnPoint = jailController.guardRoom.guardSpawns[1];
                    break;
                case GuardAssignment.Booking0:
                    if (jailController.booking.guardSpawns.Count > 0)
                        assignedSpawnPoint = jailController.booking.guardSpawns[0];
                    break;
                case GuardAssignment.Booking1:
                    if (jailController.booking.guardSpawns.Count > 1)
                        assignedSpawnPoint = jailController.booking.guardSpawns[1];
                    break;
            }

            if (assignedSpawnPoint != null)
            {
                ModLogger.Info($"Guard {badgeNumber} assigned to spawn point: {assignedSpawnPoint.name}");
            }
            else
            {
                ModLogger.Warn($"Could not find spawn point for assignment {assignment}");
            }
        }

        private void SetupPatrolRoute()
        {
            // Use JailController patrol points instead of hardcoded positions
            var jailController = Core.ActiveJailController;
            if (jailController == null || jailController.patrolPoints.Count == 0)
            {
                ModLogger.Warn($"No patrol points available for guard {badgeNumber}");
                return;
            }

            // Convert Transform patrol points to Vector3 positions
            var patrolPositions = new System.Collections.Generic.List<Vector3>();
            foreach (var point in jailController.patrolPoints)
            {
                if (point != null)
                    patrolPositions.Add(point.position);
            }

            patrolRoute.points = patrolPositions.ToArray();
            patrolRoute.speed = 2.5f; // Sufficient speed for stair climbing during patrols
            patrolRoute.waitTime = 8f; // Longer wait times for thorough patrol

            ModLogger.Info($"Guard {badgeNumber} patrol route setup with {patrolRoute.points.Length} points");
        }

        private void SetupStationaryPosition()
        {
            // Use assigned spawn point as stationary position
            if (assignedSpawnPoint != null && navAgent != null)
            {
                SetDestinationOnce(assignedSpawnPoint.position);
                ModLogger.Info($"Guard {badgeNumber} stationed at {assignedSpawnPoint.name}");
            }
        }

        private void SetupWatchTowerPosition()
        {
            // Watch tower guard stays at elevated position
            Vector3 watchPosition = new Vector3(58f, 10f, -222f);
            if (navAgent != null)
            {
                SetDestinationOnce(watchPosition);
            }
        }

        private void SetupIntakePosition()
        {
            // Intake officer stays near booking area but mobile for escort duties
            if (assignedSpawnPoint != null && navAgent != null)
            {
                SetDestinationOnce(assignedSpawnPoint.position);
                ModLogger.Info($"Intake officer {badgeNumber} positioned at booking area");
                
                // Test LookController: face player if present during setup
                var playerEyePos = GetPlayerEyePosition();
                if (playerEyePos.HasValue)
                {
                    FaceTargetWithLookController(playerEyePos.Value, 10, false, "player during setup");
                }
            }
        }

        private void SetupResponsePosition()
        {
            // Response guard stays at central location
            Vector3 responsePosition = new Vector3(55f, 8.6593f, -218f);
            if (navAgent != null)
            {
                SetDestinationOnce(responsePosition);
            }
        }

        /// <summary>
        /// Set destination only if not already set to avoid constant movement
        /// </summary>
        private void SetDestinationOnce(Vector3 destination)
        {
            if (navAgent == null) return;

            // Only set destination if it's different from current or we haven't reached it
            if (Vector3.Distance(currentDestination, destination) > 1f || !hasReachedDestination)
            {
                currentDestination = destination;
                navAgent.SetDestination(destination);
                hasReachedDestination = false;
                lastDestinationTime = Time.time;
                ModLogger.Debug($"Guard {badgeNumber} setting new destination: {destination}");
            }
        }

        /// <summary>
        /// Check if we've reached our destination
        /// </summary>
        private bool CheckReachedDestination()
        {
            if (navAgent == null || hasReachedDestination) return hasReachedDestination;

            if (!navAgent.pathPending && navAgent.remainingDistance < 1.5f)
            {
                hasReachedDestination = true;
                ModLogger.Debug($"Guard {badgeNumber} reached destination");
                return true;
            }

            return false;
        }

        /// <summary>
        /// IL2CPP-compatible simplified escort update method
        /// Called from Update() instead of coroutines for better compatibility
        /// </summary>
        private void UpdateSimplifiedEscort()
        {
            if (role != GuardRole.IntakeOfficer || !isIntakeActive || currentEscortTarget == null)
                return;

            escortTimer += Time.deltaTime;

            switch (currentEscortState)
            {
                case EscortState.Idle:
                    // Start escort sequence
                    StartSimplifiedEscort();
                    break;

                case EscortState.MovingToHoldingCell:
                    if (MoveToDestination(currentEscortDestination))
                    {
                        currentEscortState = EscortState.OpeningHoldingCell;
                        escortTimer = 0f;
                    }
                    break;

                case EscortState.OpeningHoldingCell:
                    if (escortTimer > 2f) // Wait 2 seconds for door animation
                    {
                        currentEscortState = EscortState.WaitingForPrisoner;
                        escortTimer = 0f;
                    }
                    break;

                case EscortState.WaitingForPrisoner:
                    if (escortTimer > 3f) // Wait 3 seconds for prisoner to exit
                    {
                        currentEscortState = EscortState.EscortingToMugshot;
                        // Set next destination to mugshot station
                        var mugshotStation = GameObject.Find("Booking/MugshotStation");
                        if (mugshotStation != null)
                        {
                            currentEscortDestination = mugshotStation.transform.position + Vector3.forward * 2f;
                        }
                        escortTimer = 0f;
                    }
                    break;

                case EscortState.EscortingToMugshot:
                    if (MoveToDestination(currentEscortDestination))
                    {
                        currentEscortState = EscortState.WaitingAtMugshot;
                        escortTimer = 0f;
                        ModLogger.Info($"Guard {badgeNumber} arrived at mugshot station");
                    }
                    break;

                case EscortState.WaitingAtMugshot:
                    // TODO: Check if mugshot is complete
                    if (escortTimer > 10f) // Simulate mugshot completion after 10 seconds
                    {
                        currentEscortState = EscortState.EscortingToScanner;
                        var scannerStation = GameObject.Find("Booking/ScannerStation");
                        if (scannerStation != null)
                        {
                            currentEscortDestination = scannerStation.transform.position + Vector3.forward * 2f;
                        }
                        escortTimer = 0f;
                    }
                    break;

                case EscortState.EscortingToScanner:
                    if (MoveToDestination(currentEscortDestination))
                    {
                        currentEscortState = EscortState.WaitingAtScanner;
                        escortTimer = 0f;
                        ModLogger.Info($"Guard {badgeNumber} arrived at scanner station");
                    }
                    break;

                case EscortState.WaitingAtScanner:
                    // TODO: Check if scan is complete
                    if (escortTimer > 10f) // Simulate scan completion after 10 seconds
                    {
                        currentEscortState = EscortState.EscortComplete;
                        ModLogger.Info($"Guard {badgeNumber} completed prisoner escort");
                        CompleteSimplifiedEscort();
                    }
                    break;

                case EscortState.EscortComplete:
                    // Reset for next escort
                    break;
            }
        }

        private void StartSimplifiedEscort()
        {
            if (currentEscortTarget == null) return;

            ModLogger.Info($"Guard {badgeNumber} starting simplified escort");
            
            // Find holding cell door position
            var holdingCells = GameObject.Find("Jail/Cells/HoldingCells");
            if (holdingCells != null)
            {
                // Simple approximation - move to holding cell area
                currentEscortDestination = holdingCells.transform.position + Vector3.forward * 3f;
                currentEscortState = EscortState.MovingToHoldingCell;
                isMovingToDestination = true;
            }
            else
            {
                ModLogger.Error("Could not find holding cells for escort");
            }
        }

        private bool MoveToDestination(Vector3 destination)
        {
            if (navAgent == null) return false;

            if (!isMovingToDestination)
            {
                navAgent.SetDestination(destination);
                isMovingToDestination = true;
            }

            // Check if we've reached the destination
            if (!navAgent.pathPending && navAgent.remainingDistance < 1.5f)
            {
                isMovingToDestination = false;
                return true;
            }

            return false;
        }

        private void CompleteSimplifiedEscort()
        {
            currentEscortState = EscortState.Idle;
            isIntakeActive = false;
            currentEscortTarget = null;
            isMovingToDestination = false;
            escortTimer = 0f;
            
            // Return to assigned position
            if (assignedSpawnPoint != null)
            {
                if (navAgent != null)
                {
                    navAgent.SetDestination(assignedSpawnPoint.position);
                }
            }
        }

        private IEnumerator GuardBehaviorLoop()
        {
            while (isOnDuty)
            {
                // Check if we've reached our destination
                CheckReachedDestination();

                switch (role)
                {
                    case GuardRole.GuardRoomStationary:
                    case GuardRole.BookingStationary:
                        yield return HandleStationaryBehavior();
                        // Check for coordinated patrol opportunity
                        yield return CheckForPatrolOpportunity();
                        break;
                    case GuardRole.IntakeOfficer:
                        yield return HandleIntakeOfficerBehavior();
                        break;
                    case GuardRole.CoordinatedPatrol:
                        yield return HandleCoordinatedPatrolBehavior();
                        break;
                    case GuardRole.ResponseGuard:
                        yield return HandleResponseBehavior();
                        break;
                }

                // Behavior check interval
                yield return new WaitForSeconds(2f);
            }
        }

        /// <summary>
        /// Handle coordinated patrol behavior with partner synchronization
        /// </summary>
        /// <summary>
        /// Handle intake officer behavior - escort prisoners through booking process
        /// </summary>
        private IEnumerator HandleIntakeOfficerBehavior()
        {
            // Check if there's a prisoner waiting for intake
            if (!isIntakeActive)
            {
                yield return CheckForIntakeAssignment();
            }

            if (isIntakeActive && currentEscortTarget != null)
            {
                // Handle active escort
                yield return HandlePrisonerEscort();
            }
            else
            {
                // Return to station when not escorting
                if (hasReachedDestination && assignedSpawnPoint != null)
                {
                    // Test LookController: Look at player if present, otherwise look around naturally
                    if (UnityEngine.Random.Range(0f, 1f) < 0.2f) // More frequent checks than before
                    {
                        var playerEyePos = GetPlayerEyePosition();
                        if (playerEyePos.HasValue)
                        {
                            // Face player with LookController
                            FaceTargetWithLookController(playerEyePos.Value, 10, false, "player at station");
                        }
                        else if (lookControllerAvailable)
                        {
                            // Look in a random direction naturally using spawn point as reference
                            Vector3 basePos = assignedSpawnPoint.position;
                            Vector3 randomLookTarget = basePos + assignedSpawnPoint.forward * 5f + 
                                                     UnityEngine.Random.insideUnitSphere * 3f;
                            randomLookTarget.y = basePos.y + 1.7f; // Eye level
                            FaceTargetWithLookController(randomLookTarget, 5, false, "random look around");
                        }
                        else
                        {
                            // Fallback to old rotation method if LookController not available
                            float baseRotation = assignedSpawnPoint.eulerAngles.y;
                            float lookDirection = baseRotation + UnityEngine.Random.Range(-45f, 45f);
                            var targetRotation = Quaternion.Euler(0, lookDirection, 0);
                            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 1.5f);
                        }
                    }
                }
                else if (!hasReachedDestination && assignedSpawnPoint != null)
                {
                    SetDestinationOnce(assignedSpawnPoint.position);
                }
            }
        }

        private IEnumerator HandleCoordinatedPatrolBehavior()
        {
            if (patrolRoute.points == null || patrolRoute.points.Length == 0)
            {
                // End patrol if no route available
                yield return EndCoordinatedPatrol();
                yield break;
            }

            // Handle patrol start delay for coordination
            if (patrolStartDelay > 0f)
            {
                patrolStartDelay -= Time.deltaTime;
                yield break;
            }

            // Move to next patrol point if we've reached the current one
            if (hasReachedDestination)
            {
                // Wait at patrol point
                if (Time.time - lastDestinationTime > patrolRoute.waitTime)
                {
                    // Check if we've completed the patrol route
                    if (currentPatrolIndex >= patrolRoute.points.Length - 1)
                    {
                        ModLogger.Info($"Guard {badgeNumber} completed patrol route");
                        yield return EndCoordinatedPatrol();
                        yield break;
                    }

                    // Move to next point
                    currentPatrolIndex++;
                    Vector3 targetPoint = patrolRoute.points[currentPatrolIndex];

                    if (navAgent != null)
                    {
                        navAgent.speed = patrolRoute.speed;

                        // Leader coordinates movement
                        if (isPatrolLeader && patrolPartner != null)
                        {
                            // Ensure partner is ready before moving
                            if (patrolPartner.hasReachedDestination ||
                                Vector3.Distance(transform.position, patrolPartner.transform.position) < 8f)
                            {
                                SetDestinationOnce(targetPoint);
                                ModLogger.Debug($"Patrol leader {badgeNumber} moving to point {currentPatrolIndex}");
                            }
                        }
                        else if (!isPatrolLeader)
                        {
                            // Partner follows with slight delay
                            yield return new WaitForSeconds(1f);
                            SetDestinationOnce(targetPoint);
                            ModLogger.Debug($"Patrol partner {badgeNumber} following to point {currentPatrolIndex}");
                        }
                        else
                        {
                            // Solo patrol (partner lost)
                            SetDestinationOnce(targetPoint);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// End coordinated patrol and return to stationary positions
        /// </summary>
        private IEnumerator EndCoordinatedPatrol()
        {
            ModLogger.Info($"Guard {badgeNumber} ending coordinated patrol");

            // Reset patrol state - simplified for IL2CPP compatibility
            if (isPatrolLeader)
            {
                if (patrolPartner != null)
                {
                    MelonCoroutines.Start(patrolPartner.EndCoordinatedPatrol());
                }
            }

            // Reset guard state
            switch (assignment)
            {
                case GuardAssignment.GuardRoom0:
                case GuardAssignment.GuardRoom1:
                    role = GuardRole.GuardRoomStationary;
                    break;
                case GuardAssignment.Booking0:
                case GuardAssignment.Booking1:
                    role = GuardRole.BookingStationary;
                    break;
            }

            // Clear patrol data
            patrolPartner = null;
            isPatrolLeader = false;
            currentPatrolIndex = 0;
            patrolStartDelay = 0f;

            // Return to assigned position
            if (assignedSpawnPoint != null && navAgent != null)
            {
                navAgent.speed = 2.2f; // Sufficient speed for stair climbing when returning
                SetDestinationOnce(assignedSpawnPoint.position);
                ModLogger.Info($"Guard {badgeNumber} returning to station at {assignedSpawnPoint.name}");
            }

            yield return new WaitForSeconds(1f);
        }

        private IEnumerator HandleStationaryBehavior()
        {
            // Ensure guard is at assigned position
            if (!hasReachedDestination && assignedSpawnPoint != null)
            {
                SetDestinationOnce(assignedSpawnPoint.position);
                yield return new WaitForSeconds(1f);
                yield break;
            }

            // Stationary guards look around occasionally
            if (hasReachedDestination && UnityEngine.Random.Range(0f, 1f) < 0.15f)
            {
                // More natural looking around behavior
                float baseRotation = assignedSpawnPoint != null ? assignedSpawnPoint.eulerAngles.y : transform.eulerAngles.y;
                float lookDirection = baseRotation + UnityEngine.Random.Range(-60f, 60f);

                // Smooth rotation
                var targetRotation = Quaternion.Euler(0, lookDirection, 0);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 2f);
            }

            yield break;
        }

        /// <summary>
        /// Check if this guard should participate in a coordinated patrol
        /// IL2CPP-safe simplified version
        /// </summary>
        private IEnumerator CheckForPatrolOpportunity()
        {
            // Only guards from same area can patrol together
            if (!CanParticipateInPatrol()) yield break;

            // Simplified patrol logic - let PrisonNPCManager coordinate patrols
            if (PrisonNPCManager.Instance != null)
            {
                yield return PrisonNPCManager.Instance.TryAssignPatrol(this);
            }

            yield break;
        }


        /// <summary>
        /// Start coordinated patrol - simplified IL2CPP-safe version
        /// </summary>
        public void StartCoordinatedPatrol(JailGuardBehavior partner = null)
        {
            ModLogger.Info($"Guard {badgeNumber} starting coordinated patrol");

            // Convert to patrol role temporarily
            role = GuardRole.CoordinatedPatrol;

            if (partner != null)
            {
                patrolPartner = partner;
                isPatrolLeader = true;
                partner.role = GuardRole.CoordinatedPatrol;
                partner.patrolPartner = this;
                partner.isPatrolLeader = false;
                ModLogger.Info($"✓ Coordinated patrol: Leader {badgeNumber}, Partner {partner.badgeNumber}");
            }
            else
            {
                isPatrolLeader = true;
                ModLogger.Info($"✓ Solo patrol initiated: {badgeNumber}");
            }

            // Setup patrol route
            SetupPatrolRoute();
        }

        /// <summary>
        /// Check if guard can participate in patrol - IL2CPP safe
        /// </summary>
        public bool CanParticipateInPatrol()
        {
            // Must be at assigned position and on duty
            if (!isOnDuty || !hasReachedDestination) return false;

            // Only stationary guards can leave for patrol (not intake officers)
            if (role != GuardRole.GuardRoomStationary && role != GuardRole.BookingStationary) return false;

            return true;
        }

        private IEnumerator HandleWatchTowerBehavior()
        {
            // Watch tower guards just stay in position and occasionally scan
            if (hasReachedDestination && UnityEngine.Random.Range(0f, 1f) < 0.05f) // 5% chance to scan
            {
                float scanDirection = transform.eulerAngles.y + UnityEngine.Random.Range(-30f, 30f);
                transform.eulerAngles = new Vector3(0, scanDirection, 0);
            }

            // TODO: Implement camera monitoring behavior
            // This could integrate with SecurityCamera components
            yield break;
        }

        /// <summary>
        /// Check if there's a prisoner ready for intake processing
        /// </summary>
        private IEnumerator CheckForIntakeAssignment()
        {
            var bookingProcess = BookingProcess.Instance;
            if (bookingProcess != null && bookingProcess.NeedsPrisonerEscort() && !isIntakeActive)
            {
                var prisoner = bookingProcess.GetPrisonerForEscort();
                if (prisoner != null)
                {
                    StartPrisonerEscort(prisoner);
                    ModLogger.Info($"Intake officer {badgeNumber} assigned to escort {prisoner.name}");
                }
            }
            yield break;
        }

        /// <summary>
        /// Handle escorting a prisoner through the intake process
        /// </summary>
        private IEnumerator HandlePrisonerEscort()
        {
            if (currentEscortTarget == null)
            {
                EndPrisonerEscort();
                yield break;
            }

            // Check if we have waypoints to follow
            if (escortWaypoints.Count > 0)
            {
                var nextWaypoint = escortWaypoints.Peek();
                float distanceToWaypoint = Vector3.Distance(transform.position, nextWaypoint);

                // Move to waypoint if not close enough
                if (distanceToWaypoint > 2f)
                {
                    SetDestinationOnce(nextWaypoint);
                }
                else
                {
                    // Reached waypoint, proceed to next
                    escortWaypoints.Dequeue();
                    ModLogger.Debug($"Intake officer reached waypoint, {escortWaypoints.Count} remaining");

                    // Test LookController: Face the prisoner during escort
                    if (currentEscortTarget != null)
                    {
                        Vector3 prisonerEyePos = currentEscortTarget.transform.position + Vector3.up * 1.7f;
                        FaceTargetWithLookController(prisonerEyePos, 15, false, "prisoner during escort");
                    }

                    // Wait briefly at each waypoint
                    yield return new WaitForSeconds(1f);
                }
            }
            else
            {
                // No more waypoints - check if escort is complete
                var bookingProcess = BookingProcess.Instance;
                if (bookingProcess != null && bookingProcess.IsEscortComplete())
                {
                    EndPrisonerEscort();
                }
            }
        }

        /// <summary>
        /// Start escorting a prisoner using enhanced escort system
        /// </summary>
        public void StartPrisonerEscort(GameObject prisoner)
        {
            if (prisoner == null)
            {
                ModLogger.Error("Cannot start escort - prisoner is null");
                return;
            }

            // Prevent multiple escort calls
            if (isIntakeActive && currentEscortTarget != null)
            {
                ModLogger.Warn($"Escort already in progress for {currentEscortTarget.name} - ignoring new request for {prisoner.name}");
                return;
            }

            currentEscortTarget = prisoner;
            isIntakeActive = true;

            ModLogger.Info($"Intake officer {badgeNumber} starting prisoner escort for {prisoner.name}");

            // Use MelonCoroutines for both Mono and IL2CPP - they're safe in both
            MelonCoroutines.Start(HandlePrisonerEscortEnhanced(prisoner));
        }

        /// <summary>
        /// End prisoner escort and return to station
        /// </summary>
        public void EndPrisonerEscort()
        {
            currentEscortTarget = null;
            isIntakeActive = false;
            escortWaypoints.Clear();

            ModLogger.Info($"Intake officer {badgeNumber} completed escort duty");

            // Ensure npcComponent is initialized before using it
            if (npcComponent == null)
            {
                EnsureNPCComponentInitialized();
            }

            // Text messaging is optional - escort completion works without it
            if (TrySendNPCMessage("Processing complete."))
            {
                ModLogger.Debug($"Guard {badgeNumber} notified prisoner processing is complete");
            }
            else
            {
                ModLogger.Debug($"Guard {badgeNumber} completed escort (text messaging unavailable)");
            }
        }

        /// <summary>
        /// Setup waypoints for prisoner escort using GuardPoints and DoorPoints
        /// </summary>
        private void SetupEscortWaypoints()
        {
            escortWaypoints.Clear();

            var jailController = Core.ActiveJailController;
            if (jailController == null) return;

            // Enhanced escort path using actual GuardPoint positions
            var mugshotGuardPoint = FindGuardPoint("MugshotStation");
            var scannerGuardPoint = FindGuardPoint("ScannerStation");
            var storageGuardPoint = FindGuardPoint("Storage");

            if (mugshotGuardPoint != null) escortWaypoints.Enqueue(mugshotGuardPoint.position);
            if (scannerGuardPoint != null) escortWaypoints.Enqueue(scannerGuardPoint.position);
            if (storageGuardPoint != null) escortWaypoints.Enqueue(storageGuardPoint.position);

            ModLogger.Info($"Setup {escortWaypoints.Count} escort waypoints for intake officer");
        }

        /// <summary>
        /// Find GuardPoint Transform for a specific station
        /// </summary>
        private Transform FindGuardPoint(string stationName)
        {
            try
            {
                var jailController = Core.ActiveJailController;
                if (jailController == null) return null;

                // Look for GuardPoint in the station hierarchy
                Transform stationTransform = null;

                switch (stationName)
                {
                    case "MugshotStation":
                        stationTransform = jailController.transform.Find("Booking/MugshotStation");
                        break;
                    case "ScannerStation":
                        stationTransform = jailController.transform.Find("Booking/ScannerStation");
                        break;
                    case "Storage":
                        stationTransform = jailController.transform.Find("Storage");
                        break;
                }

                if (stationTransform != null)
                {
                    var guardPoint = stationTransform.Find("GuardPoint");
                    if (guardPoint != null)
                    {
                        ModLogger.Debug($"Found GuardPoint for {stationName}: {guardPoint.position}");
                        return guardPoint;
                    }
                }

                ModLogger.Warn($"GuardPoint not found for {stationName}");
                return null;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error finding GuardPoint for {stationName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Navigate to DoorPoint and open door safely
        /// </summary>
        private IEnumerator ApproachAndOpenDoor(GameObject doorObject, string doorPointName)
        {
            // Note: JailDoor is not a Component, it's managed by JailController
            // This method is deprecated in favor of direct door access through JailController
            ModLogger.Warn($"ApproachAndOpenDoor is deprecated - use JailController door system instead");
            yield break;
        }

        /// <summary>
        /// Close and optionally lock a door from current position
        /// </summary>
        private IEnumerator CloseAndLockDoor(GameObject doorObject, bool lockAfterClosing = false)
        {
            // Note: JailDoor is not a Component, it's managed by JailController
            // This method is deprecated in favor of direct door access through JailController
            ModLogger.Warn($"CloseAndLockDoor is deprecated - use JailController door system instead");
            yield break;
        }

        /// <summary>
        /// Navigate to a specific position using NavMeshAgent
        /// </summary>
        private IEnumerator NavigateToPosition(Vector3 targetPosition)
        {
            if (navAgent == null)
            {
                navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (navAgent == null)
                {
                    ModLogger.Error($"Guard {badgeNumber} has no NavMeshAgent for navigation");
                    yield break;
                }
            }

            navAgent.SetDestination(targetPosition);
            ModLogger.Debug($"Guard {badgeNumber} navigating to position {targetPosition}");

            // Wait until we reach the destination using actual distance, not remainingDistance
            float maxWaitTime = 30f; // 30 seconds max
            float startTime = Time.time;
            
            while (Time.time - startTime < maxWaitTime)
            {
                float actualDistance = Vector3.Distance(transform.position, targetPosition);
                
                if (actualDistance <= 0.7f) // Close enough to destination
                {
                    break;
                }
                
                // Check if we're stuck
                if (navAgent.pathStatus == UnityEngine.AI.NavMeshPathStatus.PathInvalid)
                {
                    ModLogger.Warn($"Guard {badgeNumber} cannot reach position {targetPosition} - invalid path");
                    break;
                }
                
                yield return new WaitForSeconds(0.1f);
            }
            
            float finalDistance = Vector3.Distance(transform.position, targetPosition);
            ModLogger.Debug($"Guard {badgeNumber} finished navigation - distance to target: {finalDistance:F2}m");
        }

        /// <summary>
        /// Check if guard has access to operate a specific door
        /// </summary>
        private bool CheckDoorAccess(GameObject doorObject)
        {
            try
            {
                // Note: JailDoor is not a Component, it's managed by JailController
                // For now, assume intake officers have access during escort duties
                if (role == GuardRole.IntakeOfficer && isIntakeActive)
                {
                    return true;
                }
                
                // Other guards have limited access based on their role
                switch (role)
                {
                    case GuardRole.GuardRoomStationary:
                    case GuardRole.BookingStationary:
                        return true; // Stationary guards have access to their areas
                    default:
                        return false;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error checking door access: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find position of a station for escort waypoints (deprecated - use FindGuardPoint instead)
        /// </summary>
        private Vector3? FindStationPosition(string stationType)
        {
            // Use GuardPoint system instead of hardcoded positions
            var guardPoint = FindGuardPoint($"{stationType}Station");
            return guardPoint?.position;
        }

        /// <summary>
        /// Simple sequential prisoner escort with explicit door handling
        /// </summary>
        private IEnumerator HandlePrisonerEscortEnhanced(GameObject prisoner)
        {
            if (prisoner == null)
            {
                ModLogger.Error("Cannot escort - prisoner is null");
                yield break;
            }

            var player = prisoner.GetComponent<Player>();
            if (player == null)
            {
                ModLogger.Error("Prisoner object has no Player component");
                yield break;
            }

            ModLogger.Info($"Starting escort for {player.name}");

            // Dynamic delay before starting escort (10-15 seconds)
            float escortDelay = UnityEngine.Random.Range(10f, 15f);
            ModLogger.Info($"Guard {badgeNumber} waiting {escortDelay:F1} seconds before starting escort");
            yield return new WaitForSeconds(escortDelay);

            // Sequential escort steps
            yield return Step1_OpenHoldingCell(player);
            yield return Step1b_SuperviseBookingStations(player);
            yield return Step2_EscortThroughInnerDoor(player);
            yield return Step3_SuperviseStorage(player);
            yield return Step4_EscortToCell(player);
            yield return Step5_ReturnToPost();

            ModLogger.Info($"✓ Escort completed for {player.name}");
            EndPrisonerEscort();
        }

        /// <summary>
        /// Step 1: Open holding cell and wait for prisoner to exit
        /// </summary>
        private IEnumerator Step1_OpenHoldingCell(Player prisoner)
        {
            ModLogger.Info($"Step 1: Opening holding cell for {prisoner.name}");
            
            var jailController = Core.ActiveJailController;
            if (jailController?.holdingCells == null || jailController.holdingCells.Count == 0)
            {
                ModLogger.Error("No holding cells found");
                yield break;
            }

            // Find prisoner's holding cell
            var holdingCell = jailController.holdingCells[0]; // Assuming first cell for now
            if (holdingCell?.cellDoor == null)
            {
                ModLogger.Error("Holding cell door not found");
                yield break;
            }

            // Use new DoorInteractionController for reliable holding cell interaction
            ShowInstruction(prisoner, "Exit the holding cell and follow me");
            yield return OpenCellDoor(holdingCell.cellDoor, prisoner);
            ModLogger.Info("✓ Step 1 complete: Holding cell secured");
        }

        /// <summary>
        /// Step 1b: Supervise prisoner at booking stations (mugshot and fingerprint)
        /// </summary>
        private IEnumerator Step1b_SuperviseBookingStations(Player prisoner)
        {
            ModLogger.Info($"Step 1b: Supervising {prisoner.name} at booking stations");

            var bookingProcess = BookingProcess.Instance;
            float maxWaitTime = 120f; // 2 minutes max
            float waitStartTime = Time.time;
            
            if (bookingProcess == null)
            {
                ModLogger.Error("Booking process not found");
                yield break;
            }

            // Position guard to supervise mugshot station first
            var mugshotGuardPoint = FindGuardPoint("MugshotStation") ?? FindGuardPoint("Mugshot");
            if (mugshotGuardPoint != null)
            {
                yield return NavigateToPosition(mugshotGuardPoint.position);
                yield return RotateToGuardPoint(mugshotGuardPoint, "MugshotStation");
                ModLogger.Info("Guard positioned to supervise mugshot station");
            }

            // Wait for player to arrive at station first
            yield return WaitForPlayerToArrive(prisoner, "mugshot station", 5f);
            
            // Wait for mugshot completion
            if (bookingProcess.mugshotStation != null && !bookingProcess.mugshotComplete)
            {
                ShowInstruction(prisoner, "Stand at the mugshot station and follow the prompts");
                ModLogger.Info("Waiting for mugshot completion...");
                
                while (!bookingProcess.mugshotComplete && (Time.time - waitStartTime) < maxWaitTime)
                {
                    // Continue checking if player is still nearby
                    if (Vector3.Distance(transform.position, prisoner.transform.position) > 15f)
                    {
                        ModLogger.Info("Player moved too far away, waiting for return...");
                        yield return WaitForPlayerToArrive(prisoner, "mugshot station", 5f);
                    }
                    yield return new WaitForSeconds(2f);
                }
                
                if (bookingProcess.mugshotComplete)
                {
                    ModLogger.Info("✓ Mugshot completed");
                }
                else
                {
                    ModLogger.Info("⏰ Mugshot timed out, proceeding anyway");
                }
            }

            // Move to fingerprint scanner supervision
            var scannerGuardPoint = FindGuardPoint("ScannerStation") ?? FindGuardPoint("Scanner") ?? FindGuardPoint("Fingerprint");
            if (scannerGuardPoint != null)
            {
                yield return NavigateToPosition(scannerGuardPoint.position);
                yield return RotateToGuardPoint(scannerGuardPoint, "ScannerStation");
                ModLogger.Info("Guard positioned to supervise fingerprint scanner");
            }

            // Wait for player to arrive at scanner first
            yield return WaitForPlayerToArrive(prisoner, "scanner station", 5f);
            
            // Wait for fingerprint completion
            if (bookingProcess.scannerStation != null && !bookingProcess.fingerprintComplete)
            {
                ShowInstruction(prisoner, "Use the fingerprint scanner");
                ModLogger.Info("Waiting for fingerprint completion...");
                
                while (!bookingProcess.fingerprintComplete && (Time.time - waitStartTime) < maxWaitTime)
                {
                    // Continue checking if player is still nearby
                    if (Vector3.Distance(transform.position, prisoner.transform.position) > 15f)
                    {
                        ModLogger.Info("Player moved too far away, waiting for return...");
                        yield return WaitForPlayerToArrive(prisoner, "scanner station", 5f);
                    }
                    yield return new WaitForSeconds(2f);
                }
                
                if (bookingProcess.fingerprintComplete)
                {
                    ModLogger.Info("✓ Fingerprint scan completed");
                }
                else
                {
                    ModLogger.Info("⏰ Fingerprint scan timed out, proceeding anyway");
                }
            }

            // Final check that both stations are complete

            if (bookingProcess.IsBookingComplete())
            {
                ModLogger.Info("✓ Step 1b complete: Booking stations completed");
            }
            else
            {
                ModLogger.Warn("Booking stations timeout - proceeding anyway");
            }
        }

        /// <summary>
        /// Step 2: Escort through inner door from booking to hall
        /// </summary>
        private IEnumerator Step2_EscortThroughInnerDoor(Player prisoner)
        {
            ModLogger.Info($"Step 2: Escorting {prisoner.name} through inner door");
            
            var jailController = Core.ActiveJailController;
            var innerDoor = jailController?.booking?.bookingInnerDoor;
            
            if (innerDoor == null)
            {
                ModLogger.Error("Booking inner door not found");
                yield break;
            }

            // Use new DoorInteractionController for reliable pass-through interaction
            ShowInstruction(prisoner, "Follow me through the door");
            yield return PassThroughDoor(innerDoor, prisoner);
            ModLogger.Info("✓ Step 2 complete: Inner door secured, in hall area");
        }

        /// <summary>
        /// Step 3: Move to storage area and supervise interactions
        /// </summary>
        private IEnumerator Step3_SuperviseStorage(Player prisoner)
        {
            ModLogger.Info($"Step 3: Supervising storage for {prisoner.name}");

            // Move to storage guard position
            var storageGuardPoint = FindGuardPoint("Storage");
            if (storageGuardPoint != null)
            {
                yield return NavigateToPosition(storageGuardPoint.position);
                transform.rotation = storageGuardPoint.rotation;
                ModLogger.Info("Guard positioned at storage area");
            }

            // Enable storage stations
            EnableStorageStation("InventoryDropOff");
            yield return new WaitForSeconds(1f);

            // Wait for player to arrive at storage area first
            yield return WaitForPlayerToArrive(prisoner, "storage area", 8f);

            // Supervise inventory drop-off
            ShowInstruction(prisoner, "Place your belongings in the tray");
            yield return WaitForStorageInteraction(prisoner, "InventoryDropOff");

            // Enable pickup station
            EnableStorageStation("InventoryPickup");
            yield return new WaitForSeconds(1f);

            // Wait a moment for the station to be ready
            yield return WaitForPlayerToArrive(prisoner, "prison items station", 8f);

            // Supervise prison items pickup
            ShowInstruction(prisoner, "Collect your prison items");
            yield return WaitForStorageInteraction(prisoner, "InventoryPickup");
            
            // Additional wait to ensure player has time to collect items
            yield return new WaitForSeconds(3f);

            ModLogger.Info("✓ Step 3 complete: Storage supervision finished");
        }

        /// <summary>
        /// Step 4: Escort to jail cell through prison entrance
        /// </summary>
        private IEnumerator Step4_EscortToCell(Player prisoner)
        {
            ModLogger.Info($"Step 4: Escorting {prisoner.name} to jail cell");

            var jailController = Core.ActiveJailController;
            var prisonEntryDoor = jailController?.booking?.prisonEntryDoor;
            
            if (prisonEntryDoor != null)
            {
                // Use new DoorInteractionController for reliable prison entry
                ShowInstruction(prisoner, "Enter the prison area");
                yield return PassThroughDoor(prisonEntryDoor, prisoner);
                ModLogger.Info("Prison entry door secured");
            }

            // Find available cell and escort prisoner
            yield return AssignAndEscortToCell(prisoner);
            ModLogger.Info("✓ Step 4 complete: Prisoner in cell");
        }

        /// <summary>
        /// Step 5: Guard returns to assigned position
        /// </summary>
        private IEnumerator Step5_ReturnToPost()
        {
            ModLogger.Info($"Step 5: Guard {badgeNumber} returning to post");

            // Check if this is a Booking Intake officer (they stay in booking area)
            bool isBookingOfficer = role == GuardRole.IntakeOfficer || 
                                   assignedSpawnPoint.name.Contains("Booking") ||
                                   badgeNumber.Contains("G1002"); // Specific check for intake officer

            if (isBookingOfficer)
            {
                ModLogger.Info($"Guard {badgeNumber} is Booking Intake officer - returning to booking area");
                // Booking officers just return to their booking position directly
                yield return GuardReturnToPost();
            }
            else
            {
                // Other guards need to go through guard door to guard station
                var jailController = Core.ActiveJailController;
                var guardDoor = jailController?.booking?.guardDoor;
                
                if (guardDoor != null)
                {
                    // Use new DoorInteractionController for reliable return to guard station
                    yield return PassThroughDoor(guardDoor, null);
                }

                // Return to original guard position
                yield return GuardReturnToPost();
            }

            ModLogger.Info("✓ Step 5 complete: Guard returned to post");
        }

        /// <summary>
        /// Assign prisoner to available cell and escort them there
        /// </summary>
        private IEnumerator AssignAndEscortToCell(Player prisoner)
        {
            var jailController = Core.ActiveJailController;
            if (jailController?.cells == null || jailController.cells.Count == 0)
            {
                ModLogger.Error("No jail cells available");
                yield break;
            }

            // Find first available cell
            CellDetail availableCell = null;
            foreach (var cell in jailController.cells)
            {
                if (cell != null && !cell.isOccupied)
                {
                    availableCell = cell;
                    break;
                }
            }

            if (availableCell == null)
            {
                ModLogger.Warn("No available jail cells found");
                yield break;
            }

            // Wait for player to arrive at the cell first
            yield return WaitForPlayerToArrive(prisoner, $"cell {availableCell.cellName}", 8f);
            
            // Use new DoorInteractionController for reliable cell door operation
            ShowInstruction(prisoner, $"Enter {availableCell.cellName}");
            yield return OpenCellDoor(availableCell.cellDoor, prisoner);

            // Wait for prisoner to actually enter the cell
            float waitStartTime = Time.time;
            float maxWaitTime = 15f; // 15 seconds to enter cell
            
            while (Time.time - waitStartTime < maxWaitTime)
            {
                // Check if prisoner is near cell position (simplified check)
                if (availableCell.spawnPoints != null && availableCell.spawnPoints.Count > 0)
                {
                    float distanceToCell = Vector3.Distance(prisoner.transform.position, availableCell.spawnPoints[0].position);
                    if (distanceToCell < 3f) // Within 3 meters of cell spawn point
                    {
                        ModLogger.Info($"Prisoner {prisoner.name} has entered {availableCell.cellName}");
                        break;
                    }
                }
                
                yield return new WaitForSeconds(1f); // Check every second
            }

            // Additional wait to ensure prisoner is settled
            yield return new WaitForSeconds(2f);

            // Mark cell as occupied
            availableCell.isOccupied = true;
            ModLogger.Info($"Prisoner {prisoner.name} secured in {availableCell.cellName}");
        }

        /// <summary>
        /// Extract prisoner from holding cell using door control
        /// </summary>
        private IEnumerator ExtractFromHoldingCell(Player prisoner)
        {
            ModLogger.Info($"Extracting prisoner {prisoner.name} from holding cell");

            // Find prisoner's holding cell - for now, use HoldingCell_00
            var jailController = Core.ActiveJailController;
            if (jailController == null)
            {
                ModLogger.Error("JailController not found for holding cell extraction");
                yield break;
            }

            // Use the JailController's door system to open holding cell doors
            if (jailController.holdingCells != null && jailController.holdingCells.Count > 0)
            {
                // Find the first available holding cell
                var holdingCell = jailController.holdingCells[0];
                if (holdingCell != null && holdingCell.cellDoor != null && holdingCell.cellDoor.IsValid())
                {
                    ModLogger.Info($"Found holding cell door: {holdingCell.cellDoor.doorName}");
                    
                    // Find the correct DoorPoint based on guard's approach side
                    var doorPoint = GetCorrectDoorPoint(holdingCell.cellDoor, "GuardRoom");
                    if (doorPoint != null)
                    {
                        // Navigate to door point first  
                        yield return NavigateToPosition(doorPoint.position);
                        
                        // Face the door point's intended direction
                        ModLogger.Debug($"Guard {badgeNumber} current rotation before: {transform.rotation.eulerAngles}");
                        ModLogger.Debug($"Guard {badgeNumber} DoorPoint target rotation: {doorPoint.rotation.eulerAngles}");
                        transform.rotation = doorPoint.rotation;
                        ModLogger.Debug($"Guard {badgeNumber} rotation after setting: {transform.rotation.eulerAngles}");
                    }

                    // Check distance before opening door
                    float distanceToDoor = Vector3.Distance(transform.position, holdingCell.cellDoor.doorHolder.position);
                    ModLogger.Debug($"Guard {badgeNumber} distance to door: {distanceToDoor:F1}m");
                    
                    // Only open door if close enough (within 8 meters - accommodates DoorPoint positioning)
                    if (distanceToDoor <= 8f && (holdingCell.cellDoor.IsClosed() || holdingCell.cellDoor.IsLocked()))
                    {
                        if (holdingCell.cellDoor.IsLocked())
                        {
                            holdingCell.cellDoor.UnlockDoor();
                            ModLogger.Debug($"Guard {badgeNumber} unlocked holding cell door");
                        }

                        // CRITICAL: Wait for prisoner to arrive before opening door
                        yield return WaitForPlayerToArrive(prisoner, "holding cell door", 8f);

                        holdingCell.cellDoor.OpenDoor();
                        ModLogger.Debug($"Guard {badgeNumber} opening holding cell door");
                        
                        // Disable NavMesh obstacle when door opens
                        SetDoorNavMeshObstacle(holdingCell.cellDoor, false);

                        // Wait for door to open
                        float waitTime = 0f;
                        while (holdingCell.cellDoor.IsAnimating() && waitTime < 5f)
                        {
                            yield return new WaitForSeconds(0.1f);
                            waitTime += 0.1f;
                        }
                    }
                }
                else
                {
                    ModLogger.Warn("Holding cell door is not valid");
                }
            }
            else
            {
                ModLogger.Warn("No holding cells found in JailController");
            }

            // Call the prisoner
            ShowDirection(prisoner, "Follow the guard");
            TrySendNPCMessage("Come with me for processing");

            // Wait for prisoner to exit cell and monitor position
            if (jailController.holdingCells != null && jailController.holdingCells.Count > 0)
            {
                var holdingCell = jailController.holdingCells[0];
                yield return MonitorPrisonerExit(prisoner, holdingCell);
            }

            ModLogger.Debug($"Prisoner {prisoner.name} extracted from holding cell");
        }

        /// <summary>
        /// Monitor prisoner exit from holding cell and close door when they're out
        /// </summary>
        private IEnumerator MonitorPrisonerExit(Player prisoner, CellDetail holdingCell)
        {
            if (prisoner == null || holdingCell?.cellDoor == null)
            {
                ModLogger.Warn("Cannot monitor prisoner exit - missing prisoner or holding cell");
                yield break;
            }

            ModLogger.Debug($"Monitoring prisoner {prisoner.name} exit from holding cell");

            // Get actual cell bounds for precise detection
            Vector3 cellCenter = Vector3.zero;
            Vector3 cellSize = Vector3.zero;
            bool validBounds = false;
            
            if (holdingCell.cellBounds != null)
            {
                cellCenter = holdingCell.cellBounds.position;
                ModLogger.Debug($"Found cellBounds at: {cellCenter}");
                
                // Try to get bounds from collider first
                var collider = holdingCell.cellBounds.GetComponent<Collider>();
                if (collider != null && collider.bounds.size.magnitude > 0.1f)
                {
                    cellSize = collider.bounds.size;
                    validBounds = true;
                    ModLogger.Debug($"Using collider bounds: {cellSize}");
                }
                else
                {
                    // Try renderer bounds
                    var renderer = holdingCell.cellBounds.GetComponent<Renderer>();
                    if (renderer != null && renderer.bounds.size.magnitude > 0.1f)
                    {
                        cellSize = renderer.bounds.size;
                        validBounds = true;
                        ModLogger.Debug($"Using renderer bounds: {cellSize}");
                    }
                    else
                    {
                        // Try local scale (this should work for your bounds objects)
                        cellSize = holdingCell.cellBounds.localScale;
                        if (cellSize.magnitude > 0.1f)
                        {
                            validBounds = true;
                            ModLogger.Debug($"Using local scale bounds: {cellSize}");
                        }
                        else
                        {
                            // Try finding HoldingCellBounds[0] child manually
                            var boundsChild = holdingCell.cellTransform?.Find("HoldingCellBounds[0]");
                            if (boundsChild != null)
                            {
                                cellCenter = boundsChild.position;
                                cellSize = boundsChild.localScale;
                                validBounds = true;
                                ModLogger.Debug($"Found bounds child manually: {cellCenter}, scale: {cellSize}");
                            }
                        }
                    }
                }
            }
            
            if (!validBounds)
            {
                // Try to find bounds by GameObject name search
                var boundsGameObject = GameObject.Find("HoldingCellBounds[0]");
                if (boundsGameObject != null)
                {
                    cellCenter = boundsGameObject.transform.position;
                    cellSize = boundsGameObject.transform.localScale;
                    validBounds = true;
                    ModLogger.Debug($"Found bounds by name search: center={cellCenter}, scale={cellSize}");
                }
                else
                {
                    // Fallback to door position with reasonable cell dimensions
                    cellCenter = holdingCell.cellDoor.doorHolder?.position ?? transform.position;
                    cellSize = new Vector3(8f, 4f, 8f); // Reasonable holding cell size
                    ModLogger.Debug($"Using fallback bounds: center={cellCenter}, size={cellSize}");
                }
            }

            ModLogger.Info($"Cell bounds - Center: {cellCenter}, Size: {cellSize}");

            // Monitor prisoner position
            float monitorStartTime = Time.time;
            float maxMonitorTime = 30f; // Maximum time to monitor
            bool prisonerExited = false;
            int checkCount = 0;

            while (!prisonerExited && Time.time - monitorStartTime < maxMonitorTime)
            {
                checkCount++;
                
                // Check if prisoner is outside the cell bounds (using box bounds check)
                Vector3 prisonerPos = prisoner.transform.position;
                Vector3 relativePos = prisonerPos - cellCenter;
                
                // Check if prisoner is outside any axis bounds (with small buffer)
                float buffer = 1f; // 1m buffer outside cell
                bool outsideX = Mathf.Abs(relativePos.x) > (cellSize.x / 2f + buffer);
                bool outsideZ = Mathf.Abs(relativePos.z) > (cellSize.z / 2f + buffer);
                
                // Fallback: simple distance check in case bounds detection fails
                float distanceFromCenter = Vector3.Distance(prisonerPos, cellCenter);
                bool outsideDistance = distanceFromCenter > 5f; // 5m from center
                
                // Log position every 10 checks for debugging
                if (checkCount % 10 == 0)
                {
                    ModLogger.Info($"Position check #{checkCount}: Prisoner at {prisonerPos}, relative {relativePos}, distance={distanceFromCenter:F1}m, outsideX={outsideX}, outsideZ={outsideZ}, outsideDistance={outsideDistance}");
                }
                
                if (outsideX || outsideZ || outsideDistance)
                {
                    prisonerExited = true;
                    ModLogger.Info($"Prisoner {prisoner.name} has exited holding cell bounds after {checkCount} checks (position: {prisonerPos})");
                }
                else
                {
                    // Still inside, check more frequently for responsive detection
                    yield return new WaitForSeconds(0.2f);
                }
            }
            
            if (!prisonerExited)
            {
                ModLogger.Warn($"Prisoner exit detection timed out after {maxMonitorTime}s and {checkCount} position checks");
            }

            // Close the holding cell door now that prisoner is out
            if (prisonerExited || Time.time - monitorStartTime >= maxMonitorTime)
            {
                yield return new WaitForSeconds(1f); // Brief delay to ensure prisoner is clear

                if (holdingCell.cellDoor.IsOpen())
                {
                    holdingCell.cellDoor.CloseDoor();
                    ModLogger.Debug($"Guard {badgeNumber} closed holding cell door after prisoner exit");

                    // Wait for door to close completely
                    float waitTime = 0f;
                    while (holdingCell.cellDoor.IsAnimating() && waitTime < 5f)
                    {
                        yield return new WaitForSeconds(0.1f);
                        waitTime += 0.1f;
                    }

                    // Re-enable NavMesh obstacle when door closes
                    SetDoorNavMeshObstacle(holdingCell.cellDoor, true);

                    // Lock the door for security
                    if (holdingCell.cellDoor.IsClosed())
                    {
                        holdingCell.cellDoor.LockDoor();
                        ModLogger.Debug($"Guard {badgeNumber} locked holding cell door");
                    }
                }
            }
        }

        /// <summary>
        /// Escort prisoner to mugshot station and supervise
        /// </summary>
        private IEnumerator EscortToMugshotStation(Player prisoner)
        {
            var mugshotGuardPoint = FindGuardPoint("MugshotStation");
            if (mugshotGuardPoint == null)
            {
                ModLogger.Error("Could not find MugshotStation GuardPoint");
                yield break;
            }

            ModLogger.Info($"Escorting {prisoner.name} to mugshot station");

            // Navigate to guard point at mugshot station
            yield return NavigateToPosition(mugshotGuardPoint.position);
            
            // Face the guard point's intended direction with smooth rotation
            yield return RotateToGuardPoint(mugshotGuardPoint, "MugshotStation");

            // Enable the mugshot station and provide instructions
            EnableStationInteraction("MugshotStation");
            ShowInstruction(prisoner, "Stand in front of the camera");

            // Wait at guard point until mugshot is complete
            while (!IsStationComplete(prisoner, "Mugshot"))
            {
                yield return new WaitForSeconds(1f);
            }

            // Acknowledge completion
            ShowProgress(prisoner, "Mugshot complete");
            ModLogger.Debug($"Mugshot completed for {prisoner.name}");
        }

        /// <summary>
        /// Escort prisoner to scanner station and supervise
        /// </summary>
        private IEnumerator EscortToScannerStation(Player prisoner)
        {
            var scannerGuardPoint = FindGuardPoint("ScannerStation");
            if (scannerGuardPoint == null)
            {
                ModLogger.Error("Could not find ScannerStation GuardPoint");
                yield break;
            }

            ModLogger.Info($"Escorting {prisoner.name} to scanner station");

            // Navigate to guard point at scanner station
            yield return NavigateToPosition(scannerGuardPoint.position);
            
            // Face the guard point's intended direction with smooth rotation
            yield return RotateToGuardPoint(scannerGuardPoint, "ScannerStation");

            // Enable the scanner station and provide instructions
            EnableStationInteraction("ScannerStation");
            ShowInstruction(prisoner, "Place your hand on the scanner");

            // Wait at guard point until scan is complete
            while (!IsStationComplete(prisoner, "Scanner"))
            {
                yield return new WaitForSeconds(1f);
            }

            // Acknowledge completion
            ShowProgress(prisoner, "Fingerprint scan complete");
            ModLogger.Debug($"Fingerprint scan completed for {prisoner.name}");
        }

        /// <summary>
        /// Escort prisoner to storage for inventory processing
        /// </summary>
        private IEnumerator EscortToStorage(Player prisoner)
        {
            ModLogger.Info($"Escorting {prisoner.name} to storage for inventory processing");

            // First, open the inner door to access storage using the concrete booking structure
            var jailController = Core.ActiveJailController;
            
            if (jailController?.booking?.bookingInnerDoor != null && jailController.booking.bookingInnerDoor.IsValid())
            {
                var innerDoor = jailController.booking.bookingInnerDoor;
                ModLogger.Debug($"Found booking inner door: {innerDoor.doorName}");
                
                // Use DoorPoint_Booking to approach from booking side for storage access
                var doorPoint = GetCorrectDoorPoint(innerDoor, "Booking");
                if (doorPoint != null)
                {
                    // Navigate to door point first
                    yield return NavigateToPosition(doorPoint.position);
                    
                    // Face the door point's intended direction
                    ModLogger.Debug($"Guard {badgeNumber} current rotation before: {transform.rotation.eulerAngles}");
                    ModLogger.Debug($"Guard {badgeNumber} Inner Door DoorPoint target rotation: {doorPoint.rotation.eulerAngles}");
                    transform.rotation = doorPoint.rotation;
                    ModLogger.Debug($"Guard {badgeNumber} rotation after setting: {transform.rotation.eulerAngles}");
                    
                    // Verify guard is close enough to door to operate it safely
                    float distanceToDoor = Vector3.Distance(transform.position, innerDoor.doorHolder.position);
                    ModLogger.Debug($"Guard {badgeNumber} distance to inner door: {distanceToDoor:F1}m");
                    
                    if (distanceToDoor <= 5f) // Must be within 5 meters to safely operate door
                    {
                        ModLogger.Debug($"Guard {badgeNumber} at safe distance, opening inner door");
                        
                        // Prison protocol: Unlock, open, pass through, wait for prisoner, then close and lock
                        if (innerDoor.IsLocked())
                        {
                            innerDoor.UnlockDoor();
                            ModLogger.Debug($"Guard {badgeNumber} unlocked inner door");
                        }

                        // CRITICAL: Wait for player to arrive before opening door
                        yield return WaitForPlayerToArrive(prisoner, "inner door", 8f);

                        innerDoor.OpenDoor();
                        ModLogger.Debug($"Guard {badgeNumber} opening inner door");
                        
                        // Disable NavMesh obstacle when door opens
                        SetDoorNavMeshObstacle(innerDoor, false);

                        // Wait for door to open
                        yield return new WaitForSeconds(2f);
                        
                        // Move through to the Hall side (DoorPoint_Hall)
                        var hallDoorPoint = GetCorrectDoorPoint(innerDoor, "Hall");
                        if (hallDoorPoint != null)
                        {
                            ModLogger.Debug($"Guard {badgeNumber} moving through inner door to hall side");
                            yield return NavigateToPosition(hallDoorPoint.position);
                            transform.rotation = hallDoorPoint.rotation;
                        }
                        
                        // Wait for prisoner to follow through
                        ShowInstruction(prisoner, "Follow me through the door");
                        
                        // Wait for player to actually pass through the door
                        yield return WaitForPlayerToArrive(prisoner, "other side of inner door", 12f);
                        yield return new WaitForSeconds(2f); // Additional buffer time
                        
                        // Close and lock door behind us (from Hall side)
                        innerDoor.CloseDoor();
                        SetDoorNavMeshObstacle(innerDoor, true);
                        innerDoor.LockDoor();
                        ModLogger.Debug($"Guard {badgeNumber} closed and locked inner door from Hall side");
                    }
                    else
                    {
                        ModLogger.Warn($"Guard {badgeNumber} too far from inner door ({distanceToDoor:F1}m) - cannot safely operate door");
                    }
                }
                else
                {
                    ModLogger.Error("Could not find DoorPoint_Booking for inner door access");
                }
            }
            else
            {
                ModLogger.Error("Could not access Booking Inner Door through concrete booking structure");
            }

            // Navigate to storage guard point
            var storageGuardPoint = FindGuardPoint("Storage");
            if (storageGuardPoint != null)
            {
                yield return NavigateToPosition(storageGuardPoint.position);
                
                // Face the storage guard point's intended direction
                ModLogger.Debug($"Guard {badgeNumber} current rotation before: {transform.rotation.eulerAngles}");
                ModLogger.Debug($"Guard {badgeNumber} Storage GuardPoint target rotation: {storageGuardPoint.rotation.eulerAngles}");
                transform.rotation = storageGuardPoint.rotation;
                ModLogger.Debug($"Guard {badgeNumber} rotation after setting: {transform.rotation.eulerAngles}");
            }
            else
            {
                ModLogger.Warn("Could not find Storage GuardPoint");
            }

            // Wait for player to arrive at storage area first
            yield return WaitForPlayerToArrive(prisoner, "storage area", 8f);

            // Supervise inventory drop-off
            ShowInstruction(prisoner, "Place your belongings in the tray");
            yield return WaitForStorageInteraction(prisoner, "InventoryDropOff");

            // Wait for player to get ready for pickup
            yield return WaitForPlayerToArrive(prisoner, "prison items station", 8f);

            // Supervise inventory pickup
            ShowInstruction(prisoner, "Collect your prison items");
            yield return WaitForStorageInteraction(prisoner, "InventoryPickup");

            ShowProgress(prisoner, "Inventory processing complete");
            ModLogger.Debug($"Storage processing completed for {prisoner.name}");
        }

        /// <summary>
        /// Escort prisoner to their assigned jail cell after processing
        /// </summary>
        private IEnumerator EscortToJailCell(Player prisoner)
        {
            ModLogger.Info($"Escorting {prisoner.name} to assigned jail cell");

            // Find an available jail cell
            var jailController = Core.ActiveJailController;
            if (jailController?.cells != null && jailController.cells.Count > 0)
            {
                // Find the first available cell
                var availableCell = jailController.cells.FirstOrDefault(cell => cell != null && !cell.isOccupied);
                if (availableCell != null)
                {
                    ModLogger.Debug($"Found available jail cell: {availableCell.cellName}");

                    // Navigate back through storage -> booking -> prison area to reach jail cells
                    yield return NavigateToJailCells();

                    // Guard stays OUTSIDE the cell - navigate to door area but not inside
                    var cellGuardPoint = FindGuardPoint($"Cell_{availableCell.cellName}") ?? FindGuardPoint("JailCells");
                    if (cellGuardPoint != null)
                    {
                        yield return NavigateToPosition(cellGuardPoint.position);
                        transform.rotation = cellGuardPoint.rotation;
                    }
                    else if (availableCell.cellDoor?.doorHolder != null)
                    {
                        // Position guard OUTSIDE cell door (3 meters back from door)
                        Vector3 doorDirection = availableCell.cellDoor.doorHolder.forward;
                        Vector3 guardPosition = availableCell.cellDoor.doorHolder.position - doorDirection * 3f;
                        yield return NavigateToPosition(guardPosition);
                        
                        // Face the door
                        transform.LookAt(availableCell.cellDoor.doorHolder.position);
                        ModLogger.Debug($"Guard positioned outside cell door at safe distance");
                    }

                    // Open the jail cell door from OUTSIDE
                    if (availableCell.cellDoor != null && availableCell.cellDoor.IsValid())
                    {
                        ModLogger.Debug($"Opening jail cell door: {availableCell.cellDoor.doorName}");
                        
                        if (availableCell.cellDoor.IsLocked())
                        {
                            availableCell.cellDoor.UnlockDoor();
                        }
                        
                        // CRITICAL: Wait for player to arrive before opening cell door
                        yield return WaitForPlayerToArrive(prisoner, $"cell {availableCell.cellName}", 8f);
                        
                        availableCell.cellDoor.OpenDoor();
                        SetDoorNavMeshObstacle(availableCell.cellDoor, false);
                        yield return new WaitForSeconds(2f);
                    }

                    // Wait for prisoner to arrive at cell before instructing entry
                    yield return WaitForPlayerToArrive(prisoner, $"cell {availableCell.cellName}", 8f);
                    
                    // Direct prisoner to enter the cell while guard stays outside
                    ShowInstruction(prisoner, $"Enter cell {availableCell.cellName}. This is your assigned cell.");
                    yield return new WaitForSeconds(5f); // Give prisoner time to enter
                    
                    // Wait for prisoner to be inside, then close door (guard still outside)
                    ShowInstruction(prisoner, "Make yourself comfortable. You'll be here for a while.");
                    yield return new WaitForSeconds(3f);

                    // Close and lock the cell door from outside
                    if (availableCell.cellDoor != null)
                    {
                        availableCell.cellDoor.CloseDoor();
                        yield return new WaitForSeconds(1f);
                        availableCell.cellDoor.LockDoor();
                        SetDoorNavMeshObstacle(availableCell.cellDoor, true);
                        
                        // Mark cell as occupied
                        availableCell.isOccupied = true;
                        ModLogger.Info($"Prisoner {prisoner.name} secured in cell {availableCell.cellName}");
                        ModLogger.Debug($"Guard {badgeNumber} secured cell door from outside - remaining outside cell");
                    }
                }
                else
                {
                    ModLogger.Warn("No available jail cells found - prisoner remains in booking area");
                    ShowInstruction(prisoner, "All cells are occupied. You'll need to wait in the booking area.");
                }
            }
            else
            {
                ModLogger.Error("No jail cells configured in JailController");
                ShowInstruction(prisoner, "There are no jail cells available. Contact administration.");
            }
        }

        /// <summary>
        /// Navigate guard through doors from storage back to jail cells area
        /// </summary>
        private IEnumerator NavigateToJailCells()
        {
            ModLogger.Debug($"Guard {badgeNumber} navigating from storage to jail cells area");

            // Phase 1: Exit storage through inner door (Storage -> Booking Hall using Hall side)
            yield return ExitThroughDoor("Booking_InnerDoor", "Hall", "BookingHall");

            // Phase 2: Navigate through booking area to prison (using Prison_EnterDoor to Prison side)
            yield return ExitThroughDoor("Prison_EnterDoor", "Prison", "Prison");

            // Phase 3: Navigate to jail cells area
            var jailCellsPoint = FindGuardPoint("JailCells") ?? FindGuardPoint("Prison");
            if (jailCellsPoint != null)
            {
                ModLogger.Debug($"Guard {badgeNumber} navigating to jail cells area");
                yield return NavigateToPosition(jailCellsPoint.position);
                transform.rotation = jailCellsPoint.rotation;
            }
        }

        /// <summary>
        /// Helper methods for enhanced escort system
        /// </summary>
        private void EnableStationInteraction(string stationName)
        {
            // Enable interaction at the specified station
            // This will be integrated with the station components later
            ModLogger.Debug($"Guard {badgeNumber} enabling {stationName} interaction");
        }

        private bool IsStationComplete(Player prisoner, string stationType)
        {
            // Check if the specified station has been completed for this prisoner
            var bookingProcess = BookingProcess.Instance;
            if (bookingProcess == null)
            {
                ModLogger.Debug($"BookingProcess not available for station check: {stationType}");
                return false;
            }

            switch (stationType)
            {
                case "Mugshot":
                    return bookingProcess.mugshotComplete;
                case "Scanner":
                    return bookingProcess.fingerprintComplete;
                default:
                    ModLogger.Warn($"Unknown station type for completion check: {stationType}");
                    return false;
            }
        }

        /// <summary>
        /// Check if booking stations (mugshot and fingerprint) are complete, but not inventory
        /// </summary>
        /// <summary>
        /// Check if storage processing is complete (both inventory drop-off and pickup)
        /// </summary>
        private bool IsStorageComplete(Player player)
        {
            var booking = BookingProcess.Instance;
            if (booking == null) return false;
            
            bool dropComplete = booking.inventoryDropOffComplete;
            bool pickupComplete = booking.inventoryProcessed;
            
            ModLogger.Debug($"Storage completion check - Drop: {dropComplete}, Pickup: {pickupComplete}");
            return dropComplete && pickupComplete;
        }

        private bool AreStationsComplete(Player prisoner)
        {
            var bookingProcess = BookingProcess.Instance;
            if (bookingProcess == null)
            {
                ModLogger.Debug($"BookingProcess not available for stations check");
                return false;
            }

            // Check if mugshot and fingerprint are complete (but not inventory drop-off)
            bool stationsComplete = bookingProcess.mugshotComplete && bookingProcess.fingerprintComplete;
            ModLogger.Debug($"Stations complete check for {prisoner.name}: mugshot={bookingProcess.mugshotComplete}, fingerprint={bookingProcess.fingerprintComplete}, stationsComplete={stationsComplete}");
            return stationsComplete;
        }

        private bool IsBookingComplete(Player prisoner)
        {
            // Check if both booking stations are complete via the BookingProcess system
            var bookingProcess = BookingProcess.Instance;
            if (bookingProcess != null && bookingProcess.IsBookingComplete())
            {
                ModLogger.Debug($"Booking process confirmed complete for {prisoner.name}");
                return true;
            }
            
            ModLogger.Debug($"Booking process not yet complete for {prisoner.name}");
            return false;
        }

        private IEnumerator WaitForStorageInteraction(Player prisoner, string interactionType)
        {
            ModLogger.Debug($"Guard {badgeNumber} enabling {interactionType} station for {prisoner.name}");

            // Enable the specific storage station
            EnableStorageStation(interactionType);

            // Wait for the interaction to complete with timeout
            float timeout = 60f; // 60 second timeout
            float startTime = Time.time;
            
            if (interactionType == "InventoryDropOff")
            {
                ModLogger.Debug($"Waiting for inventory drop-off completion...");
                while (!BookingProcess.Instance.inventoryDropOffComplete && Time.time - startTime < timeout)
                {
                    yield return new WaitForSeconds(0.5f);
                }
                
                if (BookingProcess.Instance.inventoryDropOffComplete)
                {
                    ModLogger.Debug($"✓ Inventory drop-off completed for {prisoner.name}");
                }
                else
                {
                    ModLogger.Warn($"⚠ Inventory drop-off timed out for {prisoner.name}");
                }
            }
            else if (interactionType == "InventoryPickup")
            {
                ModLogger.Debug($"Waiting for prison items pickup completion...");
                while (!BookingProcess.Instance.inventoryProcessed && Time.time - startTime < timeout)
                {
                    yield return new WaitForSeconds(0.5f);
                }
                
                if (BookingProcess.Instance.inventoryProcessed)
                {
                    ModLogger.Debug($"✓ Prison items pickup completed for {prisoner.name}");
                }
                else
                {
                    ModLogger.Warn($"⚠ Prison items pickup timed out for {prisoner.name}");
                }
            }
        }

        /// <summary>
        /// Enable specific storage station for prisoner interaction
        /// </summary>
        private void EnableStorageStation(string stationType)
        {
            try
            {
                var bookingProcess = BookingProcess.Instance;
                if (bookingProcess == null)
                {
                    ModLogger.Error("BookingProcess.Instance is null, cannot enable storage station");
                    return;
                }

                if (stationType == "InventoryDropOff")
                {
                    if (bookingProcess.inventoryDropOffStation != null)
                    {
                        // Force enable the station by setting it to allow interactions
                        bookingProcess.inventoryDropOffStation.enabled = true;
                        
                        // Set a flag to allow storage interactions even after booking completion
                        SetStorageInteractionAllowed(true);
                        
                        ModLogger.Info($"✓ Enabled InventoryDropOff station for storage interaction");
                    }
                    else
                    {
                        ModLogger.Error("InventoryDropOff station not found in BookingProcess");
                    }
                }
                else if (stationType == "InventoryPickup")
                {
                    // For InventoryPickup, we need to find the JailInventoryPickupStation component directly
                    var pickupStation = UnityEngine.Object.FindObjectOfType<Behind_Bars.Systems.Jail.JailInventoryPickupStation>();
                    if (pickupStation != null)
                    {
                        pickupStation.enabled = true;
                        SetStorageInteractionAllowed(true);
                        ModLogger.Info($"✓ Enabled JailInventoryPickupStation for storage interaction");
                    }
                    else
                    {
                        ModLogger.Error("JailInventoryPickupStation not found in scene");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Failed to enable storage station {stationType}: {ex.Message}");
            }
        }

        /// <summary>
        /// Set flag to allow storage interactions regardless of booking status
        /// </summary>
        private void SetStorageInteractionAllowed(bool allowed)
        {
            var bookingProcess = BookingProcess.Instance;
            if (bookingProcess != null)
            {
                bookingProcess.storageInteractionAllowed = allowed;
                ModLogger.Info($"✓ Storage interaction allowed set to: {allowed}");
            }
            else
            {
                ModLogger.Error("BookingProcess.Instance is null, cannot set storage interaction flag");
            }
        }

        private void ShowInstruction(Player prisoner, string message)
        {
            // Show instruction notification to the player
            ModLogger.Debug($"Guard {badgeNumber} instructed prisoner: {message}");
            // UI notification will be integrated with the actual UI system later
        }

        private void ShowProgress(Player prisoner, string message)
        {
            ModLogger.Debug($"Guard {badgeNumber} progress update: {message}");
            // UI notification will be integrated with the actual UI system later
        }

        private void ShowDirection(Player prisoner, string message)
        {
            ModLogger.Debug($"Guard {badgeNumber} direction: {message}");
            // UI notification will be integrated with the actual UI system later
        }

        private IEnumerator HandleResponseBehavior()
        {
            // Response guards just idle in position waiting for alerts
            // Only move if there's an actual incident (not implemented yet)

            // TODO: Implement response to jail alerts/incidents
            yield break;
        }

        public void OnPlayerInteraction(GameObject player)
        {
            try
            {
                if (npcComponent == null) return;

                string message = GetGuardInteractionMessage();
                npcComponent.SendTextMessage(message);

                // Create response options
                var responses = CreateGuardResponseOptions(player);
                if (responses.Length > 0)
                {
                    MelonCoroutines.Start(ShowResponsesAfterDelay(responses, 1f));
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error in guard interaction: {e.Message}");
            }
        }

        private string GetGuardInteractionMessage()
        {
            switch (role)
            {
                case GuardRole.GuardRoomStationary:
                    return $"Officer {badgeNumber}: You need something, inmate?";
                case GuardRole.BookingStationary:
                    return $"Officer {badgeNumber}: Keep moving, nothing to see here.";
                case GuardRole.IntakeOfficer:
                    if (isIntakeActive)
                        return $"Officer {badgeNumber}: Follow me for processing.";
                    else
                        return $"Officer {badgeNumber}: Report to intake when called.";
                case GuardRole.CoordinatedPatrol:
                    return $"Officer {badgeNumber}: On patrol - stay back.";
                case GuardRole.ResponseGuard:
                    return $"Response Officer {badgeNumber}: Any trouble here?";
                default:
                    return $"Guard {badgeNumber}: What do you want?";
            }
        }

#if !MONO
        private Il2CppScheduleOne.Messaging.Response[] CreateGuardResponseOptions(GameObject player)
#else
        private ScheduleOne.Messaging.Response[] CreateGuardResponseOptions(GameObject player)
#endif
        {
            var responseList = new System.Collections.Generic.List<
#if !MONO
                Il2CppScheduleOne.Messaging.Response
#else
                ScheduleOne.Messaging.Response
#endif
            >();

            // Basic interaction responses
#if !MONO
            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "I need medical attention",
                label = "medical_request",
                callback = new System.Action(() => HandleMedicalRequest(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "When is my next meal?",
                label = "meal_request",
                callback = new System.Action(() => HandleMealInquiry(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "Nothing, officer",
                label = "dismiss",
                callback = new System.Action(() => HandleDismiss(player))
            });
#else
            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "I need medical attention",
                label = "medical_request",
                callback = () => HandleMedicalRequest(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "When is my next meal?",
                label = "meal_request",
                callback = () => HandleMealInquiry(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "Nothing, officer",
                label = "dismiss",
                callback = () => HandleDismiss(player)
            });
#endif

            return responseList.ToArray();
        }

        private IEnumerator ShowResponsesAfterDelay(
#if !MONO
            Il2CppScheduleOne.Messaging.Response[] responses,
#else
            ScheduleOne.Messaging.Response[] responses,
#endif
            float delay)
        {
            yield return new WaitForSeconds(delay);

            if (npcComponent != null && npcComponent.MSGConversation != null)
            {
                npcComponent.MSGConversation.ClearResponses();
#if !MONO
                var responsesList = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Messaging.Response>();
                foreach (var response in responses)
                {
                    responsesList.Add(response);
                }
#else
                var responsesList = new System.Collections.Generic.List<ScheduleOne.Messaging.Response>(responses);
#endif
                npcComponent.MSGConversation.ShowResponses(responsesList, 0f, true);
            }
        }

        private void HandleMedicalRequest(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Medical requests go through the warden. Submit a formal request.");
            }
        }

        private void HandleMealInquiry(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Meals are served at 8 AM, 12 PM, and 6 PM. Don't be late.");
            }
        }

        private void HandleDismiss(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Stay out of trouble.");
            }
        }

        private string GenerateBadgeNumber()
        {
            return $"G{UnityEngine.Random.Range(1000, 9999)}";
        }

        public void EndShift()
        {
            isOnDuty = false;

            // Clean up any active escort
            if (isIntakeActive)
            {
                EndPrisonerEscort();
            }

            // Unregister from PrisonNPCManager
            if (PrisonNPCManager.Instance != null)
            {
                PrisonNPCManager.Instance.UnregisterGuard(this);
            }

            // End any ongoing patrol
            if (role == GuardRole.CoordinatedPatrol)
            {
                MelonCoroutines.Start(EndCoordinatedPatrol());
            }

            ModLogger.Info($"Guard {badgeNumber} ended their shift");
        }

        public bool IsOnDuty() => isOnDuty;

        public GuardRole GetRole() => role;

        public GuardAssignment GetAssignment() => assignment;

        public string GetBadgeNumber() => badgeNumber;

        public bool IsIntakeOfficer() => role == GuardRole.IntakeOfficer;

        public bool IsAvailableForIntake() => role == GuardRole.IntakeOfficer && !isIntakeActive;

        public GameObject GetCurrentEscortTarget() => currentEscortTarget;

        /// <summary>
        /// Navigate guard back to safe position after completing escort
        /// </summary>
        private IEnumerator GuardReturnToPost()
        {
            ModLogger.Debug($"Guard {badgeNumber} returning to post after escort completion");

            // Navigate the guard back through the proper exit path from Storage → Hall → Prison → GuardRoom
            yield return NavigateExitPath();

            // Find a safe guard position to return to
            var guardSpawn = FindGuardSpawnPosition();
            if (guardSpawn != null)
            {
                ModLogger.Debug($"Guard {badgeNumber} returning to spawn position: {guardSpawn.position}");
                yield return NavigateToPosition(guardSpawn.position);
                
                // Face the spawn point's intended direction
                transform.rotation = guardSpawn.rotation;
                ModLogger.Debug($"Guard {badgeNumber} returned to post and facing spawn direction");
            }
            else
            {
                // Fallback: prioritize booking area for intake officers
                bool isBookingOfficer = role == GuardRole.IntakeOfficer || 
                                       assignedSpawnPoint.name.Contains("Booking") ||
                                       badgeNumber.Contains("G1002");

                var fallbackPoint = isBookingOfficer ? 
                    (FindGuardPoint("Booking") ?? FindGuardPoint("GuardRoom")) :
                    (FindGuardPoint("GuardRoom") ?? FindGuardPoint("Booking"));
                    
                if (fallbackPoint != null)
                {
                    string areaName = isBookingOfficer ? "booking area" : "guard room area";
                    ModLogger.Debug($"Guard {badgeNumber} returning to {areaName}");
                    yield return NavigateToPosition(fallbackPoint.position);
                    transform.rotation = fallbackPoint.rotation;
                }
                else
                {
                    ModLogger.Warn($"Guard {badgeNumber} could not find safe return position");
                }
            }
        }

        /// <summary>
        /// Navigate guard through the proper exit path with door management
        /// </summary>
        private IEnumerator NavigateExitPath()
        {
            ModLogger.Debug($"Guard {badgeNumber} navigating exit path from storage");

            // Phase 1: Exit from Storage to Hall (through Booking_InnerDoor using Hall side)
            yield return ExitThroughDoor("Booking_InnerDoor", "Hall", "BookingHall");

            // Phase 2: Exit from Hall to Prison (through Prison_EnterDoor using Prison side)
            yield return ExitThroughDoor("Prison_EnterDoor", "Prison", "Prison");

            // Phase 3: Navigate to GuardRoom area
            var guardRoomPoint = FindGuardPoint("GuardRoom");
            if (guardRoomPoint != null)
            {
                ModLogger.Debug($"Guard {badgeNumber} navigating to guard room");
                yield return NavigateToPosition(guardRoomPoint.position);
                transform.rotation = guardRoomPoint.rotation;
            }
        }

        /// <summary>
        /// Handle door opening and navigation for guard exit using concrete door structure
        /// </summary>
        private IEnumerator ExitThroughDoor(string doorName, string targetDoorPointName, string destinationAreaName)
        {
            ModLogger.Debug($"Guard {badgeNumber} exiting through door: {doorName} to {destinationAreaName}");

            var jailController = Core.ActiveJailController;
            JailDoor doorToUse = null;

            // Use concrete door structure based on door name
            if (doorName.Contains("InnerDoor") && jailController?.booking?.bookingInnerDoor != null)
            {
                doorToUse = jailController.booking.bookingInnerDoor;
                ModLogger.Debug($"Using concrete booking inner door: {doorToUse.doorName}");
            }
            else if (doorName.Contains("EnterDoor") && jailController?.booking?.prisonEntryDoor != null)
            {
                doorToUse = jailController.booking.prisonEntryDoor;
                ModLogger.Debug($"Using concrete prison entry door: {doorToUse.doorName}");
            }
            else if (doorName.Contains("GuardDoor") && jailController?.booking?.guardDoor != null)
            {
                doorToUse = jailController.booking.guardDoor;
                ModLogger.Debug($"Using concrete guard door: {doorToUse.doorName}");
            }

            if (doorToUse == null || !doorToUse.IsValid())
            {
                ModLogger.Warn($"Could not find concrete door {doorName} for guard exit");
                yield break;
            }

            // Find the target door point for proper positioning
            var doorPoint = GetCorrectDoorPoint(doorToUse, targetDoorPointName);
            if (doorPoint == null)
            {
                ModLogger.Warn($"Could not find door point {targetDoorPointName} for {doorName}");
                yield break;
            }

            // Navigate to the door point first
            ModLogger.Debug($"Guard {badgeNumber} moving to door point for {doorName}");
            yield return NavigateToPosition(doorPoint.position);

            // Face the door point's direction
            transform.rotation = doorPoint.rotation;

            // Check if we're close enough to open the door safely
            float distanceToDoor = Vector3.Distance(transform.position, doorToUse.doorHolder.position);
            ModLogger.Debug($"Guard {badgeNumber} distance to {doorName}: {distanceToDoor:F1}m");
            if (distanceToDoor <= 5f) // Consistent 5-meter safety limit
            {
                ModLogger.Debug($"Guard {badgeNumber} opening door {doorName} for exit");
                
                if (doorToUse.IsLocked())
                {
                    doorToUse.UnlockDoor();
                }
                
                doorToUse.OpenDoor();
                SetDoorNavMeshObstacle(doorToUse, false);
                yield return new WaitForSeconds(2f); // Wait for door animation
                // Move through the door to the destination area
                if (destinationAreaName == "Hall")
                {
                    var hallPoint = FindGuardPoint("Hall") ?? FindGuardPoint("Booking");
                    if (hallPoint != null)
                    {
                        yield return NavigateToPosition(hallPoint.position);
                    }
                }
                else if (destinationAreaName == "Prison")
                {
                    var prisonPoint = FindGuardPoint("Prison") ?? FindGuardPoint("GuardRoom");
                    if (prisonPoint != null)
                    {
                        yield return NavigateToPosition(prisonPoint.position);
                    }
                }
                
                // Close the door behind us after a delay
                MelonCoroutines.Start(CloseDoorAfterDelay(doorToUse, 3f));
            }
            else
            {
                ModLogger.Warn($"Guard {badgeNumber} too far from door {doorName} ({distanceToDoor:F1}m) to open it");
            }
        }

        /// <summary>
        /// Find door info from various potential locations
        /// </summary>
        private JailDoor FindDoorInfo(string doorName)
        {
            var jailController = Core.ActiveJailController;
            if (jailController == null) return null;

            // Check holding cells
            if (jailController.holdingCells != null)
            {
                foreach (var cell in jailController.holdingCells)
                {
                    if (cell.cellDoor != null && cell.cellDoor.doorName.Contains(doorName))
                    {
                        return cell.cellDoor;
                    }
                }
            }

            // Check regular jail cells (using cells instead of jailCells)
            if (jailController.cells != null)
            {
                foreach (var cell in jailController.cells)
                {
                    if (cell.cellDoor != null && cell.cellDoor.doorName.Contains(doorName))
                    {
                        return cell.cellDoor;
                    }
                }
            }

            // For Booking doors, create a temporary door info if needed
            var doorGameObject = GameObject.Find($"Booking/{doorName}");
            if (doorGameObject != null)
            {
                // Create a temporary JailDoor for booking doors
                return new JailDoor
                {
                    doorName = doorName,
                    doorInstance = doorGameObject
                };
            }

            return null;
        }

        /// <summary>
        /// Close door after delay
        /// </summary>
        private IEnumerator CloseDoorAfterDelay(JailDoor doorInfo, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (doorInfo != null)
            {
                ModLogger.Debug($"Closing door {doorInfo.doorName} after guard exit");
                doorInfo.CloseDoor();
            }
        }

        private Transform FindGuardSpawnPosition()
        {
            // Look for the guard's original spawn point
            var jailController = Core.ActiveJailController;
            if (jailController?.guardRoom?.guardSpawns != null)
            {
                // Return to GuardSpawn[0] (where IntakeOfficer spawns)
                if (jailController.guardRoom.guardSpawns.Count > 0)
                {
                    return jailController.guardRoom.guardSpawns[0];
                }
            }

            // Fallback: Try to find GuardSpawn GameObjects directly
            var guardSpawn0 = GameObject.Find("GuardRoom/GuardSpawn[0]");
            if (guardSpawn0 != null)
            {
                return guardSpawn0.transform;
            }

            // Another fallback: find any guard spawn
            var guardSpawn = GameObject.Find("GuardSpawn");
            return guardSpawn?.transform;
        }

        /// <summary>
        /// Get the correct DoorPoint for the guard's approach side
        /// </summary>
        private Transform GetCorrectDoorPoint(JailDoor door, string approachSide)
        {
            if (door?.doorHolder == null) return null;

            // Try to find the specific DoorPoint for the approach side
            var specificDoorPoint = door.doorHolder.Find($"DoorPoint_{approachSide}");
            if (specificDoorPoint != null)
            {
                ModLogger.Debug($"Found specific DoorPoint_{approachSide} for {door.doorName}");
                return specificDoorPoint;
            }

            // Fallback to generic DoorPoint
            var genericDoorPoint = door.doorHolder.Find("DoorPoint");
            if (genericDoorPoint != null)
            {
                ModLogger.Debug($"Using generic DoorPoint for {door.doorName}");
                return genericDoorPoint;
            }

            ModLogger.Warn($"No DoorPoint found for {door.doorName} from {approachSide} side");
            return null;
        }

        /// <summary>
        /// Overloaded method for GameObject-based door point finding
        /// </summary>
        private Transform GetCorrectDoorPoint(GameObject doorGameObject, string approachSide)
        {
            if (doorGameObject == null) return null;

            // Try to find the specific DoorPoint for the approach side
            var specificDoorPoint = doorGameObject.transform.Find($"DoorPoint_{approachSide}");
            if (specificDoorPoint != null)
            {
                ModLogger.Debug($"Found specific DoorPoint_{approachSide} for {doorGameObject.name}");
                return specificDoorPoint;
            }

            // Fallback to generic DoorPoint
            var genericDoorPoint = doorGameObject.transform.Find("DoorPoint");
            if (genericDoorPoint != null)
            {
                ModLogger.Debug($"Using generic DoorPoint for {doorGameObject.name}");
                return genericDoorPoint;
            }

            ModLogger.Warn($"No DoorPoint found for {doorGameObject.name} from {approachSide} side");
            return null;
        }

        /// <summary>
        /// New robust door interaction using DoorInteractionController
        /// </summary>
        private IEnumerator InteractWithDoor(JailDoor door, Player prisoner = null)
        {
            if (doorController == null)
            {
                ModLogger.Error($"Guard {badgeNumber} has no DoorInteractionController");
                yield break;
            }

            if (door == null)
            {
                ModLogger.Error($"Guard {badgeNumber} cannot interact with null door");
                yield break;
            }

            // Ensure door has proper interaction type set
            if (door.interactionType == default)
            {
                door.SetInteractionTypeFromDoorType();
            }

            ModLogger.Info($"Guard {badgeNumber} starting {door.interactionType} interaction with {door.doorName}");

            // Start the door interaction
            doorController.StartDoorInteraction(door, door.interactionType, prisoner);

            // Wait for interaction to complete
            while (doorController.IsBusy())
            {
                yield return null;
            }

            ModLogger.Info($"Guard {badgeNumber} completed interaction with {door.doorName}");
        }

        /// <summary>
        /// Simplified method for opening a cell door (operation-only)
        /// </summary>
        private IEnumerator OpenCellDoor(JailDoor cellDoor, Player prisoner = null)
        {
            yield return InteractWithDoor(cellDoor, prisoner);
        }

        /// <summary>
        /// Simplified method for moving through a door (pass-through)
        /// </summary>
        private IEnumerator PassThroughDoor(JailDoor door, Player prisoner = null)
        {
            yield return InteractWithDoor(door, prisoner);
        }

        /// <summary>
        /// Find existing JailDoor component or create a temporary one for door operations
        /// </summary>
        private JailDoor FindOrCreateJailDoor(GameObject doorGameObject, string doorName)
        {
            if (doorGameObject == null) return null;

            // First, try to find existing JailDoor in the jail controller
            var jailController = Core.ActiveJailController;
            if (jailController != null)
            {
                // Check holding cells
                if (jailController.holdingCells != null)
                {
                    foreach (var cell in jailController.holdingCells)
                    {
                        if (cell.cellDoor != null && 
                            (cell.cellDoor.doorName == doorName || 
                             cell.cellDoor.doorInstance == doorGameObject))
                        {
                            return cell.cellDoor;
                        }
                    }
                }

                // Check regular jail cells
                if (jailController.cells != null)
                {
                    foreach (var cell in jailController.cells)
                    {
                        if (cell.cellDoor != null && 
                            (cell.cellDoor.doorName == doorName || 
                             cell.cellDoor.doorInstance == doorGameObject))
                        {
                            return cell.cellDoor;
                        }
                    }
                }

                // Check booking doors if available
                if (jailController.booking?.doors != null)
                {
                    foreach (var door in jailController.booking.doors)
                    {
                        if (door != null && 
                            (door.doorName == doorName || 
                             door.doorInstance == doorGameObject))
                        {
                            return door;
                        }
                    }
                }
            }

            // If no existing JailDoor found, create a temporary one
            ModLogger.Debug($"Creating temporary JailDoor for {doorName}");
            return new JailDoor
            {
                doorName = doorName,
                doorInstance = doorGameObject,
                doorHolder = doorGameObject.transform
            };
        }

        /// <summary>
        /// Record missing GuardPoints for jail structure completion
        /// </summary>
        private void RecordMissingGuardPoints()
        {
            string[] requiredPoints = {
                "JailCells", "Prison", "GuardRoom", "Storage", "MugshotStation", "ScannerStation"
            };

            ModLogger.Info("=== MISSING GUARDPOINTS REPORT ===");
            foreach (string pointName in requiredPoints)
            {
                var point = FindGuardPoint(pointName);
                if (point == null)
                {
                    ModLogger.Warn($"❌ MISSING GuardPoint: {pointName} - Please add to jail structure");
                }
                else
                {
                    ModLogger.Debug($"✓ Found GuardPoint: {pointName} at {point.position}");
                }
            }
            ModLogger.Info("=== END GUARDPOINTS REPORT ===");
        }

        /// <summary>
        /// Smoothly rotate guard to face the player after being stationary and at correct position
        /// </summary>
        private IEnumerator RotateToGuardPoint(Transform guardPoint, string stationName)
        {
            // Get the correct transform to rotate (like DoorInteractionController)
            Transform rotationTransform = GetCorrectRotationTransform();
            
            // Wait for guard to be completely stationary first
            yield return WaitForGuardToBeStationary();
            
            // CRITICAL: Only rotate if guard is very close to the guard point position
            if (guardPoint != null)
            {
                float distanceToGuardPoint = Vector3.Distance(transform.position, guardPoint.position);
                ModLogger.Debug($"Guard {badgeNumber} distance to guard point {stationName}: {distanceToGuardPoint:F2}m");
                
                if (distanceToGuardPoint > 0.5f)
                {
                    ModLogger.Debug($"Guard {badgeNumber} too far from guard point {stationName} ({distanceToGuardPoint:F2}m) - skipping rotation");
                    yield break; // Don't rotate if not close enough
                }
            }
            
            // Test LookController for intake officer first, otherwise use manual rotation
            if (role == GuardRole.IntakeOfficer)
            {
                var playerEyePos = GetPlayerEyePosition();
                if (playerEyePos.HasValue)
                {
                    // Face the player using LookController with body rotation for important station interactions
                    FaceTargetWithLookController(playerEyePos.Value, 20, true, $"player at {stationName}");
                    ModLogger.Debug($"Intake officer {badgeNumber} using LookController to face player at {stationName}");
                    yield break; // Exit early since LookController handles everything
                }
                else if (guardPoint != null && lookControllerAvailable)
                {
                    // Face the station direction using LookController
                    Vector3 stationLookTarget = guardPoint.position + guardPoint.forward * 2f;
                    stationLookTarget.y = guardPoint.position.y + 1.7f; // Eye level
                    FaceTargetWithLookController(stationLookTarget, 15, true, $"station direction at {stationName}");
                    ModLogger.Debug($"Intake officer {badgeNumber} using LookController for station direction at {stationName}");
                    yield break; // Exit early since LookController handles everything
                }
            }

            if (rotationTransform != null)
            {
                // Look for the player to face them (fallback method or non-intake officers)
                var player = GetCurrentPlayer();
                Quaternion targetRotation;
                
                if (player != null)
                {
                    // Face the player
                    Vector3 directionToPlayer = (player.transform.position - rotationTransform.position).normalized;
                    directionToPlayer.y = 0; // Keep rotation only on Y axis
                    targetRotation = Quaternion.LookRotation(directionToPlayer);
                    ModLogger.Debug($"Guard {badgeNumber} at {stationName} facing player (manual rotation)");
                }
                else
                {
                    // Fallback to guard point rotation if no player found
                    targetRotation = guardPoint != null ? guardPoint.rotation : rotationTransform.rotation;
                    ModLogger.Debug($"Guard {badgeNumber} at {stationName} using fallback rotation (manual rotation)");
                }
                
                float angleDifference = Quaternion.Angle(rotationTransform.rotation, targetRotation);
                
                // Only rotate if there's a significant difference (more than 15 degrees)
                if (angleDifference > 15f)
                {
                    ModLogger.Debug($"Guard {badgeNumber} rotating at {stationName} (angle difference: {angleDifference:F1}°)");
                    
                    // Quick, smooth rotation
                    float rotationStartTime = Time.time;
                    float rotationSpeed = 180f; // degrees per second
                    float maxRotationTime = 1f; // Max 1 second
                    
                    while (Quaternion.Angle(rotationTransform.rotation, targetRotation) > 5f && 
                           Time.time - rotationStartTime < maxRotationTime)
                    {
                        rotationTransform.rotation = Quaternion.RotateTowards(
                            rotationTransform.rotation, 
                            targetRotation, 
                            rotationSpeed * Time.deltaTime
                        );
                        yield return null;
                    }
                    rotationTransform.rotation = targetRotation;
                    ModLogger.Debug($"Guard {badgeNumber} rotation completed at {stationName}");
                }
                else
                {
                    ModLogger.Debug($"Guard {badgeNumber} already facing correct direction at {stationName} (angle difference: {angleDifference:F1}°)");
                }
            }
        }
        
        /// <summary>
        /// Get the current player being escorted or supervised
        /// </summary>
        private Player GetCurrentPlayer()
        {
            // Try to get the player from current escort target
            if (currentEscortTarget != null)
            {
                var player = currentEscortTarget.GetComponent<Player>();
                if (player != null) return player;
            }
            
            // Fallback to nearest player in reasonable range
            var allPlayers = GameObject.FindObjectsOfType<Player>();
            Player nearestPlayer = null;
            float nearestDistance = float.MaxValue;
            
            foreach (var player in allPlayers)
            {
                if (player != null)
                {
                    float distance = Vector3.Distance(transform.position, player.transform.position);
                    if (distance < 10f && distance < nearestDistance) // Within 10 meters
                    {
                        nearestPlayer = player;
                        nearestDistance = distance;
                    }
                }
            }
            
            return nearestPlayer;
        }

        /// <summary>
        /// Wait for the player to arrive within a certain distance of the guard
        /// </summary>
        private IEnumerator WaitForPlayerToArrive(Player prisoner, string locationName, float maxDistance)
        {
            if (prisoner == null)
            {
                ModLogger.Warn($"No prisoner to wait for at {locationName}");
                yield break;
            }
            
            ModLogger.Info($"Guard {badgeNumber} waiting for {prisoner.name} to arrive at {locationName}");
            
            float maxWaitTime = 30f; // 30 seconds max to prevent infinite waiting
            float startWaitTime = Time.time;
            
            while (Vector3.Distance(transform.position, prisoner.transform.position) > maxDistance)
            {
                // Check timeout
                if (Time.time - startWaitTime > maxWaitTime)
                {
                    ModLogger.Info($"⏰ Timeout waiting for {prisoner.name} at {locationName}, proceeding anyway");
                    break;
                }
                
                // Show periodic reminders to help player
                if ((Time.time - startWaitTime) % 10f < 1f) // Every 10 seconds
                {
                    ShowInstruction(prisoner, $"Please come to the {locationName}");
                }
                
                yield return new WaitForSeconds(1f);
            }
            
            if (Vector3.Distance(transform.position, prisoner.transform.position) <= maxDistance)
            {
                ModLogger.Info($"✓ {prisoner.name} arrived at {locationName}");
            }
        }

        /// <summary>
        /// Initialize the LookController for natural facing behavior
        /// </summary>
        private void InitializeLookController()
        {
            try
            {
                // Get the Avatar component from the NPC
                if (npcComponent != null)
                {
#if !MONO
                    var npc = npcComponent.TryCast<Il2CppScheduleOne.NPCs.NPC>();
                    guardAvatar = npc?.Avatar;
#else
                    var npc = npcComponent as ScheduleOne.NPCs.NPC;
                    guardAvatar = npc?.Avatar;
#endif
                }

                // Try to find Avatar in children if not directly accessible
                if (guardAvatar == null)
                {
#if !MONO
                    guardAvatar = GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                    guardAvatar = GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
#endif
                }

                // Get the LookController from the Avatar
                if (guardAvatar != null)
                {
                    lookController = guardAvatar.LookController;
                    lookControllerAvailable = (lookController != null);
                    
                    ModLogger.Info($"Guard {badgeNumber}: LookController {(lookControllerAvailable ? "found" : "not found")} on Avatar");
                }
                else
                {
                    ModLogger.Warn($"Guard {badgeNumber}: Avatar not found - falling back to manual rotation");
                    lookControllerAvailable = false;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Guard {badgeNumber}: Error initializing LookController: {e.Message}");
                lookControllerAvailable = false;
            }
        }


        /// <summary>
        /// Use LookController to face a target position naturally (like police officers do)
        /// </summary>
        private void FaceTargetWithLookController(Vector3 targetPosition, int priority = 10, bool rotateBody = false, string context = "")
        {
            // Only use LookController for intake officer initially as a test
            if (role != GuardRole.IntakeOfficer)
            {
                return;
            }

            if (lookControllerAvailable && lookController != null)
            {
                try
                {
                    lookController.OverrideLookTarget(targetPosition, priority, rotateBody);
                    ModLogger.Debug($"Guard {badgeNumber}: Using LookController to face {context} (priority {priority}, rotateBody: {rotateBody})");
                }
                catch (Exception e)
                {
                    ModLogger.Error($"Guard {badgeNumber}: Error using LookController: {e.Message}");
                    lookControllerAvailable = false; // Disable to prevent further errors
                }
            }
            else
            {
                ModLogger.Debug($"Guard {badgeNumber}: LookController not available, skipping face target for {context}");
            }
        }

        /// <summary>
        /// Get the current player position for LookController targeting
        /// </summary>
        private Vector3? GetPlayerEyePosition()
        {
            var player = GetCurrentPlayer();
            if (player != null)
            {
                // Use EyePosition like police officers do
                try
                {
#if !MONO
                    var playerCasted = player.TryCast<Il2CppScheduleOne.PlayerScripts.Player>();
                    return playerCasted?.EyePosition ?? player.transform.position + Vector3.up * 1.7f;
#else
                    var playerCasted = player as ScheduleOne.PlayerScripts.Player;
                    return playerCasted?.EyePosition ?? player.transform.position + Vector3.up * 1.7f;
#endif
                }
                catch (Exception)
                {
                    // Fallback to head-level position
                    return player.transform.position + Vector3.up * 1.7f;
                }
            }
            return null;
        }

        /// <summary>
        /// Find the correct transform to rotate for this NPC type (same as DoorInteractionController)
        /// </summary>
        private Transform GetCorrectRotationTransform()
        {
            // For NPCs created with DirectNPCBuilder, there might be a character model child
            string[] characterNames = { "Character", "Model", "Body", "Root", "Armature", "Avatar" };
            
            foreach (string name in characterNames)
            {
                var characterTransform = transform.Find(name);
                if (characterTransform != null)
                {
                    return characterTransform;
                }
            }
            
            // Look for any child that might be the character model
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child.GetComponent<Animator>() != null || 
                    child.GetComponent<SkinnedMeshRenderer>() != null ||
                    child.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                {
                    return child;
                }
            }
            
            // Fallback to main transform
            return transform;
        }

        /// <summary>
        /// Wait for the guard to be completely stationary before proceeding (same as DoorInteractionController)
        /// </summary>
        private IEnumerator WaitForGuardToBeStationary()
        {
            Vector3 lastPosition = transform.position;
            float stationaryTime = 0f;
            float requiredStationaryTime = 0.5f; // Guard must be still for 0.5 seconds (increased)
            float maxWaitTime = 5f; // Increased max wait time
            float startWaitTime = Time.time;
            
            // Also check velocity if NavMeshAgent is available
            while (stationaryTime < requiredStationaryTime && Time.time - startWaitTime < maxWaitTime)
            {
                yield return new WaitForSeconds(0.1f);
                
                float distanceMoved = Vector3.Distance(transform.position, lastPosition);
                bool isMoving = false;
                
                // Check NavMeshAgent velocity if available
                if (navAgent != null)
                {
                    float velocity = navAgent.velocity.magnitude;
                    if (velocity > 0.1f) // Still moving
                    {
                        isMoving = true;
                    }
                }
                
                if (distanceMoved < 0.02f && !isMoving) // Even stricter movement threshold
                {
                    stationaryTime += 0.1f;
                }
                else
                {
                    stationaryTime = 0f; // Reset if guard moved
                    lastPosition = transform.position;
                }
            }
            
            ModLogger.Debug($"Guard {badgeNumber} is now stationary for station rotation (waited {stationaryTime:F1}s)");
        }

        /// <summary>
        /// Enhanced navigation that automatically handles doors along the path
        /// This ensures guards interact with all doors they encounter during movement
        /// </summary>
        private IEnumerator NavigateWithAutomaticDoorHandling(Vector3 destination, Player prisoner = null, float timeoutSeconds = 30f)
        {
            if (navAgent == null)
            {
                ModLogger.Error($"Guard {badgeNumber} has no NavMeshAgent for navigation");
                yield break;
            }

            ModLogger.Info($"Guard {badgeNumber} navigating to {destination} with automatic door handling");
            
            navAgent.SetDestination(destination);
            float startTime = Time.time;
            Vector3 lastPosition = transform.position;
            float stuckTimer = 0f;
            const float stuckThreshold = 3f; // If not moving for 3 seconds, check for doors

            while (Vector3.Distance(transform.position, destination) > 1.5f && 
                   Time.time - startTime < timeoutSeconds)
            {
                // Check if we're stuck (not moving much)
                float distanceMoved = Vector3.Distance(transform.position, lastPosition);
                if (distanceMoved < 0.1f)
                {
                    stuckTimer += Time.deltaTime;
                    
                    if (stuckTimer >= stuckThreshold)
                    {
                        ModLogger.Debug($"Guard {badgeNumber} appears stuck - checking for nearby doors");
                        
                        // Look for doors within interaction range
                        var nearbyDoor = FindNearestDoorInPath();
                        if (nearbyDoor != null && !doorController.IsBusy())
                        {
                            ModLogger.Info($"Guard {badgeNumber} found door blocking path: {nearbyDoor.doorName}");
                            yield return InteractWithDoor(nearbyDoor, prisoner);
                            
                            // Reset navigation after door interaction
                            navAgent.SetDestination(destination);
                        }
                        
                        stuckTimer = 0f;
                    }
                }
                else
                {
                    stuckTimer = 0f;
                }
                
                lastPosition = transform.position;
                yield return new WaitForSeconds(0.2f);
            }

            ModLogger.Info($"Guard {badgeNumber} reached destination");
        }

        /// <summary>
        /// Find the nearest door that might be blocking the guard's path
        /// </summary>
        private JailDoor FindNearestDoorInPath()
        {
            var jailController = Core.ActiveJailController;
            if (jailController == null) return null;

            float minDistance = float.MaxValue;
            JailDoor nearestDoor = null;
            
            // Check all known doors
            var allDoors = new List<JailDoor>();
            
            // Add booking doors
            if (jailController.booking != null)
            {
                // bookingEntryDoor not available in this system
                if (jailController.booking.bookingInnerDoor != null) allDoors.Add(jailController.booking.bookingInnerDoor);
                if (jailController.booking.guardDoor != null) allDoors.Add(jailController.booking.guardDoor);
                if (jailController.booking.prisonEntryDoor != null) allDoors.Add(jailController.booking.prisonEntryDoor);
            }
            
            // Add cell doors
            if (jailController.holdingCells != null)
            {
                foreach (var cell in jailController.holdingCells)
                {
                    if (cell.cellDoor != null) allDoors.Add(cell.cellDoor);
                }
            }
            
            if (jailController.cells != null)
            {
                foreach (var cell in jailController.cells)
                {
                    if (cell.cellDoor != null) allDoors.Add(cell.cellDoor);
                }
            }

            // Find the closest door that's within interaction range and closed
            foreach (var door in allDoors)
            {
                if (door?.doorHolder == null) continue;
                
                float distance = Vector3.Distance(transform.position, door.doorHolder.position);
                if (distance < 4f && distance < minDistance && door.IsClosed())
                {
                    // Check if this door is roughly in our path direction
                    Vector3 directionToDoor = (door.doorHolder.position - transform.position).normalized;
                    Vector3 movementDirection = navAgent.velocity.normalized;
                    
                    if (Vector3.Dot(directionToDoor, movementDirection) > 0.3f) // Door is roughly in our movement direction
                    {
                        minDistance = distance;
                        nearestDoor = door;
                    }
                }
            }

            return nearestDoor;
        }

        /// <summary>
        /// Enhanced escort method that uses automatic door handling
        /// </summary>
        private IEnumerator EscortPrisonerWithDoorHandling(Player prisoner, Vector3 destination, string instructionText = "Follow the guard")
        {
            if (prisoner == null) yield break;

            ShowInstruction(prisoner, instructionText);
            
            // Use the enhanced navigation that handles doors automatically
            yield return NavigateWithAutomaticDoorHandling(destination, prisoner);
            
            // Wait a moment for prisoner to catch up
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Record all stations in the jail/booking process for enable/disable management
        /// </summary>
        private System.Collections.Generic.Dictionary<string, StationInfo> recordedStations = new System.Collections.Generic.Dictionary<string, StationInfo>();

        private void RecordAllStations()
        {
            recordedStations.Clear();

            // Record booking stations
            RecordStation("MugshotStation", "Booking");
            RecordStation("ScannerStation", "Booking");
            RecordStation("InventoryDropOff", "Storage");
            RecordStation("InventoryPickup", "Storage");

            // Record guard points
            RecordGuardPoint("MugshotStation");
            RecordGuardPoint("ScannerStation");
            RecordGuardPoint("Storage");

            ModLogger.Info($"Recorded {recordedStations.Count} stations and guard points for intake management");
        }

        private void RecordStation(string stationName, string area)
        {
            var station = FindStationInArea(stationName, area);
            if (station != null)
            {
                recordedStations[stationName] = new StationInfo
                {
                    StationTransform = station,
                    StationType = "InteractionStation",
                    Area = area,
                    IsEnabled = true
                };
                ModLogger.Debug($"✓ Recorded {stationName} in {area}");
            }
            else
            {
                ModLogger.Warn($"Could not find {stationName} in {area}");
            }
        }

        private void RecordGuardPoint(string pointName)
        {
            var guardPoint = FindGuardPoint(pointName);
            if (guardPoint != null)
            {
                recordedStations[$"{pointName}_GuardPoint"] = new StationInfo
                {
                    StationTransform = guardPoint,
                    StationType = "GuardPoint",
                    Area = "Guard",
                    IsEnabled = true
                };
                ModLogger.Debug($"✓ Recorded GuardPoint for {pointName}");
            }
            else
            {
                ModLogger.Warn($"Could not find GuardPoint for {pointName}");
            }
        }

        private Transform FindStationInArea(string stationName, string area)
        {
            // Search in the specified area
            var jailController = Core.ActiveJailController;
            if (jailController == null) return null;

            Transform areaTransform = null;
            switch (area)
            {
                case "Booking":
                    areaTransform = jailController.transform.Find("Booking");
                    break;
                case "Storage":
                    areaTransform = jailController.transform.Find("Storage");
                    break;
            }

            if (areaTransform != null)
            {
                return areaTransform.Find(stationName);
            }

            return null;
        }

        public void EnableStation(string stationName, bool enabled)
        {
            if (recordedStations.TryGetValue(stationName, out var stationInfo))
            {
                stationInfo.IsEnabled = enabled;
                
                // Enable/disable interaction components
                var interaction = stationInfo.StationTransform.GetComponentInChildren<MonoBehaviour>();
                if (interaction != null)
                {
                    interaction.enabled = enabled;
                }

                ModLogger.Info($"Station {stationName}: {(enabled ? "ENABLED" : "DISABLED")}");
            }
        }

        public void DisableAllStations()
        {
            foreach (var station in recordedStations.Keys.ToList())
            {
                EnableStation(station, false);
            }
            ModLogger.Info("All stations disabled for intake process");
        }

        public void EnableAllStations()
        {
            foreach (var station in recordedStations.Keys.ToList())
            {
                EnableStation(station, true);
            }
            ModLogger.Info("All stations enabled after intake completion");
        }

        /// <summary>
        /// Control NavMesh obstacle on doors to prevent navigation through closed doors
        /// </summary>
        private void SetDoorNavMeshObstacle(JailDoor door, bool enabled)
        {
            if (door?.doorHolder == null) return;

            var obstacle = door.doorHolder.GetComponent<NavMeshObstacle>();
            if (obstacle == null)
            {
                // Add NavMesh obstacle if it doesn't exist
                obstacle = door.doorHolder.gameObject.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.size = new Vector3(2f, 3f, 0.5f); // Standard door size
                obstacle.center = Vector3.zero;
                ModLogger.Debug($"Added NavMeshObstacle to door: {door.doorName}");
            }

            obstacle.enabled = enabled;
            ModLogger.Debug($"NavMeshObstacle on {door.doorName}: {(enabled ? "ENABLED (blocking)" : "DISABLED (passable)")}");
        }
    }

    /// <summary>
    /// Jail-specific behavior for inmate NPCs with cell assignments and daily routines
    /// </summary>
    public class JailInmateBehavior : MonoBehaviour
    {
        public enum InmateStatus
        {
            NewInmate,
            RegularInmate,
            ModelInmate,
            ProblematicInmate,
            InSolitary
        }

        // Saveable fields for persistence
        [SaveableField("inmate_prisoner_id")]
        public string prisonerID = "";

        [SaveableField("inmate_cell_number")]
        public int cellNumber = -1;

        [SaveableField("inmate_sentence_days")]
        public int sentenceDaysRemaining = 30;

        [SaveableField("inmate_behavior_score")]
        public float behaviorScore = 50f;

        [SaveableField("inmate_crime_type")]
        public string crimeType = "Unknown";

        [SaveableField("inmate_time_served")]
        public float timeServed = 0f;

        [SaveableField("inmate_status")]
        public InmateStatus status = InmateStatus.NewInmate;

        // Runtime state
        private bool isInCell = false;
        private bool hasAssignedCell = false;
        private Vector3 cellPosition;
        private float lastMealTime = 0f;
        private float lastActivityTime = 0f;

        // Group and faction system
        public InmateFaction faction = InmateFaction.None;
        public int groupID = -1; // ID of current group, -1 if not in group
        private static System.Collections.Generic.Dictionary<int, InmateGroup> activeGroups = new System.Collections.Generic.Dictionary<int, InmateGroup>();
        private static int nextGroupID = 0;

#if !MONO
        private Il2CppScheduleOne.NPCs.NPC npcComponent;

        public JailInmateBehavior(System.IntPtr ptr) : base(ptr) { }
#else
        private ScheduleOne.NPCs.NPC npcComponent;
#endif

        public void Initialize(string crime, int sentence, string prisonerId = "")
        {
            crimeType = crime;
            sentenceDaysRemaining = sentence;
            prisonerID = string.IsNullOrEmpty(prisonerId) ? GeneratePrisonerID() : prisonerId;
            timeServed = 0f;
            behaviorScore = 50f; // Start with neutral behavior

            // Assign faction based on behavior score and crime type
            AssignInmateFaction(crime, sentence);

            ModLogger.Info($"Initializing inmate {prisonerID} for {crime} - {sentence} days, Faction: {faction}");

            // Apply appropriate appearance based on crime severity
            //var prisonerType = DetermineInmateTypeFromCrime(crime);
            //NPCAppearanceManager.ApplyPrisonerAppearance(gameObject, prisonerType, crime);
        }

        //private NPCAppearanceManager.PrisonerType DetermineInmateTypeFromCrime(string crime)
        //{
        //    string crimeLower = crime.ToLower();

        //    if (crimeLower.Contains("murder") || crimeLower.Contains("assault") || crimeLower.Contains("violent"))
        //    {
        //        return NPCAppearanceManager.PrisonerType.LargeThreat;
        //    }
        //    else if (crimeLower.Contains("theft") || crimeLower.Contains("petty") || crimeLower.Contains("minor"))
        //    {
        //        return NPCAppearanceManager.PrisonerType.SmallTimeCriminal;
        //    }
        //    else if (behaviorScore >= 80f)
        //    {
        //        return NPCAppearanceManager.PrisonerType.ModelPrisoner;
        //    }
        //    else if (behaviorScore < 30f)
        //    {
        //        return NPCAppearanceManager.PrisonerType.DangerousPrisoner;
        //    }

        //    return NPCAppearanceManager.PrisonerType.Regular;
        //}

        private void Start()
        {
            try
            {
#if !MONO
                npcComponent = GetComponent<Il2CppScheduleOne.NPCs.NPC>();
#else
                npcComponent = GetComponent<ScheduleOne.NPCs.NPC>();
#endif

                if (npcComponent == null)
                {
                    ModLogger.Error($"NPC component not found on inmate {gameObject.name}");
                    return;
                }

                MelonCoroutines.Start(InmateBehaviorLoop());
                MelonCoroutines.Start(UpdateTimeServed());
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error starting JailInmateBehavior: {e.Message}");
            }
        }

        private IEnumerator InmateBehaviorLoop()
        {
            while (sentenceDaysRemaining > 0)
            {
                switch (status)
                {
                    case InmateStatus.NewInmate:
                        yield return HandleNewInmateBehavior();
                        break;
                    case InmateStatus.RegularInmate:
                        yield return HandleRegularInmateBehavior();
                        break;
                    case InmateStatus.ModelInmate:
                        yield return HandleModelInmateBehavior();
                        break;
                    case InmateStatus.ProblematicInmate:
                        yield return HandleProblematicInmateBehavior();
                        break;
                    case InmateStatus.InSolitary:
                        yield return HandleSolitaryBehavior();
                        break;
                }

                yield return new WaitForSeconds(60f);
            }

            // Sentence completed
            OnSentenceCompleted();
        }

        private IEnumerator UpdateTimeServed()
        {
            while (sentenceDaysRemaining > 0)
            {
                yield return new WaitForSeconds(1f); // Update every second
                timeServed += Time.deltaTime;
            }
        }

        private IEnumerator HandleNewInmateBehavior()
        {
            // New inmates stay close to their cell and are cautious
            if (!hasAssignedCell)
            {
                yield return RequestCellAssignment();
            }

            // Gradually adjust to prison life
            if (timeServed > 60f) // After 1 minute, no longer "new"
            {
                status = InmateStatus.RegularInmate;
                ModLogger.Info($"Inmate {prisonerID} is no longer considered new");
            }

            yield return new WaitForSeconds(10f);
        }

        private IEnumerator HandleRegularInmateBehavior()
        {
            // Regular daily routine with proper jail movement
            yield return ExecuteJailMovementRoutine();

            yield return new WaitForSeconds(UnityEngine.Random.Range(15f, 45f));

            // Randomly modify behavior score
            float behaviorChange = UnityEngine.Random.Range(-2f, 2f);
            ModifyBehaviorScore(behaviorChange);

            // Check for status changes
            UpdateInmateStatus();
        }

        private IEnumerator HandleModelInmateBehavior()
        {
            // Model inmates have better behavior and follow routines
            yield return new WaitForSeconds(UnityEngine.Random.Range(30f, 60f));

            // Model inmates slowly improve their behavior
            ModifyBehaviorScore(UnityEngine.Random.Range(0f, 1f));
        }

        private IEnumerator HandleProblematicInmateBehavior()
        {
            // Problematic inmates cause trouble
            yield return new WaitForSeconds(UnityEngine.Random.Range(10f, 30f));

            // May cause incidents or lose more behavior points
            float behaviorChange = UnityEngine.Random.Range(-3f, 0f);
            ModifyBehaviorScore(behaviorChange);

            // Chance of being sent to solitary
            if (behaviorScore < 20f && UnityEngine.Random.Range(0f, 1f) < 0.1f)
            {
                status = InmateStatus.InSolitary;
                ModLogger.Info($"Inmate {prisonerID} sent to solitary confinement");
            }
        }

        private IEnumerator HandleSolitaryBehavior()
        {
            // Inmates in solitary are isolated
            yield return new WaitForSeconds(60f);

            // Gradually improve behavior in solitary
            ModifyBehaviorScore(UnityEngine.Random.Range(-1f, 2f));

            // Can be released from solitary
            if (behaviorScore > 30f && timeServed > lastActivityTime + 120f) // 2 minutes minimum
            {
                status = InmateStatus.RegularInmate;
                ModLogger.Info($"Inmate {prisonerID} released from solitary confinement");
            }
        }

        private IEnumerator RequestCellAssignment()
        {
            // Try to get a cell assignment from the jail controller
            var jailController = Core.ActiveJailController;
            if (jailController != null)
            {
                // TODO: Implement cell assignment logic with JailController
                // For now, just mark as having a cell
                hasAssignedCell = true;
                cellNumber = UnityEngine.Random.Range(1, 20);
                ModLogger.Info($"Assigned cell {cellNumber} to inmate {prisonerID}");
            }
            yield return new WaitForSeconds(1f);
        }

        public void OnPlayerInteraction(GameObject player)
        {
            try
            {
                if (npcComponent == null) return;

                string message = GetInmateInteractionMessage();
                npcComponent.SendTextMessage(message);

                var responses = CreateInmateResponseOptions(player);
                if (responses.Length > 0)
                {
                    MelonCoroutines.Start(ShowResponsesAfterDelay(responses, 1f));
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error in inmate interaction: {e.Message}");
            }
        }

        private string GetInmateInteractionMessage()
        {
            switch (status)
            {
                case InmateStatus.NewInmate:
                    return $"Prisoner {prisonerID}: I'm still figuring this place out... what do you want?";
                case InmateStatus.ModelInmate:
                    return $"Prisoner {prisonerID}: I'm trying to keep my head down and do my time. What's up?";
                case InmateStatus.ProblematicInmate:
                    return $"Prisoner {prisonerID}: What's it to you? I don't owe you anything.";
                case InmateStatus.InSolitary:
                    return $"Prisoner {prisonerID}: They got me locked up tight... shouldn't even be talking.";
                default:
                    return $"Prisoner {prisonerID}: Yeah? What do you need?";
            }
        }

#if !MONO
        private Il2CppScheduleOne.Messaging.Response[] CreateInmateResponseOptions(GameObject player)
#else
        private ScheduleOne.Messaging.Response[] CreateInmateResponseOptions(GameObject player)
#endif
        {
            var responseList = new System.Collections.Generic.List<
#if !MONO
                Il2CppScheduleOne.Messaging.Response
#else
                ScheduleOne.Messaging.Response
#endif
            >();

#if !MONO
            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "What are you in for?",
                label = "crime_inquiry",
                callback = new System.Action(() => HandleCrimeInquiry(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "How much time do you have left?",
                label = "time_inquiry",
                callback = new System.Action(() => HandleTimeInquiry(player))
            });

            responseList.Add(new Il2CppScheduleOne.Messaging.Response()
            {
                text = "Never mind",
                label = "dismiss",
                callback = new System.Action(() => HandleDismiss(player))
            });
#else
            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "What are you in for?",
                label = "crime_inquiry",
                callback = () => HandleCrimeInquiry(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "How much time do you have left?",
                label = "time_inquiry",
                callback = () => HandleTimeInquiry(player)
            });

            responseList.Add(new ScheduleOne.Messaging.Response
            {
                text = "Never mind",
                label = "dismiss",
                callback = () => HandleDismiss(player)
            });
#endif

            return responseList.ToArray();
        }

        private IEnumerator ShowResponsesAfterDelay(
#if !MONO
            Il2CppScheduleOne.Messaging.Response[] responses,
#else
            ScheduleOne.Messaging.Response[] responses,
#endif
            float delay)
        {
            yield return new WaitForSeconds(delay);

            if (npcComponent != null && npcComponent.MSGConversation != null)
            {
                npcComponent.MSGConversation.ClearResponses();
#if !MONO
                var responsesList = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Messaging.Response>();
                foreach (var response in responses)
                {
                    responsesList.Add(response);
                }
#else
                var responsesList = new System.Collections.Generic.List<ScheduleOne.Messaging.Response>(responses);
#endif
                npcComponent.MSGConversation.ShowResponses(responsesList, 0f, true);
            }
        }

        private void HandleCrimeInquiry(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage($"I'm here for {crimeType}. Not proud of it, but that's life.");
            }
        }

        private void HandleTimeInquiry(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage($"Got {sentenceDaysRemaining} days left. Been here for {Mathf.FloorToInt(timeServed)} seconds already.");
            }
        }

        private void HandleDismiss(GameObject player)
        {
            if (npcComponent != null)
            {
                npcComponent.SendTextMessage("Yeah, that's what I thought. Mind your own business.");
            }
        }

        private void ModifyBehaviorScore(float change)
        {
            behaviorScore = Mathf.Clamp(behaviorScore + change, 0f, 100f);
            lastActivityTime = Time.time;

            if (Mathf.Abs(change) > 1f) // Only log significant changes
            {
                ModLogger.Debug($"Inmate {prisonerID} behavior score changed by {change:F1} to {behaviorScore:F1}");
            }
        }

        private void UpdateInmateStatus()
        {
            InmateStatus previousStatus = status;

            if (behaviorScore >= 80f)
            {
                status = InmateStatus.ModelInmate;
            }
            else if (behaviorScore <= 25f)
            {
                status = InmateStatus.ProblematicInmate;
            }
            else
            {
                status = InmateStatus.RegularInmate;
            }

            if (status != previousStatus)
            {
                ModLogger.Info($"Inmate {prisonerID} status changed from {previousStatus} to {status}");

                // Update appearance when status changes
                //var prisonerType = DetermineInmateTypeFromCrime(crimeType);
                //NPCAppearanceManager.ApplyPrisonerAppearance(gameObject, prisonerType, crimeType);
            }
        }

        private void OnSentenceCompleted()
        {
            ModLogger.Info($"Inmate {prisonerID} has completed their sentence");
            // TODO: Handle prisoner release - remove from jail, update records, etc.
        }

        private string GeneratePrisonerID()
        {
            return $"P{UnityEngine.Random.Range(10000, 99999)}";
        }

        public string GetPrisonerID() => prisonerID;
        public int GetCellNumber() => cellNumber;
        public float GetBehaviorScore() => behaviorScore;
        public InmateStatus GetStatus() => status;
        public string GetCrimeType() => crimeType;
        public int GetRemainingDays() => sentenceDaysRemaining;

        #region Jail Movement and Grouping System

        /// <summary>
        /// Execute proper jail movement routine based on time of day and inmate status
        /// </summary>
        private IEnumerator ExecuteJailMovementRoutine()
        {
            // Check if inmate is outside jail boundaries
            if (!IsWithinJailBounds())
            {
                yield return ReturnToJailArea();
            }

            // Determine movement based on "time of day" simulation
            var currentHour = Mathf.FloorToInt((Time.time % 86400f) / 3600f); // 24 hour cycle

            if (currentHour >= 22 || currentHour <= 6) // Night time (10pm - 6am)
            {
                yield return MoveToCellArea();
            }
            else if (currentHour >= 12 && currentHour <= 13) // Lunch time
            {
                yield return MoveToMealArea();
            }
            else if (currentHour >= 14 && currentHour <= 16) // Recreation time
            {
                yield return MoveToRecreationArea();
            }
            else // General time - common areas
            {
                yield return MoveToCommonArea();
            }
        }

        /// <summary>
        /// Check if inmate is within jail boundaries
        /// </summary>
        private bool IsWithinJailBounds()
        {
            var position = transform.position;

            // Define jail boundaries (adjust these based on your jail layout)
            var jailBounds = new Bounds(
                new Vector3(65f, 9f, -220f), // Center of jail area
                new Vector3(40f, 10f, 40f)   // Size of jail area
            );

            bool withinBounds = jailBounds.Contains(position);

            if (!withinBounds)
            {
                ModLogger.Warn($"Inmate {prisonerID} is outside jail bounds at {position}");
            }

            return withinBounds;
        }

        /// <summary>
        /// Return inmate to jail area if they've wandered outside
        /// </summary>
        private IEnumerator ReturnToJailArea()
        {
            ModLogger.Info($"Returning inmate {prisonerID} to jail area");

            var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                // Move to center of jail common area
                var jailCenter = new Vector3(65f, 9f, -220f);
                navAgent.SetDestination(jailCenter);

                // Wait for movement to complete
                while (navAgent.pathPending || navAgent.remainingDistance > 2f)
                {
                    yield return new WaitForSeconds(0.5f);
                    if (!navAgent.enabled) break;
                }

                ModLogger.Info($"✓ Inmate {prisonerID} returned to jail area");
            }

            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Move inmate to cell area for rest/sleep
        /// </summary>
        private IEnumerator MoveToCellArea()
        {
            var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                // Move to cell block area
                var cellArea = new Vector3(
                    UnityEngine.Random.Range(50f, 70f),
                    12f,
                    UnityEngine.Random.Range(-235f, -205f)
                );

                navAgent.SetDestination(cellArea);
                ModLogger.Debug($"Inmate {prisonerID} moving to cell area for rest");

                // Wait for movement
                yield return WaitForMovementComplete(navAgent);
            }

            // Stay in cell area for a while
            yield return new WaitForSeconds(UnityEngine.Random.Range(30f, 120f));
        }

        /// <summary>
        /// Move inmate to meal area
        /// </summary>
        private IEnumerator MoveToMealArea()
        {
            var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                // Move to dining/kitchen area
                var mealArea = new Vector3(
                    UnityEngine.Random.Range(75f, 85f),
                    9f,
                    UnityEngine.Random.Range(-210f, -200f)
                );

                navAgent.SetDestination(mealArea);
                ModLogger.Debug($"Inmate {prisonerID} moving to meal area");

                yield return WaitForMovementComplete(navAgent);
            }

            // Stay for meal time
            yield return new WaitForSeconds(UnityEngine.Random.Range(15f, 30f));
        }

        /// <summary>
        /// Move inmate to MainRec area for group activities
        /// </summary>
        private IEnumerator MoveToRecreationArea()
        {
            ModLogger.Info($"Inmate {prisonerID} ({faction}) moving to MainRec area for recreation");

            // Check if we have access to the jail controller for MainRec bounds
            var jailController = Core.ActiveJailController;
            if (jailController != null)
            {
                ModLogger.Debug("Using ActiveJailController for MainRec area bounds");
            }

            // Move to MainRec area and engage in group behavior
            yield return ExecuteMainRecGroupBehavior();
        }

        /// <summary>
        /// Move inmate to common area for general activities
        /// </summary>
        private IEnumerator MoveToCommonArea()
        {
            var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                // Move to common area
                var commonArea = new Vector3(
                    UnityEngine.Random.Range(55f, 75f),
                    9f,
                    UnityEngine.Random.Range(-230f, -210f)
                );

                navAgent.SetDestination(commonArea);
                ModLogger.Debug($"Inmate {prisonerID} moving to common area");

                yield return WaitForMovementComplete(navAgent);
            }

            // Engage in common area activities
            yield return ExecuteCommonAreaBehavior();
        }

        /// <summary>
        /// Execute MainRec area group behavior with faction-based grouping
        /// </summary>
        private IEnumerator ExecuteMainRecGroupBehavior()
        {
            // Define MainRec area bounds (you can adjust these based on your jail layout)
            var mainRecBounds = new Bounds(
                new Vector3(70f, 9f, -220f), // Center of MainRec area  
                new Vector3(20f, 5f, 15f)     // Size of MainRec area
            );

            var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent == null) yield break;

            // First, move into MainRec area
            var initialPosition = GetRandomPositionInBounds(mainRecBounds);
            navAgent.SetDestination(initialPosition);
            yield return WaitForMovementComplete(navAgent);

            ModLogger.Info($"Inmate {prisonerID} ({faction}) arrived in MainRec area");

            // Try to join an existing compatible group or form a new one
            yield return HandleGroupFormationAndJoining();

            // Stay in MainRec area for recreation period
            float recreationEndTime = Time.time + UnityEngine.Random.Range(30f, 90f);

            while (Time.time < recreationEndTime)
            {
                // Update group behavior
                yield return UpdateGroupBehavior();
                yield return new WaitForSeconds(5f);
            }

            // Leave group when recreation ends
            LeaveCurrentGroup();
            ModLogger.Info($"Inmate {prisonerID} leaving MainRec area");
        }

        /// <summary>
        /// Common area behavior - inmates wander and interact
        /// </summary>
        private IEnumerator ExecuteCommonAreaBehavior()
        {
            // Random movement within common area
            var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (navAgent != null)
            {
                for (int i = 0; i < UnityEngine.Random.Range(2, 5); i++)
                {
                    var randomPoint = new Vector3(
                        UnityEngine.Random.Range(55f, 75f),
                        9f,
                        UnityEngine.Random.Range(-230f, -210f)
                    );

                    navAgent.SetDestination(randomPoint);
                    yield return WaitForMovementComplete(navAgent);
                    yield return new WaitForSeconds(UnityEngine.Random.Range(5f, 15f));
                }
            }
        }

        /// <summary>
        /// Find nearby inmates for grouping behaviors
        /// </summary>
        private System.Collections.Generic.List<JailInmateBehavior> FindNearbyInmates(float radius)
        {
            var nearbyInmates = new System.Collections.Generic.List<JailInmateBehavior>();
            var allInmates = UnityEngine.Object.FindObjectsOfType<JailInmateBehavior>();

            foreach (var inmate in allInmates)
            {
                if (inmate != this && inmate != null)
                {
                    float distance = Vector3.Distance(transform.position, inmate.transform.position);
                    if (distance <= radius)
                    {
                        nearbyInmates.Add(inmate);
                    }
                }
            }

            return nearbyInmates;
        }

        /// <summary>
        /// Calculate center position of a group of inmates
        /// </summary>
        private Vector3 CalculateGroupCenter(System.Collections.Generic.List<JailInmateBehavior> inmates)
        {
            if (inmates.Count == 0) return transform.position;

            Vector3 center = Vector3.zero;
            foreach (var inmate in inmates)
            {
                center += inmate.transform.position;
            }
            center /= inmates.Count;

            return center;
        }

        /// <summary>
        /// Wait for NavMeshAgent movement to complete
        /// </summary>
        private IEnumerator WaitForMovementComplete(UnityEngine.AI.NavMeshAgent navAgent)
        {
            if (navAgent == null) yield break;

            while (navAgent.enabled && navAgent.pathPending)
            {
                yield return new WaitForSeconds(0.1f);
            }

            while (navAgent.enabled && navAgent.remainingDistance > 1.5f)
            {
                yield return new WaitForSeconds(0.5f);
            }
        }

        /// <summary>
        /// Assign inmate faction based on crime and sentence
        /// </summary>
        private void AssignInmateFaction(string crime, int sentence)
        {
            float random = UnityEngine.Random.Range(0f, 1f);

            // Faction assignment based on crime type and sentence length
            if (sentence > 180) // Long sentences tend toward OldTimers
            {
                faction = random < 0.6f ? InmateFaction.OldTimers : InmateFaction.Neutral;
            }
            else if (sentence < 30) // Short sentences tend toward YoungGuns
            {
                faction = random < 0.5f ? InmateFaction.YoungGuns : InmateFaction.Neutral;
            }
            else if (random < 0.15f) // 15% chance of being a Loner
            {
                faction = InmateFaction.Loners;
            }
            else // Default to Neutral or faction based on behavior
            {
                faction = random < 0.4f ? InmateFaction.Neutral :
                         (random < 0.7f ? InmateFaction.OldTimers : InmateFaction.YoungGuns);
            }
        }

        /// <summary>
        /// Get random position within bounds
        /// </summary>
        private Vector3 GetRandomPositionInBounds(Bounds bounds)
        {
            return new Vector3(
                UnityEngine.Random.Range(bounds.min.x, bounds.max.x),
                bounds.center.y,
                UnityEngine.Random.Range(bounds.min.z, bounds.max.z)
            );
        }

        /// <summary>
        /// Handle group formation and joining logic
        /// </summary>
        private IEnumerator HandleGroupFormationAndJoining()
        {
            // Clean up inactive groups first
            CleanupInactiveGroups();

            // Try to join an existing compatible group
            var compatibleGroup = FindCompatibleGroup();

            if (compatibleGroup != null)
            {
                compatibleGroup.AddMember(this);
                yield return MoveToGroupPosition(compatibleGroup);
                ModLogger.Info($"Inmate {prisonerID} joined existing group {compatibleGroup.GroupID}");
            }
            else
            {
                // Form a new group if no compatible groups or if we're a natural leader
                if (ShouldFormNewGroup())
                {
                    yield return FormNewGroup();
                }
                else
                {
                    // Stay as individual for now
                    ModLogger.Debug($"Inmate {prisonerID} remaining individual in MainRec");
                }
            }
        }

        /// <summary>
        /// Update behavior while in a group
        /// </summary>
        private IEnumerator UpdateGroupBehavior()
        {
            if (groupID == -1) yield break; // Not in a group

            if (!activeGroups.ContainsKey(groupID))
            {
                groupID = -1; // Group was dissolved
                yield break;
            }

            var group = activeGroups[groupID];

            // Handle single member groups - try to migrate
            if (group.Members.Count == 1 && group.Members[0] == this)
            {
                ModLogger.Debug($"Group {groupID} has only one member, attempting migration");
                yield return AttemptGroupMigration();
            }
            else
            {
                // Move to proper group position if needed
                yield return MaintainGroupPosition(group);
            }
        }

        /// <summary>
        /// Find compatible group to join
        /// </summary>
        private InmateGroup FindCompatibleGroup()
        {
            foreach (var group in activeGroups.Values)
            {
                if (group.IsActive && group.CanJoinGroup(this))
                {
                    return group;
                }
            }
            return null;
        }

        /// <summary>
        /// Determine if inmate should form a new group
        /// </summary>
        private bool ShouldFormNewGroup()
        {
            // Loners rarely form groups
            if (faction == InmateFaction.Loners) return UnityEngine.Random.Range(0f, 1f) < 0.2f;

            // OldTimers are more likely to form groups
            if (faction == InmateFaction.OldTimers) return UnityEngine.Random.Range(0f, 1f) < 0.7f;

            // YoungGuns are moderately likely to form groups
            if (faction == InmateFaction.YoungGuns) return UnityEngine.Random.Range(0f, 1f) < 0.5f;

            // Neutral inmates form groups occasionally
            return UnityEngine.Random.Range(0f, 1f) < 0.3f;
        }

        /// <summary>
        /// Form a new group
        /// </summary>
        private IEnumerator FormNewGroup()
        {
            var gatherPoint = transform.position + UnityEngine.Random.insideUnitSphere * 2f;
            gatherPoint.y = transform.position.y;

            var newGroup = new InmateGroup(nextGroupID++, gatherPoint, faction);
            activeGroups[newGroup.GroupID] = newGroup;
            newGroup.AddMember(this);

            ModLogger.Info($"Inmate {prisonerID} formed new group {newGroup.GroupID} for faction {faction}");

            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Move to proper position within group
        /// </summary>
        private IEnumerator MoveToGroupPosition(InmateGroup group)
        {
            var memberIndex = group.Members.IndexOf(this);
            if (memberIndex == -1) yield break;

            var targetPosition = group.GetPositionForMember(memberIndex);
            var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();

            if (navAgent != null)
            {
                navAgent.SetDestination(targetPosition);
                yield return WaitForMovementComplete(navAgent);
            }
        }

        /// <summary>
        /// Maintain proper position within group
        /// </summary>
        private IEnumerator MaintainGroupPosition(InmateGroup group)
        {
            var memberIndex = group.Members.IndexOf(this);
            if (memberIndex == -1) yield break;

            var expectedPosition = group.GetPositionForMember(memberIndex);
            var currentPosition = transform.position;

            // If too far from expected position, move back to group  
            if (Vector3.Distance(currentPosition, expectedPosition) > 1.2f)
            {
                var navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (navAgent != null)
                {
                    navAgent.SetDestination(expectedPosition);
                    yield return WaitForMovementComplete(navAgent);
                }
            }
        }

        /// <summary>
        /// Attempt to migrate single member to another group
        /// </summary>
        private IEnumerator AttemptGroupMigration()
        {
            var compatibleGroup = FindCompatibleGroup();

            if (compatibleGroup != null)
            {
                LeaveCurrentGroup();
                compatibleGroup.AddMember(this);
                yield return MoveToGroupPosition(compatibleGroup);
                ModLogger.Info($"Inmate {prisonerID} migrated to group {compatibleGroup.GroupID}");
            }
            else
            {
                // Stay in single-member group for a while
                yield return new WaitForSeconds(10f);
            }
        }

        /// <summary>
        /// Leave current group
        /// </summary>
        private void LeaveCurrentGroup()
        {
            if (groupID == -1) return;

            if (activeGroups.ContainsKey(groupID))
            {
                var group = activeGroups[groupID];
                group.RemoveMember(this);

                // Clean up empty groups
                if (group.Members.Count == 0)
                {
                    activeGroups.Remove(groupID);
                    ModLogger.Debug($"Group {groupID} dissolved - no members remaining");
                }
            }

            groupID = -1;
        }

        /// <summary>
        /// Clean up inactive or empty groups
        /// </summary>
        private static void CleanupInactiveGroups()
        {
            var groupsToRemove = new System.Collections.Generic.List<int>();

            foreach (var kvp in activeGroups)
            {
                var group = kvp.Value;

                // Remove empty or inactive groups
                if (!group.IsActive || group.Members.Count == 0)
                {
                    groupsToRemove.Add(kvp.Key);
                }

                // Remove groups that have been inactive for too long
                if (Time.time - group.FormationTime > 300f && group.Members.Count == 0) // 5 minutes
                {
                    groupsToRemove.Add(kvp.Key);
                }
            }

            foreach (var groupId in groupsToRemove)
            {
                activeGroups.Remove(groupId);
                ModLogger.Debug($"Cleaned up inactive group {groupId}");
            }
        }

        #endregion
    }

    /// <summary>
    /// Inmate factions for group alignment and behavior
    /// </summary>
    public enum InmateFaction
    {
        None = 0,
        Neutral = 1,
        OldTimers = 2,    // Long-term inmates, experienced
        YoungGuns = 3,    // Younger, more aggressive inmates
        Loners = 4        // Prefer to be alone or in very small groups
    }

    /// <summary>
    /// Represents a group of inmates in MainRec area
    /// </summary>
    public class InmateGroup
    {
        public int GroupID { get; set; }
        public System.Collections.Generic.List<JailInmateBehavior> Members { get; set; } = new System.Collections.Generic.List<JailInmateBehavior>();
        public Vector3 GatherPoint { get; set; }
        public InmateFaction PrimaryFaction { get; set; }
        public float FormationTime { get; set; }
        public bool IsActive { get; set; } = true;

        public InmateGroup(int id, Vector3 gatherPoint, InmateFaction faction)
        {
            GroupID = id;
            GatherPoint = gatherPoint;
            PrimaryFaction = faction;
            FormationTime = Time.time;
        }

        public bool CanJoinGroup(JailInmateBehavior inmate)
        {
            // Groups have max 3 members
            if (Members.Count >= 3) return false;

            // Faction compatibility
            if (PrimaryFaction != InmateFaction.None && inmate.faction != InmateFaction.None)
            {
                return inmate.faction == PrimaryFaction || inmate.faction == InmateFaction.Neutral;
            }

            return true;
        }

        public void AddMember(JailInmateBehavior inmate)
        {
            if (!Members.Contains(inmate) && CanJoinGroup(inmate))
            {
                Members.Add(inmate);
                inmate.groupID = GroupID;
                ModLogger.Info($"Inmate {inmate.prisonerID} joined group {GroupID} ({Members.Count}/3 members)");
            }
        }

        public void RemoveMember(JailInmateBehavior inmate)
        {
            if (Members.Remove(inmate))
            {
                inmate.groupID = -1;
                ModLogger.Info($"Inmate {inmate.prisonerID} left group {GroupID} ({Members.Count}/3 members)");

                // Handle group dissolution or migration
                if (Members.Count == 0)
                {
                    IsActive = false;
                }
                else if (Members.Count == 1)
                {
                    // Single member might migrate to another group
                    var remainingMember = Members[0];
                    ModLogger.Debug($"Group {GroupID} has only one member left: {remainingMember.prisonerID}");
                }
            }
        }

        public Vector3 GetPositionForMember(int memberIndex)
        {
            // Position members much closer together, like people actually talking
            float angle = (memberIndex * 120f) * Mathf.Deg2Rad; // 120° apart for up to 3 members
            float radius = 0.8f; // Much closer - almost shoulder to shoulder

            return GatherPoint + new Vector3(
                Mathf.Cos(angle) * radius,
                0f,
                Mathf.Sin(angle) * radius
            );
        }
    }

    /// <summary>
    /// Custom SaveableField attribute for persistence
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field)]
    public class SaveableField : System.Attribute
    {
        public string SaveName { get; }
        public SaveableField(string saveName) => SaveName = saveName;
    }
}