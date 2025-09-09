using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;


#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Handles player inventory confiscation and jail gear assignment during booking
    /// </summary>
    public class InventoryDropOff : InteractableObject
    {
#if !MONO
        public InventoryDropOff(System.IntPtr ptr) : base(ptr) { }
#endif
        public bool confiscateAllItems = true;
        public List<string> allowedItems = new List<string> { "BasicClothing", "Shoes" };
        public List<string> jailGearItems = new List<string> { "PrisonUniform", "PrisonShoes" };

        public Transform storageContainer;
        public int maxStorageSlots = 50;

        private BookingProcess bookingProcess;
        private List<string> confiscatedItems = new List<string>();
        private bool processingInventory = false;

        void Start()
        {
            // Find booking process
            bookingProcess = FindObjectOfType<BookingProcess>();

            // Set up interaction directly
            SetMessage("Process inventory");
            SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            SetInteractableState(InteractableObject.EInteractableState.Default);
            ModLogger.Info("InventoryDropOff interaction setup completed");

            // Find storage container
            if (storageContainer == null)
            {
                storageContainer = transform.Find("StorageContainer");
                if (storageContainer == null)
                {
                    // Create storage container
                    GameObject storage = new GameObject("StorageContainer");
                    storage.transform.SetParent(transform);
                    storageContainer = storage.transform;
                }
            }

            ModLogger.Info("InventoryDropOff initialized");
        }

        public override void StartInteract()
        {
            if (processingInventory)
            {
                SetMessage("Processing inventory...");
                SetInteractableState(InteractableObject.EInteractableState.Invalid);
                return;
            }

            // Check if booking stations are complete
            if (bookingProcess == null || !bookingProcess.IsBookingComplete())
            {
                SetMessage("Complete mugshot and fingerprint scan first");
                SetInteractableState(InteractableObject.EInteractableState.Invalid);

                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Complete booking stations first!",
                        NotificationType.Warning
                    );
                }
                return;
            }

            base.StartInteract();
            
            // Start inventory processing
            Player currentPlayer = Player.Local;
            if (currentPlayer != null)
            {
                MelonCoroutines.Start(ProcessPlayerInventory(currentPlayer));
            }
            else
            {
                ModLogger.Error("No local player found for inventory processing!");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessPlayerInventory(Player player)
        {
            processingInventory = true;
            SetMessage("Processing inventory...");
            SetInteractableState(InteractableObject.EInteractableState.Invalid);

            ModLogger.Info($"Processing inventory for {player.name}");

            // Show notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Storing personal items...",
                    NotificationType.Instruction
                );
            }

            // Wait for processing effect
            yield return new WaitForSeconds(2f);

            // Confiscate items from player inventory
            yield return MelonCoroutines.Start(ConfiscatePlayerItems(player));

            // Wait a moment
            yield return new WaitForSeconds(1f);

            // Issue jail gear
            yield return MelonCoroutines.Start(IssueJailGear(player));

            // Wait for completion effect
            yield return new WaitForSeconds(1f);

            // Mark inventory processing as complete
            try
            {
                if (bookingProcess != null)
                {
                    bookingProcess.inventoryProcessed = true;
                    bookingProcess.confiscatedItems.AddRange(confiscatedItems);
                    ModLogger.Info("Inventory processing marked as complete");
                }

                // Show completion notification
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Inventory processed - booking complete!",
                        NotificationType.Progress
                    );
                }

                // Update interaction state
                SetMessage("Inventory processed");
                SetInteractableState(InteractableObject.EInteractableState.Label);

                ModLogger.Info("Inventory processing completed successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error processing inventory: {ex.Message}");

                // Show error notification
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Error processing inventory",
                        NotificationType.Warning
                    );
                }
            }

            processingInventory = false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ConfiscatePlayerItems(Player player)
        {
            confiscatedItems.Clear();

            // Get player inventory
            var inventory = PlayerSingleton<PlayerInventory>.Instance;
            if (inventory == null)
            {
                ModLogger.Warn("Player inventory not found");
                yield break;
            }

            ModLogger.Info("Starting item confiscation process");

            // Simulate confiscation process
            // Note: Actual inventory manipulation would require deeper integration
            // with Schedule I's inventory system

            // For now, we'll simulate the process and log what would be confiscated
            string[] simulatedItems = {
                "Phone", "Keys", "Wallet", "Drugs", "Weapons",
                "PersonalClothing", "Jewelry", "Electronics"
            };

            foreach (string item in simulatedItems)
            {
                // Simulate confiscation delay
                yield return new WaitForSeconds(0.3f);

                confiscatedItems.Add(item);
                ModLogger.Debug($"Confiscated: {item}");
            }

            ModLogger.Info($"Confiscated {confiscatedItems.Count} items from player");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator IssueJailGear(Player player)
        {
            ModLogger.Info("Issuing jail gear to player");

            // Simulate issuing jail uniform and basic items
            foreach (string gearItem in jailGearItems)
            {
                yield return new WaitForSeconds(0.5f);
                ModLogger.Debug($"Issued: {gearItem}");
            }

            try
            {
                // Show gear issued notification
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Jail uniform issued",
                        NotificationType.Progress
                    );
                }

                ModLogger.Info("Jail gear issued successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error issuing jail gear: {ex.Message}");
            }
        }

        /// <summary>
        /// Get list of confiscated items for records
        /// </summary>
        public List<string> GetConfiscatedItems()
        {
            return new List<string>(confiscatedItems);
        }

        /// <summary>
        /// Return confiscated items on player release
        /// </summary>
        public void ReturnPlayerItems(Player player)
        {
            try
            {
                ModLogger.Info($"Returning {confiscatedItems.Count} items to {player.name}");

                foreach (string item in confiscatedItems)
                {
                    ModLogger.Debug($"Returned: {item}");
                }

                // Clear confiscated items after return
                confiscatedItems.Clear();

                ModLogger.Info("Items returned successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error returning items: {ex.Message}");
            }
        }

        public bool IsComplete()
        {
            return bookingProcess != null && bookingProcess.inventoryProcessed;
        }

        void Update()
        {
            // Update interaction state based on completion and booking status
            if (!processingInventory)
            {
                if (IsComplete())
                {
                    SetMessage("Inventory processed");
                    SetInteractableState(InteractableObject.EInteractableState.Label);
                }
                else if (bookingProcess != null && bookingProcess.IsBookingComplete())
                {
                    SetMessage("Process inventory");
                    SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                else
                {
                    SetMessage("Complete booking stations first");
                    SetInteractableState(InteractableObject.EInteractableState.Invalid);
                }
            }
        }
    }
}