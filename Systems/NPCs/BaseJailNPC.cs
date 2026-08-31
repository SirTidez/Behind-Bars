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

        /// <summary>
        /// Coarse-grained state used by the shared update dispatcher. Derived role state machines may
        /// maintain a more specific state, but should keep this value synchronized with the base
        /// navigation lifecycle unless they intentionally override the base state transition.
        /// </summary>
        public enum NPCState
        {
            /// <summary>The NPC is not currently navigating or performing a tracked action.</summary>
            Idle,
            /// <summary>The NPC has an active navigation destination.</summary>
            Moving,
            /// <summary>The NPC is performing an interaction owned by the derived behavior.</summary>
            Interacting,
            /// <summary>The NPC is waiting for an external condition before continuing.</summary>
            Waiting,
            /// <summary>The NPC is performing role-specific work.</summary>
            Working,
            /// <summary>Initialization or recovery failed and the base handler is monitoring recovery.</summary>
            Error
        }

        /// <summary>The NavMesh agent used by the base navigation implementation.</summary>
        protected NavMeshAgent navAgent;
        /// <summary>The shared coarse-grained state dispatched by <see cref="NPCUpdateManager"/>.</summary>
        protected NPCState currentState = NPCState.Idle;
        /// <summary>True only after component, avatar, and role initialization has completed successfully.</summary>
        protected bool isInitialized = false;
        /// <summary>Unity-time timestamp at which <see cref="currentState"/> was most recently entered.</summary>
        protected float stateStartTime = 0f;

        // Health and Combat are supplied by the native NPC graph when the prefab path provides them.

        /// <summary>The native avatar reference used by shared presentation helpers.</summary>
#if !MONO
        protected Il2CppScheduleOne.AvatarFramework.Avatar npcAvatar;
        /// <summary>Optional native look controller; not all prepared NPC graphs expose one.</summary>
        protected Il2CppScheduleOne.AvatarFramework.Animation.AvatarLookController lookController;
        /// <summary>The native NPC component used for messages and avatar lookup.</summary>
        protected Il2CppScheduleOne.NPCs.NPC npcComponent;
#else
        protected ScheduleOne.AvatarFramework.Avatar npcAvatar;
        /// <summary>Optional native look controller; not all prepared NPC graphs expose one.</summary>
        protected ScheduleOne.AvatarFramework.Animation.AvatarLookController lookController;
        /// <summary>The native NPC component used for messages and avatar lookup.</summary>
        protected ScheduleOne.NPCs.NPC npcComponent;
#endif
        /// <summary>Whether <see cref="lookController"/> was found during avatar initialization.</summary>
        protected bool lookControllerAvailable = false;

        /// <summary>Destination most recently accepted by <see cref="MoveTo"/>.</summary>
        protected Vector3 currentDestination;
        /// <summary>Tracks the shared completion flag used by navigation and derived behaviors.</summary>
        protected bool hasReachedDestination = true;
        /// <summary>Unity-time timestamp at which the current destination was accepted.</summary>
        protected float lastDestinationTime = 0f;
        /// <summary>World-space completion tolerance used by the base destination test.</summary>
        protected float positionTolerance = 1.5f;
        /// <summary>Unity-time timestamp of the last observed movement progress.</summary>
        protected float lastMovementObservedTime = 0f;
        /// <summary>Position sampled during the previous movement-check tick.</summary>
        protected Vector3 lastPosition;
        /// <summary>Seconds without measurable movement before the base stuck notification fires.</summary>
        protected const float stuckThreshold = 5f;
        /// <summary>Minimum displacement treated as progress during stuck detection, in world units.</summary>
        protected const float minMovementDistance = 0.1f;

        // State Management
#if MONO
        /// <summary>Raised after the base state has changed; this event is available only in MONO builds.</summary>
        public System.Action<NPCState, NPCState> OnStateChanged;
        /// <summary>Raised when base navigation confirms the current destination; MONO-only.</summary>
        public System.Action<Vector3> OnDestinationReached;
        /// <summary>Raised when the movement watchdog reissues a stalled destination; MONO-only.</summary>
        public System.Action OnStuck;
        /// <summary>Raised when the base attack hook receives a player attacker; MONO-only.</summary>
        public System.Action<Player> OnAttacked;
