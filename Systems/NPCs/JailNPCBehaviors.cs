using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
using Il2CppSystem.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
#else
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Messaging;
#endif

namespace Behind_Bars.Systems.NPCs
{
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
                    role = GuardRole.BookingStationary;
                    break;
                case GuardAssignment.Booking1:
                    role = GuardRole.IntakeOfficer; // Booking1 is the intake officer
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
        private bool TrySendNPCMessage(string message)
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
                    // Occasional look around at station
                    if (UnityEngine.Random.Range(0f, 1f) < 0.1f)
                    {
                        float baseRotation = assignedSpawnPoint.eulerAngles.y;
                        float lookDirection = baseRotation + UnityEngine.Random.Range(-45f, 45f);
                        var targetRotation = Quaternion.Euler(0, lookDirection, 0);
                        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 1.5f);
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
        /// Start escorting a prisoner
        /// </summary>
        public void StartPrisonerEscort(GameObject prisoner)
        {
            currentEscortTarget = prisoner;
            isIntakeActive = true;
            
            // Get escort path from booking process or jail controller
            SetupEscortWaypoints();
            
            // Ensure npcComponent is initialized before using it
            if (npcComponent == null)
            {
                EnsureNPCComponentInitialized();
            }
            
            // Text messaging is optional - guard escort works without it
            if (TrySendNPCMessage("Follow me for processing."))
            {
                ModLogger.Debug($"Guard {badgeNumber} instructing prisoner to follow");
            }
            else
            {
                ModLogger.Debug($"Guard {badgeNumber} starting escort (text messaging unavailable)");
            }
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
        /// Setup waypoints for prisoner escort
        /// </summary>
        private void SetupEscortWaypoints()
        {
            escortWaypoints.Clear();
            
            var jailController = Core.ActiveJailController;
            if (jailController == null) return;
            
            // Create escort path: Holding -> Mugshot -> Scanner -> Storage -> Cell
            // For now, use basic waypoints - can be enhanced later
            var mugshotPos = FindStationPosition("Mugshot");
            var scannerPos = FindStationPosition("Scanner");
            var storagePos = FindStationPosition("Storage");
            
            if (mugshotPos.HasValue) escortWaypoints.Enqueue(mugshotPos.Value);
            if (scannerPos.HasValue) escortWaypoints.Enqueue(scannerPos.Value);
            if (storagePos.HasValue) escortWaypoints.Enqueue(storagePos.Value);
            
            ModLogger.Info($"Setup {escortWaypoints.Count} escort waypoints for intake");
        }
        
        /// <summary>
        /// Find position of a station for escort waypoints
        /// </summary>
        private Vector3? FindStationPosition(string stationType)
        {
            // Simple implementation - can be enhanced with actual station positions
            var jailController = Core.ActiveJailController;
            if (jailController == null) return null;
            
            switch (stationType.ToLower())
            {
                case "mugshot":
                    return jailController.transform.position + new Vector3(2f, 0f, -3f);
                case "scanner":
                    return jailController.transform.position + new Vector3(-2f, 0f, -3f);
                case "storage":
                    return jailController.transform.position + new Vector3(0f, 0f, -6f);
                default:
                    return null;
            }
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