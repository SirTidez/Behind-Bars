using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;



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
        public bool inventoryProcessed = false;
        
        public Texture2D mugshotImage;
        public string fingerprintData;
        public List<string> confiscatedItems = new List<string>();
        
        public MugshotStation mugshotStation;
        public ScannerStation scannerStation;
        public Transform inventoryDropOff;
        
        public bool requireBothStations = true;
        public bool allowAnyOrder = true;
        public float notificationDuration = 4f;
        
        private Player currentPlayer;
        private bool bookingInProgress = false;
        private static BookingProcess _instance;
        
        public static BookingProcess Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<BookingProcess>();
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
            
            ModLogger.Info($"BookingProcess initialized - Mugshot: {mugshotStation != null}, Scanner: {scannerStation != null}");
        }
        
        void FindBookingStations()
        {
            if (mugshotStation == null)
                mugshotStation = FindObjectOfType<MugshotStation>();
                
            if (scannerStation == null)
                scannerStation = FindObjectOfType<ScannerStation>();
                
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
        public void StartBooking(Player player)
        {
            if (bookingInProgress)
            {
                ModLogger.Warn($"Booking already in progress for {currentPlayer?.name}");
                return;
            }
            
            currentPlayer = player;
            bookingInProgress = true;
            
            // Reset booking status
            ResetBookingStatus();
            
            ModLogger.Info($"Starting booking process for player: {player.name}");
            
            // Show initial instructions
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Complete mugshot and fingerprint scan", 
                    NotificationType.Instruction
                );
            }
            
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
            
            // Show completion notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Booking complete! Proceed to Storage", 
                    NotificationType.Direction
                );
            }
            
            // Proceed to inventory phase
            MelonCoroutines.Start(StartInventoryPhase());
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
            
            // Notify jail system that booking is complete
            var jailSystem = Core.Instance?.JailSystem;
            if (jailSystem != null)
            {
                // TODO: Add method to JailSystem to handle booking completion
                ModLogger.Info("Notifying JailSystem of booking completion");
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
            
            // Show progress notification
            if (BehindBarsUIManager.Instance != null)
            {
                string message = mugshotComplete ? "All stations complete!" : "Fingerprint complete - take mugshot next";
                BehindBarsUIManager.Instance.ShowNotification(message, NotificationType.Progress);
            }
            
            CheckBookingCompletion();
        }
        
        void CheckBookingCompletion()
        {
            if (IsBookingComplete())
            {
                CompleteBooking();
            }
            else
            {
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
                return mugshotComplete && fingerprintComplete;
            }
            else
            {
                return mugshotComplete || fingerprintComplete;
            }
        }
        
        /// <summary>
        /// Reset booking status for new player
        /// </summary>
        void ResetBookingStatus()
        {
            mugshotComplete = false;
            fingerprintComplete = false;
            inventoryProcessed = false;
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
            
            // Add inventory task if needed
            if (IsBookingComplete())
            {
                string inventoryStatus = inventoryProcessed ? "✓" : "☐";
                tasks.Add($"{inventoryStatus} Inventory Drop-off");
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
            
            if (mugshotImage == null)
            {
                // Create dummy mugshot
                mugshotImage = new Texture2D(256, 256);
            }
            
            if (string.IsNullOrEmpty(fingerprintData))
            {
                fingerprintData = "TEST_FINGERPRINT_" + System.DateTime.Now.Ticks;
            }
            
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
            // Check for booking completion and handle guard door control
            if (IsBookingComplete() && !guardEscortTriggered)
            {
                guardEscortTriggered = true;
                
                // Delay guard action to make it feel more realistic
                MelonCoroutines.Start(DelayedGuardEscort());
            }
            
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
        public bool inventoryProcessed;
        public System.DateTime completionTime;
        public List<string> confiscatedItems;
    }
}