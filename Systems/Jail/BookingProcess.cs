using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems;
using BBHelpers = Behind_Bars.Helpers.Helpers;



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
        private DisciplinaryResumeStage disciplinaryResumeStage = DisciplinaryResumeStage.None;
        private static BookingProcess _instance;
        private readonly List<Coroutine> sceneCoroutineHandles = new List<Coroutine>();

        /// <summary>
        /// The next canonical intake destination after a disciplinary hold. This is an
        /// in-memory checkpoint for the active booking only; the individual completion
        /// flags remain the source of truth for the completed work.
        /// </summary>
        private enum DisciplinaryResumeStage
        {
            None,
            Mugshot,
            Fingerprint,
            Storage,
            CellEscort
        }

        // Events for state machine integration.  These remain public in the Mono
        // surface for backwards compatibility, but an IL2CPP-injected component
        // must not expose managed delegate fields in its native type metadata.
#if MONO
        public System.Action<Player> OnMugshotCompleted;
        public System.Action<Player> OnFingerprintCompleted;
        public System.Action<Player> OnInventoryDropOffCompleted;
        public System.Action<Player> OnBookingStarted;
        public System.Action<Player> OnBookingCompleted;
#else
        internal System.Action<Player> OnMugshotCompleted;
        internal System.Action<Player> OnFingerprintCompleted;
        internal System.Action<Player> OnInventoryDropOffCompleted;
        internal System.Action<Player> OnBookingStarted;
        internal System.Action<Player> OnBookingCompleted;
