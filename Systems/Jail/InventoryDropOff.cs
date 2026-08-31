using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Systems.Jail;
using BBHelpers = Behind_Bars.Helpers.Helpers;


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
    /// Legacy booking interaction that stages a simulated personal-item confiscation and
    /// jail-gear handoff. It currently records/logs fixed item names; it does not perform
    /// a native inventory transfer or provide a real storage container.
    /// </summary>
    public class InventoryDropOff : InteractableObject
    {
#if !MONO
        public InventoryDropOff(System.IntPtr ptr) : base(ptr) { }
#endif
        // These settings describe the legacy simulation. allowedItems and the storage
        // fields are retained for compatibility and are not authoritative inventory data.
        public bool confiscateAllItems = true;
        public List<string> allowedItems = new List<string> { "BasicClothing", "Shoes" };
        public List<string> jailGearItems = new List<string> { "PrisonUniform", "PrisonShoes" };

        public Transform storageContainer;
        public int maxStorageSlots = 50;

        // processingInventory gates repeat interaction while the coroutine is active;
        // confiscatedItems is a volatile name snapshot, not a recoverable item store.
        private BookingProcess bookingProcess;
        private List<string> confiscatedItems = new List<string>();
        private bool processingInventory = false;
        private bool hasCachedInteractionMessage;
        private string cachedInteractionMessage;
        private bool hasCachedInteractionState;
        private int cachedInteractionState;

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SetInteractionMessage(string message)
        {
            if (hasCachedInteractionMessage && cachedInteractionMessage == message)
            {
                return;
            }

            SetMessage(message);
            cachedInteractionMessage = message;
            hasCachedInteractionMessage = true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SetInteractionState(InteractableObject.EInteractableState state)
        {
            int stateValue = (int)state;
            if (hasCachedInteractionState && cachedInteractionState == stateValue)
            {
                return;
            }

            SetInteractableState(state);
            cachedInteractionState = stateValue;
            hasCachedInteractionState = true;
        }

        void Start()
        {
            // Find booking process
            bookingProcess = BBHelpers.FindObjectOfTypeSafe<BookingProcess>();

            // Set up interaction directly
            SetInteractionMessage("Process inventory");
            SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            SetInteractionState(InteractableObject.EInteractableState.Default);
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

        /// <summary>
        /// Starts the legacy simulated inventory phase only after booking requirements are
        /// complete. The phase marks BookingProcess inventory state after its timed effects.
        /// </summary>
        public override void StartInteract()
        {
            if (processingInventory)
            {
                SetInteractionMessage("Processing inventory...");
                SetInteractionState(InteractableObject.EInteractableState.Invalid);
                return;
            }

            // Check if booking stations are complete
            if (bookingProcess == null || !bookingProcess.IsBookingComplete())
            {
                SetInteractionMessage("Complete mugshot and fingerprint scan first");
                SetInteractionState(InteractableObject.EInteractableState.Invalid);

                if (Core.ResolveUIManager() != null)
                {
                    Core.ResolveUIManager().ShowNotification(
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
        // Orchestrates the legacy delay -> simulated confiscation -> simulated gear sequence
        // and publishes completion to BookingProcess; it does not move native item stacks.
        private IEnumerator ProcessPlayerInventory(Player player)
        {
            processingInventory = true;
            SetInteractionMessage("Processing inventory...");
            SetInteractionState(InteractableObject.EInteractableState.Invalid);

            ModLogger.Info($"Processing inventory for {player.name}");

            // Show notification
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
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
                if (Core.ResolveUIManager() != null)
                {
                    Core.ResolveUIManager().ShowNotification(
                        "Inventory processed - booking complete!",
                        NotificationType.Progress
                    );
                }

                // Update interaction state
                SetInteractionMessage("Inventory processed");
                SetInteractionState(InteractableObject.EInteractableState.Label);

                ModLogger.Info("Inventory processing completed successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error processing inventory: {ex.Message}");

                // Show error notification
                if (Core.ResolveUIManager() != null)
                {
                    Core.ResolveUIManager().ShowNotification(
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
        // Current behavior intentionally records a fixed simulated list. Keep this warning
        // adjacent to the method so future docs do not mistake the log for item removal.
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
        // Logs each configured gear item and re-enables player inventory before marking the
        // booking checkpoint; no native gear item is created by this legacy routine.
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
                if (Core.ResolveUIManager() != null)
                {
                    Core.ResolveUIManager().ShowNotification(
                        "Jail uniform issued",
                        NotificationType.Progress
                    );
                }

                // RE-ENABLE INVENTORY after prison gear is issued
                PlayerSingleton<PlayerInventory>.Instance.enabled = true;
                PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(true);
                ModLogger.Info("Prison gear pickup: Re-enabled inventory component, inventory access, and equipping");

                // Mark prison gear pickup as complete in booking process
                ModLogger.Info("Attempting to find BookingProcess to mark gear pickup complete...");
                var bookingProcess = BBHelpers.FindObjectOfTypeSafe<BookingProcess>();
                if (bookingProcess != null)
                {
                    ModLogger.Info($"Found BookingProcess! Current state - Mugshot: {bookingProcess.mugshotComplete}, Fingerprint: {bookingProcess.fingerprintComplete}, Prison Gear: {bookingProcess.prisonGearPickupComplete}");
                    bookingProcess.SetPrisonGearPickupComplete();
                    ModLogger.Info($"Prison gear pickup marked as complete! New state - Mugshot: {bookingProcess.mugshotComplete}, Fingerprint: {bookingProcess.fingerprintComplete}, Prison Gear: {bookingProcess.prisonGearPickupComplete}");
                    ModLogger.Info($"IsBookingComplete: {bookingProcess.IsBookingComplete()}");
                }
                else
                {
                    ModLogger.Error("BookingProcess not found! Cannot mark prison gear pickup as complete");
                }

                ModLogger.Info("Jail gear issued successfully - player inventory re-enabled");
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
        /// Logs the simulated confiscated-item names and clears the volatile list. Despite
        /// the legacy method name, this implementation does not restore native inventory.
        /// </summary>
        /// <param name="player">Player associated with the simulated record.</param>
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

        /// <summary>
        /// Returns whether the associated BookingProcess has marked inventory processing
        /// complete. It does not prove that native items were stored or restored.
        /// </summary>
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
                    SetInteractionMessage("Inventory processed");
                    SetInteractionState(InteractableObject.EInteractableState.Label);
                }
                else if (bookingProcess != null && bookingProcess.IsBookingComplete())
                {
                    SetInteractionMessage("Process inventory");
                    SetInteractionState(InteractableObject.EInteractableState.Default);
                }
                else
                {
                    SetInteractionMessage("Complete booking stations first");
                    SetInteractionState(InteractableObject.EInteractableState.Invalid);
                }
            }
        }
    }
}