#endif

        /// <summary>
        /// Resolves shared component references before Unity's start phase and installs the direct state
        /// dispatch path. The method intentionally does not mark the NPC initialized; that happens only
        /// after validation and role initialization in <see cref="Start"/>.
        /// </summary>
        protected virtual void Awake()
        {
            InitializeComponents();
            InitializeStateHandlers();
        }

        /// <summary>
        /// Validates the base graph, initializes the avatar and role behavior, and then publishes the
        /// initialized flag used by throttled update callbacks. Failure disables this component so the
        /// update manager cannot drive a partially initialized NPC.
        /// </summary>
        protected virtual void Start()
        {
            if (!ValidateComponents())
            {
                ModLogger.Error($"BaseJailNPC: Failed to initialize components on {gameObject.name}");
                enabled = false;
                return;
            }

            InitializeAvatar();
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

        /// <summary>
        /// Removes this instance from the throttled update manager. This is paired with
        /// <see cref="OnEnable"/> because Unity may disable a pooled NPC without destroying it.
        /// </summary>
        protected virtual void OnDisable()
        {
            if (NPCUpdateManager.Instance != null)
            {
                NPCUpdateManager.Instance.UnregisterNPC(this);
            }
        }

        /// <summary>
        /// Entry point used by <see cref="NPCUpdateManager"/> for the throttled state tick. It is a
        /// no-op until initialization has completed, which protects pooled/partially constructed NPCs.
        /// </summary>
        internal void DispatchStateUpdate(float currentTime)
        {
            if (!isInitialized) return;
            OnStateUpdateTick(currentTime);
        }

        /// <summary>
        /// Entry point used by <see cref="NPCUpdateManager"/> for the throttled movement watchdog tick.
        /// </summary>
        internal void DispatchMovementCheck(float currentTime)
        {
            if (!isInitialized) return;
            OnMovementCheckTick(currentTime);
        }

        /// <summary>Runs the base state handler for a throttled state tick; roles may override this hook.</summary>
        protected virtual void OnStateUpdateTick(float currentTime)
        {
            UpdateState();
        }

        /// <summary>Runs stuck detection for a throttled movement tick; roles may override this hook.</summary>
        protected virtual void OnMovementCheckTick(float currentTime)
        {
            CheckStuckMovement(currentTime);
        }

        #region Initialization

        /// <summary>Finds the shared NavMesh/native NPC components and seeds movement diagnostics.</summary>
        protected virtual void InitializeComponents()
        {
            navAgent = GetComponent<NavMeshAgent>();
            npcComponent = GetComponent<NPC>();
            lastPosition = transform.position;
        }

        /// <summary>
        /// Verifies the minimum component surface required by the base movement implementation. Native
        /// role-specific graph validation belongs to the prefab lifecycle, not this lightweight check.
        /// </summary>
        protected virtual bool ValidateComponents()
        {
            if (navAgent == null)
            {
                ModLogger.Error($"BaseJailNPC: NavMeshAgent not found on {gameObject.name}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Resolves the avatar and optional look controller. Missing optional presentation components do
        /// not fail initialization because the deterministic planar look fallback remains available.
        /// </summary>
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

        /// <summary>
        /// Hook for derived state setup. It is intentionally empty: the base class uses a direct switch
        /// instead of delegate/reflection registration so the injected IL2CPP surface stays predictable.
        /// </summary>
        protected virtual void InitializeStateHandlers()
        {
            // Intentionally empty. State dispatch uses a direct switch for IL2CPP safety.
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

        /// <summary>Hook for role-specific initialization after shared components and avatar setup.</summary>
        protected virtual void InitializeNPC()
        {
            // Derived behaviors override this to provide role-specific initialization.
        }

        #endregion

        #region State Management

        /// <summary>
        /// Transitions the shared state in a strict exit/assign/enter/notify order. Re-entering the same
        /// state is ignored, and derived state machines that override this method must preserve that
        /// invariant unless they also replace all base navigation consumers.
        /// </summary>
        /// <param name="newState">The coarse-grained state to enter.</param>
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

        /// <summary>Extension hook invoked after <see cref="currentState"/> is assigned.</summary>
        /// <param name="state">The state just entered.</param>
        protected virtual void EnterState(NPCState state)
        {
            // Override in derived classes for specific enter behavior
        }

        /// <summary>Extension hook invoked before the shared state is replaced.</summary>
        /// <param name="state">The state being exited.</param>
        protected virtual void ExitState(NPCState state)
        {
            // Override in derived classes for specific exit behavior
        }

        /// <summary>
        /// Dispatches the current coarse state to its virtual handler. Role-specific state machines may
        /// override the update hook, but should preserve the distinction between movement completion and
        /// their own workflow transitions.
        /// </summary>
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

        /// <summary>Default idle handler; derived roles own any idle activity.</summary>
        protected virtual void HandleIdleState()
        {
            // Default idle behavior - do nothing
        }

        /// <summary>
        /// Completes base navigation when either the agent's complete path or direct world distance falls
        /// within tolerance, then notifies listeners before returning to <see cref="NPCState.Idle"/>.
        /// </summary>
        protected virtual void HandleMovingState()
        {
            if (HasReachedDestination())
            {
                // ModLogger.Info($"BaseJailNPC: Destination reached, firing OnDestinationReached event for {gameObject.name}");
                NotifyDestinationReached(currentDestination);
                ChangeState(NPCState.Idle);
            }
        }

        /// <summary>Default interaction handler; derived roles own interaction completion.</summary>
        protected virtual void HandleInteractingState()
        {
            // Override in derived classes
        }

        /// <summary>Default waiting handler; derived roles own the condition that releases the wait.</summary>
        protected virtual void HandleWaitingState()
        {
            // Override in derived classes
        }

        /// <summary>Default work handler; derived roles own work progress and completion.</summary>
        protected virtual void HandleWorkingState()
        {
            // Override in derived classes
        }

        /// <summary>
        /// Logs the error state and attempts a coarse recovery to idle after five Unity seconds. This is
        /// only a base recovery policy; role-specific failures should make their own cleanup explicit.
        /// </summary>
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

        /// <summary>
        /// Accepts a NavMesh destination, resets movement diagnostics, and enters the shared moving state.
        /// The request is rejected when the agent is unavailable/off-mesh or when SetDestination fails.
        /// A positive tolerance replaces the previous completion tolerance; the default preserves it.
        /// </summary>
        /// <param name="destination">World-space point to navigate toward.</param>
        /// <param name="tolerance">Optional positive completion tolerance in world units.</param>
        /// <returns>True when the native agent accepted the destination.</returns>
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

        /// <summary>
        /// Reports completion only for a valid on-mesh agent with no incomplete path. Completion is true
        /// when either remaining path distance or direct world distance is inside the configured tolerance.
        /// </summary>
        /// <returns>True when navigation can be treated as complete.</returns>
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

        /// <summary>
        /// Resets the native path, marks the shared movement flag complete, and returns the base state to
        /// idle. Derived workflows that own a separate movement state must coordinate their own state too.
        /// </summary>
        public virtual void StopMovement()
        {
            if (navAgent != null && navAgent.enabled)
            {
                navAgent.ResetPath();
                hasReachedDestination = true;
                ChangeState(NPCState.Idle);
            }
        }

        /// <summary>
        /// Watches displacement while moving and, after <see cref="stuckThreshold"/> seconds without
        /// progress, notifies MONO listeners and reissues the current destination as a best-effort recovery.
        /// </summary>
        /// <param name="currentTime">Unity-time value supplied by the update manager.</param>
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

        /// <summary>Raises the MONO-only state event; there is no delegate surface on IL2CPP.</summary>
        protected virtual void NotifyStateChanged(NPCState oldState, NPCState newState)
        {
#if MONO
            OnStateChanged?.Invoke(oldState, newState);
#endif
        }

        /// <summary>Raises the MONO-only destination event after base completion is confirmed.</summary>
        protected virtual void NotifyDestinationReached(Vector3 destination)
        {
#if MONO
            OnDestinationReached?.Invoke(destination);
#endif
        }

        /// <summary>Raises the MONO-only stuck event before the base retry is attempted.</summary>
        protected virtual void NotifyStuck()
        {
#if MONO
            OnStuck?.Invoke();
#endif
        }

        /// <summary>Raises the MONO-only attack event after the base attack hook receives a player.</summary>
        protected virtual void NotifyAttacked(Player attacker)
        {
#if MONO
            OnAttacked?.Invoke(attacker);
#endif
        }

        #endregion

        #region Look Controller

        /// <summary>
        /// Starts deterministic planar rotation toward a world-space target. The native look controller is
        /// optional, so this path deliberately uses the protected coroutine on both runtimes.
        /// </summary>
        /// <param name="target">World-space point to face.</param>
        /// <param name="duration">Maximum requested turn duration in seconds.</param>
        public virtual void LookAt(Vector3 target, float duration = 2f)
        {
            // The native AvatarLookController is not exposed consistently on
            // both runtimes. A planar body rotation is deterministic and
            // makes the dialogue call sites truthful even when that optional
            // controller is absent.
            MelonCoroutines.Start(LookAtTarget(target, duration));
        }

        /// <summary>Faces the supplied transform when it is non-null.</summary>
        /// <param name="target">Transform whose position should be faced.</param>
        /// <param name="duration">Maximum requested turn duration in seconds.</param>
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
        /// <summary>
        /// Smoothly rotates on the horizontal plane. The effective duration scales with angular distance,
        /// is clamped to avoid an imperceptible snap, and always writes the exact final rotation.
        /// </summary>
        /// <param name="target">World-space point to face.</param>
        /// <param name="duration">Requested maximum turn duration in seconds.</param>
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

        /// <summary>Returns the shared coarse-grained state.</summary>
        public NPCState GetCurrentState() => currentState;
        /// <summary>Returns elapsed Unity time since the shared state was entered.</summary>
        public float GetStateTime() => Time.time - stateStartTime;
        /// <summary>Returns whether the shared state is idle.</summary>
        public bool IsIdle() => currentState == NPCState.Idle;
        /// <summary>Returns whether the shared state is moving.</summary>
        public bool IsMoving() => currentState == NPCState.Moving;
        /// <summary>Returns the NavMesh agent resolved during initialization, when present.</summary>
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

        /// <summary>
        /// Enables or disables both this behavior and its NavMesh agent. It does not unregister directly;
        /// Unity lifecycle callbacks perform update-manager registration cleanup.
        /// </summary>
        /// <param name="enabled">Whether this behavior and its agent should run.</param>
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

        /// <summary>
        /// Sends native world-space dialogue when the NPC graph is available, otherwise logs the message
        /// and reports failure. This fallback is diagnostic only and does not substitute for native UI.
        /// </summary>
        /// <param name="message">World-space dialogue text.</param>
        /// <param name="duration">Native dialogue display duration in seconds.</param>
        /// <returns>True when the native NPC accepted the message.</returns>
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

        /// <summary>
        /// Sends a native text conversation message, creating the native conversation surface when needed.
        /// Invalid or unavailable native state returns false without using the diagnostic world-space fallback.
        /// </summary>
        /// <param name="message">Non-empty text message.</param>
        /// <returns>True when the native NPC accepted the message.</returns>
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

        /// <summary>
        /// Releases optional look-controller ownership during destruction. Derived classes must still stop
        /// their own global coroutines and call this base cleanup when they override the lifecycle method.
        /// </summary>
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

        /// <summary>Draws editor-only destination and coarse-state gizmos for initialized NPCs.</summary>
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