#endif

        public static BookingProcess Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Core.JailController?.BookingProcessController;
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
            
            ModLogger.Debug($"BookingProcess initialized - Mugshot: {mugshotStation != null}, Scanner: {scannerStation != null}, InventoryDropOff: {inventoryDropOffStation != null}");
        }
        
        void FindBookingStations()
        {
            if (mugshotStation == null)
                mugshotStation = BBHelpers.FindObjectOfTypeSafe<MugshotStation>();
                
            if (scannerStation == null)
                scannerStation = BBHelpers.FindObjectOfTypeSafe<ScannerStation>();
                
            if (inventoryDropOffStation == null)
            {
                inventoryDropOffStation = BBHelpers.FindObjectOfTypeSafe<InventoryDropOffStation>();

                // Disable the InventoryDropOffStation - we're replacing it with prison gear pickup
                if (inventoryDropOffStation != null)
                {
                    inventoryDropOffStation.gameObject.SetActive(false);
                    ModLogger.Debug("InventoryDropOffStation disabled - replaced by prison gear pickup system");
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
            // CRITICAL: Clean up any previous booking state first
            if (bookingInProgress && currentPlayer != null)
            {
                ModLogger.Warn($"Booking already in progress for {currentPlayer?.name} - forcing cleanup of old booking");

                // Clear old player's cell assignment BEFORE setting new player
                var cellManager = Core.ResolveCellAssignmentManager();
                if (cellManager != null)
                {
                    cellManager.ReleasePlayerFromCell(currentPlayer);
                    ModLogger.Info($"Cleared cell assignment for previous player: {currentPlayer.name}");
                }

                // Force cancel old booking
                bookingInProgress = false;
            }

            // Reset booking flags FIRST (before setting currentPlayer)
            mugshotComplete = false;
            fingerprintComplete = false;
            inventoryDropOffComplete = false;
            prisonGearPickupComplete = false;
            inventoryProcessed = false;
            escortRequested = false;
            escortInProgress = false;
            prisonGearEventFired = false;
            disciplinaryResumeStage = DisciplinaryResumeStage.None;
            mugshotImage = null;
            fingerprintData = null;
            confiscatedItems.Clear();

            // NOW set the new player and sentence
            currentPlayer = player;
            currentSentence = sentence;
            bookingInProgress = true;

            // IMPORTANT: Completely reset any previous jail timer from a prior arrest
            var uiWrapper = Core.ResolveUIManager().GetUIWrapper();
            if (uiWrapper != null)
            {
                float bookingBailAmount = ResolveBookingBailAmount();
                uiWrapper.ResetTimer(bookingBailAmount);
                ModLogger.Info($"Reset jail timer for new booking with immediate bail ${bookingBailAmount:F0}");
            }

            // Clear the NEW player's prison items flag (from previous arrest if any)
            var playerHandler = Behind_Bars.Core.GetPlayerHandler(currentPlayer);
            if (playerHandler?.RemoveConfiscatedItem("PRISON_ITEMS_RECEIVED") == true)
            {
                ModLogger.Info("Cleared PRISON_ITEMS_RECEIVED flag for new booking");
            }

            // NOTE: Do NOT clear persistent storage here - it contains the items we just confiscated!
            // Persistent storage is cleared AFTER successful release in ReleaseOfficer.FinalizeInventoryPickup()

            // CRITICAL: Reset the JailInventoryPickupStation for repeat arrests
            var jailController = Core.JailController;
            if (jailController != null)
            {
                // Find JailInventoryPickup GameObject and get its component
                var jailInventoryPickupTransform = jailController.transform.Find("Storage/JailInventoryPickup");
                if (jailInventoryPickupTransform != null)
                {
                    var jailInventoryStation = BBHelpers.GetComponentSafe<JailInventoryPickupStation>(jailInventoryPickupTransform.gameObject);
                    if (jailInventoryStation != null)
                    {
                        jailInventoryStation.ResetForNewInmate();
                        ModLogger.Info("Reset JailInventoryPickupStation for new inmate");
                    }
                    else
                    {
                        ModLogger.Warn("JailInventoryPickupStation component not found");
                    }
                }
                else
                {
                    ModLogger.Warn("JailInventoryPickup GameObject not found for reset");
                }

                // Hide the release-side personal belongings station during intake.
                if (jailController.storage?.inventoryPickup != null)
                {
                    var inventoryPickupStation = BBHelpers.GetComponentSafe<InventoryPickupStation>(jailController.storage.inventoryPickup.gameObject);
                    if (inventoryPickupStation != null)
                    {
                        inventoryPickupStation.ResetForBooking();
                    }
                    else if (jailController.storage.inventoryPickup.gameObject.activeSelf)
                    {
                        jailController.storage.inventoryPickup.gameObject.SetActive(false);
                        ModLogger.Info("Disabled InventoryPickup GameObject for booking intake");
                    }
                }
            }

            // CRITICAL: Cancel any active intake officer escort for previous arrest
            var npcManager = Core.Instance?.NpcManager;
            if (npcManager != null)
            {
                var intakeOfficerBehavior = npcManager.GetIntakeOfficer();
                if (intakeOfficerBehavior != null && intakeOfficerBehavior.IsProcessingIntake())
                {
                    ModLogger.Warn("Intake officer still processing from previous arrest - canceling old intake");

                    // Get the IntakeOfficerStateMachine component
                    var intakeOfficerStateMachine = BBHelpers.GetComponentSafe<IntakeOfficerStateMachine>(intakeOfficerBehavior.gameObject);
                    if (intakeOfficerStateMachine != null)
                    {
                        intakeOfficerStateMachine.CancelIntake();
                        ModLogger.Info("Canceled old intake officer process");
                    }
                    else
                    {
                        ModLogger.Warn("IntakeOfficerStateMachine component not found on intake officer");
                    }
                }
            }

            ModLogger.Info($"=== STARTING BOOKING PROCESS for {player.name} ===");

            // Trigger booking started event for state machine
            OnBookingStarted?.Invoke(player);

            // The OnBookingStarted event triggers IntakeOfficer.HandleBookingStarted which starts the escort
            // We need to verify the officer actually started processing before proceeding
            StartManagedCoroutine(VerifyAndMonitorEscort());

            // Update UI with task list
            UpdateTaskListUI();

            StartManagedCoroutine(MonitorBookingProgress());
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
            Core.ResolveUIManager().ShowNotification(
                    "Booking complete! Guard will take you to storage",
                    NotificationType.Progress
                );

            // Escort is already in progress, no need to request again
        }

        /// <summary>
        /// Finalizes an active booking after the canonical intake officer has secured the
        /// prisoner in their assigned cell. This is required for disciplinary resumes that
        /// begin directly at the final escort and therefore do not create the legacy escort
        /// monitor responsible for calling <see cref="FinishBooking"/>.
        /// </summary>
        public bool FinishBookingAfterCellEscort(Player player)
        {
            if (player == null || !bookingInProgress || currentPlayer != player)
            {
                return false;
            }

            if (!IsBookingComplete())
            {
                ModLogger.Error($"Cannot finish booking after cell escort for {player.name}: required booking steps are incomplete");
                return false;
            }

            var cellManager = Core.ResolveCellAssignmentManager();
            if (cellManager == null || cellManager.GetPlayerCellNumber(player) < 0)
            {
                ModLogger.Error($"Cannot finish booking after cell escort for {player.name}: the player has no confirmed assigned cell");
                return false;
            }

            ModLogger.Info($"Final-cell escort secured {player.name}; finishing active booking and starting sentence");
            FinishBooking();
            return true;
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
            var jailManager = Core.Instance?.JailManager;
            if (jailManager != null && currentSentence != null)
            {
                ModLogger.Info("Booking complete - starting UI timer countdown and jail time");

                float bailAmount = ResolveBookingBailAmount();
                var bailSystem = Core.ResolveBailSystem();

                // Start the UI timer countdown now that booking is complete
                var uiWrapper = Core.ResolveUIManager().GetUIWrapper();
                if (uiWrapper != null)
                {
                    uiWrapper.StartDynamicUpdates(currentSentence.JailTime, bailAmount);
                    
                    // Update the bail amount in the UI wrapper
                    if (bailAmount > 0)
                    {
                        uiWrapper.UpdateBailAmount(bailAmount);
                        ModLogger.Info($"[BAIL] Updated jail status UI bail amount to ${bailAmount:F0}");
                    }
                    
                    ModLogger.Info($"UI timer started: {currentSentence.JailTime}s jail time, ${bailAmount} bail");
                }

                // Show bail UI if player can afford it
                if (bailAmount > 0 && bailSystem != null && bailSystem.CanPlayerAffordBail(currentPlayer, bailAmount))
                {
                    Core.ResolveUIManager().ShowBailUI(bailAmount);
                    ModLogger.Info($"[BAIL] Showing bail UI for {currentPlayer.name}: ${bailAmount:F0}");
                }
                else if (bailAmount > 0)
                {
                    ModLogger.Info($"[BAIL] Player {currentPlayer.name} cannot afford bail of ${bailAmount:F0}");
                }

                // CRITICAL: Start the jail sentence coroutine to handle bail key press detection
                // This coroutine checks for the B key press and processes bail payments
                StartManagedCoroutine(jailManager.StartJailTimeAfterBooking(currentPlayer, currentSentence));
                ModLogger.Info($"[BAIL] Started jail sentence coroutine for bail key detection");
            }
            else if (currentSentence == null)
            {
                ModLogger.Warn("No jail sentence available - cannot start jail time");
            }
            
            // Show final notification
            Core.ResolveUIManager().ShowNotification(
                    "Processing complete", 
                    NotificationType.Progress
                );
            
            currentPlayer = null;
            currentSentence = null;
        }

        /// <summary>
        /// Resolves bail as soon as booking begins and stores the same value used when the
        /// sentence timer starts. This keeps the custody panel truthful during intake.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private float ResolveBookingBailAmount()
        {
            if (currentPlayer == null || currentSentence == null || currentSentence.FineAmount <= 0f)
            {
                return 0f;
            }

            var bailSystem = Core.ResolveBailSystem();
            if (bailSystem != null)
            {
                var bailOffer = bailSystem.CalculateBailAmount(currentPlayer, currentSentence.FineAmount);
                float bailAmount = Mathf.Max(0f, bailOffer.Amount);
                bailSystem.StoreBailAmount(currentPlayer, bailAmount);
                ModLogger.Info($"[BAIL] Calculated and stored immediate booking bail: ${bailAmount:F0} for {currentPlayer.name} (fine: ${currentSentence.FineAmount:F0})");
                return bailAmount;
            }

            var jailManager = Core.Instance?.JailManager;
            float fallbackAmount = jailManager != null
                ? Mathf.Max(0f, jailManager.CalculateBailAmount(currentSentence.FineAmount, currentSentence.Severity))
                : 0f;
            ModLogger.Warn($"[BAIL] BailSystem unavailable; immediate booking bail fallback is ${fallbackAmount:F0}");
            return fallbackAmount;
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
            string message = fingerprintComplete ? "All stations complete!" : "Mugshot complete - scan fingerprint next";
            Core.ResolveUIManager().ShowNotification(message, NotificationType.Progress);

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
            string message = mugshotComplete ? "Booking stations complete - proceed to storage!" : "Fingerprint complete - take mugshot next";
            Core.ResolveUIManager().ShowNotification(message, NotificationType.Progress);

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
            Core.ResolveUIManager().ShowNotification(
                    "Inventory secured - booking complete!",
                    NotificationType.Progress
                );

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
            Core.ResolveUIManager().ShowNotification(
                    "Prison gear issued - booking complete!",
                    NotificationType.Progress
                );

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
            disciplinaryResumeStage = DisciplinaryResumeStage.None;
            mugshotImage = null;
            fingerprintData = null;
            confiscatedItems.Clear();

            // CRITICAL: Clear prison items received flag for repeat arrests
            if (currentPlayer != null)
            {
                var playerHandler = Behind_Bars.Core.GetPlayerHandler(currentPlayer);
                if (playerHandler?.RemoveConfiscatedItem("PRISON_ITEMS_RECEIVED") == true)
                {
                    ModLogger.Info("Cleared PRISON_ITEMS_RECEIVED flag for repeat arrest");
                }

                // CRITICAL: Clear cell assignment from previous arrest to prevent early escort completion
                var cellManager = Core.ResolveCellAssignmentManager();
                if (cellManager != null)
                {
                    cellManager.ReleasePlayerFromCell(currentPlayer);
                    ModLogger.Info("Cleared cell assignment from previous arrest");
                }
            }
        }
        
        /// <summary>
        /// Update task list UI to show progress
        /// </summary>
        void UpdateTaskListUI()
        {
            var uiManager = Core.ResolveUIManager();
            
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
                var jailController = BBHelpers.FindObjectOfTypeSafe<JailController>();
                if (jailController == null)
                {
                    ModLogger.Warn("JailController not found for guard door control");
                    return;
                }
                
                // Open holding cell doors when booking is complete
                if (IsBookingComplete() || inventoryProcessed)
                {
                    ModLogger.Info("Booking complete - guards opening holding cell doors");
                    
                    // Only open the exact holding cell occupied by this prisoner. UnlockAll
                    // also clears unrelated cell locks and can leak an interrupted intake route.
                    try
                    {
                        int holdingCellIndex = currentPlayer != null
                            ? jailController.FindPlayerHoldingCell(currentPlayer)
                            : -1;
                        if (holdingCellIndex >= 0 && jailController.doorController != null)
                        {
                            if (jailController.doorController.UnlockAndOpenHoldingCellDoor(holdingCellIndex))
                            {
                                ModLogger.Info($"Guards opened holding cell {holdingCellIndex} for the active prisoner");
                            }
                            else
                            {
                                ModLogger.Warn($"Could not open holding cell {holdingCellIndex} for the active prisoner");
                            }
                        }
                        else
                        {
                            ModLogger.Warn("Guard door control skipped: active prisoner was not assigned to a holding cell");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Error unlocking doors: {ex.Message}");
                    }
                    
                    // Show notification that guards are escorting
                    Core.ResolveUIManager().ShowNotification(
                            "Guards are escorting you from holding", 
                            NotificationType.Progress
                        );
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
            if (currentPlayer == null)
            {
                ModLogger.Error("RequestGuardEscort: currentPlayer is null!");
                return;
            }

            if (escortRequested)
            {
                ModLogger.Warn($"Escort already requested for {currentPlayer.name} - skipping duplicate request");
                return;
            }

            escortRequested = true;

            ModLogger.Info($"=== REQUESTING GUARD ESCORT for {currentPlayer.name} ===");

            // Request escort from NpcManager
            var npcManager = Core.Instance?.NpcManager;
            if (npcManager != null)
            {
                ModLogger.Info($"NpcManager found - checking if intake officer is available...");
                bool isAvailable = npcManager.IsIntakeOfficerAvailable();
                ModLogger.Info($"Intake officer available: {isAvailable}");

                bool escortAssigned = npcManager.RequestPrisonerEscort(currentPlayer.gameObject);
                if (escortAssigned)
                {
                    escortInProgress = true;
                    ModLogger.Info($"✓ Guard escort SUCCESSFULLY assigned for {currentPlayer.name}");

                    Core.ResolveUIManager().ShowNotification(
                            "Guard is coming to escort you",
                            NotificationType.Progress
                        );

                    // Start monitoring escort progress
                    StartManagedCoroutine(MonitorEscortProgress());
                }
                else
                {
                    // Retry after a short delay
                    ModLogger.Warn("⚠ No guard available for escort - retrying in 2 seconds");
                    StartManagedCoroutine(RetryEscortRequest());
                }
            }
            else
            {
                // Fallback to old system
                ModLogger.Error("NpcManager not available - using fallback escort");
                StartManagedCoroutine(StartInventoryPhase());
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator VerifyAndMonitorEscort()
        {
            // Wait one frame for OnBookingStarted event handlers to complete
            yield return null;

            // Check if IntakeOfficer is now processing this player
            var npcManager = Core.Instance?.NpcManager;
            if (npcManager != null)
            {
                var intakeOfficer = npcManager.GetIntakeOfficer();
                if (intakeOfficer != null && intakeOfficer.IsProcessingIntake())
                {
                    // Officer is processing! Start monitoring
                    escortInProgress = true;
                    escortRequested = true;
                    ModLogger.Info($"✓ IntakeOfficer already processing via event system - starting escort monitoring");

                    Core.ResolveUIManager().ShowNotification(
                            "Guard is escorting you through booking",
                            NotificationType.Progress
                        );

                    StartManagedCoroutine(MonitorEscortProgress());
                }
                else
                {
                    // Officer didn't start - need to request escort manually
                    ModLogger.Warn("IntakeOfficer didn't respond to event - manually requesting escort");
                    RequestGuardEscort();
                }
            }
            else
            {
                // No NPC manager - use fallback
                ModLogger.Warn("NpcManager not available - using fallback");
                StartManagedCoroutine(StartInventoryPhase());
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator RetryEscortRequest()
        {
            int retryCount = 0;
            int maxRetries = 5; // Try 5 times over 10 seconds

            while (retryCount < maxRetries && currentPlayer != null)
            {
                yield return new WaitForSeconds(2f);
                retryCount++;

                ModLogger.Info($"Retrying guard escort request (attempt {retryCount}/{maxRetries})...");

                var npcManager = Core.Instance?.NpcManager;
                if (npcManager != null)
                {
                    bool escortAssigned = npcManager.RequestPrisonerEscort(currentPlayer.gameObject);
                    if (escortAssigned)
                    {
                        escortInProgress = true;
                        ModLogger.Info($"✓ Guard escort assigned on retry {retryCount} for {currentPlayer.name}");

                        Core.ResolveUIManager().ShowNotification(
                                "Guard is coming to escort you",
                                NotificationType.Progress
                            );

                        StartManagedCoroutine(MonitorEscortProgress());
                        yield break; // Success - exit retry loop
                    }
                    else
                    {
                        ModLogger.Warn($"⚠ Retry {retryCount} failed - officer still busy");
                    }
                }
            }

            // All retries failed - DO NOT use fallback for re-arrests, player should be stuck waiting
            // This prevents timer from starting prematurely
            ModLogger.Error($"⚠ Guard escort failed after {maxRetries} retries - player will wait for officer to become available");

            // Keep booking in progress but show message to player
            Core.ResolveUIManager().ShowNotification(
                    "Waiting for guard to become available...",
                    NotificationType.Instruction
                );
        }
        
        /// <summary>
        /// Monitor escort progress and handle completion
        /// </summary>
        private IEnumerator MonitorEscortProgress()
        {
            // Monitor indefinitely until escort is complete - no timeout
            while (escortInProgress)
            {
                // Check if escort is complete
                if (IsEscortComplete())
                {
                    CompleteEscortProcess();
                    yield break;
                }

                yield return new WaitForSeconds(2f);
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
            
            // Never start a sentence unless a final cell assignment succeeded.
            // The canonical intake path normally finalizes through
            // FinishBookingAfterCellEscort; this protects the legacy monitor too.
            if (AssignPlayerCell())
            {
                FinishBooking();
            }
            else
            {
                ModLogger.Error("Escort completed without a confirmed cell assignment; booking remains active for recovery");
            }
        }
        
        /// <summary>
        /// Assign a cell to the current player
        /// </summary>
        private bool AssignPlayerCell()
        {
            if (currentPlayer == null) return false;
            
            var cellManager = Core.ResolveCellAssignmentManager();
            if (cellManager != null)
            {
                int cellNumber = cellManager.AssignPlayerToCell(currentPlayer);
                if (cellNumber >= 0)
                {
                    ModLogger.Debug($"Player {currentPlayer.name} assigned to cell {cellNumber}");
                    
                    Core.ResolveUIManager().ShowNotification(
                            $"You have been assigned to cell {cellNumber}", 
                            NotificationType.Direction
                        );
                    return true;
                }
                else
                {
                    ModLogger.Error($"Failed to assign cell to {currentPlayer.name}");
                    return false;
                }
            }
            else
            {
                ModLogger.Warn("CellAssignmentManager not available");
                return false;
            }
        }
        
        private IEnumerator DelayedGuardEscort()
        {
            // Wait a moment for the last station to complete
            yield return new WaitForSeconds(2f);
            
            // Show guard escort notification
            Core.ResolveUIManager().ShowNotification(
                    "Guard: \"Booking complete. Follow me.\"", 
                    NotificationType.Direction
                );
            
            yield return new WaitForSeconds(1f);
            
            // Handle door control
            HandleGuardDoorControl();
        }

        // Debug/Testing methods
        void Update()
        {
            if (!Core.EnableDeveloperShortcuts)
            {
                return;
            }

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
        /// Check if booking is currently in progress (for timer checks)
        /// </summary>
        public bool IsBookingInProgress()
        {
            return bookingInProgress;
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
            // CRITICAL: Escort cannot be complete if booking isn't complete yet!
            // This prevents timer from starting before booking is done on repeat arrests
            if (!IsBookingComplete())
            {
                ModLogger.Debug("IsEscortComplete: Booking not complete yet, escort cannot be complete");
                return false;
            }

            // Escort is only complete when player is actually assigned to a cell AND in that cell
            if (currentPlayer == null) return true;

            // Check if player is properly assigned to a cell
            var cellManager = Core.ResolveCellAssignmentManager();
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
        /// Cancels scene-owned booking work before the Main scene unloads. This deliberately
        /// does not alter saved sentence data; it only releases volatile escorts, UI, and retries.
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

            bookingInProgress = false;
            escortRequested = false;
            escortInProgress = false;
            storageInteractionAllowed = false;
            disciplinaryResumeStage = DisciplinaryResumeStage.None;
            guardEscortTriggered = false;

            var intakeOfficer = Core.Instance?.NpcManager?.GetIntakeOfficer();
            var intakeStateMachine = intakeOfficer != null
                ? BBHelpers.GetComponentSafe<IntakeOfficerStateMachine>(intakeOfficer.gameObject)
                : null;
            intakeStateMachine?.CancelIntake();

            currentPlayer = null;
            currentSentence = null;
            ModLogger.Debug("BookingProcess cancelled for Main-scene exit");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Coroutine StartManagedCoroutine(IEnumerator routine)
        {
            var coroutine = MelonCoroutines.Start(routine) as Coroutine;
            if (coroutine != null)
            {
                sceneCoroutineHandles.Add(coroutine);
            }
            return coroutine;
        }

        private void OnDestroy()
        {
            CancelForSceneExit();
            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// Stops the active booking for a disciplinary hold while retaining the completed
        /// intake checkpoint. The resumed officer route starts at the first unfinished
        /// canonical station instead of resetting property, clothing, or prior scans.
        /// </summary>
        public bool SuspendForDisciplinaryHold(Player player)
        {
            if (player == null || currentPlayer != player || currentSentence == null)
            {
                return false;
            }

            disciplinaryResumeStage = ResolveDisciplinaryResumeStage();
            bookingInProgress = false;
            escortRequested = false;
            escortInProgress = false;
            storageInteractionAllowed = false;

            ModLogger.Warn(
                $"Suspended booking for {player.name} at disciplinary checkpoint {disciplinaryResumeStage}: " +
                $"mugshot={mugshotComplete}, fingerprint={fingerprintComplete}, " +
                $"prisonGear={prisonGearPickupComplete}, inventoryProcessed={inventoryProcessed}");
            return true;
        }

        /// <summary>
        /// Returns the next canonical intake destination for a disciplinary resume.
        /// Mugshot and fingerprint are deliberately ordered before storage even when a
        /// previous inventory implementation has already toggled a legacy item flag.
        /// </summary>
        private DisciplinaryResumeStage ResolveDisciplinaryResumeStage()
        {
            if (!mugshotComplete)
            {
                return DisciplinaryResumeStage.Mugshot;
            }

            if (!fingerprintComplete)
            {
                return DisciplinaryResumeStage.Fingerprint;
            }

            if (!prisonGearPickupComplete)
            {
                return DisciplinaryResumeStage.Storage;
            }

            return DisciplinaryResumeStage.CellEscort;
        }

        /// <summary>
        /// Resumes an interrupted intake after a disciplinary holding period. The caller
        /// uses the existing booking rather than StartBooking, because StartBooking clears
        /// the very checkpoint this path must preserve.
        /// </summary>
        public bool ResumeAfterDisciplinaryHold(Player player, float addedGameMinutes, string holdingCellName)
        {
            if (player == null || currentPlayer != player || currentSentence == null)
            {
                ModLogger.Error("Cannot resume disciplinary intake: active booking player or sentence was unavailable");
                return false;
            }

            var intakeOfficer = Core.Instance?.NpcManager?.GetIntakeOfficer();
            var intakeStateMachine = intakeOfficer != null
                ? BBHelpers.GetComponentSafe<IntakeOfficerStateMachine>(intakeOfficer.gameObject)
                : null;
            if (intakeStateMachine == null || !intakeStateMachine.PrepareDisciplinaryRepeatIntake(player, holdingCellName))
            {
                ModLogger.Error($"Cannot resume disciplinary intake: intake officer could not bind {player.name} to {holdingCellName}");
                return false;
            }

            currentSentence.JailTime += Mathf.Max(0f, addedGameMinutes);
            bookingInProgress = true;
            escortRequested = false;
            escortInProgress = false;
            storageInteractionAllowed = false;

            // The flags and current sentence remain intact. OnBookingStarted re-enters the
            // canonical officer flow from the punishment holding cell; the state machine
            // then advances to the checkpoint captured above after the player steps out.
            OnBookingStarted?.Invoke(player);
            UpdateTaskListUI();
            StartManagedCoroutine(MonitorBookingProgress());

            ModLogger.Info(
                $"Resuming intake for {player.name} after disciplinary hold from {holdingCellName}; " +
                $"next={disciplinaryResumeStage}, added {addedGameMinutes:F0} game minutes");
            disciplinaryResumeStage = DisciplinaryResumeStage.None;
            return true;
        }

        /// <summary>
        /// Debug method to check intake officer status
        /// </summary>
        private void DebugIntakeOfficerStatus()
        {
            ModLogger.Info("=== INTAKE OFFICER DEBUG ===");

            var npcManager = Core.Instance?.NpcManager;
            if (npcManager == null)
            {
                ModLogger.Error("NpcManager is NULL!");
                return;
            }

            var intakeOfficer = npcManager.GetIntakeOfficer();
            if (intakeOfficer == null)
            {
                ModLogger.Error("No intake officer found!");

                // Check all registered guards
                var guards = npcManager.GetRegisteredGuards();
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
                ModLogger.Info($"  Available: {npcManager.IsIntakeOfficerAvailable()}");
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
