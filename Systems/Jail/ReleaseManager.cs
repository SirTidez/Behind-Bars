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
using Behind_Bars.Systems;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.PlayerScripts;
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
        public static ReleaseManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    ModLogger.Debug("[ReleaseManager] Creating singleton instance");
                    var go = new GameObject("ReleaseManager");
                    go.SetActive(true); // Ensure GameObject is active
                    _instance = go.AddComponent<ReleaseManager>();
                    DontDestroyOnLoad(go);
                    ModLogger.Debug("[ReleaseManager] Singleton instance created successfully");
                }
                return _instance;
            }
        }

        #endregion

        #region Release Types

        public enum ReleaseType
        {
            TimeServed,
            BailPayment,
            CourtOrder,
            Emergency
        }

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

        [System.Serializable]
        public class ReleaseRequest
        {
            public Player player;
            public ReleaseType releaseType;
            public ReleaseStatus status;
            public DateTime releaseTime;
            public float bailAmount;
            public string releaseReason;
            public Vector3 exitPosition;
            public bool inventoryProcessed;
            public List<string> itemsToReturn = new List<string>();
            public ReleaseOfficerBehavior assignedOfficer;

            public ReleaseRequest(Player player, ReleaseType type, string reason = "")
            {
                this.player = player;
                this.releaseType = type;
                this.releaseReason = reason;
                this.releaseTime = DateTime.Now;
                this.status = ReleaseStatus.NotStarted;
                this.inventoryProcessed = false;
            }
        }

        #endregion

        #region Fields

        public Transform prisonExitPoint; // Set in inspector or found automatically
        public float releaseProcessTimeout = 300f; // 5 minutes max

        // Active releases being processed
        private Dictionary<Player, ReleaseRequest> activeReleases = new Dictionary<Player, ReleaseRequest>();

        // Queue for releases when guards are busy
        private Queue<ReleaseRequest> releaseQueue = new Queue<ReleaseRequest>();

        // Available release officers
        private List<ReleaseOfficerBehavior> availableOfficers = new List<ReleaseOfficerBehavior>();

        #endregion

        #region Events

        public static System.Action<Player, ReleaseType> OnReleaseStarted;
        public static System.Action<Player, ReleaseType> OnReleaseCompleted;
        public static System.Action<Player, string> OnReleaseFailed;

        #endregion

        #region Initialization

        void Awake()
        {
            ModLogger.Debug("[ReleaseManager] Awake() called");
            if (_instance == null)
            {
                _instance = this;
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
                    // Look for a designated exit point
                    var exitTransform = jailController.transform.Find("PrisonExit");
                    if (exitTransform != null)
                    {
                        prisonExitPoint = exitTransform;
                        ModLogger.Debug($"Found prison exit point at {prisonExitPoint.position}");
                    }
                    else
                    {
                        // Create a default exit point outside the jail
                        var defaultExit = new GameObject("DefaultPrisonExit");
                        defaultExit.transform.position = new Vector3(0, 1, 0); // Adjust as needed
                        prisonExitPoint = defaultExit.transform;
                        ModLogger.Warn("Created default prison exit point - should be configured properly");
                    }
                }
            }
        }

        private void InitializeOfficers()
        {
            // Find existing release officers
            var existingOfficers = FindObjectsOfType<ReleaseOfficerBehavior>();
            foreach (var officer in existingOfficers)
            {
                if (officer.IsAvailable())
                {
                    availableOfficers.Add(officer);
                }
            }

            // DON'T create release officers during initialization to avoid interfering with intake guards
            // Release officers will be created on-demand when actually needed

            ModLogger.Debug($"ReleaseManager found {availableOfficers.Count} existing release officers");
        }

        private void CreateReleaseOfficerFromGuardSpawn()
        {
            var jailController = Core.JailController;
            if (jailController?.booking?.guardSpawns != null && jailController.booking.guardSpawns.Count > 1)
            {
                var guardSpawn = jailController.booking.guardSpawns[1]; // Use second guard spawn
                ModLogger.Info($"Looking for existing guard at booking spawn [1]: {guardSpawn.position}");

                // First, look for any existing guard near guardSpawn[1] (Booking1 assignment)
                var allGuards = FindObjectsOfType<PrisonGuard>();
                PrisonGuard booking1Guard = null;

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
                    // Use the existing guard - just add ReleaseOfficerBehavior component
                    var existingReleaseOfficer = booking1Guard.GetComponent<ReleaseOfficerBehavior>();
                    if (existingReleaseOfficer == null)
                    {
                        // Add ReleaseOfficerBehavior to existing guard
                        var releaseOfficer = booking1Guard.gameObject.AddComponent<ReleaseOfficerBehavior>();
                        if (releaseOfficer != null)
                        {
                            releaseOfficer.SetAvailable(true);
                            availableOfficers.Add(releaseOfficer);
                            ModLogger.Debug($"✅ Added ReleaseOfficerBehavior to existing Booking1 guard: {releaseOfficer.GetBadgeNumber()}");
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
                    ModLogger.Warn("No Booking1 guard found - release system will not work properly");
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
        /// Start the release process for a player
        /// </summary>
        public bool InitiateRelease(Player player, ReleaseType releaseType, float bailAmount = 0f, string reason = "")
        {
            if (player == null)
            {
                ModLogger.Error("Cannot initiate release for null player");
                return false;
            }

            if (activeReleases.ContainsKey(player))
            {
                var existingRequest = activeReleases[player];
                ModLogger.Warn($"Release already in progress for {player.name} with status {existingRequest.status}");

                // If the existing release is stuck (older than 1 minute) or officer is missing, force clean it up
                bool isStuck = (DateTime.Now - existingRequest.releaseTime).TotalMinutes > 1;
                bool officerMissing = existingRequest.assignedOfficer == null;
                bool officerIdle = existingRequest.assignedOfficer?.GetCurrentState() == ReleaseOfficerBehavior.ReleaseState.Idle;

                if (isStuck || officerMissing || officerIdle)
                {
                    string cleanupReason = isStuck ? "timeout" : officerMissing ? "missing officer" : "officer idle";
                    ModLogger.Debug($"Forcing cleanup of stuck release for {player.name} - Reason: {cleanupReason}, Age: {(DateTime.Now - existingRequest.releaseTime).TotalMinutes:F1} minutes, Officer: {existingRequest.assignedOfficer?.GetBadgeNumber() ?? "none"}");
                    FailRelease(existingRequest, $"Stuck release cleanup: {cleanupReason}");
                }
                else
                {
                    return false;
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
                activeReleases[player] = releaseRequest;
                StartReleaseProcess(releaseRequest);
                OnReleaseStarted?.Invoke(player, releaseType);
                return true;
            }
            else
            {
                // Queue the release if no officers available
                releaseQueue.Enqueue(releaseRequest);
                ModLogger.Debug($"Release for {player.name} queued - no officers available");

                // Notify player they're being processed
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Release processing - please wait",
                        NotificationType.Instruction
                    );
                }
                return true;
            }
        }

        /// <summary>
        /// Emergency release (skips normal process)
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

            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Emergency release completed",
                    NotificationType.Progress
                );
            }
        }

        #endregion

        #region Release Process Management

        private bool TryAssignOfficer(ReleaseRequest request)
        {
            ModLogger.Debug($"TryAssignOfficer: Looking for available officers - total officers: {availableOfficers.Count}");

            foreach (var officer in availableOfficers)
            {
                ModLogger.Debug($"TryAssignOfficer: Checking officer {officer?.GetBadgeNumber()} - Available: {officer?.IsAvailable()}");

                if (officer != null && officer.IsAvailable())
                {
                    request.assignedOfficer = officer;
                    officer.SetAvailable(false);

                    // Subscribe to officer events
                    officer.OnEscortCompleted += (player) => HandleEscortCompleted(request, player);
                    officer.OnEscortFailed += (player, reason) => HandleEscortFailed(request, player, reason);
                    officer.OnStatusUpdate += (player, state) => HandleStatusUpdate(request, player, state);

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

                // Try again with newly created officer
                foreach (var officer in availableOfficers)
                {
                    if (officer != null && officer.IsAvailable())
                    {
                        request.assignedOfficer = officer;
                        officer.SetAvailable(false);

                        // Subscribe to officer events
                        officer.OnEscortCompleted += (player) => HandleEscortCompleted(request, player);
                        officer.OnEscortFailed += (player, reason) => HandleEscortFailed(request, player, reason);
                        officer.OnStatusUpdate += (player, state) => HandleStatusUpdate(request, player, state);

                        ModLogger.Debug($"Assigned newly created officer {officer.GetBadgeNumber()} to release {request.player.name}");
                        return true;
                    }
                }
            }

            return false;
        }

        private void StartReleaseProcess(ReleaseRequest request)
        {
            ModLogger.Debug($"Starting release process for {request.player.name}");
            request.status = ReleaseStatus.GuardDispatched;

            // Start the escort sequence
            if (request.assignedOfficer != null)
            {
                MelonCoroutines.Start(ReleaseProcessCoroutine(request));
            }
            else
            {
                FailRelease(request, "No officer assigned");
            }
        }

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
        }

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
                request.assignedOfficer.ChangeReleaseState(ReleaseOfficerBehavior.ReleaseState.WaitingAtStorage);
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

        private void CompleteRelease(ReleaseRequest request)
        {
            ModLogger.Debug($"Completing release for {request.player.name}");
            request.status = ReleaseStatus.Completed;

            // CRITICAL: Hide officer command notification before teleporting player
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.HideOfficerCommand();
                ModLogger.Debug($"Hidden officer command notification for {request.player.name}");
            }

            // CRITICAL: Clear ALL escort registrations for this player to prevent conflicts
            OfficerCoordinator.Instance.UnregisterAllEscortsForPlayer(request.player);

            // Teleport player to freedom
            var exitRotation = GetPlayerExitRotation();
            request.player.transform.position = request.exitPosition;
            request.player.transform.rotation = Quaternion.Euler(exitRotation);

            // Complete player release (restore systems, clear flags, START PAROLE TIMER)
            // Parole term timer starts immediately here
            CompletePlayerRelease(request.player, request.releaseType);

            // Show parole conditions UI and wait for acknowledgment (if player will be on parole)
            var rapSheet = RapSheetManager.Instance.GetRapSheet(request.player);
            bool willBeOnParole = rapSheet != null && (rapSheet.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole());

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
                MelonCoroutines.Start(WaitForParoleConditionsAcknowledgment(request.player, bailAmountPaid, fineAmount, termLength, lsiLevel, lsiBreakdown, jailTimeInfo, recentCrimes, generalConditions, specialConditions));
            }

            // Release the officer
            if (request.assignedOfficer != null)
            {
                request.assignedOfficer.SetAvailable(true);
                request.assignedOfficer.ReturnToPost();
            }

            // Clean up
            activeReleases.Remove(request.player);

            // Process queued releases
            ProcessQueuedReleases();

            // Fire event
            OnReleaseCompleted?.Invoke(request.player, request.releaseType);

            ModLogger.Debug($"Release completed for {request.player.name} - all escorts cleared");
        }

        private void FailRelease(ReleaseRequest request, string reason)
        {
            ModLogger.Error($"Release failed for {request.player.name}: {reason}");
            request.status = ReleaseStatus.Failed;

            // CRITICAL: Clear ALL escort registrations for this player to prevent conflicts
            OfficerCoordinator.Instance.UnregisterAllEscortsForPlayer(request.player);

            // Release the officer
            if (request.assignedOfficer != null)
            {
                request.assignedOfficer.SetAvailable(true);
                request.assignedOfficer.ReturnToPost();
            }

            // Clean up
            activeReleases.Remove(request.player);

            // Process queued releases
            ProcessQueuedReleases();

            // Fire event
            OnReleaseFailed?.Invoke(request.player, reason);
            // Notify player
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    $"Release failed: {reason}",
                    NotificationType.Warning
                );
            }

            ModLogger.Debug($"Release failed for {request.player.name} - all escorts cleared");
        }

        private void ProcessQueuedReleases()
        {
            if (releaseQueue.Count > 0)
            {
                var nextRelease = releaseQueue.Dequeue();
                if (TryAssignOfficer(nextRelease))
                {
                    activeReleases[nextRelease.player] = nextRelease;
                    StartReleaseProcess(nextRelease);
                    OnReleaseStarted?.Invoke(nextRelease.player, nextRelease.releaseType);
                }
                else
                {
                    // Put it back in queue if still no officers available
                    releaseQueue.Enqueue(nextRelease);
                }
            }
        }

        #endregion

        #region Utility Methods

        private Vector3 GetPlayerExitPosition(Player player)
        {
            // Use specific prison exit coordinates
            return new Vector3(13.7402f, 1.4857f, 38.1558f);
        }

        private Vector3 GetPlayerExitRotation()
        {
            return new Vector3(0f, 80.1529f, 0f); // Facing away from wall/pillar
        }

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
                var pickupStation = FindObjectOfType<InventoryPickupStation>();
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
                    playerMovement.canMove = true;
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
                var jailSystem = Core.Instance?.JailSystem;
                if (jailSystem != null)
                {
                    jailSystem.ClearPlayerJailStatus(player);
                }

                // Update player handler
                var playerHandler = Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    if (releaseType == ReleaseType.BailPayment)
                    {
                        var releaseRequest = activeReleases.ContainsKey(player) ? activeReleases[player] : null;
                        playerHandler.OnReleasedOnBail(releaseRequest?.bailAmount ?? 0f);
                    }
                    else
                    {
                        playerHandler.OnReleasedFromJail(0f); // Time served is tracked elsewhere
                    }
                }

                // CRITICAL: Clear persistent storage snapshot AFTER successful release (not during storage phase)
                var persistentData = Behind_Bars.Systems.Data.PersistentPlayerData.Instance;
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
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
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

                    // Extend the paused parole
                    float totalAdditional = additionalTime + violationPenalty;
                    rapSheet.CurrentParoleRecord.ExtendPausedParole(totalAdditional);

                    float newTotalTime = pausedRemainingTime + totalAdditional;
                    float newGameDays = newTotalTime / (60f * 24f); // Convert game minutes to game days
                    ModLogger.Debug($"[PAROLE] New total parole time: {newTotalTime} game minutes ({newGameDays:F1} game days / {GameTimeManager.FormatGameTime(newTotalTime)})");

                    // Resume parole
                    rapSheet.CurrentParoleRecord.ResumeParole();
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
                    var paroleSystem = Core.Instance?.ParoleSystem;
                    if (paroleSystem != null)
                    {
                        paroleSystem.StartParole(player, paroleDuration, showUI: false);
                        ModLogger.Debug($"[PAROLE] ✓ New parole started successfully for {player.name}");
                    }
                    else
                    {
                        ModLogger.Error($"[PAROLE] ✗ ParoleSystem not available - cannot start parole");
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
        private float CalculateParoleDuration(RapSheet rapSheet, bool hasRapSheet)
        {
            // Game time scale: 1 game minute = 1 real second, 1 game hour = 60 game minutes, 1 game day = 1440 game minutes
            // Parole terms are calculated in game time units
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
        /// Calculate additional parole time from new crimes (without base time or LSI modifier)
        /// Used when extending paused parole
        /// </summary>
        /// <returns>Additional time in game minutes</returns>
        private float CalculateAdditionalParoleTime(RapSheet rapSheet, bool hasRapSheet)
        {
            const float CRIME_MULTIPLIER = 240f;   // 4 game hours per crime (240 game minutes)

            if (!hasRapSheet)
            {
                return 0f;
            }

            // Just calculate crime-based time, without base or LSI modifier
            // This represents the additional burden of new crimes
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
                    var bailSystem = new BailSystem();
                    bailAmountPaid = bailSystem.GetBailAmount(player);
                    
                    // If stored amount is 0 but release type is BailPayment, try to get from active release request
                    if (bailAmountPaid == 0f && activeReleases.ContainsKey(player))
                    {
                        bailAmountPaid = activeReleases[player].bailAmount;
                    }
                }
                // If releaseType is not BailPayment, bailAmountPaid remains 0 (timed out)
                
                // Get fine amount and rap sheet
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
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
                float originalSentenceTime = JailTimeTracker.Instance.GetOriginalSentenceTime(player);
                float timeServed = JailTimeTracker.Instance.GetTimeServed(player);
                
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
                        // Get crimes added recently (within last 2 hours)
                        DateTime twoHoursAgo = DateTime.Now.AddHours(-2);
                        var recentCrimeInstances = allCrimes.Where(c => c != null && c.Timestamp >= twoHoursAgo)
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
                ViolationType.Other => "Parole Violation",
                _ => "Parole Violation"
            };
        }

        /// <summary>
        /// Split conditions into general (for everyone) and special (based on crimes/criteria)
        /// </summary>
        private (List<string> generalConditions, List<string> specialConditions) SplitConditionsIntoGeneralAndSpecial(Player player, RapSheet rapSheet, List<string> allConditions)
        {
            var generalConditions = new List<string>
            {
                "Report to parole officer as required",
                "No possession of illegal items",
                "Comply with all search requests",
                "Remain within designated areas"
            };
            
            var specialConditions = new List<string>();
            
            // If we have a rap sheet, check for specific crimes that warrant special conditions
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
                        
                        // Check for drug-related crimes
                        if (lowerName.Contains("drug") || lowerName.Contains("possession") || lowerName.Contains("trafficking"))
                        {
                            hasDrugCrimes = true;
                        }
                        
                        // Check for violent crimes
                        if (lowerName.Contains("assault") || lowerName.Contains("murder") || lowerName.Contains("manslaughter") || 
                            lowerName.Contains("violence") || lowerName.Contains("battery"))
                        {
                            hasViolentCrimes = true;
                        }
                        
                        // Check for theft crimes
                        if (lowerName.Contains("theft") || lowerName.Contains("burglary") || lowerName.Contains("robbery") || 
                            lowerName.Contains("steal") || lowerName.Contains("larceny"))
                        {
                            hasTheftCrimes = true;
                        }
                        
                        // Check for weapon crimes
                        if (lowerName.Contains("weapon") || lowerName.Contains("firearm") || lowerName.Contains("gun") || 
                            lowerName.Contains("brandish") || lowerName.Contains("discharge"))
                        {
                            hasWeaponCrimes = true;
                        }
                    }
                    
                    // Add special conditions based on crime types
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
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
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
                    playerMovement.canMove = false;
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
                    playerMovement.canMove = true;
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

        /// <summary>
        /// Wait for player to acknowledge parole conditions UI
        /// Freezes player, shows UI, waits for dismissal, then starts grace period
        /// </summary>
        private IEnumerator WaitForParoleConditionsAcknowledgment(Player player, float bailAmountPaid, float fineAmount, float termLengthGameMinutes, 
            LSILevel lsiLevel, (int totalScore, int crimeCountScore, int severityScore, int violationScore, int pastParoleScore, LSILevel resultingLevel) lsiBreakdown,
            (float originalSentenceTime, float timeServed) jailTimeInfo, List<string> recentCrimes, List<string> generalConditions, List<string> specialConditions)
        {
            bool hasError = false;
            
            try
            {
                // Freeze player
                FreezePlayer(player);

                // Show UI with all release summary data
                BehindBarsUIManager.Instance.ShowParoleConditionsUI(player, bailAmountPaid, fineAmount, termLengthGameMinutes, lsiLevel, lsiBreakdown, jailTimeInfo, recentCrimes, generalConditions, specialConditions);
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

            // Hide UI and start grace period
            try
            {
                BehindBarsUIManager.Instance.HideParoleConditionsUI();

                // IMPORTANT: Grace period was already started when release completed
                // No need to record release time again - it's already recorded
                // Parole term timer is already running from teleportation,
                // and grace period for searches started immediately upon release

                // Unfreeze player
                UnfreezePlayer(player);

                // Show parole status UI now that release summary UI is dismissed
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowParoleStatus();
                    ModLogger.Debug($"Showing parole status UI for {player.name} after release summary dismissal");
                }
                
                ModLogger.Info($"Parole conditions acknowledged by {player.name} - parole status UI shown");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error in WaitForParoleConditionsAcknowledgment cleanup: {ex.Message}");
                // Ensure player is unfrozen even on error
                UnfreezePlayer(player);
            }
        }

        #endregion

        #region Public Interface

        public bool IsReleaseInProgress(Player player)
        {
            return activeReleases.ContainsKey(player);
        }

        public ReleaseStatus GetReleaseStatus(Player player)
        {
            return activeReleases.ContainsKey(player) ? activeReleases[player].status : ReleaseStatus.NotStarted;
        }

        public void RegisterOfficer(ReleaseOfficerBehavior officer)
        {
            if (!availableOfficers.Contains(officer))
            {
                availableOfficers.Add(officer);
                ModLogger.Debug($"Registered release officer: {officer.GetBadgeNumber()}");
            }
        }

        public void UnregisterOfficer(ReleaseOfficerBehavior officer)
        {
            if (availableOfficers.Contains(officer))
            {
                availableOfficers.Remove(officer);
                ModLogger.Debug($"Unregistered release officer: {officer.GetBadgeNumber()}");
            }
        }

        /// <summary>
        /// Cancel any active release process for a player (used during new arrest)
        /// </summary>
        public void CancelPlayerRelease(Player player)
        {
            try
            {
                if (activeReleases.ContainsKey(player))
                {
                    var request = activeReleases[player];
                    ModLogger.Debug($"Cancelling active release for {player.name} (Status: {request.status})");

                    // Free up the assigned officer
                    if (request.assignedOfficer != null)
                    {
                        request.assignedOfficer.SetAvailable(true);
                        ModLogger.Debug($"Freed officer {request.assignedOfficer.GetBadgeNumber()} from cancelled release");
                    }

                    // Remove from active releases
                    activeReleases.Remove(player);
                    ModLogger.Debug($"Removed {player.name} from active releases");
                }
                else
                {
                    ModLogger.Debug($"No active release found for {player.name} - nothing to cancel");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error cancelling release for {player.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by InventoryPickupStation when inventory processing is complete
        /// </summary>
        public void OnInventoryProcessingComplete(Player player)
        {
            if (activeReleases.ContainsKey(player))
            {
                var request = activeReleases[player];
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
        /// Called when player confirms they are ready to proceed (NEW)
        /// </summary>
        public void OnPlayerConfirmationReceived(Player player)
        {
            if (activeReleases.ContainsKey(player))
            {
                var request = activeReleases[player];
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
        /// Called when exit scan is completed (NEW)
        /// </summary>
        public void OnExitScanCompleted(Player player)
        {
            if (activeReleases.ContainsKey(player))
            {
                var request = activeReleases[player];
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
        private void HandleEscortCompleted(ReleaseRequest request, Player player)
        {
            if (request.player != player)
            {
                ModLogger.Warn($"HandleEscortCompleted: Player mismatch - expected {request.player.name}, got {player?.name}");
                return;
            }

            ModLogger.Debug($"Release Officer completed escort for {player.name}");
            CompleteRelease(request);
        }

        /// <summary>
        /// Called when a Release Officer fails an escort
        /// </summary>
        private void HandleEscortFailed(ReleaseRequest request, Player player, string reason)
        {
            if (request.player != player)
            {
                ModLogger.Warn($"HandleEscortFailed: Player mismatch - expected {request.player.name}, got {player?.name}");
                return;
            }

            ModLogger.Error($"Release Officer failed escort for {player.name}: {reason}");
            FailRelease(request, reason);
        }

        /// <summary>
        /// Called when a Release Officer updates their status during escort
        /// </summary>
        private void HandleStatusUpdate(ReleaseRequest request, Player player, ReleaseOfficerBehavior.ReleaseState officerState)
        {
            if (request.player != player)
            {
                ModLogger.Warn($"HandleStatusUpdate: Player mismatch - expected {request.player.name}, got {player?.name}");
                return;
            }

            // Map Release Officer states to ReleaseManager statuses
            ReleaseStatus newStatus = officerState switch
            {
                ReleaseOfficerBehavior.ReleaseState.EscortingToStorage => ReleaseStatus.EscortingToStorage,
                ReleaseOfficerBehavior.ReleaseState.WaitingAtStorage => ReleaseStatus.InventoryProcessing,
                ReleaseOfficerBehavior.ReleaseState.EscortingToExitScanner => ReleaseStatus.EscortingToExit,
                ReleaseOfficerBehavior.ReleaseState.WaitingForExitScan => ReleaseStatus.EscortingToExit,
                _ => request.status // Keep current status if no mapping
            };

            if (newStatus != request.status)
            {
                request.status = newStatus;
                ModLogger.Debug($"Release status updated for {player.name}: {newStatus}");
            }
        }

        #endregion

        #region Debug

        void Update()
        {
            // Debug: Monitor active releases
            if (activeReleases.Count > 0 && Time.frameCount % 300 == 0) // Every 5 seconds
            {
                foreach (var kvp in activeReleases)
                {
                    ModLogger.Debug($"Active release: {kvp.Key.name} - Status: {kvp.Value.status}");
                }
            }
        }

        #endregion
    }
}