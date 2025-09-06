using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Behind_Bars.Helpers;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Simple test NPC that follows a moveable target for pathfinding debugging
    /// </summary>
    public class TestNPCController : MonoBehaviour
    {
        private NavMeshAgent navAgent;
        private Transform target;
        private static Transform moveableTarget;
        
        // Animation components
        private Component avatarAnimationComponent;
        private Component velocityCalculatorComponent;
        private float animationSpeedMultiplier = 0.5f; // Good walking animation speed
        
        // Patrol system
        public bool usePatrolMode = false; // Start with moveable target, toggle with P key
        private System.Collections.Generic.List<UnityEngine.Vector3> patrolPoints;
        private int currentPatrolIndex = 0;
        private float patrolWaitTime = 3f; // Wait at each patrol point
        private float lastPatrolTime = 0f;
        
        void Start()
        {
            navAgent = GetComponent<NavMeshAgent>();
            if (navAgent == null)
            {
                ModLogger.Error("TestNPCController: NavMeshAgent not found!");
                return;
            }
            
            // Configure NavMesh agent for testing
            navAgent.speed = 2.5f;
            navAgent.stoppingDistance = 0.8f;
            navAgent.acceleration = 6f;
            navAgent.angularSpeed = 180f;
            navAgent.autoBraking = true;
            navAgent.autoRepath = true;
            
            // Disable any other NPC behaviors that might interfere
            DisableOtherBehaviors();
            
            ModLogger.Info($"Test NPC created at {transform.position} with NavMeshAgent");
            
            // Debug all components that might be controlling this NPC
            DebugControllingComponents();
            
            // Initialize animation system
            InitializeAnimationSystem();
            
            // Initialize patrol points from the jail system
            InitializePatrolPoints();
            
            // Set initial mode and target
            if (usePatrolMode && patrolPoints != null && patrolPoints.Count > 0)
            {
                target = null; // Will use patrol points
                ModLogger.Info("TestNPC starting in PATROL MODE - press P to toggle to moveable target");
            }

            // Start the main behavior loop
            MelonCoroutines.Start(TestNPCBehavior());
        }
        
        private void AdjustNPCSpeed(float change)
        {
            if (navAgent != null)
            {
                navAgent.speed = Mathf.Clamp(navAgent.speed + change, 0.5f, 10f);
                ModLogger.Info($"TestNPC speed adjusted to: {navAgent.speed:F1} (use +/- keys to adjust, R to reset)");
            }
        }
        
        
        private void SetWalkingSpeed()
        {
            if (navAgent != null)
            {
                navAgent.speed = 2f;
                animationSpeedMultiplier = 0.5f;
                ModLogger.Info($"✓ Set to WALKING: Speed={navAgent.speed:F1}, Animation={animationSpeedMultiplier:F1}");
            }
        }
        
        private void SetRunningSpeed()
        {
            if (navAgent != null)
            {
                navAgent.speed = 5.75f; // Average of 5.5-6
                animationSpeedMultiplier = 1f;
                ModLogger.Info($"✓ Set to RUNNING: Speed={navAgent.speed:F1}, Animation={animationSpeedMultiplier:F1}");
            }
        }
        
        private void InitializeAnimationSystem()
        {
            ModLogger.Info("Initializing animation system for TestNPC...");
            
            try
            {
                // Look for AvatarAnimation component (could be on this object or a child)
#if !MONO
                avatarAnimationComponent = GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation>();
                if (avatarAnimationComponent == null)
                {
                    // Try to find it by type name since IL2CPP can be tricky
                    var allComponents = GetComponentsInChildren<Component>();
                    foreach (var comp in allComponents)
                    {
                        if (comp != null && comp.GetType().Name.Contains("AvatarAnimation"))
                        {
                            avatarAnimationComponent = comp;
                            break;
                        }
                    }
                }
#else
                avatarAnimationComponent = GetComponentInChildren<ScheduleOne.AvatarFramework.Animation.AvatarAnimation>();
#endif
                
                if (avatarAnimationComponent != null)
                {
                    ModLogger.Info($"✓ Found AvatarAnimation component: {avatarAnimationComponent.GetType().Name}");
                }
                else
                {
                    ModLogger.Warn("AvatarAnimation component not found - animations may not work properly");
                }
                
                // Look for SmoothedVelocityCalculator
#if !MONO
                velocityCalculatorComponent = GetComponentInChildren<Il2CppScheduleOne.Tools.SmoothedVelocityCalculator>();
                if (velocityCalculatorComponent == null)
                {
                    var allComponents = GetComponentsInChildren<Component>();
                    foreach (var comp in allComponents)
                    {
                        if (comp != null && comp.GetType().Name.Contains("SmoothedVelocityCalculator"))
                        {
                            velocityCalculatorComponent = comp;
                            break;
                        }
                    }
                }
#else
                velocityCalculatorComponent = GetComponentInChildren<ScheduleOne.Tools.SmoothedVelocityCalculator>();
#endif
                
                if (velocityCalculatorComponent != null)
                {
                    ModLogger.Info($"✓ Found SmoothedVelocityCalculator component: {velocityCalculatorComponent.GetType().Name}");
                }
                else
                {
                    ModLogger.Warn("SmoothedVelocityCalculator component not found - will use NavMeshAgent velocity instead");
                }
                
                ModLogger.Info("✓ Animation system initialization completed");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error initializing animation system: {e.Message}");
            }
        }
        
        private void InitializePatrolPoints()
        {
            patrolPoints = new System.Collections.Generic.List<UnityEngine.Vector3>();
            
            // Get patrol points directly from Core's ActiveJailController
            var jailController = Core.ActiveJailController;
            if (jailController != null)
            {
                foreach (var point in jailController.patrolPoints)
                {
                    patrolPoints.Add(point.position);
                    ModLogger.Debug($"Added patrol point: {point.name} at {point.position}");
                }
                ModLogger.Info($"✓ Loaded {patrolPoints.Count} patrol points from Core.ActiveJailController");
            }
            else
            {
                ModLogger.Warn("Core.ActiveJailController not found, patrol mode will not work");
            }
        }
        
        private void DisableOtherBehaviors()
        {
            ModLogger.Info("Disabling other behaviors that might interfere with TestNPC...");
            
            // Find and disable GuardStateMachine more thoroughly
            var guardSMs = GetComponents<GuardStateMachine>();
            ModLogger.Info($"Found {guardSMs.Length} GuardStateMachine components");
            foreach (var guardSM in guardSMs)
            {
                guardSM.enabled = false;
                ModLogger.Warn($"✓ DISABLED GuardStateMachine on TestNPC: {guardSM.GetType().Name}");
            }
            
            // Find and disable InmateStateMachine 
            var inmateSMs = GetComponents<InmateStateMachine>();
            ModLogger.Info($"Found {inmateSMs.Length} InmateStateMachine components");
            foreach (var inmateSM in inmateSMs)
            {
                inmateSM.enabled = false;
                ModLogger.Warn($"✓ DISABLED InmateStateMachine on TestNPC: {inmateSM.GetType().Name}");
            }
                        
            ModLogger.Info("✓ Finished disabling interfering behaviors on TestNPC");
        }

        private Transform FindPatrolPointTarget()
        {
            // Use the patrol points registered in JailController
            var jailController = UnityEngine.Object.FindObjectOfType<JailController>();
            if (jailController == null)
            {
                ModLogger.Error("JailController not found for patrol points");
                return null;
            }
            
            var patrolPoints = jailController.patrolPoints;
            if (patrolPoints.Count == 0)
            {
                ModLogger.Warn("No patrol points registered in JailController");
                return null;
            }
            
            // Choose a good patrol point for testing - prefer one that's not too close
            Transform bestPatrol = null;
            float bestDistance = 0f;
            
            foreach (var patrol in patrolPoints)
            {
                float distance = Vector3.Distance(transform.position, patrol.position);
                if (distance > 5f && distance < 20f) // Good testing distance
                {
                    if (bestPatrol == null || distance > bestDistance)
                    {
                        bestPatrol = patrol;
                        bestDistance = distance;
                    }
                }
            }
            
            // If no good distance patrol found, just use the first one
            if (bestPatrol == null)
            {
                bestPatrol = patrolPoints[0];
                bestDistance = Vector3.Distance(transform.position, bestPatrol.position);
            }
            
            ModLogger.Info($"✓ TestNPC will follow patrol point: {bestPatrol.name} (distance: {bestDistance:F1})");
            ModLogger.Info($"Available patrol points: {string.Join(", ", patrolPoints.Select(p => p.name))}");
            
            return bestPatrol;
        }
        
        private Transform FindNextPatrolPoint()
        {
            // Use the patrol points registered in JailController
            var jailController = UnityEngine.Object.FindObjectOfType<JailController>();
            if (jailController == null)
            {
                ModLogger.Error("JailController not found for next patrol point");
                return null;
            }
            
            // Get all patrol points except the current target
            var otherPatrolPoints = jailController.patrolPoints.Where(p => p != target).ToList();
            
            if (otherPatrolPoints.Count == 0)
            {
                ModLogger.Debug("No other patrol points found for rotation");
                return null;
            }
            
            // Pick a random patrol point for variety
            int randomIndex = UnityEngine.Random.Range(0, otherPatrolPoints.Count);
            Transform nextPatrol = otherPatrolPoints[randomIndex];
            
            ModLogger.Debug($"Selected next patrol point: {nextPatrol.name} from {otherPatrolPoints.Count} options");
            return nextPatrol;
        }
        
        private void DebugControllingComponents()
        {
            ModLogger.Info("=== TestNPC Component Analysis ===");
            
            // Check all components on this GameObject
            var allComponents = GetComponents<Component>();
            ModLogger.Info($"Root TestNPC components ({allComponents.Length} total):");
            foreach (var component in allComponents)
            {
                string status = "";
                if (component is MonoBehaviour mb)
                {
                    status = $" (Enabled: {mb.enabled})";
                    
                    // Double-check state machines are disabled
                    if (component is GuardStateMachine guardSM && guardSM.enabled)
                    {
                        guardSM.enabled = false;
                        ModLogger.Error($"⚠️  RE-DISABLED GuardStateMachine that was still enabled!");
                        status = " (Enabled: FALSE - FORCE DISABLED)";
                    }
                    else if (component is InmateStateMachine inmateSM && inmateSM.enabled)
                    {
                        inmateSM.enabled = false;
                        ModLogger.Error($"⚠️  RE-DISABLED InmateStateMachine that was still enabled!");
                        status = " (Enabled: FALSE - FORCE DISABLED)";
                    }
                }
                ModLogger.Info($"  - {component.GetType().Name}{status}");
            }
            
            // Check ALL avatar child components - this is where the scripts likely are!
            var allChildComponents = GetComponentsInChildren<Component>();
            ModLogger.Info($"All TestNPC + Avatar components ({allChildComponents.Length} total):");
            foreach (var component in allChildComponents)
            {
                if (component.gameObject != gameObject) // Skip root components we already listed
                {
                    string status = "";
                    string objectPath = GetGameObjectPath(component.gameObject);
                    
                    if (component is MonoBehaviour mb)
                    {
                        status = $" (Enabled: {mb.enabled})";
                        
                        // Disable any avatar scripts that might interfere with movement
                        if (IsLikelyMovementScript(component.GetType().Name))
                        {
                            mb.enabled = false;
                            ModLogger.Warn($"⚠️  DISABLED avatar movement script: {component.GetType().Name} on {objectPath}");
                        }
                    }
                    
                    ModLogger.Debug($"  Avatar child: {objectPath} -> {component.GetType().Name}{status}");
                }
            }
            
            // Check for potential controlling components specifically
            var npcComponent = GetComponent<
#if !MONO
                Il2CppScheduleOne.NPCs.NPC
#else
                ScheduleOne.NPCs.NPC
#endif
            >();
            
            if (npcComponent != null)
            {
                ModLogger.Warn($"⚠️  Found Schedule One NPC component - this might have built-in AI!");
                // Try to disable it if possible
                if (npcComponent is MonoBehaviour npcMB)
                {
                    npcMB.enabled = false;
                    ModLogger.Info("✓ Disabled Schedule One NPC component");
                }
            }
            
            // Check PatrolSystem assignment and prevent it
            try 
            {
                var assignedPatrol = PatrolSystem.AssignPatrolPointToGuard(transform);
                if (assignedPatrol != null)
                {
                    ModLogger.Warn($"⚠️  TestNPC got assigned patrol point: {assignedPatrol.name} - this will conflict!");
                    PatrolSystem.ReleasePatrolPointAssignment(transform);
                    ModLogger.Info("✓ Released patrol point assignment from TestNPC");
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Debug($"PatrolSystem check failed (expected): {e.Message}");
            }
            
            // Check NavMeshAgent settings
            if (navAgent != null)
            {
                ModLogger.Debug($"NavAgent: OnNavMesh={navAgent.isOnNavMesh}, HasPath={navAgent.hasPath}, Enabled={navAgent.enabled}");
                ModLogger.Debug($"NavAgent: Position={transform.position}");
                
                // Make sure NavAgent is properly configured for manual control
                navAgent.updateRotation = true;
                navAgent.updatePosition = true;
                navAgent.isStopped = false;
            }
            
            ModLogger.Info("=== End Component Analysis ===");
        }
        
        private string GetGameObjectPath(GameObject obj)
        {
            string path = obj.name;
            Transform parent = obj.transform.parent;
            while (parent != null && parent != transform)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
        
        private bool IsLikelyMovementScript(string typeName)
        {
            // Common patterns for movement/AI scripts that might interfere
            string[] movementPatterns = {
                "Movement", "Controller", "AI", "Brain", "Behavior", "Behaviour", 
                "Navigation", "Pathfinding", "Walk", "Move", "Locomotion",
                "StateMachine", "FSM", "Decision", "Action", "Task",
                "NPC", "Character", "Agent", "Pilot", "Driver"
            };
            
            string upperTypeName = typeName.ToUpper();
            foreach (string pattern in movementPatterns)
            {
                if (upperTypeName.Contains(pattern.ToUpper()))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private IEnumerator FollowTarget()
        {
            float lastDebugTime = 0f;
            float lastDestinationUpdate = 0f;
            Vector3 lastTargetPosition = Vector3.zero;
            Vector3 lastNPCPosition = transform.position;
            
            while (target != null && navAgent != null)
            {
                if (navAgent.enabled && navAgent.isOnNavMesh)
                {
                    float distanceToTarget = Vector3.Distance(transform.position, target.position);
                    
                    // Update destination if target moved significantly or time passed
                    bool targetMoved = Vector3.Distance(target.position, lastTargetPosition) > 0.3f;
                    bool timeoutReached = Time.time - lastDestinationUpdate > 1f;
                    bool needsNewPath = !navAgent.hasPath || navAgent.pathStatus != NavMeshPathStatus.PathComplete;
                    
                    if (targetMoved || timeoutReached || needsNewPath)
                    {
                        navAgent.SetDestination(target.position);
                        lastTargetPosition = target.position;
                        lastDestinationUpdate = Time.time;
                        ModLogger.Debug($"TestNPC updating path to {target.position:F1}");
                    }
                    
                    // Debug info every 2 seconds
                    if (Time.time - lastDebugTime > 2f)
                    {
                        string pathStatus = navAgent.hasPath ? 
                            $"Path: {navAgent.path.corners.Length} corners, Status: {navAgent.pathStatus}" : "No path";
                        ModLogger.Info($"TestNPC: Pos={transform.position:F1}, Target={target.position:F1}, " +
                                     $"Dist={distanceToTarget:F1}, Speed={navAgent.velocity.magnitude:F1}, {pathStatus}");
                        lastDebugTime = Time.time;
                    }
                }
                else
                {
                    // Check if position changed unexpectedly
                    Vector3 currentPos = transform.position;
                    if (Vector3.Distance(currentPos, lastNPCPosition) > 0.01f)
                    {
                        ModLogger.Warn($"TestNPC position changed unexpectedly! From {lastNPCPosition:F2} to {currentPos:F2}");
                    }
                    
                    ModLogger.Warn($"TestNPC NavAgent not on NavMesh or disabled: OnMesh={navAgent.isOnNavMesh}, Enabled={navAgent.enabled}");
                    ModLogger.Debug($"TestNPC trying to find NavMesh near {currentPos:F2}");
                    
                    // Try to get back on NavMesh
                    if (UnityEngine.AI.NavMesh.SamplePosition(currentPos, out UnityEngine.AI.NavMeshHit hit, 5f, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        ModLogger.Info($"Found NavMesh at {hit.position:F2}, trying to warp back...");
                        navAgent.Warp(hit.position);
                    }
                }
                
                lastNPCPosition = transform.position;
                
                yield return new WaitForSeconds(0.1f); // Frequent updates for responsive movement
            }
        }
        
        private IEnumerator TestNPCBehavior()
        {
            ModLogger.Info("TestNPCBehavior coroutine started");
            
            // Start the animation update coroutine
            MelonCoroutines.Start(UpdateAnimations());
            
            while (true)
            {
                if (usePatrolMode && patrolPoints != null && patrolPoints.Count > 0)
                {
                    // Patrol mode - move between patrol points
                    yield return PatrolBehavior();
                }
                else if (target != null)
                {
                    // Moveable target mode - follow the red cube
                    yield return FollowTarget();
                }
                else
                {
                    // Wait for target or patrol points to be available
                    yield return new WaitForSeconds(1f);
                }
            }
        }
        
        private IEnumerator UpdateAnimations()
        {
            ModLogger.Info("Animation update coroutine started");
            
            while (true)
            {
                try
                {
                    UpdateMovementAnimation();
                }
                catch (System.Exception e)
                {
                    ModLogger.Error($"Error updating animation: {e.Message}");
                }
                
                // Update animations frequently for smooth movement
                yield return new WaitForSeconds(0.05f);
            }
        }
        
        private void UpdateMovementAnimation()
        {
            if (avatarAnimationComponent == null || navAgent == null)
                return;
                
            Vector3 velocity = Vector3.zero;
            
            // Get velocity from SmoothedVelocityCalculator if available, otherwise use NavMeshAgent
            if (velocityCalculatorComponent != null)
            {
                try
                {
#if !MONO
                    var calculator = velocityCalculatorComponent.TryCast<Il2CppScheduleOne.Tools.SmoothedVelocityCalculator>();
                    if (calculator != null)
                    {
                        velocity = calculator.Velocity;
                    }
#else
                    var calculator = velocityCalculatorComponent as ScheduleOne.Tools.SmoothedVelocityCalculator;
                    if (calculator != null)
                    {
                        velocity = calculator.Velocity;
                    }
#endif
                }
                catch
                {
                    // Fall back to NavMeshAgent velocity
                    velocity = navAgent.velocity;
                }
            }
            else
            {
                // Use NavMeshAgent velocity directly
                velocity = navAgent.velocity;
            }
            
            // Transform velocity to local space and scale for animation
            Vector3 localVelocity = transform.InverseTransformVector(velocity);
            
            // Scale animation speed to match movement
            localVelocity *= animationSpeedMultiplier;
            
            // Set animation parameters
            try
            {
#if !MONO
                var avatarAnim = avatarAnimationComponent.TryCast<Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation>();
                if (avatarAnim != null)
                {
                    float direction = localVelocity.z;
                    float strafe = localVelocity.x;
                    
                    avatarAnim.SetDirection(direction);
                    avatarAnim.SetStrafe(strafe);
                }
#else
                var avatarAnim = avatarAnimationComponent as ScheduleOne.AvatarFramework.Animation.AvatarAnimation;
                if (avatarAnim != null)
                {
                    float direction = localVelocity.z;
                    float strafe = localVelocity.x;
                    
                    avatarAnim.SetDirection(direction);
                    avatarAnim.SetStrafe(strafe);
                }
#endif
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error setting animation parameters: {e.Message}");
            }
        }
        
        private IEnumerator PatrolBehavior()
        {
            if (patrolPoints == null || patrolPoints.Count == 0)
            {
                ModLogger.Warn("No patrol points available for patrol mode");
                yield return new WaitForSeconds(1f);
                yield break;
            }
            
            // Go to current patrol point
            Vector3 targetPatrolPoint = patrolPoints[currentPatrolIndex];
            
            if (navAgent != null && navAgent.enabled && navAgent.isOnNavMesh)
            {
                navAgent.SetDestination(targetPatrolPoint);
                ModLogger.Info($"TestNPC patrolling to point {currentPatrolIndex}: {targetPatrolPoint:F1}");
                
                // Wait until we reach the patrol point
                while (Vector3.Distance(transform.position, targetPatrolPoint) > navAgent.stoppingDistance + 0.5f)
                {
                    // Check if we switched out of patrol mode
                    if (!usePatrolMode) yield break;
                    
                    yield return new WaitForSeconds(0.2f);
                }
                
                ModLogger.Info($"✓ Reached patrol point {currentPatrolIndex}");
                
                // Wait at patrol point
                float waitStartTime = Time.time;
                while (Time.time - waitStartTime < patrolWaitTime)
                {
                    // Check if we switched out of patrol mode
                    if (!usePatrolMode) yield break;
                    
                    yield return new WaitForSeconds(0.1f);
                }
                
                // Move to next patrol point
                currentPatrolIndex = (currentPatrolIndex + 1) % patrolPoints.Count;
                ModLogger.Debug($"Next patrol point: {currentPatrolIndex}");
            }
            else
            {
                ModLogger.Warn("NavAgent not ready for patrolling");
                yield return new WaitForSeconds(1f);
            }
        }
    }
    
    /// <summary>
    /// Controller for the moveable target cube
    /// </summary>
    public class MoveableTargetController : MonoBehaviour
    {
        private float moveSpeed = 4f;
        private float lastMoveTime = 0f;
        
        void Start()
        {
            ModLogger.Info("MoveableTargetController started. Controls: Numpad 8/2/4/6 (move), Numpad +/- (up/down)");
            ModLogger.Info("Target controls active - use numpad to move the red cube around");
        }
        
        void Update()
        {
            // Use numpad keys to avoid conflicts with player WASD movement
            Vector3 movement = Vector3.zero;
            
            // Horizontal movement with numpad
            if (Input.GetKey(KeyCode.Keypad8)) movement += Vector3.forward;    // Numpad 8 = forward
            if (Input.GetKey(KeyCode.Keypad2)) movement += Vector3.back;       // Numpad 2 = back  
            if (Input.GetKey(KeyCode.Keypad4)) movement += Vector3.left;       // Numpad 4 = left
            if (Input.GetKey(KeyCode.Keypad6)) movement += Vector3.right;      // Numpad 6 = right
            
            // Vertical movement
            if (Input.GetKey(KeyCode.KeypadPlus)) movement += Vector3.up;      // Numpad + = up
            if (Input.GetKey(KeyCode.KeypadMinus)) movement += Vector3.down;   // Numpad - = down
            
            // Speed modifier
            if (Input.GetKey(KeyCode.LeftShift)) moveSpeed = 8f;
            else moveSpeed = 4f;
            
            if (movement != Vector3.zero)
            {
                Vector3 newPosition = transform.position + movement.normalized * moveSpeed * Time.deltaTime;
                
                // Try to keep target on NavMesh for better testing
                if (UnityEngine.AI.NavMesh.SamplePosition(newPosition, out UnityEngine.AI.NavMeshHit hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    newPosition = hit.position + Vector3.up * 1f; // Keep elevated
                }
                
                transform.position = newPosition;
                
                // Log movement less frequently
                if (Time.time - lastMoveTime > 1f)
                {
                    ModLogger.Debug($"Target moved to: {transform.position:F1}");
                    lastMoveTime = Time.time;
                }
            }
            
            // Quick teleport commands for testing different areas
            if (Input.GetKeyDown(KeyCode.Keypad1)) TeleportToArea("Cell Area", transform.position + Vector3.forward * 15f);
            if (Input.GetKeyDown(KeyCode.Keypad3)) TeleportToArea("Kitchen Area", transform.position + Vector3.right * 15f);
            if (Input.GetKeyDown(KeyCode.Keypad7)) TeleportToArea("Guard Room", transform.position + Vector3.back * 15f);
            if (Input.GetKeyDown(KeyCode.Keypad9)) TeleportToArea("Recreation Area", transform.position + Vector3.left * 15f);
        }
        
        void TeleportToArea(string areaName, Vector3 targetPos)
        {
            if (UnityEngine.AI.NavMesh.SamplePosition(targetPos, out UnityEngine.AI.NavMeshHit hit, 10f, UnityEngine.AI.NavMesh.AllAreas))
            {
                transform.position = hit.position + Vector3.up * 1f;
                ModLogger.Info($"Target teleported to {areaName} at {transform.position:F1}");
            }
            else
            {
                ModLogger.Warn($"No NavMesh found near {areaName}");
            }
        }
    }
}