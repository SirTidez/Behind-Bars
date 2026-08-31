using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Players;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Crimes;
using Behind_Bars.Systems.Dialogue;
using Behind_Bars.Systems;
using Behind_Bars.Systems.Parole;
using BBHelpers = Behind_Bars.Helpers.Helpers;
#if !MONO
using ActiveReleaseOfficerBehavior = Behind_Bars.Systems.NPCs.ReleaseOfficerBehavior;
#else
using ActiveReleaseOfficerBehavior = Behind_Bars.Systems.NPCs.ReleaseOfficerBehavior;
#endif

#if !MONO
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.Law;
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Centralized manager for all prisoner release processes
    /// Coordinates guard escort, inventory return, and player exit
    /// </summary>
    public class ReleaseManager : MonoBehaviour
    {
#if !MONO
        public ReleaseManager(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Singleton Pattern

        private static ReleaseManager _instance;
        private static bool _isManagedBySystemManager;

        /// <summary>
        /// Returns true when a release manager has already been registered.
        /// </summary>
        public static bool HasRegisteredInstance => _instance != null;

        /// <summary>
        /// Registers an existing release manager instance as the active singleton.
        /// This does not create a new instance.
        /// </summary>
        public static ReleaseManager RegisterInstance(ReleaseManager instance, bool managedBySystemManager = false)
        {
            if (instance == null)
            {
                return null;
            }

            if (_instance != null && _instance != instance)
            {
                ModLogger.Debug("[ReleaseManager] Destroying duplicate instance during registration");
                Destroy(instance.gameObject);
                return _instance;
            }

            _instance = instance;
            _isManagedBySystemManager = managedBySystemManager;
            return _instance;
        }

        /// <summary>
        /// Ensures a release manager exists for the current runtime.
        /// Prefers an already registered instance, otherwise creates a managed one.
        /// </summary>
        public static ReleaseManager BootstrapManagedInstance()
        {
            if (TryGetRegisteredInstance(out var existing))
            {
                return existing;
            }

            ModLogger.Debug("[ReleaseManager] Bootstrapping managed instance");
            var go = new GameObject("ReleaseManager");
            go.SetActive(true);

            var manager = BBHelpers.AddComponentSafe<ReleaseManager>(go);
            if (manager == null)
            {
                Destroy(go);
                ModLogger.Error("[ReleaseManager] Failed to bootstrap managed instance");
                return null;
            }

            DontDestroyOnLoad(go);
            return RegisterInstance(manager, true);
        }

        /// <summary>
        /// Returns the registered instance if present, or tries to adopt an existing scene instance.
        /// </summary>
        public static bool TryGetRegisteredInstance(out ReleaseManager instance)
        {
            if (_instance != null)
            {
                instance = _instance;
                return true;
            }

            var existing = BBHelpers.FindObjectOfTypeSafe<ReleaseManager>();
            if (existing != null)
            {
                instance = RegisterInstance(existing, false);
                return instance != null;
            }

            instance = null;
            return false;
        }

        /// <summary>
        /// Destroys the system-managed instance if one exists.
        /// </summary>
        public static bool ShutdownManagedInstance()
        {
            if (_instance == null || !_isManagedBySystemManager)
            {
                return false;
            }

            var instance = _instance;
            instance?.CleanupAllActiveReleaseCallbacks();
            _instance = null;
            _isManagedBySystemManager = false;

            if (instance != null)
            {
                Destroy(instance.gameObject);
            }

            return true;
        }

        /// <summary>
        /// Gets the registered release manager, adopting or bootstrapping a compatible instance
        /// when no manager has been registered yet.
        /// </summary>
        public static ReleaseManager Instance
        {
            get
            {
                if (TryGetRegisteredInstance(out var registered))
                {
                    return registered;
                }

                ModLogger.Warn("[ReleaseManager] Instance requested before bootstrap; creating compatibility fallback");
                return BootstrapManagedInstance();
            }
        }

        #endregion

        #region Release Types

        /// <summary>
        /// Release pathways supported by the manager.  The type controls summary/bail handling;
        /// all normal pathways still pass through the officer escort state machine.
        /// </summary>
        public enum ReleaseType
        {
            TimeServed,
            BailPayment,
            CourtOrder,
            Emergency
        }

        /// <summary>
        /// Coarse manager status mirrored from the release-officer/canonical scanner callbacks.
        /// </summary>
        public enum ReleaseStatus
        {
            NotStarted,
            GuardDispatched,
            EscortingToStorage,
            InventoryProcessing,
            EscortingToExit,
            Completed,
            Failed
        }

        #endregion

        #region Release Data

        /// <summary>
        /// Volatile state for one player's release attempt.  The request is keyed by the resolved
        /// runtime player key and is removed from <c>activeReleases</c> on completion or failure.
        /// Mono callback delegates are non-serialized; IL2CPP uses the internal notification bridge
        /// instead.
        /// </summary>
        [System.Serializable]
        public class ReleaseRequest
        {
            // Identity and lifecycle state used by the active-release dictionary and queue.
            public string playerKey;
            public Player player;
            public ReleaseType releaseType;
            public ReleaseStatus status;
            public DateTime releaseTime;

            // Release summary and inventory handoff data captured when the request is created.
            public float bailAmount;
            public string releaseReason;
            public Vector3 exitPosition;
            public bool inventoryProcessed;
            public List<string> itemsToReturn = new List<string>();
            public ActiveReleaseOfficerBehavior assignedOfficer;
#if MONO
            // Mono-only event delegates are kept on the request so they can be detached by identity.
            [System.NonSerialized] public System.Action<Player> escortCompletedCallback;
            [System.NonSerialized] public System.Action<Player, string> escortFailedCallback;
            [System.NonSerialized] public System.Action<Player, ActiveReleaseOfficerBehavior.ReleaseState> statusUpdateCallback;
#endif

            /// <summary>
            /// Create a not-started request and derive its stable runtime key from the player.
            /// </summary>
            public ReleaseRequest(Player player, ReleaseType type, string reason = "")
            {
                this.player = player;
                this.playerKey = GetPlayerRuntimeKey(player);
                this.releaseType = type;
                this.releaseReason = reason;
                this.releaseTime = DateTime.Now;
                this.status = ReleaseStatus.NotStarted;
                this.inventoryProcessed = false;
            }
        }

        #endregion

        #region Fields

        // Authored exit reference and watchdog duration.  The exit transform is currently resolved
        // to fixed coordinates by GetPlayerExitPosition; releaseProcessTimeout is measured in real
        // Unity seconds by the release-process watchdog.
        public Transform prisonExitPoint; // Set in inspector or found automatically
        public float releaseProcessTimeout = 300f; // 5 minutes max
        private float _nextQueueProcessTime;

        // Active requests are keyed by playerKey. CompleteRelease verifies and removes the matching
        // object before invoking completion callbacks; FailRelease removes during its cleanup path,
        // so callers must avoid sending duplicate failure notifications.
        private Dictionary<string, ReleaseRequest> activeReleases = new Dictionary<string, ReleaseRequest>();

        // Requests without an officer wait here. ProcessQueuedReleases starts at most one eligible
        // request per invocation and requeues it when officers remain unavailable.
        private Queue<ReleaseRequest> releaseQueue = new Queue<ReleaseRequest>();
        private readonly List<Coroutine> sceneCoroutineHandles = new List<Coroutine>();

        // Snapshot of officers supplied by the NPC manager; availability is refreshed before each
        // assignment attempt rather than being a permanently authoritative registry.
        private List<ActiveReleaseOfficerBehavior> availableOfficers = new List<ActiveReleaseOfficerBehavior>();


        private const float PostReleaseOfficerTeleportDistance = 2f;
        private const float PostReleaseImmediateArrestDistance = 8f;
        private const float PostReleaseWarningDurationSeconds = 10f;
        private const float PostReleaseOfficerSideDistance = 1.25f;
        private const float PostReleaseOfficerForwardOffset = 0.4f;
        private const string PostReleaseDialogueContainerName = "Parole_PostRelease_Compliance";
        private const string PostReleaseFinalChoiceLabel = "confirm_conditions";

        #endregion

        #region Events

        // Keep Mono's public integration surface, but do not export managed delegate fields from
        // the IL2CPP-injected release manager type.  Each event fires only after the corresponding
        // request transition has been accepted by this manager.
#if MONO
        public static System.Action<Player, ReleaseType> OnReleaseStarted;
        public static System.Action<Player, ReleaseType> OnReleaseCompleted;
        public static System.Action<Player, string> OnReleaseFailed;
#else
        internal static System.Action<Player, ReleaseType> OnReleaseStarted;
        internal static System.Action<Player, ReleaseType> OnReleaseCompleted;
        internal static System.Action<Player, string> OnReleaseFailed;
#endif

        #endregion

        #region Initialization

        void Awake()
        {
            ModLogger.Debug("[ReleaseManager] Awake() called");
            if (_instance == null)
            {
                RegisterInstance(this, false);
                DontDestroyOnLoad(gameObject);
                ModLogger.Debug("[ReleaseManager] Instance set in Awake()");
            }
            else if (_instance != this)
            {
                ModLogger.Debug("[ReleaseManager] Destroying duplicate instance");
                Destroy(gameObject);
                return;
            }
        }

        private void OnDestroy()
        {
            CancelForSceneExit();
            CleanupAllActiveReleaseCallbacks();

            if (_instance == this)
            {
                _instance = null;
                _isManagedBySystemManager = false;
                ModLogger.Debug("[ReleaseManager] Instance cleared in OnDestroy()");
            }
        }

        void Start()
        {
            ModLogger.Debug("[ReleaseManager] Start() called, beginning initialization");
            InitializeExitPoint();
            InitializeOfficers();
            ModLogger.Debug("ReleaseManager initialized");
            ModLogger.Debug("[ReleaseManager] Initialization complete");
        }

        private void InitializeExitPoint()
        {
            if (prisonExitPoint == null)
            {
                // Try to find prison exit point automatically
                var jailController = Core.JailController;
                if (jailController != null)
                {
                    // Prefer the active jail hierarchy. The legacy PrisonExit marker no longer
                    // exists in current builds; ExitTrigger is the scene-owned release boundary.
                    var exitTransform = jailController.transform.Find("PrisonExit") ??
                                        jailController.exitScanner?.exitTrigger ??
                                        jailController.transform.Find("Hallway/ExitScannerStation/ExitTrigger") ??
                                        jailController.transform.Find("ExitScannerStation/ExitTrigger") ??
                                        jailController.exitScanner?.areaRoot;
                    if (exitTransform != null)
                    {
                        prisonExitPoint = exitTransform;
                        ModLogger.Debug($"Found prison exit point at {prisonExitPoint.position} ({prisonExitPoint.name})");
                    }
                    else
                    {
                        ModLogger.Error("No jail exit trigger or exit-scanner anchor was found; release escorts remain unavailable until the jail hierarchy is ready.");
                    }
                }
            }
        }

        private void InitializeOfficers()
        {
            RefreshAvailableOfficersFromRegistry();

            // DON'T create release officers during initialization to avoid interfering with intake guards
            // Release officers will be created on-demand when actually needed

            ModLogger.Debug($"ReleaseManager found {availableOfficers.Count} existing release officers");
        }

        private void RefreshAvailableOfficersFromRegistry()
        {
            availableOfficers.Clear();

            var npcManager = Core.Instance?.NpcManager;
            if (npcManager == null)
            {
                ModLogger.Warn("ReleaseManager: NpcManager not available - cannot refresh release officer registry");
                return;
            }

            var registeredOfficers = npcManager.GetRegisteredReleaseOfficers();
            foreach (var officer in registeredOfficers)
            {
                if (officer != null && officer.IsAvailable())
                {
                    availableOfficers.Add(officer);
                }
            }
        }

        private void CreateReleaseOfficerFromGuardSpawn()
        {
            var jailController = Core.JailController;
            if (jailController?.booking?.guardSpawns != null && jailController.booking.guardSpawns.Count > 1)
            {
                var guardSpawn = jailController.booking.guardSpawns[1]; // Use second guard spawn
                ModLogger.Info($"Looking for existing guard at booking spawn [1]: {guardSpawn.position}");

                // First, look for any existing guard near guardSpawn[1] (Booking1 assignment)
                var allGuards = BBHelpers.FindObjectsOfTypeSafe<PrisonGuard>();
                PrisonGuard booking1Guard = null;
                GameObject releaseOfficerHost = null;

                foreach (var guard in allGuards)
                {
                    // Check if this guard has Booking1 assignment
                    if (guard.GetAssignment() == GuardBehavior.GuardAssignment.Booking1)
                    {
                        booking1Guard = guard;
                        ModLogger.Info($"Found existing Booking1 guard: {guard.name}");
                        break;
                    }
                }

                if (booking1Guard != null)
                {
                    releaseOfficerHost = booking1Guard.gameObject;
                }

                if (releaseOfficerHost == null)
                {
                    var allNpcs = BBHelpers.FindObjectsOfTypeSafe<NPC>();
                    NPC bookingNpc = null;

                    foreach (var npc in allNpcs)
                    {
                        if (npc == null || npc.gameObject == null)
                            continue;

                        string npcName = npc.gameObject.name;
                        if (!npcName.StartsWith("PrisonGuard_", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (npcName.Contains("Booking1", StringComparison.OrdinalIgnoreCase))
                        {
                            bookingNpc = npc;
                            break;
                        }

                    }

                    if (bookingNpc != null)
                    {
                        releaseOfficerHost = bookingNpc.gameObject;
                        ModLogger.Warn($"Using NPC fallback host for release escort: {releaseOfficerHost.name}");
                    }
                }

                if (releaseOfficerHost != null)
                {
                    // Use the existing guard - just add ReleaseOfficerBehavior component
                    var existingReleaseOfficer = BBHelpers.GetComponentSafe<ActiveReleaseOfficerBehavior>(releaseOfficerHost);
                    if (existingReleaseOfficer == null)
                    {
                        // Add ReleaseOfficerBehavior to existing guard
                        var releaseOfficer = BBHelpers.AddComponentSafe<ActiveReleaseOfficerBehavior>(releaseOfficerHost);
                        if (releaseOfficer != null)
                        {
                            availableOfficers.Add(releaseOfficer);
                            ModLogger.Debug($"✅ Added release officer behavior to guard: {releaseOfficer.GetBadgeNumber()}");
                        }
                    }
                    else
                    {
                        // Already has release officer behavior
                        if (!availableOfficers.Contains(existingReleaseOfficer))
                        {
                            availableOfficers.Add(existingReleaseOfficer);
                        }
                        ModLogger.Debug($"✅ Using existing release officer: {existingReleaseOfficer.GetBadgeNumber()}");
                    }
                }
                else
                {
                    ModLogger.Warn("No valid release officer host guard found - release system will not work properly");
                }
            }
            else
            {
                ModLogger.Error("Guard spawn [1] not available for release officer");
            }
        }

        #endregion

        #region Public Release Methods

        /// <summary>
        /// Start or queue a release process for a player.  Duplicate active/queued requests are
        /// treated as already accepted, while stale requests may be failed and cleaned up before a
        /// new request is assigned.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public bool InitiateRelease(Player player, ReleaseType releaseType, float bailAmount = 0f, string reason = "")
        {
            if (player == null)
            {
                ModLogger.Error("Cannot initiate release for null player");
                return false;
            }

            string playerKey = GetPlayerRuntimeKey(player);

            if (activeReleases.ContainsKey(playerKey))
            {
                var existingRequest = activeReleases[playerKey];
                ModLogger.Warn($"Release already in progress for {player.name} with status {existingRequest.status}");

                double releaseAgeSeconds = (DateTime.Now - existingRequest.releaseTime).TotalSeconds;
                if (releaseAgeSeconds < 20)
                {
                    ModLogger.Debug($"Duplicate release request ignored for {player.name} (age: {releaseAgeSeconds:F1}s)");
                    return true;
                }

                // If the existing release is stuck (older than 1 minute) or officer is missing, force clean it up
                bool isStuck = (DateTime.Now - existingRequest.releaseTime).TotalMinutes > 1;
                bool officerMissing = existingRequest.assignedOfficer == null;
                bool officerIdle = existingRequest.assignedOfficer?.GetCurrentState() == ActiveReleaseOfficerBehavior.ReleaseState.Idle;

                if (isStuck || officerMissing || officerIdle)
                {
                    string cleanupReason = isStuck ? "timeout" : officerMissing ? "missing officer" : "officer idle";
                    if (cleanupReason != "officer idle")
                        ModLogger.Debug($"Forcing cleanup of stuck release for {player.name} - Reason: {cleanupReason}, Age: {(DateTime.Now - existingRequest.releaseTime).TotalMinutes:F1} minutes, Officer: {existingRequest.assignedOfficer?.GetBadgeNumber() ?? "none"}");
                    FailRelease(existingRequest, $"Stuck release cleanup: {cleanupReason}");
                }
                else
                {
                    return true;
                }
            }

            // Create release request
            var releaseRequest = new ReleaseRequest(player, releaseType, reason)
            {
                bailAmount = bailAmount,
                exitPosition = GetPlayerExitPosition(player)
            };

            // Get items to return (legal items only)
            releaseRequest.itemsToReturn = GetLegalItemsForReturn(player);

            ModLogger.Debug($"Initiating {releaseType} release for {player.name}");

            // Try to assign an officer immediately
            if (TryAssignOfficer(releaseRequest))
            {
                activeReleases[playerKey] = releaseRequest;
                StartReleaseProcess(releaseRequest);
                OnReleaseStarted?.Invoke(player, releaseType);
                return true;
            }

            if (HasQueuedRelease(playerKey))
            {
                ModLogger.Debug($"Release already queued for {player.name}; duplicate request ignored");
                return true;
            }

            // Queue the release if no officers available
            releaseQueue.Enqueue(releaseRequest);
            ModLogger.Debug($"Release for {player.name} queued - no officers available");

            // Notify player they're being processed
            Core.ResolveUIManager().ShowNotification(
                    "Release processing - please wait",
                    NotificationType.Instruction
                );

            return true;
        }

        /// <summary>
        /// Complete an emergency release by teleporting directly to the fixed exit and restoring
        /// player release systems without the normal officer escort sequence.
        /// </summary>
        public void EmergencyRelease(Player player)
        {
            ModLogger.Debug($"Emergency release for {player.name}");

            // Skip escort process and teleport directly
            var exitPosition = GetPlayerExitPosition(player);
            var exitRotation = GetPlayerExitRotation();
            player.transform.position = exitPosition;
            player.transform.rotation = Quaternion.Euler(exitRotation);

            // Restore inventory and clear jail status
            CompletePlayerRelease(player, ReleaseType.Emergency);

            Core.ResolveUIManager().ShowNotification(
                    "Emergency release completed",
                    NotificationType.Progress
                );
        }

        #endregion

        #region Release Process Management

        /// <summary>
        /// Refresh the officer pool and attach the request callbacks to the first available officer.
        /// If the pool is empty, this may create one officer on demand; a false result leaves the
        /// request unassigned for queueing.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool TryAssignOfficer(ReleaseRequest request)
        {
            RefreshAvailableOfficersFromRegistry();
            ModLogger.Debug($"TryAssignOfficer: Looking for available officers - total officers: {availableOfficers.Count}");

            foreach (var officer in availableOfficers)
            {
                ModLogger.Debug($"TryAssignOfficer: Checking officer {officer?.GetBadgeNumber()} - Available: {officer?.IsAvailable()}");

                if (officer != null && officer.IsAvailable())
                {
                    AttachOfficerCallbacks(request, officer);
                    officer.SetAvailable(false);

                    ModLogger.Debug($"SUCCESS: Assigned officer {officer.GetBadgeNumber()} to release {request.player.name}");
                    return true;
                }
            }

            ModLogger.Warn($"TryAssignOfficer: No available officers found in pool of {availableOfficers.Count} officers");

            // If no officer available, try to create one NOW (on-demand)
            if (availableOfficers.Count == 0)
            {
                ModLogger.Debug("No release officers available - creating one on-demand");
                CreateReleaseOfficerFromGuardSpawn();
                RefreshAvailableOfficersFromRegistry();

                // Try again with newly created officer
                foreach (var officer in availableOfficers)
                {
                    if (officer != null && officer.IsAvailable())
                    {
                        AttachOfficerCallbacks(request, officer);
                        officer.SetAvailable(false);

                        ModLogger.Debug($"Assigned newly created officer {officer.GetBadgeNumber()} to release {request.player.name}");
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Mark a request as dispatched, prepare the supervising officer, and start the assigned
        /// release officer's coroutine.  A missing assignment fails the request immediately.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void StartReleaseProcess(ReleaseRequest request)
        {
            ModLogger.Debug($"Starting release process for {request.player.name}");
            request.status = ReleaseStatus.GuardDispatched;

            // Start the supervising officer's walk from the courthouse now, in parallel
            // with the release officer's trip to the cell. The officer waits at the
            // bottom of the police-station steps until the player dismisses the
            // conditions summary. Keeping them just ahead of the spawn prevents the
            // player from having to wait for a late approach after release.
            // This is deliberately part of release initiation (bail and time served), not
            // exit-scanner completion. Ensure the dynamic manager exists first, because
            // it owns the supervisor rather than the prison NPC manager.
            Core.Instance?.NpcManager?.EnsureDynamicParoleOfficerManager();
            Core.ResolveDynamicParoleOfficerManager()?.PrepareSupervisingOfficerForRelease(
                request.player,
                GetSupervisingOfficerReleaseMeetingPoint(request.exitPosition));

            // Start the escort sequence
            if (request.assignedOfficer != null)
            {
                StartSceneCoroutine(ReleaseProcessCoroutine(request));
            }
            else
            {
                FailRelease(request, "No officer assigned");
            }
        }

        /// <summary>
        /// Start the canonical release-officer state machine and wait for completion or failure.
        /// On IL2CPP this coroutine is only a lifetime/timeout watchdog because state transitions
        /// arrive through the notification bridge; Mono waits on the same request status directly.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ReleaseProcessCoroutine(ReleaseRequest request)
        {
            // NEW STATE MACHINE APPROACH: Simply start the Release Officer and let it handle the entire process
            ModLogger.Debug($"Starting Release Officer state machine for {request.player.name}");
            request.status = ReleaseStatus.GuardDispatched;

            // Start the Release Officer's state machine - it will handle everything
            request.assignedOfficer.StartReleaseEscort(request.player);

#if !MONO
            float il2cppStartTime = Time.time;
            while (request.status != ReleaseStatus.Completed && request.status != ReleaseStatus.Failed)
            {
                if (request.assignedOfficer == null)
                {
                    FailRelease(request, "Assigned release officer became null");
                    yield break;
                }

                if (Time.time - il2cppStartTime > releaseProcessTimeout)
                {
                    FailRelease(request, "Release process timeout");
                    yield break;
                }

                // IL2CPP state changes are delivered directly by the canonical
                // release officer. This loop is only a lifetime/timeout watchdog;
                // it deliberately does not inspect officer state on a timer.
                yield return null;
            }

            yield break;
#else

            // Wait for the Release Officer to complete the entire process
            // The Release Officer will call OnEscortCompleted when done
            float startTime = Time.time;
            while (request.status != ReleaseStatus.Completed && request.status != ReleaseStatus.Failed)
            {
                // Check timeout
                if (Time.time - startTime > releaseProcessTimeout)
                {
                    FailRelease(request, "Release process timeout");
                    yield break;
                }

                yield return new WaitForSeconds(1f);
            }

            ModLogger.Debug($"Release process coroutine completed with status: {request.status}");
#endif
        }

        /// <summary>
        /// Wait for inventory processing to report completion, then force the flag after the fixed
        /// timeout so the release flow cannot remain blocked indefinitely.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForInventoryProcessing(ReleaseRequest request)
        {
            float timeout = 60f; // 1 minute timeout for inventory processing
            float startTime = Time.time;

            while (!request.inventoryProcessed && Time.time - startTime < timeout)
            {
                yield return new WaitForSeconds(1f);
            }

            if (!request.inventoryProcessed)
            {
                ModLogger.Warn($"Inventory processing timeout for {request.player.name}");
                request.inventoryProcessed = true; // Force completion
            }
        }

        /// <summary>
        /// Wait until the player moves beyond the storage exit distance, or until the fixed timeout
        /// expires.  The assigned officer remains in a storage-waiting state during this window,
        /// and timeout proceeds rather than failing the release.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForPlayerToExitStorage(ReleaseRequest request)
        {
            float timeout = 60f; // 1 minute timeout for player to exit storage
            float startTime = Time.time;
            Vector3 storageLocation = GetStorageLocationForWaiting();
            const float exitDistance = 8f; // Player must be 8+ meters from storage to be considered "exited"

            ModLogger.Debug($"Waiting for {request.player.name} to move away from storage area (>{exitDistance}m from {storageLocation})");

            if (request.assignedOfficer != null)
            {
                // Tell the officer to stay at storage and wait
                request.assignedOfficer.ChangeReleaseState(ActiveReleaseOfficerBehavior.ReleaseState.WaitingAtStorage);
                ModLogger.Debug($"Officer {request.assignedOfficer.GetBadgeNumber()} waiting at storage for player to exit");
            }

            while (Time.time - startTime < timeout)
            {
                if (request.player == null) break; // Player disconnected or something

                float distanceFromStorage = Vector3.Distance(request.player.transform.position, storageLocation);

                if (distanceFromStorage > exitDistance)
                {
                    ModLogger.Info($"{request.player.name} has exited storage area (distance: {distanceFromStorage:F1}m)");
                    break;
                }

                // Check every 2 seconds
                yield return new WaitForSeconds(2f);
            }

            if (Time.time - startTime >= timeout)
            {
                ModLogger.Warn($"Timeout waiting for {request.player.name} to exit storage - proceeding anyway");
            }

            ModLogger.Info($"Storage exit wait complete for {request.player.name}");
        }

        /// <summary>
        /// Claim an active request for exactly-once completion, then perform teleport, escort cleanup,
        /// player restoration, parole setup, queued-request processing, and the completion event.
        /// Stale or duplicate completion signals are ignored before external side effects run.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void CompleteRelease(ReleaseRequest request)
        {
            if (request == null || request.status == ReleaseStatus.Completed)
            {
                return;
            }

            // Completion can be signalled by both the officer state machine
            // and the exit-scanner callback. Claim the active request before
            // invoking external systems so only the winner performs the
            // teleport, inventory handoff, parole setup, and sentence stop.
            if (!activeReleases.TryGetValue(request.playerKey, out var activeRequest) || !ReferenceEquals(activeRequest, request))
            {
                ModLogger.Debug($"Ignoring stale release-completion callback for {request.playerKey}");
                return;
            }

            ModLogger.Debug($"Completing release for {request.player.name}");
            request.status = ReleaseStatus.Completed;
            activeReleases.Remove(request.playerKey);

            // CRITICAL: Hide officer command notification before teleporting player
            Core.ResolveUIManager().HideOfficerCommand();
            ModLogger.Debug($"Hidden officer command notification for {request.player.name}");

            // CRITICAL: Clear ALL escort registrations for this player to prevent conflicts
            OfficerCoordinator.Instance.UnregisterAllEscortsForPlayer(request.player);

            // Teleport player to freedom
            var exitRotation = GetPlayerExitRotation();
            request.player.transform.position = request.exitPosition;
            request.player.transform.rotation = Quaternion.Euler(exitRotation);

            ReleaseAssignedOfficer(request, true);

            // Complete player release (restore systems, clear flags, START PAROLE TIMER)
            // Parole term timer starts immediately here
            CompletePlayerRelease(request.player, request.releaseType);

            // Show parole conditions UI and wait for acknowledgment (if player will be on parole)
            var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(request.player);
            bool willBeOnParole = rapSheet != null && (rapSheet.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole());

            // CRITICAL: Stop tracking jail time BEFORE calculating release summary
            // This ensures time served is captured correctly at the moment of release
            // Do this for ALL releases, not just parole releases
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null ? jailManager.IsTrackingSentence(request.player) : Core.ResolveJailTimeTracker().IsTracking(request.player))
            {
                if (jailManager != null)
                {
                    jailManager.StopSentenceTracking(request.player);
                }
                else
                {
                    Core.ResolveJailTimeTracker().StopTracking(request.player);
                }
                ModLogger.Debug($"Stopped jail time tracking for {request.player.name} before release summary calculation");
            }

            // CRITICAL: Record release time IMMEDIATELY to start grace period
            // This prevents searches from happening while the release summary UI is visible
            // The grace period will be extended/updated after UI dismissal if needed
            if (willBeOnParole)
            {
                ParoleSearchSystem.Instance.RecordReleaseTime(request.player);
                ModLogger.Debug($"Recorded release time for {request.player.name} - grace period started immediately");
            }

            if (willBeOnParole)
            {
                // Calculate all release summary data (bail, fine, term, LSI, crimes, conditions)
                // Pass release type to determine if bail was paid
                var (bailAmountPaid, fineAmount, termLength, lsiLevel, lsiBreakdown, jailTimeInfo, recentCrimes, generalConditions, specialConditions) = CalculateReleaseSummaryData(request.player, request.releaseType);

                // Freeze player and show UI
                // Grace period already started above - this prevents searches during UI display
                StartSceneCoroutine(WaitForParoleConditionsAcknowledgment(request.player, bailAmountPaid, fineAmount, termLength, lsiLevel, lsiBreakdown, jailTimeInfo, recentCrimes, generalConditions, specialConditions));
            }

            // Process queued releases
            ProcessQueuedReleases();

            // Fire event
            OnReleaseCompleted?.Invoke(request.player, request.releaseType);

            ModLogger.Debug($"Release completed for {request.player.name} - all escorts cleared");
        }

        /// <summary>
        /// Mark a request failed, cancel prepared supervision, release the assigned officer, remove
        /// the active entry, process the queue, and notify listeners/player.  An officer-idle cleanup
        /// is intentionally quiet to avoid surfacing a false failure notification.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void FailRelease(ReleaseRequest request, string reason)
        {
            ModLogger.Error($"Release failed for {request.player.name}: {reason}");
            request.status = ReleaseStatus.Failed;
            DynamicParoleOfficerManager.Instance?.CancelPreparedSupervisingOfficerForRelease(request.player);
            var officerIdle = reason.Contains("officer idle");

            // CRITICAL: Clear ALL escort registrations for this player to prevent conflicts
            OfficerCoordinator.Instance.UnregisterAllEscortsForPlayer(request.player);

            ReleaseAssignedOfficer(request, true);

            // Clean up
            activeReleases.Remove(request.playerKey);

            // Process queued releases
            ProcessQueuedReleases();

            // Fire event
            OnReleaseFailed?.Invoke(request.player, reason);
            // Notify player
            if (!officerIdle)
            {
                Core.ResolveUIManager().ShowNotification(
                    $"Release failed: {reason}",
                    NotificationType.Warning
                );
            }
            
            if (!officerIdle)
                ModLogger.Debug($"Release failed for {request.player.name} - all escorts cleared");
            else
            {
                Core.ResolveUIManager().ShowNotification(
                    "Processing release... Please wait!",
                    NotificationType.Instruction);
            }
        }

        /// <summary>
        /// Discard stale queued requests and attempt to start one eligible request.  If no officer
        /// can be assigned, the dequeued request is restored to the tail and processing stops for
        /// this invocation.
        /// </summary>
        private void ProcessQueuedReleases()
        {
            while (releaseQueue.Count > 0)
            {
                var nextRelease = releaseQueue.Dequeue();
                if (nextRelease == null || nextRelease.player == null ||
                    !Core.ResolveJailTimeTracker().IsInJail(nextRelease.player))
                {
                    ModLogger.Debug($"Discarded stale queued release for {nextRelease?.playerKey ?? "unknown"}");
                    continue;
                }

                if (TryAssignOfficer(nextRelease))
                {
                    activeReleases[nextRelease.playerKey] = nextRelease;
                    StartReleaseProcess(nextRelease);
                    OnReleaseStarted?.Invoke(nextRelease.player, nextRelease.releaseType);
                }
                else
                {
                    // Put it back in queue if still no officers available
                    releaseQueue.Enqueue(nextRelease);
                }

                // An eligible request was either started or restored to the
                // queue. Do not consume the whole queue in one frame.
                break;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Return the authored prison exit position currently used for every player.  The player
        /// argument is retained for the call-site contract but is not consulted by this fixed-point
        /// implementation.
        /// </summary>
        private Vector3 GetPlayerExitPosition(Player player)
        {
            // Use specific prison exit coordinates
            return new Vector3(13.7402f, 1.4857f, 38.1558f);
        }

        /// <summary>
        /// The supervising officer waits at the foot of the police-station steps rather
        /// than on top of the player's release spawn.  It is derived from the release
        /// orientation so the staging point remains coupled to the authored exit.
        /// </summary>
        private Vector3 GetSupervisingOfficerReleaseMeetingPoint(Vector3 exitPosition)
        {
            Vector3 forward = Quaternion.Euler(GetPlayerExitRotation()) * Vector3.forward;
            return exitPosition + forward * 1.6f;
        }

        /// <summary>
        /// Resolve the stable runtime key used to correlate a player across active and queued
        /// release requests; null players produce an empty key.
        /// </summary>
        private static string GetPlayerRuntimeKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Core.ResolvePlayerKey(player);
        }

        private Vector3 GetPlayerExitRotation()
        {
            return new Vector3(0f, 80.1529f, 0f); // Facing away from wall/pillar
        }

        /// <summary>
        /// Resolve the storage-wait location in fallback order: authored inventory drop-off,
        /// discovered pickup station, then jail root, with the jail root repeated as the exception
        /// fallback.
        /// </summary>
        private Vector3 GetStorageLocationForWaiting()
        {
            try
            {
                // Use the same storage location logic as the escort
                var jailController = Core.JailController;
                if (jailController?.storage?.inventoryDropOff != null)
                {
                    return jailController.storage.inventoryDropOff.position;
                }

                // Fallback to inventory pickup station if it exists
                var pickupStation = BBHelpers.FindObjectOfTypeSafe<InventoryPickupStation>();
                if (pickupStation != null)
                {
                    return pickupStation.transform.position;
                }

                // Final fallback to jail center
                return Core.JailController?.transform.position ?? Vector3.zero;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting storage location for waiting: {ex.Message}");
                return Core.JailController?.transform.position ?? Vector3.zero;
            }
        }

        /// <summary>
        /// Collect confiscated item names that do not match the local name-based contraband list.
        /// The result is best-effort and returns an empty list when the player handler or lookup
        /// fails; it is used to seed the later storage snapshot.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private List<string> GetLegalItemsForReturn(Player player)
        {
            var legalItems = new List<string>();

            try
            {
                var playerHandler = Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    var confiscatedItems = playerHandler.GetConfiscatedItems();

                    // Filter out contraband items - only return legal items
                    foreach (var item in confiscatedItems)
                    {
                        if (!IsItemContraband(item))
                        {
                            legalItems.Add(item);
                        }
                        else
                        {
                            ModLogger.Info($"Keeping contraband item: {item}");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting legal items for {player.name}: {ex.Message}");
            }

            return legalItems;
        }

        /// <summary>
        /// Apply the current fallback contraband heuristic, which checks case-insensitive name
        /// substrings against a fixed list.  It does not consult the game's authoritative item
        /// classification system.
        /// </summary>
        private bool IsItemContraband(string itemName)
        {
            // Simple contraband check based on item name
            // This could be enhanced to use the actual game's contraband system
            var contrabandItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Drugs", "Cocaine", "Heroin", "Methamphetamine", "Cannabis",
                "Weapons", "Gun", "Knife", "Pistol", "Rifle", "Explosive",
                "Stolen", "Counterfeit", "Illegal"
            };

            foreach (var contraband in contrabandItems)
            {
                if (itemName.Contains(contraband, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Restore player movement/inventory, clear jail and persistent storage state, notify the
        /// player handler for the release type, and start parole supervision.  Failures are caught
        /// and logged here rather than propagated to the outer release callback.
        /// </summary>
        private void CompletePlayerRelease(Player player, ReleaseType releaseType)
        {
            try
            {
                // CRITICAL: Clear ALL escort registrations first to prevent future conflicts
                OfficerCoordinator.Instance.UnregisterAllEscortsForPlayer(player);

                // Restore player inventory
                InventoryProcessor.UnlockPlayerInventory(player);

                // CRITICAL: Restore player movement (may have been frozen during exit scan)
#if !MONO
                var playerMovement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    playerMovement.CanMove = true;
                    playerMovement.enabled = true;
                    ModLogger.Info($"Restored player movement for {player.name}");
                }
#else
                var playerMovement = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    playerMovement.CanMove = true;
                    playerMovement.enabled = true;
                    ModLogger.Info($"Restored player movement for {player.name}");
                }
#endif

                // Clear jail status
                var jailManager = Core.Instance?.JailManager;
                if (jailManager != null)
                {
                    jailManager.ClearPlayerJailStatus(player);
                }

                // Update player handler
                var playerHandler = Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    if (releaseType == ReleaseType.BailPayment)
                    {
                        string playerKey = GetPlayerRuntimeKey(player);
                        var releaseRequest = activeReleases.ContainsKey(playerKey) ? activeReleases[playerKey] : null;
                        playerHandler.OnReleasedOnBail(releaseRequest?.bailAmount ?? 0f);
                    }
                    else
                    {
                        playerHandler.OnReleasedFromJail(0f); // Time served is tracked elsewhere
                    }
                }

                // CRITICAL: Clear persistent storage snapshot AFTER successful release (not during storage phase)
                var persistentData = Core.ResolvePersistentPlayerData();
                if (persistentData != null)
                {
                    persistentData.ClearPlayerSnapshot(player);
                    ModLogger.Debug($"Cleared persistent storage snapshot after successful release for {player.name}");
                }

                // Start parole supervision for ALL releases (bail and time-served)
                // Bailing out doesn't allow you to skip parole - it only gets you out of jail quicker
                StartParoleForReleasedPlayer(player);

                ModLogger.Debug($"Player release completed for {player.name} via {releaseType} - all systems cleared");

            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error completing player release: {ex.Message}");
            }
        }

        /// <summary>
        /// Start or resume parole supervision for a released player
        /// If player has paused parole, extends and resumes it; otherwise starts new parole
        /// </summary>
        private void StartParoleForReleasedPlayer(Player player)
        {
            try
            {
                ModLogger.Debug($"[PAROLE] === Processing Parole for Released Player: {player.name} ===");

                // Get cached rap sheet (loads from file only once)
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                bool rapSheetLoaded = rapSheet != null;

                if (!rapSheetLoaded)
                {
                    ModLogger.Warn($"[PAROLE] No rap sheet found for {player.name} - using default parole term");
                }

                // Check if player has paused parole from previous incarceration
                bool hasPausedParole = rapSheet?.CurrentParoleRecord != null &&
                                      rapSheet.CurrentParoleRecord.IsOnParole() &&
                                      rapSheet.CurrentParoleRecord.IsPaused();

                if (hasPausedParole)
                {
                    // RESUME AND EXTEND paused parole
                    float pausedRemainingTime = rapSheet.CurrentParoleRecord.GetPausedRemainingTime(); // In game minutes
                    ModLogger.Debug($"[PAROLE] Player has paused parole with {pausedRemainingTime} game minutes ({GameTimeManager.FormatGameTime(pausedRemainingTime)}) remaining");

                    // Calculate additional time from new crimes (in game minutes)
                    float additionalTime = CalculateAdditionalParoleTime(rapSheet, rapSheetLoaded);

                    // Add violation penalties (in game minutes)
                    int violationCount = rapSheet.CurrentParoleRecord.GetViolationCount();
                    float violationPenalty = CalculateViolationPenalty(violationCount);

                    ModLogger.Debug($"[PAROLE] Additional time from new crimes: {additionalTime} game minutes ({GameTimeManager.FormatGameTime(additionalTime)})");
                    ModLogger.Debug($"[PAROLE] Violation penalty: {violationPenalty} game minutes ({GameTimeManager.FormatGameTime(violationPenalty)}) for {violationCount} violations");

                    // Extend the paused parole using helper method that marks RapSheet as changed
                    float totalAdditional = additionalTime + violationPenalty;
                    rapSheet.ExtendPausedParole(totalAdditional);

                    float newTotalTime = pausedRemainingTime + totalAdditional;
                    float newGameDays = newTotalTime / (60f * 24f); // Convert game minutes to game days
                    ModLogger.Debug($"[PAROLE] New total parole time: {newTotalTime} game minutes ({newGameDays:F1} game days / {GameTimeManager.FormatGameTime(newTotalTime)})");

                    // Resume parole using helper method that marks RapSheet as changed
                    rapSheet.ResumeParole();
                    ModLogger.Debug($"[PAROLE] ✓ Paused parole resumed and extended for {player.name}");
                }
                else
                {
                    // START NEW parole term (duration is in game minutes)
                    float paroleDuration = CalculateParoleDuration(rapSheet, rapSheetLoaded); // Returns game minutes
                    float gameDays = paroleDuration / (60f * 24f); // Convert game minutes to game days

                    ModLogger.Debug($"[PAROLE] Starting new parole term: {paroleDuration} game minutes ({gameDays:F1} game days / {GameTimeManager.FormatGameTime(paroleDuration)})");
                    if (rapSheet != null)
                    {
                        ModLogger.Debug($"[PAROLE] Crime count: {rapSheet.GetCrimeCount()}, LSI Level: {rapSheet.LSILevel}");
                    }

                    // Start parole through ParoleSystem (expects game minutes)
                    // Don't show UI immediately - it will be shown after release summary UI is dismissed
                    var paroleManager = Core.ResolveParoleManager();
                    if (paroleManager != null)
                    {
                        paroleManager.StartParole(player, paroleDuration, showUI: false);
                        ModLogger.Debug($"[PAROLE] ✓ New parole started successfully for {player.name}");
                    }
                    else
                    {
                        ModLogger.Error($"[PAROLE] ✗ ParoleManager not available - cannot start parole");
                    }
                }

                ModLogger.Debug($"[PAROLE] === Parole Processing Complete ===");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[PAROLE] Error processing parole for {player.name}: {ex.Message}");
                ModLogger.Error($"[PAROLE] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Calculate parole duration based on rap sheet data
        /// Formula: Base time + (crimes * multiplier) + LSI modifier
        /// </summary>
        /// <returns>Parole duration in game minutes</returns>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private float CalculateParoleDuration(RapSheet rapSheet, bool hasRapSheet)
        {
            // Durations are calculated in game-minute units.  Conversion to wall-clock time is left
            // to the game-time consumers; this calculation does not assume a fixed real-time ratio.
            const float BASE_PAROLE_TIME = 2880f;  // 2 game days (2880 game minutes)
            const float MIN_PAROLE_TIME = 1440f;   // 1 game day (1440 game minutes)
            const float MAX_PAROLE_TIME = 10080f;  // 7 game days (10080 game minutes)
            const float CRIME_MULTIPLIER = 240f;   // 4 game hours per crime (240 game minutes)

            if (!hasRapSheet)
            {
                return BASE_PAROLE_TIME;
            }

            float paroleDuration = BASE_PAROLE_TIME;

            // Factor 1: Crime count (more crimes = longer parole)
            int crimeCount = rapSheet.GetCrimeCount();
            float crimeBonus = crimeCount * CRIME_MULTIPLIER;
            paroleDuration += crimeBonus;

            float crimeGameHours = (crimeBonus / 60f);
            ModLogger.Debug($"[PAROLE CALC] Base: {BASE_PAROLE_TIME} game min (2 days) + Crime bonus: {crimeCount} crimes × {CRIME_MULTIPLIER} game min = +{crimeGameHours:F1} game hours");

            // Factor 2: LSI level modifier (higher risk = longer parole)
            float lsiMultiplier = rapSheet.LSILevel switch
            {
                LSILevel.None => 1.0f,
                LSILevel.Minimum => 1.0f,  // No change
                LSILevel.Medium => 1.25f,  // +25%
                LSILevel.High => 1.5f,     // +50%
                LSILevel.Severe => 2.0f,   // +100%
                _ => 1.0f
            };

            paroleDuration *= lsiMultiplier;

            float gameDaysBeforeClamp = paroleDuration / (60f * 24f); // Convert game minutes to game days
            ModLogger.Debug($"[PAROLE CALC] LSI Level: {rapSheet.LSILevel} (×{lsiMultiplier}) = {gameDaysBeforeClamp:F1} game days");

            // Clamp to reasonable bounds
            paroleDuration = Mathf.Clamp(paroleDuration, MIN_PAROLE_TIME, MAX_PAROLE_TIME);

            float finalGameDays = paroleDuration / (60f * 24f); // Convert game minutes to game days
            ModLogger.Debug($"[PAROLE CALC] Final duration: {paroleDuration} game minutes ({finalGameDays:F1} game days)");

            return paroleDuration;
        }

        /// <summary>
        /// Calculate additional parole time from the rap sheet's current crime count (without base
        /// time or LSI modifier).  Used when extending paused parole.
        /// </summary>
        /// <returns>Additional time in game minutes</returns>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private float CalculateAdditionalParoleTime(RapSheet rapSheet, bool hasRapSheet)
        {
            const float CRIME_MULTIPLIER = 240f;   // 4 game hours per crime (240 game minutes)

            if (!hasRapSheet)
            {
                return 0f;
            }

            // Calculate the current crime-based burden without base or LSI modifier.  GetCrimeCount
            // is a total snapshot here; it is not a delta computed since the prior parole term.
            int crimeCount = rapSheet.GetCrimeCount();
            float additionalTime = crimeCount * CRIME_MULTIPLIER;

            ModLogger.Debug($"[PAROLE CALC] Additional time for {crimeCount} new crimes: {additionalTime} game minutes ({GameTimeManager.FormatGameTime(additionalTime)})");

            return additionalTime;
        }

        /// <summary>
        /// Calculate penalty time for parole violations
        /// Each violation adds additional supervision time
        /// </summary>
        /// <returns>Penalty time in game minutes</returns>
        private float CalculateViolationPenalty(int violationCount)
        {
            const float VIOLATION_PENALTY = 480f;  // 8 game hours per violation (480 game minutes)

            float penalty = violationCount * VIOLATION_PENALTY;

            ModLogger.Debug($"[PAROLE CALC] Violation penalty for {violationCount} violations: {penalty} game minutes ({GameTimeManager.FormatGameTime(penalty)})");

            return penalty;
        }

        /// <summary>
        /// Calculate all release summary data for the parole conditions UI
        /// Returns bail amount, fine amount, parole term length, LSI level, LSI breakdown, jail time info, recent crimes, and conditions (split into general and special)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public (float bailAmountPaid, float fineAmount, float termLengthGameMinutes, LSILevel lsiLevel, 
            (int totalScore, int crimeCountScore, int severityScore, int violationScore, int pastParoleScore, LSILevel resultingLevel) lsiBreakdown,
            (float originalSentenceTime, float timeServed) jailTimeInfo,
            List<string> recentCrimes, List<string> generalConditions, List<string> specialConditions) CalculateReleaseSummaryData(Player player, ReleaseType releaseType)
        {
            try
            {
                // Get bail amount - check if bail was actually paid based on release type
                float bailAmountPaid = 0f;
                if (releaseType == ReleaseType.BailPayment)
                {
                    // Bail was paid - get the stored amount
                    var bailSystem = Core.ResolveBailSystem();
                    if (bailSystem != null)
                    {
                        bailAmountPaid = bailSystem.GetBailAmount(player);
                    }
                    
                    // If stored amount is 0 but release type is BailPayment, try to get from active release request
                    string playerKey = GetPlayerRuntimeKey(player);
                    if (bailAmountPaid == 0f && activeReleases.ContainsKey(playerKey))
                    {
                        bailAmountPaid = activeReleases[playerKey].bailAmount;
                    }
                }
                // If releaseType is not BailPayment, bailAmountPaid intentionally remains 0; this
                // branch is not a timeout or failure indication.
                
                // Get fine amount and rap sheet
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                float fineAmount = FineCalculator.Instance.CalculateTotalFine(player, rapSheet);
                
                // Get LSI level and breakdown
                LSILevel lsiLevel = LSILevel.None;
                var lsiBreakdown = (totalScore: 0, crimeCountScore: 0, severityScore: 0, violationScore: 0, pastParoleScore: 0, resultingLevel: LSILevel.None);
                if (rapSheet != null)
                {
                    lsiLevel = rapSheet.LSILevel;
                    lsiBreakdown = rapSheet.GetLSIBreakdown();
                }
                
                // Get jail time info (original sentence vs time served)
                var jailManager = Core.Instance?.JailManager;
                float originalSentenceTime = jailManager != null
                    ? jailManager.GetOriginalSentenceTime(player)
                    : Core.ResolveJailTimeTracker().GetOriginalSentenceTime(player);
                float timeServed = jailManager != null
                    ? jailManager.GetTimeServed(player)
                    : Core.ResolveJailTimeTracker().GetTimeServed(player);
                
                var jailTimeInfo = (originalSentenceTime, timeServed);
                
                // Get recent crimes from rap sheet (crimes added during this arrest session)
                // Also include parole violations converted to crimes
                // Get crimes added within the last 2 hours (should cover the arrest session)
                List<string> recentCrimes = new List<string>();
                if (rapSheet != null)
                {
                    // First, convert recent parole violations to crimes if they exist
                    if (rapSheet.CurrentParoleRecord != null)
                    {
                        var violations = rapSheet.CurrentParoleRecord.GetViolations();
                        if (violations != null && violations.Count > 0)
                        {
                            DateTime twoHoursAgo = DateTime.Now.AddHours(-2);
                            foreach (var violation in violations)
                            {
                                // Only include violations from the last 2 hours (current arrest session)
                                if (violation.ViolationTime >= twoHoursAgo)
                                {
                                    string violationName = GetViolationCrimeName(violation.ViolationType);
                                    if (!string.IsNullOrEmpty(violationName) && !recentCrimes.Contains(violationName))
                                    {
                                        recentCrimes.Add(violationName);
                                    }
                                }
                            }
                        }
                    }
                    
                    var allCrimes = rapSheet.GetAllCrimes();
                    if (allCrimes != null && allCrimes.Count > 0)
                    {
                        // Get crimes added recently (within last 2 game hours = 120 game minutes)
                        float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
                        float twoGameHoursAgo = currentGameTime - 120f; // 2 game hours = 120 game minutes
                        var recentCrimeInstances = allCrimes.Where(c => c != null && c.Timestamp >= twoGameHoursAgo)
                            .OrderByDescending(c => c.Timestamp)
                            .Take(10) // Get up to 10 most recent crimes
                            .ToList();
                        
                        if (recentCrimeInstances.Count > 0)
                        {
                            foreach (var crime in recentCrimeInstances)
                            {
                                // Use GetCrimeName() helper for safe access
                                string crimeName = crime.GetCrimeName();
                                if (!string.IsNullOrEmpty(crimeName) && !recentCrimes.Contains(crimeName))
                                {
                                    recentCrimes.Add(crimeName);
                                }
                            }
                        }
                        
                        // If no recent crimes found, fall back to last 5 crimes overall
                        if (recentCrimes.Count == 0)
                        {
                            int startIndex = Math.Max(0, allCrimes.Count - 5);
                            for (int i = startIndex; i < allCrimes.Count; i++)
                            {
                                var crime = allCrimes[i];
                                // Use GetCrimeName() helper for safe access
                                string crimeName = crime.GetCrimeName();
                                if (!string.IsNullOrEmpty(crimeName))
                                {
                                    recentCrimes.Add(crimeName);
                                }
                            }
                        }
                    }
                }
                
                // Calculate parole term length
                var (termLength, allConditions) = CalculateParoleTermLength(player);
                
                // Split conditions into general and special
                var (generalConditions, specialConditions) = SplitConditionsIntoGeneralAndSpecial(player, rapSheet, allConditions);
                
                return (bailAmountPaid, fineAmount, termLength, lsiLevel, lsiBreakdown, jailTimeInfo, recentCrimes, generalConditions, specialConditions);
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[RELEASE SUMMARY] Error calculating release summary data: {ex.Message}");
                // Return defaults
                var (termLength, allConditions) = CalculateParoleTermLength(player);
                var (generalConditions, specialConditions) = SplitConditionsIntoGeneralAndSpecial(player, null, allConditions);
                return (0f, 0f, termLength, LSILevel.None, (0, 0, 0, 0, 0, LSILevel.None), (0f, 0f), new List<string>(), generalConditions, specialConditions);
            }
        }

        /// <summary>
        /// Convert ViolationType to a crime name for display
        /// </summary>
        private string GetViolationCrimeName(ViolationType violationType)
        {
            return violationType switch
            {
                ViolationType.ContrabandPossession => "Parole Violation - Contraband Possession",
                ViolationType.MissedCheckIn => "Parole Violation - Missed Check-In",
                ViolationType.NewCrime => "Parole Violation - New Crime",
                ViolationType.RestrictedAreaViolation => "Parole Violation - Restricted Area",
                ViolationType.CurfewViolation => "Parole Violation - Curfew Violation",
                ViolationType.ContactWithKnownCriminals => "Parole Violation - Contact with Known Criminals",
                ViolationType.IllegalWeaponPossession => "Parole Violation - Illegal Weapon",
                ViolationType.Other => "Parole Violation",
                _ => "Parole Violation"
            };
        }

        /// <summary>
        /// Split conditions into general (for everyone) and special (based on crimes/criteria)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private (List<string> generalConditions, List<string> specialConditions) SplitConditionsIntoGeneralAndSpecial(Player player, RapSheet rapSheet, List<string> allConditions)
        {
            // Try to use the ParoleConditionManager for dynamic conditions based on active parole conditions
            if (rapSheet != null)
            {
                        try
                        {
                            var conditionManager = Core.ResolveParoleConditionManager();

                            // If conditions are already initialized (from StartParole), use them
                            var activeConditions = conditionManager.GetActiveConditions();
                    if (activeConditions != null && activeConditions.Count > 0)
                    {
                        var (dynamicGeneral, dynamicSpecial) = conditionManager.GetConditionDescriptions();
                        return (dynamicGeneral, dynamicSpecial);
                    }
                }
                catch (Exception e)
                {
                    ModLogger.Warn($"ReleaseManager: Failed to get conditions from ParoleConditionManager: {e.Message}");
                }
            }

            // Fallback: hardcoded conditions (used when ParoleConditionManager hasn't initialized yet)
            var generalConditions = new List<string>
            {
                "Report to parole officer as required",
                "No possession of illegal items",
                "Comply with all search requests",
                "Remain within designated areas"
            };
            
            var specialConditions = new List<string>();
            
            if (rapSheet != null)
            {
                var crimes = rapSheet.GetAllCrimes();
                if (crimes != null && crimes.Count > 0)
                {
                    bool hasDrugCrimes = false;
                    bool hasViolentCrimes = false;
                    bool hasTheftCrimes = false;
                    bool hasWeaponCrimes = false;
                    
                    foreach (var crime in crimes)
                    {
                        string crimeName = crime.GetCrimeName();
                        string lowerName = crimeName.ToLower();
                        
                        if (lowerName.Contains("drug") || lowerName.Contains("possession") || lowerName.Contains("trafficking"))
                            hasDrugCrimes = true;
                        if (lowerName.Contains("assault") || lowerName.Contains("murder") || lowerName.Contains("manslaughter") || 
                            lowerName.Contains("violence") || lowerName.Contains("battery"))
                            hasViolentCrimes = true;
                        if (lowerName.Contains("theft") || lowerName.Contains("burglary") || lowerName.Contains("robbery") || 
                            lowerName.Contains("steal") || lowerName.Contains("larceny"))
                            hasTheftCrimes = true;
                        if (lowerName.Contains("weapon") || lowerName.Contains("firearm") || lowerName.Contains("gun") || 
                            lowerName.Contains("brandish") || lowerName.Contains("discharge"))
                            hasWeaponCrimes = true;
                    }
                    
                    if (hasDrugCrimes)
                    {
                        specialConditions.Add("Submit to random drug testing");
                        specialConditions.Add("No association with known drug dealers");
                    }
                    if (hasViolentCrimes)
                    {
                        specialConditions.Add("Attend anger management counseling");
                        specialConditions.Add("No contact with victims");
                    }
                    if (hasTheftCrimes)
                    {
                        specialConditions.Add("No entry into retail establishments without supervision");
                        specialConditions.Add("Maintain employment or educational program");
                    }
                    if (hasWeaponCrimes)
                    {
                        specialConditions.Add("No possession of firearms or weapons");
                        specialConditions.Add("Surrender all weapons to parole supervisor");
                    }
                }
            }
            
            return (generalConditions, specialConditions);
        }

        /// <summary>
        /// Calculate parole term length and get default conditions (for UI display before parole starts)
        /// This duplicates the logic from StartParoleForReleasedPlayer but only calculates, doesn't start parole
        /// </summary>
        /// <returns>Tuple of (termLengthGameMinutes, conditionsList)</returns>
        public (float termLengthGameMinutes, List<string> conditions) CalculateParoleTermLength(Player player)
        {
            try
            {
                // Get cached rap sheet
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                bool rapSheetLoaded = rapSheet != null;

                if (!rapSheetLoaded)
                {
                    ModLogger.Warn($"[PAROLE CALC] No rap sheet found for {player.name} - using default parole term");
                    float defaultTerm = 2880f; // 2 game days
                    return (defaultTerm, GetDefaultParoleConditions());
                }

                // Check if player has paused parole
                bool hasPausedParole = rapSheet.CurrentParoleRecord != null &&
                                      rapSheet.CurrentParoleRecord.IsOnParole() &&
                                      rapSheet.CurrentParoleRecord.IsPaused();

                float termLength;

                if (hasPausedParole)
                {
                    // Calculate extended paused parole term
                    float pausedRemainingTime = rapSheet.CurrentParoleRecord.GetPausedRemainingTime();
                    float additionalTime = CalculateAdditionalParoleTime(rapSheet, rapSheetLoaded);
                    int violationCount = rapSheet.CurrentParoleRecord.GetViolationCount();
                    float violationPenalty = CalculateViolationPenalty(violationCount);
                    termLength = pausedRemainingTime + additionalTime + violationPenalty;
                }
                else
                {
                    // Calculate new parole term
                    termLength = CalculateParoleDuration(rapSheet, rapSheetLoaded);
                }

                return (termLength, GetDefaultParoleConditions());
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[PAROLE CALC] Error calculating parole term length: {ex.Message}");
                return (2880f, GetDefaultParoleConditions()); // Default 2 game days
            }
        }

        /// <summary>
        /// Get default parole conditions list (placeholder for now)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private List<string> GetDefaultParoleConditions()
        {
            return new List<string>
            {
                "Report to parole officer as required",
                "No possession of illegal items",
                "Comply with all search requests",
                "Remain within designated areas"
            };
        }

        /// <summary>
        /// Freeze player movement and controls
        /// </summary>
        private void FreezePlayer(Player player)
        {
            try
            {
#if !MONO
                var playerMovement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    playerMovement.CanMove = false;
                    ModLogger.Info($"Froze player movement for {player.name}");
                }
#else
                var playerMovement = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    playerMovement.CanMove = false;
                    ModLogger.Info($"Froze player movement for {player.name}");
                }
#endif
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error freezing player: {ex.Message}");
            }
        }

        /// <summary>
        /// Unfreeze player movement and controls
        /// </summary>
        private void UnfreezePlayer(Player player)
        {
            try
            {
#if !MONO
                var playerMovement = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    playerMovement.CanMove = true;
                    ModLogger.Info($"Unfroze player movement for {player.name}");
                }
#else
                var playerMovement = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerMovement>.Instance;
                if (playerMovement != null)
                {
                    playerMovement.CanMove = true;
                    ModLogger.Info($"Unfroze player movement for {player.name}");
                }
#endif
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error unfreezing player: {ex.Message}");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator HandlePostReleaseOfficerCompliance(Player player, System.Action onImmediateArrestTriggered)
        {
            if (player == null)
            {
                yield break;
            }

            bool playerUnfrozen = false;
            bool violationRecorded = false;
            bool warningActive = false;
            float warningEndTime = 0f;
            float nextWarningUiUpdateTime = 0f;

            ParoleOfficerBehavior officer = null;
            NPCDialogueWrapper dialogue = null;
            bool completedDialogue = false;
            bool dialogueEverActive = false;
            Vector3 officerOriginalPosition = Vector3.zero;
            bool officerHadOriginalPosition = false;
            Vector3 dialogueAnchorPosition = Vector3.zero;
            bool hasDialogueAnchorPosition = false;

            try
            {
                officer = ResolvePostReleaseOfficer(player);

                if (officer != null)
                {
                    officerOriginalPosition = officer.transform.position;
                    officerHadOriginalPosition = true;
                    TeleportOfficerInFrontOfPlayer(officer, player, PostReleaseOfficerTeleportDistance);
                    dialogueAnchorPosition = officer.transform.position;
                    hasDialogueAnchorPosition = true;
                    dialogue = BuildPostReleaseComplianceDialogue(officer, player, () => completedDialogue = true);

                    if (dialogue != null)
                    {
                        officer.StopMovement();
                        dialogue.UseContainerOnInteract(PostReleaseDialogueContainerName);

#if !MONO
                        // IL2CPP: start without dialogue behaviour first to avoid camera lockups.
                        dialogue.Start(PostReleaseDialogueContainerName, false, "ENTRY");
                        if (!dialogue.IsDialogueInProgress)
                        {
                            dialogue.Start(PostReleaseDialogueContainerName, true, "ENTRY");
                        }
#else
                        dialogue.Start(PostReleaseDialogueContainerName, true, "ENTRY");
#endif

                        dialogueEverActive = dialogue.IsDialogueInProgress;
                        if (!dialogueEverActive)
                        {
                            ModLogger.Warn($"Post-release compliance: dialogue failed to start for {player.name}");
                            dialogue.Dispose();
                            dialogue = null;
                        }
                    }
                }
                else
                {
                    ModLogger.Warn($"Post-release compliance: no parole officer available for {player.name}");
                }

                // Requirement: begin dialogue before unfreeze.
                UnfreezePlayer(player);
                playerUnfrozen = true;

                // If dialogue couldn't start, don't block release flow.
                if (officer == null || dialogue == null)
                {
                    yield break;
                }

                bool wasDialogueActive = dialogue.IsDialogueInProgress;
                dialogueEverActive |= wasDialogueActive;

                while (!completedDialogue)
                {
                    if (player == null)
                    {
                        break;
                    }

                    bool isDialogueActive = dialogue.IsDialogueInProgress;
                    if (isDialogueActive)
                    {
                        dialogueEverActive = true;

                        if (officer != null)
                        {
                            HoldOfficerForDialogue(officer, player, dialogueAnchorPosition, hasDialogueAnchorPosition);
                        }
                    }

                    if (!warningActive)
                    {
                        if (dialogueEverActive && wasDialogueActive && !isDialogueActive)
                        {
                            warningActive = true;
                            warningEndTime = Time.time + PostReleaseWarningDurationSeconds;
                            nextWarningUiUpdateTime = 0f;
                            dialogue.UseContainerOnInteract(PostReleaseDialogueContainerName);
                            ModLogger.Info($"Post-release compliance: warning window started for {player.name}");
                        }
                    }
                    else
                    {
                        if (officer != null && !isDialogueActive)
                        {
                            KeepOfficerAtPlayerSide(officer, player);
                        }

                        if (officer != null)
                        {
                            float distanceToOfficer = Vector3.Distance(player.transform.position, officer.transform.position);
                            if (distanceToOfficer >= PostReleaseImmediateArrestDistance)
                            {
                                if (!violationRecorded)
                                {
                                    RecordPostReleaseDialogueViolation(player, "Ran from the supervising officer instead of completing the initial parole compliance dialogue.");
                                    violationRecorded = true;
                                }

                                Core.ResolveUIManager().HideOfficerCommand();
                                TriggerImmediateParoleComplianceArrest(player);
                                onImmediateArrestTriggered?.Invoke();
                                yield break;
                            }
                        }

                        int secondsRemaining = Mathf.Max(0, Mathf.CeilToInt(warningEndTime - Time.time));
                        if (Time.time >= nextWarningUiUpdateTime)
                        {
                            ShowPostReleaseComplianceWarningCommand(secondsRemaining);
                            nextWarningUiUpdateTime = Time.time + 1f;
                        }

                        if (!isDialogueActive)
                        {
                            dialogue.UseContainerOnInteract(PostReleaseDialogueContainerName);
                        }

                        if (Time.time >= warningEndTime)
                        {
                            if (!violationRecorded)
                            {
                                RecordPostReleaseDialogueViolation(player, "Failed to complete initial parole compliance dialogue before warning deadline.");
                                violationRecorded = true;
                            }

                            Core.ResolveUIManager().HideOfficerCommand();
                            TriggerPoliceCallForParoleNonCompliance(player);
                            yield break;
                        }
                    }

                    wasDialogueActive = isDialogueActive;
                    yield return null;
                }

                Core.ResolveUIManager().HideOfficerCommand();
                dialogue.End();
                if (completedDialogue && officer != null)
                {
                    ReturnOfficerToPost(officer, officerOriginalPosition, officerHadOriginalPosition);
                }
                ModLogger.Info($"Post-release compliance: dialogue completed for {player.name}");
            }
            finally
            {
                Core.ResolveUIManager().HideOfficerCommand();
                dialogue?.Dispose();
                if (!playerUnfrozen)
                {
                    UnfreezePlayer(player);
                }
            }
        }

        private ParoleOfficerBehavior ResolvePostReleaseOfficer(Player player)
        {
            var npcManager = Core.Instance?.NpcManager;
            if (npcManager == null)
            {
                return null;
            }

            var supervisingOfficer = npcManager.GetSupervisingOfficer();
            if (IsOfficerAvailableForPostRelease(supervisingOfficer, player))
            {
                return supervisingOfficer;
            }

            var officers = npcManager.GetRegisteredParoleOfficers();
            if (officers == null || officers.Count == 0)
            {
                return null;
            }

            return officers
                .Where(officer => IsOfficerAvailableForPostRelease(officer, player))
                .OrderBy(o => Vector3.Distance(o.transform.position, player.transform.position))
                .FirstOrDefault();
        }

        private bool IsOfficerAvailableForPostRelease(ParoleOfficerBehavior officer, Player player)
        {
            if (officer == null || !officer.isActiveAndEnabled || officer.gameObject == null || !officer.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (!officer.IsOnDuty() || officer.IsProcessingIntake())
            {
                return false;
            }

            var currentParolee = officer.GetCurrentParolee();
            if (currentParolee != null && currentParolee != player)
            {
                return false;
            }

            return officer.GetCurrentActivity() switch
            {
                ParoleOfficerBehavior.ParoleOfficerActivity.Idle => true,
                ParoleOfficerBehavior.ParoleOfficerActivity.MonitoringArea => true,
                _ => false
            };
        }

        private void TeleportOfficerInFrontOfPlayer(ParoleOfficerBehavior officer, Player player, float distanceFromPlayer)
        {
            if (officer == null || player == null)
            {
                return;
            }

            Vector3 planarForward = GetPlayerFacingDirection(player);

            Vector3 targetPosition = player.transform.position + planarForward * distanceFromPlayer;
            if (Physics.Raycast(targetPosition + Vector3.up * 8f, Vector3.down, out RaycastHit hit, 20f, ~0, QueryTriggerInteraction.Ignore))
            {
                targetPosition.y = hit.point.y;
            }
            else
            {
                targetPosition.y = player.transform.position.y;
            }

            officer.transform.position = targetPosition;

            Vector3 lookDirection = player.transform.position - targetPosition;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.001f)
            {
                officer.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            }

            ModLogger.Debug($"Post-release compliance: positioned officer {officer.GetBadgeNumber()} at {targetPosition} facing player");
        }

        private Vector3 GetPlayerFacingDirection(Player player)
        {
            Vector3 forward = Vector3.zero;

            try
            {
#if !MONO
                var playerCamera = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerCamera>.Instance;
#else
                var playerCamera = ScheduleOne.DevUtilities.PlayerSingleton<ScheduleOne.PlayerScripts.PlayerCamera>.Instance;
#endif
                if (playerCamera != null)
                {
                    forward = playerCamera.transform.forward;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Post-release compliance: failed to read player camera forward ({ex.Message})");
            }

            if (forward.sqrMagnitude < 0.001f)
            {
                forward = player.transform.forward;
            }

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
            {
                forward = Vector3.forward;
            }

            return forward.normalized;
        }

        private void KeepOfficerAtPlayerSide(ParoleOfficerBehavior officer, Player player, bool instant = false)
        {
            if (officer == null || player == null)
            {
                return;
            }

            Vector3 right = player.transform.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.001f)
            {
                right = Vector3.right;
            }
            right.Normalize();

            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            Vector3 targetPosition = player.transform.position + (right * PostReleaseOfficerSideDistance) + (forward * PostReleaseOfficerForwardOffset);
            if (Physics.Raycast(targetPosition + Vector3.up * 8f, Vector3.down, out RaycastHit hit, 20f, ~0, QueryTriggerInteraction.Ignore))
            {
                targetPosition.y = hit.point.y;
            }
            else
            {
                targetPosition.y = player.transform.position.y;
            }

            if (instant)
            {
                officer.transform.position = targetPosition;
            }
            else
            {
                officer.transform.position = Vector3.MoveTowards(officer.transform.position, targetPosition, 8f * Time.deltaTime);
            }

            Vector3 lookDirection = player.transform.position - officer.transform.position;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.001f)
            {
                officer.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            }
        }

        private void HoldOfficerForDialogue(ParoleOfficerBehavior officer, Player player, Vector3 anchorPosition, bool hasAnchorPosition)
        {
            if (officer == null || player == null)
            {
                return;
            }

            officer.StopMovement();

            if (hasAnchorPosition)
            {
                officer.transform.position = anchorPosition;
            }

            Vector3 lookDirection = player.transform.position - officer.transform.position;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.001f)
            {
                officer.transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            }
        }

        private void ReturnOfficerToPost(ParoleOfficerBehavior officer, Vector3 originalPosition, bool hasOriginalPosition)
        {
            if (officer == null)
            {
                return;
            }

            Vector3 fallbackPosition = hasOriginalPosition ? originalPosition : officer.transform.position;
            officer.ReturnToAssignedPost(fallbackPosition);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private NPCDialogueWrapper BuildPostReleaseComplianceDialogue(ParoleOfficerBehavior officer, Player player, System.Action onComplete)
        {
            if (officer == null || player == null)
            {
                return null;
            }

            var wrapper = new NPCDialogueWrapper(officer.gameObject);
            wrapper.BuildAndRegisterContainer(PostReleaseDialogueContainerName, builder =>
            {
                builder.SetAllowExit(true);
                builder.AddNode("ENTRY",
                    "Stop right there. Before you proceed, confirm you understand your parole obligations.",
                    choices =>
                    {
                        choices.Add("review", "Review obligations.", "review_node");
                    });

                builder.AddNode("review_node",
                    "You must report when instructed, avoid violations, and follow all supervision commands. Confirm now.",
                    choices =>
                    {
                        choices.Add(PostReleaseFinalChoiceLabel, "I understand and will comply.", "confirmed_node");
                    });

                builder.AddNode("confirmed_node",
                    "Acknowledged. You are now under parole supervision. Do not violate your conditions.");
            });

            wrapper.OnChoiceSelected(PostReleaseFinalChoiceLabel, onComplete);
            return wrapper;
        }

        private void ShowPostReleaseComplianceWarningCommand(int secondsRemaining)
        {
            string message = $"Interact with the supervising officer now. {secondsRemaining}s remaining - stay close or risk arrest/police pursuit.";
            var commandData = new OfficerCommandData("SUPERVISING OFFICER", message, 1, 1, false);
            Core.ResolveUIManager().UpdateOfficerCommand(commandData);
        }

        private void RecordPostReleaseDialogueViolation(Player player, string details)
        {
            try
            {
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    return;
                }

                var violation = new ViolationRecord(ViolationType.Other, details, 2.5f);
                if (rapSheet.AddParoleViolation(violation))
                {
                    rapSheet.UpdateLSILevel();
                    ModLogger.Info($"Post-release compliance violation recorded for {player.name}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error recording post-release compliance violation for {player?.name}: {ex.Message}");
            }
        }

        private void TriggerImmediateParoleComplianceArrest(Player player)
        {
            try
            {
                var jailManager = Core.Instance?.JailManager;
                if (jailManager == null)
                {
                    ModLogger.Error("Post-release compliance arrest failed: JailManager unavailable");
                    return;
                }

                ModLogger.Info($"Post-release compliance: immediate arrest triggered for {player.name}");
                MelonCoroutines.Start(jailManager.HandleImmediateArrest(player));
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error triggering immediate parole compliance arrest for {player?.name}: {ex.Message}");
            }
        }

        private void TriggerPoliceCallForParoleNonCompliance(Player player)
        {
            try
            {
                ModLogger.Info($"Post-release compliance: escalating non-compliance to police call for {player.name}");

                var lawManager = LawManager.Instance;
                if (lawManager != null)
                {
                    lawManager.PoliceCalled(player, new WitnessIntimidation());
                }

                if (player?.CrimeData != null)
                {
                    player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error escalating parole non-compliance police call for {player?.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Wait for player to acknowledge parole conditions UI
        /// Freezes player, shows UI, waits for dismissal, then starts grace period
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForParoleConditionsAcknowledgment(Player player, float bailAmountPaid, float fineAmount, float termLengthGameMinutes, 
            LSILevel lsiLevel, (int totalScore, int crimeCountScore, int severityScore, int violationScore, int pastParoleScore, LSILevel resultingLevel) lsiBreakdown,
            (float originalSentenceTime, float timeServed) jailTimeInfo, List<string> recentCrimes, List<string> generalConditions, List<string> specialConditions)
        {
            bool hasError = false;
            
            try
            {
                // Freeze player
                FreezePlayer(player);

                // Begin the canonical supervisor approach before the conditions screen is
                // visible.  The player remains frozen by this release flow, while the officer
                // walks to the release door and is ready to begin the check-in escort as soon
                // as the player dismisses the summary.
                StartSceneCoroutine(PrepareCanonicalParoleIntake(player));

                // Show UI with all release summary data
                Core.ResolveUIManager().ShowParoleConditionsUI(player, bailAmountPaid, fineAmount, termLengthGameMinutes, lsiLevel, lsiBreakdown, jailTimeInfo, recentCrimes, generalConditions, specialConditions);
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error setting up parole conditions UI: {ex.Message}");
                hasError = true;
            }

            if (hasError)
            {
                UnfreezePlayer(player);
                yield break;
            }

            // Wait for dismissal key press
            bool dismissed = false;
            bool keyWasPressed = false;

            while (!dismissed)
            {
                if (Input.GetKey(Core.BailoutKey))
                {
                    if (!keyWasPressed)
                    {
                        keyWasPressed = true;
                        dismissed = true;
                    }
                }
                else
                {
                    keyWasPressed = false;
                }

                yield return null;
            }

            // Hide UI first, then run post-release compliance flow
            try
            {
                Core.ResolveUIManager().HideParoleConditionsUI();
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding parole conditions UI: {ex.Message}");
            }

            // The supervising officer can still be finishing its scene-local spawn when the
            // player dismisses this summary.  Do not fall back to the legacy compliance
            // dialogue in that race: it teleports the officer to the player and competes with
            // the canonical intake escort/state machine.  Wait briefly for the canonical
            // behavior instead, preserving the acknowledged release summary for it.
            yield return HandOffReleaseSummaryToCanonicalIntake(player);

            try
            {
                // IMPORTANT: Grace period was already started when release completed
                // No need to record release time again - it's already recorded
                // Parole term timer is already running from teleportation,
                // and grace period for searches started immediately upon release

                // Show parole status UI now that the release summary is dismissed.
                Core.ResolveUIManager().ShowParoleStatus();
                ModLogger.Debug($"Showing parole status UI for {player.name} after release summary dismissal");

                ModLogger.Info($"Parole conditions acknowledged by {player.name} - canonical intake handoff completed");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error finalizing parole conditions acknowledgment flow: {ex.Message}");
            }
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Return whether the player has an active release request.  Queued requests are not
        /// included until an officer is assigned.
        /// </summary>
        public bool IsReleaseInProgress(Player player)
        {
            return player != null && activeReleases.ContainsKey(GetPlayerRuntimeKey(player));
        }

        /// <summary>
        /// Return the active request status, or <see cref="ReleaseStatus.NotStarted"/> for a null,
        /// unknown, or merely queued player.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public ReleaseStatus GetReleaseStatus(Player player)
        {
            if (player == null)
            {
                return ReleaseStatus.NotStarted;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            return activeReleases.ContainsKey(playerKey) ? activeReleases[playerKey].status : ReleaseStatus.NotStarted;
        }

        /// <summary>
        /// Register an officer with the NPC manager when available, then refresh this manager's
        /// local availability snapshot.
        /// </summary>
        public void RegisterOfficer(ActiveReleaseOfficerBehavior officer)
        {
            if (officer == null)
            {
                return;
            }

            var npcManager = Core.Instance?.NpcManager;
            if (npcManager != null)
            {
                npcManager.RegisterReleaseOfficer(officer);
            }

            RefreshAvailableOfficersFromRegistry();
            ModLogger.Debug($"Registered release officer: {officer.GetBadgeNumber()}");
        }

        /// <summary>
        /// Remove an officer from the NPC manager when available, then refresh this manager's local
        /// availability snapshot.
        /// </summary>
        public void UnregisterOfficer(ActiveReleaseOfficerBehavior officer)
        {
            if (officer == null)
            {
                return;
            }

            var npcManager = Core.Instance?.NpcManager;
            if (npcManager != null)
            {
                npcManager.UnregisterReleaseOfficer(officer);
            }

            RefreshAvailableOfficersFromRegistry();
            ModLogger.Debug($"Unregistered release officer: {officer.GetBadgeNumber()}");
        }

        /// <summary>
        /// Cancels any queued or active release process for a player, marking an active request failed, releasing its
        /// assigned officer, removing it from active state, and allowing queued requests to be processed again. The method
        /// expects a non-null player; the current catch/log path also dereferences player when reporting a failure.
        /// </summary>
        public void CancelPlayerRelease(Player player)
        {
            try
            {
                string playerKey = GetPlayerRuntimeKey(player);
                RemoveQueuedReleaseRequests(playerKey);

                if (activeReleases.ContainsKey(playerKey))
                {
                    var request = activeReleases[playerKey];
                    ModLogger.Debug($"Cancelling active release for {player.name} (Status: {request.status})");
                    request.status = ReleaseStatus.Failed;

                    ReleaseAssignedOfficer(request, true);

                    // Remove from active releases
                    activeReleases.Remove(playerKey);
                    ModLogger.Debug($"Removed {player.name} from active releases");

                    ProcessQueuedReleases();
                }
                else
                {
                    ModLogger.Debug($"No active release found for {player.name}; removed any queued release request");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error cancelling release for {player.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Marks an active request's inventoryProcessed flag and forwards the completion callback to its assigned release
        /// officer. A queued, unknown, or null-key request is not created by this method and only produces a warning; no
        /// release status transition occurs here.
        /// </summary>
        public void OnInventoryProcessingComplete(Player player)
        {
            string playerKey = GetPlayerRuntimeKey(player);

            if (activeReleases.ContainsKey(playerKey))
            {
                var request = activeReleases[playerKey];
                request.inventoryProcessed = true;
                ModLogger.Info($"Inventory processing marked complete for {player.name}");

                // Notify the assigned Release Officer directly
                if (request.assignedOfficer != null)
                {
                    request.assignedOfficer.OnInventoryPickupComplete(player);
                    ModLogger.Debug($"Notified Release Officer {request.assignedOfficer.GetBadgeNumber()} that inventory pickup is complete");
                }
                else
                {
                    ModLogger.Warn($"No assigned Release Officer found for {player.name} - cannot notify of inventory completion");
                }
            }
            else
            {
                ModLogger.Warn($"OnInventoryProcessingComplete called for {player.name} but no active release found");
            }
        }

        /// <summary>
        /// Forwards a ready-to-proceed confirmation to the assigned release officer for an active request. This callback does
        /// not mutate the request status or create an assignment; queued/unknown requests only produce a warning.
        /// </summary>
        public void OnPlayerConfirmationReceived(Player player)
        {
            string playerKey = GetPlayerRuntimeKey(player);

            if (activeReleases.ContainsKey(playerKey))
            {
                var request = activeReleases[playerKey];
                ModLogger.Info($"Player confirmation received for {player.name}");

                // Notify the assigned Release Officer
                if (request.assignedOfficer != null)
                {
                    request.assignedOfficer.OnPlayerConfirmedReady();
                    ModLogger.Debug($"Notified Release Officer {request.assignedOfficer.GetBadgeNumber()} of player confirmation");
                }
                else
                {
                    ModLogger.Warn($"No assigned Release Officer found for {player.name} - cannot notify of confirmation");
                }
            }
            else
            {
                ModLogger.Warn($"OnPlayerConfirmationReceived called for {player.name} but no active release found");
            }
        }

        /// <summary>
        /// Completes an active release immediately after the exit scanner reports completion. The manager trusts that the
        /// scanner already teleported the player; it does not validate the scanner source or perform a separate position check.
        /// Queued/unknown requests only produce a warning.
        /// </summary>
        public void OnExitScanCompleted(Player player)
        {
            string playerKey = GetPlayerRuntimeKey(player);

            if (activeReleases.ContainsKey(playerKey))
            {
                var request = activeReleases[playerKey];
                ModLogger.Debug($"Exit scan completed for {player.name} - completing entire release process");

                // Complete the release immediately (player has been teleported by ExitScannerStation)
                CompleteRelease(request);
            }
            else
            {
                ModLogger.Warn($"OnExitScanCompleted called for {player.name} but no active release found");
            }
        }

        /// <summary>
        /// Called when a Release Officer successfully completes an escort
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void HandleEscortCompleted(ReleaseRequest request, Player player)
        {
            if (request.playerKey != GetPlayerRuntimeKey(player))
            {
                ModLogger.Warn($"HandleEscortCompleted: Player mismatch - expected {request.player.name}, got {player?.name}");
                return;
            }

            request.player = player;

            ModLogger.Debug($"Release Officer completed escort for {player.name}");
            CompleteRelease(request);
        }

        /// <summary>
        /// Cancels release and parole-intake routines that belong to the unloading Main scene.
        /// Persistent release data is not altered; only volatile requests and callbacks are discarded.
        /// </summary>
        public void CancelForSceneExit()
        {
            foreach (var coroutine in sceneCoroutineHandles)
            {
                if (coroutine != null)
                {
                    MelonCoroutines.Stop(coroutine);
                }
            }
            sceneCoroutineHandles.Clear();

            foreach (var request in activeReleases.Values.ToList())
            {
                try
                {
                    ReleaseAssignedOfficer(request, true);
                }
                catch (Exception ex)
                {
                    // Scene teardown can race Unity object destruction. Continue releasing
                    // the remaining requests instead of leaving the manager half-cleaned.
                    ModLogger.Warn($"ReleaseManager scene-exit officer cleanup ignored an issue: {ex.Message}");
                }
            }
            activeReleases.Clear();
            releaseQueue.Clear();
            CleanupAllActiveReleaseCallbacks();
            ModLogger.Debug("ReleaseManager cancelled active scene release work");
        }

        /// <summary>
        /// Start a tracked scene coroutine so scene teardown can stop it through
        /// <see cref="CancelForSceneExit"/>.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private Coroutine StartSceneCoroutine(IEnumerator routine)
        {
            var coroutine = MelonCoroutines.Start(routine) as Coroutine;
            if (coroutine != null)
            {
                sceneCoroutineHandles.Add(coroutine);
            }
            return coroutine;
        }

        /// <summary>
        /// Called when a Release Officer fails an escort
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void HandleEscortFailed(ReleaseRequest request, Player player, string reason)
        {
            if (request.playerKey != GetPlayerRuntimeKey(player))
            {
                ModLogger.Warn($"HandleEscortFailed: Player mismatch - expected {request.player.name}, got {player?.name}");
                return;
            }

            request.player = player;

            ModLogger.Error($"Release Officer failed escort for {player.name}: {reason}");
            FailRelease(request, reason);
        }

        /// <summary>
        /// Called when a Release Officer updates their status during escort
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void HandleStatusUpdate(ReleaseRequest request, Player player, ActiveReleaseOfficerBehavior.ReleaseState officerState)
        {
            if (request.playerKey != GetPlayerRuntimeKey(player))
            {
                ModLogger.Warn($"HandleStatusUpdate: Player mismatch - expected {request.player.name}, got {player?.name}");
                return;
            }

            request.player = player;

            // Map Release Officer states to ReleaseManager statuses
            ReleaseStatus newStatus = officerState switch
            {
                ActiveReleaseOfficerBehavior.ReleaseState.EscortingToStorage => ReleaseStatus.EscortingToStorage,
                ActiveReleaseOfficerBehavior.ReleaseState.WaitingAtStorage => ReleaseStatus.InventoryProcessing,
                ActiveReleaseOfficerBehavior.ReleaseState.EscortingToExitScanner => ReleaseStatus.EscortingToExit,
                ActiveReleaseOfficerBehavior.ReleaseState.WaitingForExitScan => ReleaseStatus.EscortingToExit,
                _ => request.status // Keep current status if no mapping
            };

            if (newStatus != request.status)
            {
                request.status = newStatus;
                ModLogger.Debug($"Release status updated for {player.name}: {newStatus}");
            }
        }

#if !MONO
        /// <summary>
        /// Receives canonical release-officer state transitions without exposing
        /// delegate callbacks on the injected IL2CPP behavior surface.
        /// </summary>
        [HideFromIl2Cpp]
        internal void NotifyOfficerStateUpdate(ActiveReleaseOfficerBehavior officer, Player player, ActiveReleaseOfficerBehavior.ReleaseState officerState)
        {
            if (officer == null || player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            if (!activeReleases.TryGetValue(playerKey, out var request) || request.assignedOfficer != officer)
            {
                return;
            }

            HandleStatusUpdate(request, player, officerState);

            // ProcessingExit is the same authoritative completion point that the
            // old IL2CPP polling loop observed. Claim the request immediately so
            // a later scanner callback cannot complete it a second time.
            if (officerState == ActiveReleaseOfficerBehavior.ReleaseState.ProcessingExit)
            {
                CompleteRelease(request);
            }
        }

        /// <summary>
        /// Receive the IL2CPP-safe completion notification from the assigned officer and route it
        /// through the manager's exactly-once completion guard.
        /// </summary>
        [HideFromIl2Cpp]
        internal void NotifyOfficerEscortCompleted(ActiveReleaseOfficerBehavior officer, Player player)
        {
            if (officer == null || player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            if (activeReleases.TryGetValue(playerKey, out var request) && request.assignedOfficer == officer)
            {
                HandleEscortCompleted(request, player);
            }
        }

        /// <summary>
        /// Receive the IL2CPP-safe failure notification from the assigned officer and route it to
        /// the matching active request; notifications from another officer are ignored.
        /// </summary>
        [HideFromIl2Cpp]
        internal void NotifyOfficerEscortFailed(ActiveReleaseOfficerBehavior officer, Player player, string reason)
        {
            if (officer == null || player == null)
            {
                return;
            }

            string playerKey = GetPlayerRuntimeKey(player);
            if (activeReleases.TryGetValue(playerKey, out var request) && request.assignedOfficer == officer)
            {
                HandleEscortFailed(request, player, reason);
            }
        }
#endif

        /// <summary>
        /// Check the queue for a request with the supplied runtime player key without dequeuing it.
        /// </summary>
        private bool HasQueuedRelease(string playerKey)
        {
            foreach (var request in releaseQueue)
            {
                if (request != null && request.playerKey == playerKey)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Remove all queued requests for a player, marking each removed request failed without
        /// firing the normal active-request failure event.
        /// </summary>
        private void RemoveQueuedReleaseRequests(string playerKey)
        {
            if (string.IsNullOrEmpty(playerKey) || releaseQueue.Count == 0)
            {
                return;
            }

            int initialCount = releaseQueue.Count;
            int removedCount = 0;
            for (int index = 0; index < initialCount; index++)
            {
                var request = releaseQueue.Dequeue();
                if (request != null && request.playerKey == playerKey)
                {
                    request.status = ReleaseStatus.Failed;
                    removedCount++;
                    continue;
                }

                releaseQueue.Enqueue(request);
            }

            if (removedCount > 0)
            {
                ModLogger.Debug($"Removed {removedCount} queued release request(s) for {playerKey}");
            }
        }

        /// <summary>
        /// Associate an officer with a request and, on Mono, attach stable callback delegates that
        /// can later be removed by identity.  IL2CPP relies on the internal notification bridge and
        /// therefore stores only the officer reference here.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void AttachOfficerCallbacks(ReleaseRequest request, ActiveReleaseOfficerBehavior officer)
        {
            if (request == null || officer == null)
            {
                return;
            }

            DetachOfficerCallbacks(request);
            request.assignedOfficer = officer;

#if MONO
            request.escortCompletedCallback = player => HandleEscortCompleted(request, player);
            request.escortFailedCallback = (player, reason) => HandleEscortFailed(request, player, reason);
            request.statusUpdateCallback = (player, state) => HandleStatusUpdate(request, player, state);

            officer.OnEscortCompleted += request.escortCompletedCallback;
            officer.OnEscortFailed += request.escortFailedCallback;
            officer.OnStatusUpdate += request.statusUpdateCallback;
#endif
        }

        /// <summary>
        /// Remove the request's Mono callback delegates from its assigned officer.  The assigned
        /// officer reference itself is cleared by <see cref="ReleaseAssignedOfficer"/>.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void DetachOfficerCallbacks(ReleaseRequest request)
        {
            if (request == null)
            {
                return;
            }

            var officer = request.assignedOfficer;

#if MONO
            if (officer != null)
            {
                if (request.escortCompletedCallback != null)
                {
                    officer.OnEscortCompleted -= request.escortCompletedCallback;
                }

                if (request.escortFailedCallback != null)
                {
                    officer.OnEscortFailed -= request.escortFailedCallback;
                }

                if (request.statusUpdateCallback != null)
                {
                    officer.OnStatusUpdate -= request.statusUpdateCallback;
                }
            }

            request.escortCompletedCallback = null;
            request.escortFailedCallback = null;
            request.statusUpdateCallback = null;
#endif
        }

        /// <summary>
        /// Detach request callbacks, return the officer to the available pool, optionally send it
        /// back to post, and clear the request's officer reference.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void ReleaseAssignedOfficer(ReleaseRequest request, bool returnToPost)
        {
            if (request == null)
            {
                return;
            }

            var officer = request.assignedOfficer;
            DetachOfficerCallbacks(request);

            if (officer != null)
            {
                officer.SetAvailable(true);

                if (returnToPost)
                {
                    officer.ReturnToPost();
                }

                ModLogger.Debug($"Released officer {officer.GetBadgeNumber()} from release request for {request.player?.name ?? request.playerKey}");
            }

            request.assignedOfficer = null;
        }

        /// <summary>
        /// Detach callbacks and release officers for every active request during manager teardown.
        /// This helper does not itself clear the active-request dictionary; its callers own that
        /// lifecycle decision.
        /// </summary>
        private void CleanupAllActiveReleaseCallbacks()
        {
            if (activeReleases.Count == 0)
            {
                return;
            }

            foreach (var request in activeReleases.Values)
            {
                ReleaseAssignedOfficer(request, false);
            }
        }

        /// <summary>
        /// Wait for the canonical supervising officer's intake state machine, then hand off the
        /// dismissed release summary without reviving the retired manager-owned compliance flow.
        /// The player is unfrozen on success, invalidation, or the fixed timeout.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator HandOffReleaseSummaryToCanonicalIntake(Player player)
        {
            // The supervisor has been walking since the release request was created. In
            // the rare case scene-local spawn/navigation took longer than the release
            // escort, keep the player at the conditions handoff until the officer is
            // physically waiting at the police-station door.
            const float handoffTimeoutSeconds = 30f;
            float deadline = Time.realtimeSinceStartup + handoffTimeoutSeconds;
            float nextWaitingLogTime = Time.realtimeSinceStartup + 3f;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (player == null || player.gameObject == null)
                {
                    yield break;
                }

                var supervisingOfficer = Core.ResolveDynamicParoleOfficerManager()?.GetActiveSupervisingOfficer()
                                        ?? Core.Instance?.NpcManager?.GetSupervisingOfficer();
                var intakeStateMachine = supervisingOfficer != null
                    ? BBHelpers.GetComponentSafe<ParoleIntakeStateMachine>(supervisingOfficer.gameObject)
                    : null;

                if (intakeStateMachine != null)
                {
                    // This is idempotent while the dynamic parole manager is also bringing
                    // the officer online.  Starting here closes the release-summary timing
                    // window without bypassing the canonical behavior.
                    intakeStateMachine.StartParoleIntake(player);
                    if (intakeStateMachine.NotifyReleaseSummaryDismissed(player))
                    {
                        UnfreezePlayer(player);
                        ModLogger.Info($"ReleaseManager: Handed post-release flow to the canonical supervising officer intake escort for {player.name}");
                        yield break;
                    }

                    if (Time.realtimeSinceStartup >= nextWaitingLogTime)
                    {
                        ModLogger.Info($"ReleaseManager: Waiting for supervising officer to reach the police-station release door for {player.name}");
                        nextWaitingLogTime = Time.realtimeSinceStartup + 3f;
                    }
                }

                yield return null;
            }

            // Do not revive the retired manager-owned compliance sequence here.  It can
            // teleport the officer and leave the canonical intake state unable to resume.
            UnfreezePlayer(player);
            ModLogger.Error($"ReleaseManager: Canonical parole intake was unavailable for {handoffTimeoutSeconds:0}s after {player.name} dismissed the release summary");
        }

        /// <summary>
        /// Give the canonical supervising officer a bounded opportunity to begin parole intake
        /// while the release-summary UI is visible.  Failure only logs; it does not revive the
        /// legacy manager-owned compliance sequence.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator PrepareCanonicalParoleIntake(Player player)
        {
            const float preparationTimeoutSeconds = 8f;
            float deadline = Time.realtimeSinceStartup + preparationTimeoutSeconds;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (player == null || player.gameObject == null)
                {
                    yield break;
                }

                var supervisingOfficer = Core.ResolveDynamicParoleOfficerManager()?.GetActiveSupervisingOfficer()
                                        ?? Core.Instance?.NpcManager?.GetSupervisingOfficer();
                var intakeStateMachine = supervisingOfficer != null
                    ? BBHelpers.GetComponentSafe<ParoleIntakeStateMachine>(supervisingOfficer.gameObject)
                    : null;

                if (intakeStateMachine != null)
                {
                    intakeStateMachine.StartParoleIntake(player);
                    ModLogger.Info($"ReleaseManager: Supervising officer is approaching {player.name} while the parole conditions summary is visible");
                    yield break;
                }

                yield return null;
            }

            ModLogger.Warn($"ReleaseManager: Supervising officer was not ready to approach {player.name} while the parole conditions summary was visible");
        }

        #endregion

        #region Debug

        void Update()
        {
            if (releaseQueue.Count > 0 && Time.time >= _nextQueueProcessTime)
            {
                _nextQueueProcessTime = Time.time + 1f;
                ProcessQueuedReleases();
            }

            // Debug: Monitor active releases
            if (activeReleases.Count > 0 && Time.frameCount % 300 == 0) // Every 5 seconds
            {
                foreach (var kvp in activeReleases)
                {
                    ModLogger.Debug($"Active release: {kvp.Value.player?.name ?? kvp.Key} - Status: {kvp.Value.status}");
                }
            }
        }

        #endregion
    }
}
