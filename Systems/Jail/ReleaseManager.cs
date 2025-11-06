using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Players;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems.CrimeTracking;

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
                    MelonLogger.Msg("[ReleaseManager] Creating singleton instance");
                    var go = new GameObject("ReleaseManager");
                    go.SetActive(true); // Ensure GameObject is active
                    _instance = go.AddComponent<ReleaseManager>();
                    DontDestroyOnLoad(go);
                    MelonLogger.Msg("[ReleaseManager] Singleton instance created successfully");
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
            MelonLogger.Msg("[ReleaseManager] Awake() called");
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
                MelonLogger.Msg("[ReleaseManager] Instance set in Awake()");
            }
            else if (_instance != this)
            {
                MelonLogger.Msg("[ReleaseManager] Destroying duplicate instance");
                Destroy(gameObject);
                return;
            }
        }

        void Start()
        {
            MelonLogger.Msg("[ReleaseManager] Start() called, beginning initialization");
            InitializeExitPoint();
            InitializeOfficers();
            ModLogger.Info("ReleaseManager initialized");
            MelonLogger.Msg("[ReleaseManager] Initialization complete");
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
                        ModLogger.Info($"Found prison exit point at {prisonExitPoint.position}");
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

            ModLogger.Info($"ReleaseManager found {availableOfficers.Count} existing release officers");
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
                            ModLogger.Info($"✅ Added ReleaseOfficerBehavior to existing Booking1 guard: {releaseOfficer.GetBadgeNumber()}");
                        }
                    }
                    else
                    {
                        // Already has release officer behavior
                        if (!availableOfficers.Contains(existingReleaseOfficer))
                        {
                            availableOfficers.Add(existingReleaseOfficer);
                        }
                        ModLogger.Info($"✅ Using existing release officer: {existingReleaseOfficer.GetBadgeNumber()}");
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
                    ModLogger.Info($"Forcing cleanup of stuck release for {player.name} - Reason: {cleanupReason}, Age: {(DateTime.Now - existingRequest.releaseTime).TotalMinutes:F1} minutes, Officer: {existingRequest.assignedOfficer?.GetBadgeNumber() ?? "none"}");
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

            ModLogger.Info($"Initiating {releaseType} release for {player.name}");

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
                ModLogger.Info($"Release for {player.name} queued - no officers available");

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
            ModLogger.Info($"Emergency release for {player.name}");

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
            ModLogger.Info($"TryAssignOfficer: Looking for available officers - total officers: {availableOfficers.Count}");

            foreach (var officer in availableOfficers)
            {
                ModLogger.Info($"TryAssignOfficer: Checking officer {officer?.GetBadgeNumber()} - Available: {officer?.IsAvailable()}");

                if (officer != null && officer.IsAvailable())
                {
                    request.assignedOfficer = officer;
                    officer.SetAvailable(false);

                    // Subscribe to officer events
                    officer.OnEscortCompleted += (player) => HandleEscortCompleted(request, player);
                    officer.OnEscortFailed += (player, reason) => HandleEscortFailed(request, player, reason);
                    officer.OnStatusUpdate += (player, state) => HandleStatusUpdate(request, player, state);

                    ModLogger.Info($"SUCCESS: Assigned officer {officer.GetBadgeNumber()} to release {request.player.name}");
                    return true;
                }
            }

            ModLogger.Warn($"TryAssignOfficer: No available officers found in pool of {availableOfficers.Count} officers");

            // If no officer available, try to create one NOW (on-demand)
            if (availableOfficers.Count == 0)
            {
                ModLogger.Info("No release officers available - creating one on-demand");
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

                        ModLogger.Info($"Assigned newly created officer {officer.GetBadgeNumber()} to release {request.player.name}");
                        return true;
                    }
                }
            }

            return false;
        }

        private void StartReleaseProcess(ReleaseRequest request)
        {
            ModLogger.Info($"Starting release process for {request.player.name}");
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
            ModLogger.Info($"Starting Release Officer state machine for {request.player.name}");
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

            ModLogger.Info($"Release process coroutine completed with status: {request.status}");
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

            ModLogger.Info($"Waiting for {request.player.name} to move away from storage area (>{exitDistance}m from {storageLocation})");

            if (request.assignedOfficer != null)
            {
                // Tell the officer to stay at storage and wait
                request.assignedOfficer.ChangeReleaseState(ReleaseOfficerBehavior.ReleaseState.WaitingAtStorage);
                ModLogger.Info($"Officer {request.assignedOfficer.GetBadgeNumber()} waiting at storage for player to exit");
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
            ModLogger.Info($"Completing release for {request.player.name}");
            request.status = ReleaseStatus.Completed;

            // CRITICAL: Clear ALL escort registrations for this player to prevent conflicts
            OfficerCoordinator.Instance.UnregisterAllEscortsForPlayer(request.player);

            // Teleport player to freedom
            var exitRotation = GetPlayerExitRotation();
            request.player.transform.position = request.exitPosition;
            request.player.transform.rotation = Quaternion.Euler(exitRotation);

            // Complete player release (restore systems, clear flags)
            CompletePlayerRelease(request.player, request.releaseType);

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

            ModLogger.Info($"Release completed for {request.player.name} - all escorts cleared");
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

            ModLogger.Info($"Release failed for {request.player.name} - all escorts cleared");
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
                    ModLogger.Info($"Cleared persistent storage snapshot after successful release for {player.name}");
                }

                // Start parole supervision for time-served releases (not bail releases)
                if (releaseType == ReleaseType.TimeServed)
                {
                    StartParoleForReleasedPlayer(player);
                }
                else
                {
                    ModLogger.Info($"Player {player.name} released on bail - no parole supervision");
                }

                ModLogger.Info($"Player release completed for {player.name} via {releaseType} - all systems cleared");

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
                ModLogger.Info($"[PAROLE] === Processing Parole for Released Player: {player.name} ===");

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
                    float pausedRemainingTime = rapSheet.CurrentParoleRecord.GetPausedRemainingTime();
                    ModLogger.Info($"[PAROLE] Player has paused parole with {pausedRemainingTime}s ({pausedRemainingTime / 60f:F1} minutes) remaining");

                    // Calculate additional time from new crimes
                    float additionalTime = CalculateAdditionalParoleTime(rapSheet, rapSheetLoaded);

                    // Add violation penalties
                    int violationCount = rapSheet.CurrentParoleRecord.GetViolationCount();
                    float violationPenalty = CalculateViolationPenalty(violationCount);

                    ModLogger.Info($"[PAROLE] Additional time from new crimes: {additionalTime}s ({additionalTime / 60f:F1} minutes)");
                    ModLogger.Info($"[PAROLE] Violation penalty: {violationPenalty}s ({violationPenalty / 60f:F1} minutes) for {violationCount} violations");

                    // Extend the paused parole
                    float totalAdditional = additionalTime + violationPenalty;
                    rapSheet.CurrentParoleRecord.ExtendPausedParole(totalAdditional);

                    float newTotalTime = pausedRemainingTime + totalAdditional;
                    float newGameDays = (newTotalTime / 60f) / 24f;
                    ModLogger.Info($"[PAROLE] New total parole time: {newTotalTime}s ({newTotalTime / 60f:F1} real minutes / {newGameDays:F1} game days)");

                    // Resume parole
                    rapSheet.CurrentParoleRecord.ResumeParole();
                    ModLogger.Info($"[PAROLE] ✓ Paused parole resumed and extended for {player.name}");
                }
                else
                {
                    // START NEW parole term
                    float paroleDuration = CalculateParoleDuration(rapSheet, rapSheetLoaded);
                    float gameHours = paroleDuration / 60f;
                    float gameDays = gameHours / 24f;

                    ModLogger.Info($"[PAROLE] Starting new parole term: {paroleDuration}s ({paroleDuration / 60f:F1} real minutes / {gameDays:F1} game days)");
                    if (rapSheet != null)
                    {
                        ModLogger.Info($"[PAROLE] Crime count: {rapSheet.GetCrimeCount()}, LSI Level: {rapSheet.LSILevel}");
                    }

                    // Start parole through ParoleSystem
                    var paroleSystem = Core.Instance?.ParoleSystem;
                    if (paroleSystem != null)
                    {
                        paroleSystem.StartParole(player, paroleDuration);
                        ModLogger.Info($"[PAROLE] ✓ New parole started successfully for {player.name}");
                    }
                    else
                    {
                        ModLogger.Error($"[PAROLE] ✗ ParoleSystem not available - cannot start parole");
                    }
                }

                ModLogger.Info($"[PAROLE] === Parole Processing Complete ===");
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
        /// <returns>Parole duration in seconds</returns>
        private float CalculateParoleDuration(RapSheet rapSheet, bool hasRapSheet)
        {
            // Game time scale: 1 hour = 60s, 1 day = 24 minutes (1440s), 1 week = 168 minutes (10080s)
            // Parole terms scaled to game time for realism
            const float BASE_PAROLE_TIME = 2880f;  // 2 game days (48 real minutes)
            const float MIN_PAROLE_TIME = 1440f;   // 1 game day (24 real minutes)
            const float MAX_PAROLE_TIME = 10080f;  // 1 game week / 7 days (168 real minutes / 2.8 hours)
            const float CRIME_MULTIPLIER = 240f;   // 4 game hours per crime (4 real minutes)

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
            ModLogger.Debug($"[PAROLE CALC] Base: {BASE_PAROLE_TIME / 60f:F1} min (2 days) + Crime bonus: {crimeCount} crimes × {CRIME_MULTIPLIER / 60f:F1} min = +{crimeGameHours:F1} hours");

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

            float gameDaysBeforeClamp = (paroleDuration / 60f) / 24f;
            ModLogger.Debug($"[PAROLE CALC] LSI Level: {rapSheet.LSILevel} (×{lsiMultiplier}) = {gameDaysBeforeClamp:F1} game days");

            // Clamp to reasonable bounds
            paroleDuration = Mathf.Clamp(paroleDuration, MIN_PAROLE_TIME, MAX_PAROLE_TIME);

            float finalGameDays = (paroleDuration / 60f) / 24f;
            ModLogger.Debug($"[PAROLE CALC] Final duration: {paroleDuration / 60f:F1} real minutes ({finalGameDays:F1} game days)");

            return paroleDuration;
        }

        /// <summary>
        /// Calculate additional parole time from new crimes (without base time or LSI modifier)
        /// Used when extending paused parole
        /// </summary>
        private float CalculateAdditionalParoleTime(RapSheet rapSheet, bool hasRapSheet)
        {
            const float CRIME_MULTIPLIER = 240f;   // 4 game hours per crime (4 real minutes)

            if (!hasRapSheet)
            {
                return 0f;
            }

            // Just calculate crime-based time, without base or LSI modifier
            // This represents the additional burden of new crimes
            int crimeCount = rapSheet.GetCrimeCount();
            float additionalTime = crimeCount * CRIME_MULTIPLIER;

            ModLogger.Debug($"[PAROLE CALC] Additional time for {crimeCount} new crimes: {additionalTime}s ({additionalTime / 60f:F1} minutes)");

            return additionalTime;
        }

        /// <summary>
        /// Calculate penalty time for parole violations
        /// Each violation adds additional supervision time
        /// </summary>
        private float CalculateViolationPenalty(int violationCount)
        {
            const float VIOLATION_PENALTY = 480f;  // 8 game hours per violation (8 real minutes)

            float penalty = violationCount * VIOLATION_PENALTY;

            ModLogger.Debug($"[PAROLE CALC] Violation penalty for {violationCount} violations: {penalty}s ({penalty / 60f:F1} minutes)");

            return penalty;
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
                ModLogger.Info($"Registered release officer: {officer.GetBadgeNumber()}");
            }
        }

        public void UnregisterOfficer(ReleaseOfficerBehavior officer)
        {
            if (availableOfficers.Contains(officer))
            {
                availableOfficers.Remove(officer);
                ModLogger.Info($"Unregistered release officer: {officer.GetBadgeNumber()}");
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
                    ModLogger.Info($"Cancelling active release for {player.name} (Status: {request.status})");

                    // Free up the assigned officer
                    if (request.assignedOfficer != null)
                    {
                        request.assignedOfficer.SetAvailable(true);
                        ModLogger.Info($"Freed officer {request.assignedOfficer.GetBadgeNumber()} from cancelled release");
                    }

                    // Remove from active releases
                    activeReleases.Remove(player);
                    ModLogger.Info($"Removed {player.name} from active releases");
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
                    ModLogger.Info($"Notified Release Officer {request.assignedOfficer.GetBadgeNumber()} that inventory pickup is complete");
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
                    ModLogger.Info($"Notified Release Officer {request.assignedOfficer.GetBadgeNumber()} of player confirmation");
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
                ModLogger.Info($"Exit scan completed for {player.name} - completing entire release process");

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

            ModLogger.Info($"Release Officer completed escort for {player.name}");
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
                ModLogger.Info($"Release status updated for {player.name}: {newStatus}");
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