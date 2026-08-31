using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using MelonLoader;
using Behind_Bars.Helpers;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.AvatarFramework.Animation;
using Avatar = Il2CppScheduleOne.AvatarFramework.Avatar;
#else
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.AvatarFramework;
using ScheduleOne.AvatarFramework.Animation;
using Avatar = ScheduleOne.AvatarFramework.Avatar;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Base class for all jail NPCs - consolidates common functionality
    /// Replaces NPCStateMachine, GuardStateMachine, and core NPC behavior
    /// </summary>
    public class BaseJailNPC : MonoBehaviour
    {
#if !MONO
        public BaseJailNPC(System.IntPtr ptr) : base(ptr) { }
#endif

        // Core NPC States
        public enum NPCState
        {
            Idle,
            Moving,
            Interacting,
            Waiting,
            Working,
            Error
        }

        // Core Components
        protected NavMeshAgent navAgent;
        protected NPCState currentState = NPCState.Idle;
        protected bool isInitialized = false;
        protected float stateStartTime = 0f;

        // Health and Combat
#if !MONO
        protected Il2CppScheduleOne.NPCs.NPCHealth npcHealth;
#else
        protected ScheduleOne.NPCs.NPCHealth npcHealth;
#endif

        // Avatar and Animation Support
#if !MONO
        protected Il2CppScheduleOne.AvatarFramework.Avatar npcAvatar;
        protected Il2CppScheduleOne.AvatarFramework.Animation.AvatarLookController lookController;
        protected Il2CppScheduleOne.NPCs.NPC npcComponent;
#else
        protected ScheduleOne.AvatarFramework.Avatar npcAvatar;
        protected ScheduleOne.AvatarFramework.Animation.AvatarLookController lookController;
        protected ScheduleOne.NPCs.NPC npcComponent;
#endif
        protected bool lookControllerAvailable = false;

        // Movement and Navigation
        protected Vector3 currentDestination;
        protected bool hasReachedDestination = true;
        protected float lastDestinationTime = 0f;
        protected float positionTolerance = 1.5f;
        protected float lastMovementObservedTime = 0f;
        protected Vector3 lastPosition;
        protected const float stuckThreshold = 5f;
        protected const float minMovementDistance = 0.1f;

        // State Management
#if MONO
        // Events
        public System.Action<NPCState, NPCState> OnStateChanged;
        public System.Action<Vector3> OnDestinationReached;
        public System.Action OnStuck;
        public System.Action<Player> OnAttacked;
#endif

        protected virtual void Awake()
        {
            InitializeComponents();
            InitializeStateHandlers();
        }

        protected virtual void Start()
        {
            if (!ValidateComponents())
            {
                ModLogger.Error($"BaseJailNPC: Failed to initialize components on {gameObject.name}");
                enabled = false;
                return;
            }

            InitializeAvatar();
            SetupAttackDetection();
            InitializeNPC();
            isInitialized = true;

            ModLogger.Debug($"BaseJailNPC initialized: {gameObject.name}");
        }

        /// <summary>
        /// Performance: Event-driven updates instead of per-frame Update()
        /// Subscribes to NPCUpdateManager events for throttled updates
        /// </summary>
        protected virtual void OnEnable()
        {
            if (NPCUpdateManager.Instance != null)
            {
                NPCUpdateManager.Instance.RegisterNPC(this);
            }
        }

        protected virtual void OnDisable()
        {
            if (NPCUpdateManager.Instance != null)
            {
                NPCUpdateManager.Instance.UnregisterNPC(this);
            }
        }

        internal void DispatchStateUpdate(float currentTime)
        {
            if (!isInitialized) return;
            OnStateUpdateTick(currentTime);
        }

        internal void DispatchMovementCheck(float currentTime)
        {
            if (!isInitialized) return;
            OnMovementCheckTick(currentTime);
        }

        protected virtual void OnStateUpdateTick(float currentTime)
        {
            UpdateState();
        }

        protected virtual void OnMovementCheckTick(float currentTime)
        {
            CheckStuckMovement(currentTime);
        }

        #region Initialization

        protected virtual void InitializeComponents()
        {
            navAgent = GetComponent<NavMeshAgent>();
            npcComponent = GetComponent<NPC>();
            npcHealth = GetComponent<NPCHealth>();
            lastPosition = transform.position;
        }

        protected virtual bool ValidateComponents()
        {
            if (navAgent == null)
            {
                ModLogger.Error($"BaseJailNPC: NavMeshAgent not found on {gameObject.name}");
                return false;
            }
            return true;
        }

        protected virtual void InitializeAvatar()
        {
            try
            {
                npcAvatar = GetComponent<Avatar>();
                if (npcAvatar != null)
                {
                    lookController = npcAvatar.GetComponent<AvatarLookController>();
                    lookControllerAvailable = lookController != null;
                    ModLogger.Debug($"Avatar initialized for {gameObject.name}, LookController: {lookControllerAvailable}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to initialize avatar for {gameObject.name}: {ex.Message}");
            }
        }

        protected virtual void InitializeStateHandlers()
        {
            // Intentionally empty. State dispatch uses a direct switch for IL2CPP safety.
        }

        protected virtual void SetupAttackDetection()
        {
            if (npcHealth != null)
            {
                try
                {
                    // Create a wrapper component to monitor health changes
                    var attackMonitor = BBHelpers.GetComponentSafe<NPCAttackMonitor>(gameObject);
                    if (attackMonitor == null)
                    {
                        attackMonitor = BBHelpers.AddComponentSafe<NPCAttackMonitor>(gameObject);
                        attackMonitor.Initialize(this);
                    }
                    ModLogger.Debug($"BaseJailNPC: Attack detection setup for {gameObject.name}");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Warn($"BaseJailNPC: Could not setup attack detection for {gameObject.name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called when this NPC takes damage from a player attack
        /// Override in derived classes for specific responses
        /// </summary>
        /// <param name="attacker">The player who attacked this NPC</param>
        public virtual void OnAttackedByPlayer(Player attacker)
        {
            ModLogger.Info($"BaseJailNPC: {gameObject.name} was attacked by {attacker?.name}");
            NotifyAttacked(attacker);
        }

        protected virtual void InitializeNPC()
        {
            // Derived behaviors override this to provide role-specific initialization.
        }

        #endregion

        #region State Management

        public virtual void ChangeState(NPCState newState)
        {
            if (currentState == newState) return;

            NPCState oldState = currentState;
            ExitState(oldState);
            currentState = newState;
            stateStartTime = Time.time;
            EnterState(newState);

            NotifyStateChanged(oldState, newState);
            ModLogger.Debug($"{gameObject.name} changed state: {oldState} → {newState}");
        }

        protected virtual void EnterState(NPCState state)
        {
            // Override in derived classes for specific enter behavior
        }

        protected virtual void ExitState(NPCState state)
        {
            // Override in derived classes for specific exit behavior
        }

        protected virtual void UpdateState()
        {
            switch (currentState)
            {
                case NPCState.Idle:
                    HandleIdleState();
                    break;
                case NPCState.Moving:
                    HandleMovingState();
                    break;
                case NPCState.Interacting:
                    HandleInteractingState();
                    break;
                case NPCState.Waiting:
                    HandleWaitingState();
                    break;
                case NPCState.Working:
                    HandleWorkingState();
                    break;
                case NPCState.Error:
                    HandleErrorState();
                    break;
            }
        }

        #endregion

        #region State Handlers (Virtual - can be overridden)

        protected virtual void HandleIdleState()
        {
            // Default idle behavior - do nothing
        }

        protected virtual void HandleMovingState()
        {
            if (HasReachedDestination())
            {
                // ModLogger.Info($"BaseJailNPC: Destination reached, firing OnDestinationReached event for {gameObject.name}");
                NotifyDestinationReached(currentDestination);
                ChangeState(NPCState.Idle);
            }
        }

        protected virtual void HandleInteractingState()
        {
            // Override in derived classes
        }

        protected virtual void HandleWaitingState()
        {
            // Override in derived classes
        }

        protected virtual void HandleWorkingState()
        {
            // Override in derived classes
        }

        protected virtual void HandleErrorState()
        {
            ModLogger.Error($"{gameObject.name} is in error state");
            // Try to recover by going back to idle
            if (Time.time - stateStartTime > 5f)
            {
                ChangeState(NPCState.Idle);
            }
        }

        #endregion

        #region Navigation

        public virtual bool MoveTo(Vector3 destination, float tolerance = -1f)
        {
            if (navAgent == null || !navAgent.enabled || !navAgent.isOnNavMesh)
            {
                ModLogger.Warn($"{gameObject.name} cannot move: NavMesh agent is unavailable or off the NavMesh");
                return false;
            }

            if (tolerance > 0) positionTolerance = tolerance;

            currentDestination = destination;
            hasReachedDestination = false;
            lastDestinationTime = Time.time;
            lastMovementObservedTime = lastDestinationTime;
            lastPosition = transform.position;

            if (!navAgent.SetDestination(destination))
            {
                ModLogger.Warn($"{gameObject.name} could not set destination {destination}");
                return false;
            }
            ChangeState(NPCState.Moving);

            // ModLogger.Debug($"{gameObject.name} moving to {destination}");
            return true;
        }

        public virtual bool HasReachedDestination()
        {
            if (navAgent == null || !navAgent.enabled || !navAgent.isOnNavMesh)
            {
                return false;
            }

            if (!navAgent.pathPending && navAgent.pathStatus != NavMeshPathStatus.PathComplete)
            {
                return false;
            }

            bool pathComplete = !navAgent.pathPending && navAgent.remainingDistance < positionTolerance;
            bool distanceCheck = Vector3.Distance(transform.position, currentDestination) < positionTolerance;

            // Debug logging (commented out to reduce log spam)
            // if (currentState == NPCState.Moving && Time.frameCount % 120 == 0) // Log every 2 seconds at 60fps
            // {
            //     float currentDistance = Vector3.Distance(transform.position, currentDestination);
            //     ModLogger.Debug($"BaseJailNPC {gameObject.name}: pathPending={navAgent.pathPending}, remainingDistance={navAgent.remainingDistance:F2}, actualDistance={currentDistance:F2}, tolerance={positionTolerance}, pathComplete={pathComplete}, distanceCheck={distanceCheck}");
            // }

            return pathComplete || distanceCheck;
        }

        public virtual void StopMovement()
        {
            if (navAgent != null && navAgent.enabled)
            {
                navAgent.ResetPath();
                hasReachedDestination = true;
                ChangeState(NPCState.Idle);
            }
        }

        protected virtual void CheckStuckMovement(float currentTime)
        {
            if (currentState != NPCState.Moving)
            {
                lastMovementObservedTime = currentTime;
                lastPosition = transform.position;
                return;
            }

            float distanceMoved = Vector3.Distance(transform.position, lastPosition);

            if (distanceMoved < minMovementDistance)
            {
                if (currentTime - lastMovementObservedTime >= stuckThreshold)
                {
                    // ModLogger.Warn($"NPC {gameObject.name} appears stuck. Attempting to resolve...");
                    NotifyStuck();

                    // Try to resolve by re-setting destination
                    if (navAgent != null && navAgent.enabled)
                    {
                        navAgent.SetDestination(currentDestination);
                    }

                    lastMovementObservedTime = currentTime;
                }
            }
            else
            {
                lastMovementObservedTime = currentTime;
            }

            lastPosition = transform.position;
        }

        #endregion

        #region Notifications

        protected virtual void NotifyStateChanged(NPCState oldState, NPCState newState)
        {
#if MONO
            OnStateChanged?.Invoke(oldState, newState);
#endif
        }

        protected virtual void NotifyDestinationReached(Vector3 destination)
        {
#if MONO
            OnDestinationReached?.Invoke(destination);
#endif
        }

        protected virtual void NotifyStuck()
        {
#if MONO
            OnStuck?.Invoke();
#endif
        }

        protected virtual void NotifyAttacked(Player attacker)
        {
#if MONO
            OnAttacked?.Invoke(attacker);
#endif
        }

        #endregion

        #region Look Controller

        public virtual void LookAt(Vector3 target, float duration = 2f)
        {
            // The native AvatarLookController is not exposed consistently on
            // both runtimes. A planar body rotation is deterministic and
            // makes the dialogue call sites truthful even when that optional
            // controller is absent.
            MelonCoroutines.Start(LookAtTarget(target, duration));
        }

        public virtual void LookAt(Transform target, float duration = 2f)
        {
            if (target != null)
            {
                LookAt(target.position, duration);
            }
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        protected IEnumerator LookAtTarget(Vector3 target, float duration)
        {
            Vector3 direction = target - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
            {
                yield break;
            }

            Quaternion startRotation = transform.rotation;
            Quaternion targetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            float angle = Quaternion.Angle(startRotation, targetRotation);
            if (angle < 1f)
            {
                yield break;
            }

            // Scale the requested duration by angular distance so callers
            // retain a natural hold time without making small corrections
            // visibly sluggish.
            float turnDuration = Mathf.Clamp((angle / 180f) * Mathf.Max(duration, 0.1f), 0.08f, Mathf.Max(duration, 0.1f));
            float elapsed = 0f;
            while (elapsed < turnDuration)
            {
                elapsed += Time.deltaTime;
                float progress = Mathf.SmoothStep(0f, 1f, elapsed / turnDuration);
                transform.rotation = Quaternion.Slerp(startRotation, targetRotation, progress);
                yield return null;
            }

            transform.rotation = targetRotation;
        }

        #endregion

        #region Utility Methods

        public NPCState GetCurrentState() => currentState;
        public float GetStateTime() => Time.time - stateStartTime;
        public bool IsIdle() => currentState == NPCState.Idle;
        public bool IsMoving() => currentState == NPCState.Moving;
        public NavMeshAgent GetNavAgent() => navAgent;

        /// <summary>
        /// Get the AvatarLookController component for proper NPC rotation control
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
        protected Il2CppScheduleOne.AvatarFramework.Animation.AvatarLookController GetAvatarLookController()
        {
            if (npcComponent == null)
            {
                ModLogger.Debug($"BaseJailNPC: npcComponent is null for {gameObject.name}");
                return null;
            }

            var npc = npcComponent as Il2CppScheduleOne.NPCs.NPC;
            if (npc == null)
            {
                ModLogger.Debug($"BaseJailNPC: Failed to cast npcComponent to NPC for {gameObject.name}");
                return null;
            }

            var avatar = npc.Avatar;
            if (avatar == null)
            {
                ModLogger.Debug($"BaseJailNPC: Avatar is null for {gameObject.name}");
                return null;
            }

            var lookController = avatar.LookController;
            if (lookController == null)
            {
                ModLogger.Debug($"BaseJailNPC: LookController is null for {gameObject.name}");
            }
            else
            {
                ModLogger.Debug($"BaseJailNPC: Found AvatarLookController via NPC.Avatar.LookController for {gameObject.name}");
            }

            return lookController;
        }
#else
        protected ScheduleOne.AvatarFramework.Animation.AvatarLookController GetAvatarLookController()
        {
            if (npcComponent == null)
            {
                ModLogger.Debug($"BaseJailNPC: npcComponent is null for {gameObject.name}");
                return null;
            }

            var npc = npcComponent as ScheduleOne.NPCs.NPC;
            if (npc == null)
            {
                ModLogger.Debug($"BaseJailNPC: Failed to cast npcComponent to NPC for {gameObject.name}");
                return null;
            }

            var avatar = npc.Avatar;
            if (avatar == null)
            {
                ModLogger.Debug($"BaseJailNPC: Avatar is null for {gameObject.name}");
                return null;
            }

            var lookController = avatar.LookController;
            if (lookController == null)
            {
                ModLogger.Debug($"BaseJailNPC: LookController is null for {gameObject.name}");
            }
            else
            {
                ModLogger.Debug($"BaseJailNPC: Found AvatarLookController via NPC.Avatar.LookController for {gameObject.name}");
            }

            return lookController;
        }
#endif

        public virtual void SetEnabled(bool enabled)
        {
            this.enabled = enabled;
            if (navAgent != null)
            {
                navAgent.enabled = enabled;
            }
        }

        #endregion

        #region Messaging (for inherited classes)

        public virtual bool TrySendNPCMessage(string message, float duration = 5f)
        {
            try
            {
                if (npcComponent != null)
                {
                    // Cast to appropriate NPC type based on build configuration
#if !MONO
                    var npc = npcComponent as Il2CppScheduleOne.NPCs.NPC;
#else
                    var npc = npcComponent as ScheduleOne.NPCs.NPC;
#endif
                    if (npc != null)
                    {
                        // Use the game's native world space dialogue system
                        npc.SendWorldSpaceDialogue(message, duration);
                        ModLogger.Debug($"NPC {gameObject.name} sent message: {message}");
                        return true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Message system not available for {gameObject.name}: {ex.Message}");
            }

            // Fallback to logging the message
            ModLogger.Info($"NPC {gameObject.name}: {message}");
            return false;
        }

        public virtual bool TrySendNPCTextMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            try
            {
                if (npcComponent == null)
                {
                    return false;
                }

#if !MONO
                var npc = npcComponent as Il2CppScheduleOne.NPCs.NPC;
#else
                var npc = npcComponent as ScheduleOne.NPCs.NPC;
#endif
                if (npc == null)
                {
                    return false;
                }

                if (npc.MSGConversation == null)
                {
                    npc.CreateMessageConversation();
                }

                npc.SendTextMessage(message);
                ModLogger.Debug($"NPC {gameObject.name} sent text message: {message}");
                return true;
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Text message unavailable for {gameObject.name}: {ex.Message}");
                return false;
            }
        }

        #endregion

        protected virtual void OnDestroy()
        {
            if (lookController != null)
            {
                try
                {
                    //lookController.StopLooking();
                }
                catch { }
            }
        }

        // Debug visualization
        protected virtual void OnDrawGizmos()
        {
            if (!isInitialized) return;

            // Draw current destination
            if (currentState == NPCState.Moving && currentDestination != Vector3.zero)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(currentDestination, Vector3.one * 0.5f);
                Gizmos.DrawLine(transform.position, currentDestination);
            }

            // Draw state indicator
            Gizmos.color = GetStateColor(currentState);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 2f, 0.3f);
        }

        protected virtual Color GetStateColor(NPCState state)
        {
            switch (state)
            {
                case NPCState.Idle: return Color.white;
                case NPCState.Moving: return Color.blue;
                case NPCState.Interacting: return Color.yellow;
                case NPCState.Waiting: return new Color(1f, 0.5f, 0f); // Orange
                case NPCState.Working: return Color.green;
                case NPCState.Error: return Color.red;
                default: return Color.gray;
            }
        }
    }
}
