using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems;



#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Coordinates the entire booking process for players entering jail
    /// </summary>
    public class BookingProcess : MonoBehaviour
    {
#if !MONO
        public BookingProcess(System.IntPtr ptr) : base(ptr) { }
#endif
        
        public bool mugshotComplete = false;
        public bool fingerprintComplete = false;
        public bool inventoryDropOffComplete = false; // Deprecated - kept for compatibility
        public bool prisonGearPickupComplete = false; // NEW: Required step
        public bool inventoryProcessed = false;

        private bool prisonGearEventFired = false; // Prevent duplicate event firing
        
        public Texture2D mugshotImage;
        public string fingerprintData;
        public List<string> confiscatedItems = new List<string>();
        
        public MugshotStation mugshotStation;
        public ScannerStation scannerStation;
        public InventoryDropOffStation inventoryDropOffStation;
        public Transform inventoryDropOff;
        
        public bool requireBothStations = true;
        public bool allowAnyOrder = true;
        public float notificationDuration = 4f;
        
        private Player currentPlayer;
        private JailSystem.JailSentence currentSentence;
        public bool bookingInProgress = false;
        private bool escortRequested = false;
        private bool escortInProgress = false;
        public bool storageInteractionAllowed = false;
        private static BookingProcess _instance;

        // Events for state machine integration
        public System.Action<Player> OnMugshotCompleted;
        public System.Action<Player> OnFingerprintCompleted;
        public System.Action<Player> OnInventoryDropOffCompleted;
        public System.Action<Player> OnBookingStarted;
        public System.Action<Player> OnBookingCompleted;

        public static BookingProcess Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Core.JailController.BookingProcessController;
                }
                return _instance;
            }
        }
        
        void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
                return;
            }
        }
        
        void Start()
        {
            // Find booking stations if not assigned
            FindBookingStations();
            
            ModLogger.Info($"BookingProcess initialized - Mugshot: {mugshotStation != null}, Scanner: {scannerStation != null}, InventoryDropOff: {inventoryDropOffStation != null}");
        }
        
        void FindBookingStations()
        {
            if (mugshotStation == null)
                mugshotStation = FindObjectOfType<MugshotStation>();
                
            if (scannerStation == null)
                scannerStation = FindObjectOfType<ScannerStation>();
                
            if (inventoryDropOffStation == null)
            {
                inventoryDropOffStation = FindObjectOfType<InventoryDropOffStation>();

                // Disable the InventoryDropOffStation - we're replacing it with prison gear pickup
                if (inventoryDropOffStation != null)
                {
                    inventoryDropOffStation.gameObject.SetActive(false);
                    ModLogger.Info("InventoryDropOffStation disabled - replaced by prison gear pickup system");
                }
            }
                
            // Find inventory drop-off point
            if (inventoryDropOff == null)
            {
                // Look for Booking_StorageDoor in the scene hierarchy
                GameObject storage = GameObject.Find("Booking_StorageDoor");
                if (storage != null)
                {
                    inventoryDropOff = storage.transform;
                }
            }
        }
        
        /// <summary>
        /// Start booking process for a player
        /// </summary>
        public void StartBooking(Player player, JailSystem.JailSentence sentence = null)
        {
            if (bookingInProgress)
            {
                ModLogger.Warn($"Booking already in progress for {currentPlayer?.name}");
                return;
            }

            currentPlayer = player;
            currentSentence = sentence;
            bookingInProgress = true;
            
            // Reset booking status
            ResetBookingStatus();
            
            ModLogger.Info($"Starting booking process for player: {player.name}");

            // Trigger booking started event for state machine
            OnBookingStarted?.Invoke(player);

            // Request guard escort immediately when booking starts
            RequestGuardEscort();
            
            // Update UI with task list
            UpdateTaskListUI();
            
            MelonCoroutines.Start(MonitorBookingProgress());
        }
        
        /// <summary>
        /// Complete booking process and proceed to next phase
        /// </summary>
        public void CompleteBooking()
        {
            if (!IsBookingComplete())
            {
                ModLogger.Warn("Attempted to complete booking but requirements not met");
                return;
            }
            
            ModLogger.Info($"Booking completed for {currentPlayer?.name}");

            // Trigger booking completed event for state machine
            OnBookingCompleted?.Invoke(currentPlayer);

            // Show completion notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Booking complete! Guard will take you to storage",
                    NotificationType.Progress
                );
            }

            // Escort is already in progress, no need to request again
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator StartInventoryPhase()
        {
            yield return new WaitForSeconds(1f);
            
            // Guide player to storage area
            if (inventoryDropOff != null)
            {
                // Create waypoint or guide system here
                ModLogger.Info($"Guiding player to inventory drop-off at: {inventoryDropOff.position}");
            }
            
            // TODO: Implement inventory drop-off interaction
            // For now, just mark as complete
            yield return new WaitForSeconds(3f);
            inventoryProcessed = true;
            
            // Finish booking
            FinishBooking();
        }
        
        /// <summary>
        /// Finish entire booking process and return player to jail system
        /// </summary>
        void FinishBooking()
        {
            ModLogger.Info($"Booking process finished for {currentPlayer?.name}");
            
            // Clear booking state
            bookingInProgress = false;
            
            // Notify jail system that booking is complete and start jail time
            var jailSystem = Core.Instance?.JailSystem;
            if (jailSystem != null && currentSentence != null)
            {
                ModLogger.Info("Booking complete - starting UI timer countdown and jail time");

                // Start the UI timer countdown now that booking is complete
                if (BehindBarsUIManager.Instance != null && BehindBarsUIManager.Instance.GetUIWrapper() != null)
                {
                    float bailAmount = jailSystem.CalculateBailAmount(currentSentence.FineAmount, currentSentence.Severity);
                    BehindBarsUIManager.Instance.GetUIWrapper().StartDynamicUpdates(currentSentence.JailTime, bailAmount);
                    ModLogger.Info($"UI timer started: {currentSentence.JailTime}s jail time, ${bailAmount} bail");
                }

                // No longer need the separate jail time coroutine since UI timer will handle it
                // MelonCoroutines.Start(jailSystem.StartJailTimeAfterBooking(currentPlayer, currentSentence));
            }
            else if (currentSentence == null)
            {
                ModLogger.Warn("No jail sentence available - cannot start jail time");
            }
            
            // Show final notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Processing complete", 
                    NotificationType.Progress
                );
            }
            
            currentPlayer = null;
            currentSentence = null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator MonitorBookingProgress()
        {
            while (bookingInProgress && !IsBookingComplete())
            {
                // Update progress periodically
                yield return new WaitForSeconds(2f);
                
                // Update task list UI
                UpdateTaskListUI();
                
                // Check if player completed both stations
                if (mugshotComplete && fingerprintComplete && requireBothStations)
                {
                    CompleteBooking();
                    yield break;
                }
            }
        }
        
        /// <summary>
        /// Mark mugshot as complete
        /// </summary>
        public void SetMugshotComplete(Texture2D mugshot)
        {
            mugshotComplete = true;
            mugshotImage = mugshot;
            
            ModLogger.Info("Mugshot marked as complete");

            // Trigger mugshot completed event for state machine
            OnMugshotCompleted?.Invoke(currentPlayer);

            // Show progress notification
            if (BehindBarsUIManager.Instance != null)
            {
                string message = fingerprintComplete ? "All stations complete!" : "Mugshot complete - scan fingerprint next";
                BehindBarsUIManager.Instance.ShowNotification(message, NotificationType.Progress);
            }

            CheckBookingCompletion();
        }
        
        /// <summary>
        /// Mark fingerprint as complete
        /// </summary>
        public void SetFingerprintComplete(string fingerprintId)
        {
            fingerprintComplete = true;
            fingerprintData = fingerprintId;
            
            ModLogger.Info("Fingerprint scan marked as complete");

            // Trigger fingerprint completed event for state machine
            OnFingerprintCompleted?.Invoke(currentPlayer);

            // Show progress notification
            if (BehindBarsUIManager.Instance != null)
            {
                string message = mugshotComplete ? "Booking stations complete - proceed to storage!" : "Fingerprint complete - take mugshot next";
                BehindBarsUIManager.Instance.ShowNotification(message, NotificationType.Progress);
            }

            CheckBookingCompletion();
        }
        
        /// <summary>
        /// Mark inventory drop-off as complete
        /// </summary>
        public void SetInventoryDropOffComplete()
        {
            inventoryDropOffComplete = true;
            
            ModLogger.Info("Inventory drop-off marked as complete");

            // Trigger inventory drop-off completed event for state machine
            OnInventoryDropOffCompleted?.Invoke(currentPlayer);

            // Show progress notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Inventory secured - booking complete!",
                    NotificationType.Progress
                );
            }

            // Mark overall inventory as processed
            inventoryProcessed = true;

            CheckBookingCompletion();
        }

        /// <summary>
        /// Mark prison gear pickup as complete
        /// </summary>
        public void SetPrisonGearPickupComplete()
        {
            ModLogger.Info("SetPrisonGearPickupComplete() called!");
            prisonGearPickupComplete = true;

            ModLogger.Info($"Prison gear pickup marked as complete! New state - Mugshot: {mugshotComplete}, Fingerprint: {fingerprintComplete}, Prison Gear: {prisonGearPickupComplete}");

            // Fire event only once to prevent duplicate handling
            if (!prisonGearEventFired)
            {
                prisonGearEventFired = true;
                OnInventoryDropOffCompleted?.Invoke(currentPlayer);
                ModLogger.Info("IntakeOfficer: Fired OnInventoryDropOffCompleted event for prison gear completion");
            }
            else
            {
                ModLogger.Debug("IntakeOfficer: Prison gear event already fired, skipping duplicate");
            }

            // Show progress notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Prison gear issued - booking complete!",
                    NotificationType.Progress
                );
            }

            ModLogger.Info("Calling CheckBookingCompletion()...");
            CheckBookingCompletion();
        }

        void CheckBookingCompletion()
        {
            ModLogger.Info($"CheckBookingCompletion() - IsBookingComplete: {IsBookingComplete()}");
            if (IsBookingComplete())
            {
                ModLogger.Info("Booking is complete! Calling CompleteBooking()");
                CompleteBooking();
            }
            else
            {
                ModLogger.Info($"Booking not complete - Mugshot: {mugshotComplete}, Fingerprint: {fingerprintComplete}, Prison Gear: {prisonGearPickupComplete}");
                UpdateTaskListUI();
            }
        }
        
        /// <summary>
        /// Check if booking requirements are met
        /// </summary>
        public bool IsBookingComplete()
        {
            if (requireBothStations)
            {
                // Require mugshot, fingerprint, AND prison gear pickup
                return mugshotComplete && fingerprintComplete && prisonGearPickupComplete;
            }
            else
            {
                // Require either mugshot or fingerprint, AND prison gear pickup
                return (mugshotComplete || fingerprintComplete) && prisonGearPickupComplete;
            }
        }
        
        /// <summary>
        /// Reset booking status for new player
        /// </summary>
        void ResetBookingStatus()
        {
            mugshotComplete = false;
            fingerprintComplete = false;
            inventoryDropOffComplete = false;
            prisonGearPickupComplete = false; // Reset the new flag
            inventoryProcessed = false;
            escortRequested = false;
            escortInProgress = false;
            prisonGearEventFired = false; // Reset event flag
            mugshotImage = null;
            fingerprintData = null;
            confiscatedItems.Clear();
        }
        
        /// <summary>
        /// Update task list UI to show progress
        /// </summary>
        void UpdateTaskListUI()
        {
            if (BehindBarsUIManager.Instance == null) return;
            
            List<string> tasks = new List<string>();
            
            // Add mugshot task
            string mugshotStatus = mugshotComplete ? "✓" : "☐";
            tasks.Add($"{mugshotStatus} Mugshot");
            
            // Add fingerprint task
            string fingerprintStatus = fingerprintComplete ? "✓" : "☐";
            tasks.Add($"{fingerprintStatus} Fingerprint Scan");
            
            // Add prison gear pickup task (required after other stations)
            if (mugshotComplete && fingerprintComplete)
            {
                string gearStatus = prisonGearPickupComplete ? "✓" : "☐";
                tasks.Add($"{gearStatus} Prison Gear Pickup");
            }
            else if (mugshotComplete || fingerprintComplete)
            {
                string gearStatus = prisonGearPickupComplete ? "✓" : "☐";
                tasks.Add($"{gearStatus} Prison Gear Pickup");
            }
            
            // Show task list (would need to implement this in UI manager)
            // BehindBarsUIManager.Instance.ShowTaskList(tasks);
        }
        
        /// <summary>
        /// Get booking summary for records
        /// </summary>
        public BookingSummary GetBookingSummary()
        {
            return new BookingSummary
            {
                playerName = currentPlayer?.name ?? "Unknown",
                mugshotCaptured = mugshotComplete,
                fingerprintScanned = fingerprintComplete,
                inventoryDropOffComplete = inventoryDropOffComplete,
                inventoryProcessed = inventoryProcessed,
                completionTime = System.DateTime.Now,
                confiscatedItems = new List<string>(confiscatedItems)
            };
        }
        
        /// <summary>
        /// Force complete booking (for testing)
        /// </summary>
        public void ForceCompleteBooking()
        {
            mugshotComplete = true;
            fingerprintComplete = true;
            prisonGearPickupComplete = true; // Set the new required flag
            // inventoryDropOffComplete = true; // No longer required
            
            if (mugshotImage == null)
            {
                // Create dummy mugshot
                mugshotImage = new Texture2D(256, 256);
            }
            
            if (string.IsNullOrEmpty(fingerprintData))
            {
                fingerprintData = "TEST_FINGERPRINT_" + System.DateTime.Now.Ticks;
            }
            
            // Add dummy confiscated items
            confiscatedItems.Add("Test Item 1");
            confiscatedItems.Add("Test Item 2");
            
            CompleteBooking();
            ModLogger.Info("Booking force-completed for testing");
        }
        
        /// <summary>
        /// Handle automatic door control for guards
        /// </summary>
        public void HandleGuardDoorControl()
        {
            try
            {
                // Find the jail controller to access door controls
                var jailController = UnityEngine.Object.FindObjectOfType<JailController>();
                if (jailController == null)
                {
                    ModLogger.Warn("JailController not found for guard door control");
                    return;
                }
                
                // Open holding cell doors when booking is complete
                if (IsBookingComplete() || inventoryProcessed)
                {
                    ModLogger.Info("Booking complete - guards opening holding cell doors");
                    
                    // Use the door system to open doors
                    // This simulates what guards would do
                    // Use the public UnlockAll method which will unlock holding cell doors
                    try
                    {
                        var jailController_cast = jailController as JailController;
                        if (jailController_cast != null)
                        {
                            jailController_cast.UnlockAll();
                            ModLogger.Info("✓ Guards unlocked all holding cell doors");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Error unlocking doors: {ex.Message}");
                    }
                    
                    // Show notification that guards are escorting
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            "Guards are escorting you from holding", 
                            NotificationType.Progress
                        );
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error in guard door control: {ex.Message}");
            }
        }
        
        private bool guardEscortTriggered = false;
        
        /// <summary>
        /// Request guard escort for prisoner
        /// </summary>
        private void RequestGuardEscort()
        {
            if (escortRequested || currentPlayer == null)
            {
                return;
            }
            
            escortRequested = true;
            
            // Request escort from PrisonNPCManager
            if (PrisonNPCManager.Instance != null)
            {
                bool escortAssigned = PrisonNPCManager.Instance.RequestPrisonerEscort(currentPlayer.gameObject);
                if (escortAssigned)
                {
                    escortInProgress = true;
                    ModLogger.Info($"Guard escort requested for {currentPlayer.name}");
                    
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            "Guard is coming to escort you", 
                            NotificationType.Progress
                        );
                    }
                    
                    // Start monitoring escort progress
                    MelonCoroutines.Start(MonitorEscortProgress());
                }
                else
                {
                    // Fallback to old system if no guard available
                    ModLogger.Warn("No guard available for escort - using fallback");
                    MelonCoroutines.Start(StartInventoryPhase());
                }
            }
            else
            {
                // Fallback to old system
                ModLogger.Warn("PrisonNPCManager not available - using fallback escort");
                MelonCoroutines.Start(StartInventoryPhase());
            }
        }
        
        /// <summary>
        /// Monitor escort progress and handle completion
        /// </summary>
        private IEnumerator MonitorEscortProgress()
        {
            float escortStartTime = Time.time;
            float maxEscortTime = 300f; // 5 minutes maximum
            
            while (escortInProgress && Time.time - escortStartTime < maxEscortTime)
            {
                // Check if escort is complete
                if (IsEscortComplete())
                {
                    CompleteEscortProcess();
                    yield break;
                }
                
                yield return new WaitForSeconds(2f);
            }
            
            // Timeout - complete anyway
            if (escortInProgress)
            {
                ModLogger.Warn($"Escort timed out for {currentPlayer?.name}");
                CompleteEscortProcess();
            }
        }
        
        /// <summary>
        /// Complete the escort process
        /// </summary>
        private void CompleteEscortProcess()
        {
            escortInProgress = false;
            inventoryProcessed = true;
            
            ModLogger.Info($"Escort process completed for {currentPlayer?.name}");
            
            // Assign cell and finish booking
            AssignPlayerCell();
            FinishBooking();
        }
        
        /// <summary>
        /// Assign a cell to the current player
        /// </summary>
        private void AssignPlayerCell()
        {
            if (currentPlayer == null) return;
            
            var cellManager = CellAssignmentManager.Instance;
            if (cellManager != null)
            {
                int cellNumber = cellManager.AssignPlayerToCell(currentPlayer);
                if (cellNumber >= 0)
                {
                    ModLogger.Info($"Player {currentPlayer.name} assigned to cell {cellNumber}");
                    
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            $"You have been assigned to cell {cellNumber}", 
                            NotificationType.Direction
                        );
                    }
                }
                else
                {
                    ModLogger.Error($"Failed to assign cell to {currentPlayer.name}");
                }
            }
            else
            {
                ModLogger.Warn("CellAssignmentManager not available");
            }
        }
        
        private IEnumerator DelayedGuardEscort()
        {
            // Wait a moment for the last station to complete
            yield return new WaitForSeconds(2f);
            
            // Show guard escort notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Guard: \"Booking complete. Follow me.\"", 
                    NotificationType.Direction
                );
            }
            
            yield return new WaitForSeconds(1f);
            
            // Handle door control
            HandleGuardDoorControl();
        }

        // Debug/Testing methods
        void Update()
        {
            // Escort is now triggered immediately when booking starts, not on completion
            
            // Debug commands
            if (Input.GetKeyDown(KeyCode.F1) && Input.GetKey(KeyCode.LeftShift))
            {
                if (currentPlayer == null && Player.Local != null)
                {
                    StartBooking(Player.Local);
                }
            }
            
            if (Input.GetKeyDown(KeyCode.F2) && Input.GetKey(KeyCode.LeftShift))
            {
                ForceCompleteBooking();
            }

            // Debug: Check intake officer status
            if (Input.GetKeyDown(KeyCode.F3) && Input.GetKey(KeyCode.LeftShift))
            {
                DebugIntakeOfficerStatus();
            }
        }
        
        /// <summary>
        /// Check if prisoner needs escort (for guards to query)
        /// </summary>
        public bool NeedsPrisonerEscort()
        {
            return bookingInProgress && IsBookingComplete() && !escortRequested;
        }
        
        /// <summary>
        /// Get the current prisoner for escort (for guards)
        /// </summary>
        public GameObject GetPrisonerForEscort()
        {
            return currentPlayer?.gameObject;
        }
        
        /// <summary>
        /// Check if escort is complete (for guards)
        /// </summary>
        public bool IsEscortComplete()
        {
            // Escort is only complete when player is actually assigned to a cell AND in that cell
            if (currentPlayer == null) return true;

            // Check if player is properly assigned to a cell
            var cellManager = CellAssignmentManager.Instance;
            if (cellManager != null)
            {
                int assignedCell = cellManager.GetPlayerCellNumber(currentPlayer);
                if (assignedCell >= 0)
                {
                    // Check if player is actually IN the assigned cell
                    var jailController = Core.JailController;
                    if (jailController != null)
                    {
                        bool isInCell = jailController.IsPlayerInJailCellBounds(currentPlayer, assignedCell);
                        ModLogger.Debug($"IsEscortComplete: Player assigned to cell {assignedCell}, in cell: {isInCell}");
                        return isInCell;
                    }
                }
            }

            // Not assigned to cell or not in cell = escort not complete
            return false;
        }

        /// <summary>
        /// Get the current player being processed (for state machine)
        /// </summary>
        public Player GetCurrentPlayer()
        {
            return currentPlayer;
        }

        /// <summary>
        /// Debug method to check intake officer status
        /// </summary>
        private void DebugIntakeOfficerStatus()
        {
            ModLogger.Info("=== INTAKE OFFICER DEBUG ===");

            if (PrisonNPCManager.Instance == null)
            {
                ModLogger.Error("PrisonNPCManager.Instance is NULL!");
                return;
            }

            var intakeOfficer = PrisonNPCManager.Instance.GetIntakeOfficer();
            if (intakeOfficer == null)
            {
                ModLogger.Error("No intake officer found!");

                // Check all registered guards
                var guards = PrisonNPCManager.Instance.GetRegisteredGuards();
                ModLogger.Info($"Total registered guards: {guards.Count}");

                foreach (var guard in guards)
                {
                    if (guard != null)
                    {
                        ModLogger.Info($"  Guard: {guard.GetBadgeNumber()} - Role: {guard.GetRole()} - Assignment: {guard.GetAssignment()}");
                    }
                }
            }
            else
            {
                ModLogger.Info($"✓ Intake officer found: {intakeOfficer.GetBadgeNumber()}");
                ModLogger.Info($"  Available: {PrisonNPCManager.Instance.IsIntakeOfficerAvailable()}");
                ModLogger.Info($"  Processing: {intakeOfficer.IsProcessingIntake()}");
            }
        }

        /// <summary>
        /// Check if player is currently in holding cell bounds
        /// </summary>
        public bool IsPlayerInHoldingCell(Player player = null)
        {
            if (player == null) player = currentPlayer;
            if (player == null) return false;

            // Find holding cell bounds
            GameObject holdingBounds = GameObject.Find("HoldingCell/Bounds");
            if (holdingBounds == null)
            {
                // Try alternative naming
                holdingBounds = GameObject.Find("HoldingCell_Bounds");
            }

            if (holdingBounds == null)
            {
                ModLogger.Warn("Could not find holding cell bounds for checking player position");
                return false;
            }

            var collider = holdingBounds.GetComponent<BoxCollider>();
            if (collider == null)
            {
                ModLogger.Warn("No BoxCollider found on holding cell bounds");
                return false;
            }

            return collider.bounds.Contains(player.transform.position);
        }

        /// <summary>
        /// Check if player has exited holding cell bounds
        /// </summary>
        public bool HasPlayerExitedHoldingCell(Player player = null)
        {
            return !IsPlayerInHoldingCell(player);
        }
    }
    
    /// <summary>
    /// Summary of booking process for records
    /// </summary>
    [System.Serializable]
    public class BookingSummary
    {
        public string playerName;
        public bool mugshotCaptured;
        public bool fingerprintScanned;
        public bool inventoryDropOffComplete;
        public bool inventoryProcessed;
        public System.DateTime completionTime;
        public List<string> confiscatedItems;
    }
}