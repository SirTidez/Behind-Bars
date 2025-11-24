using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Systems.Data;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.Storage;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne.Storage;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Handles player inventory pickup when being released from jail
    /// </summary>
    public class InventoryPickupStation : MonoBehaviour
    {
#if !MONO
        public InventoryPickupStation(System.IntPtr ptr) : base(ptr) { }
#endif
        
        // InteractableObject component for IL2CPP compatibility
        private InteractableObject interactableObject;

        public float itemPickupDuration = 0.3f; // Time between picking up each item
        public Transform storageLocation; // Where items are "retrieved" from visually

        private bool isProcessing = false;
        private Player currentPlayer;
        private List<PersistentPlayerData.StoredItem> legalItems = new List<PersistentPlayerData.StoredItem>();
        private List<PersistentPlayerData.StoredItem> contrabandItems = new List<PersistentPlayerData.StoredItem>();

        // Interactive storage components
        private PrisonStorageEntity storageEntity;
        private bool storageSessionActive = false;
        private bool isDisabledByOfficer = false; // Officer has disabled this station - don't allow reopening
        
        void Start()
        {
            // Ensure this GameObject has the correct name to avoid conflicts
            if (gameObject.name != "InventoryPickup")
            {
                gameObject.name = "InventoryPickup";
                ModLogger.Debug("Renamed GameObject to 'InventoryPickup' to avoid conflicts with JailInventoryPickup");
            }

            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Debug("InventoryPickupStation interaction setup completed");

            // Find storage location if not assigned
            if (storageLocation == null)
            {
                storageLocation = transform.Find("StorageLocation");
                if (storageLocation == null)
                {
                    // Create a default storage location
                    GameObject storage = new GameObject("StorageLocation");
                    storage.transform.SetParent(transform);
                    storage.transform.localPosition = Vector3.zero;
                    storageLocation = storage.transform;
                    ModLogger.Debug("Created default StorageLocation for personal belongings");
                }
            }

            // Find PossesionCubby if it exists
            Transform possesionCubby = transform.Find("PossesionCubby");
            if (possesionCubby != null)
            {
                ModLogger.Debug("Found PossesionCubby component in InventoryPickup station");
            }
        }
        
        private void SetupInteractableComponent()
        {
            // Get or create InteractableObject component
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Debug("Added InteractableObject component to InventoryPickupStation");
            }
            else
            {
                ModLogger.Debug("Found existing InteractableObject component on InventoryPickupStation");
            }
            
            // Configure the interaction
            interactableObject.SetMessage("Retrieve personal belongings");
            interactableObject.SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);

            // Set up storage entity
            SetupStorageEntity();
            
            // Set up event listeners with IL2CPP-safe casting
#if !MONO
            // Use System.Action for IL2CPP compatibility
            interactableObject.onInteractStart.AddListener((System.Action)OnInteractStart);
#else
            // Use UnityAction for Mono
            interactableObject.onInteractStart.AddListener(OnInteractStart);
#endif
            
            ModLogger.Debug("InteractableObject component configured with event listeners");
        }

        /// <summary>
        /// Set up the storage entity component for interactive storage
        /// </summary>
        private void SetupStorageEntity()
        {
            // Get or create PrisonStorageEntity component
            storageEntity = GetComponent<PrisonStorageEntity>();
            if (storageEntity == null)
            {
                storageEntity = gameObject.AddComponent<PrisonStorageEntity>();
                ModLogger.Debug("Added PrisonStorageEntity component to InventoryPickupStation");
            }
            else
            {
                ModLogger.Debug("Found existing PrisonStorageEntity component");
            }

            // DO NOT add StorageEntityInteractable - it conflicts with our custom InteractableObject
            // We handle all interactions through OnInteractStart() which decides storage vs direct transfer
        }
        
        private void OnInteractStart()
        {
            // CRITICAL: If officer disabled this station, don't allow interaction
            if (isDisabledByOfficer)
            {
                ModLogger.Info("InventoryPickupStation: Interaction blocked - officer has disabled this station");
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Storage closed - follow the officer",
                        NotificationType.Warning
                    );
                }
                return;
            }

            // Player is interacting with the storage station
            if (isProcessing)
            {
                ModLogger.Debug("Storage interaction already in progress");
                return;
            }

            // Allow re-opening: Reset storageSessionActive if storage is actually closed
            if (storageSessionActive && storageEntity != null && !storageEntity.IsOpened)
            {
                ModLogger.Info("Storage was closed externally - resetting session flag to allow re-opening");
                storageSessionActive = false;
            }

            if (storageSessionActive)
            {
                ModLogger.Debug("Storage is currently open - interaction blocked");
                return;
            }

            if (Player.Local != null)
            {
                ModLogger.Info($"Player {Player.Local.name} interacting with InventoryPickupStation");

                // Show notification about items (block if no items, only change clothes)
                if (legalItems == null || legalItems.Count == 0)
                {
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            "No personal items in storage - you can still change clothes",
                            NotificationType.Instruction
                        );
                        RestorePlayerClothing(Player.Local);
                        return;
                    }
                    ModLogger.Info("No items in storage, but allowing access for clothing change");
                }

                // Open interactive storage (items were already populated in PrepareStorageForPlayer if any exist)
                if (storageEntity != null)
                {
                    // Unlock cursor BEFORE opening storage (prevents cursor lock issues)
                    //UnityEngine.Cursor.lockState = CursorLockMode.None;
                    //UnityEngine.Cursor.visible = true;
                    var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
                    playerCamera.FreeMouse();
                    Singleton<HUD>.Instance.SetCrosshairVisible(false);

                    try
                    {
                        // Restore player's original clothing from PersistentPlayerData
                        RestorePlayerClothing(Player.Local);

                        // Remove any remaining jail items before opening storage
                        MelonCoroutines.Start(RemovePrisonItems(Player.Local));
                        ModLogger.Info($"Attempting to open storage - IsOpened: {storageEntity.IsOpened}, CurrentAccessor: {storageEntity.CurrentPlayerAccessor?.name ?? "null"}");
                        storageEntity.Open();
                        storageSessionActive = true;
                        ModLogger.Info($"Interactive storage opened for {Player.Local.name}");

                        if (BehindBarsUIManager.Instance != null)
                        {
                            BehindBarsUIManager.Instance.ShowNotification(
                                "Ctrl+Click items to transfer to inventory. Close storage when done.",
                                NotificationType.Instruction
                            );
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Warn($"Storage open had network error (expected, harmless)");
                        storageSessionActive = true; // Mark as active anyway - storage UI is showing
                        ModLogger.Info("Storage UI opened despite network error");
                    }
                }
                else
                {
                    ModLogger.Error("StorageEntity is null - cannot open storage");
                }
            }
        }

        /// <summary>
        /// Prepare storage with player's items - DOES NOT open it (player must interact)
        /// </summary>
        public void PrepareStorageForPlayer(Player player)
        {
            if (storageEntity == null)
            {
                ModLogger.Error("StorageEntity not found when preparing storage");
                return;
            }

            currentPlayer = player;

            // CRITICAL: Clear the officer-disabled flag for new release
            isDisabledByOfficer = false;
            storageSessionActive = false; // Also reset session flag
            ModLogger.Info("InventoryPickupStation: Cleared officer-disabled flag for new release");

            // CRITICAL: Force reset the storage entity to clear old items
            if (storageEntity != null)
            {
                storageEntity.ResetForNewRelease();
                ModLogger.Info("Reset PrisonStorageEntity for new release");
            }

            // Prepare items for storage (from current arrest)
            PrepareItemsForPickup(player);

            // Show contraband notification if any items were confiscated
            if (contrabandItems.Count > 0)
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"{contrabandItems.Count} illegal items confiscated permanently",
                        NotificationType.Warning
                    );
                }
            }

            // Populate storage entity with player's legal items
            storageEntity.PopulateWithPlayerItems(player);

            ModLogger.Info($"Storage prepared for {player.name} with {legalItems.Count} legal items - waiting for player interaction");

            // Remove any jail items that might still be in inventory (early cleanup)
            MelonCoroutines.Start(RemovePrisonItems(player));

            // Show instruction to interact
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Interact with storage to retrieve your personal belongings",
                    NotificationType.Instruction
                );
            }
        }

        /// <summary>
        /// Mark this station as disabled by officer (prevents reopening after close)
        /// </summary>
        public void MarkDisabledByOfficer()
        {
            isDisabledByOfficer = true;
            storageSessionActive = true; // Also prevent reopening via session flag
            ModLogger.Info("InventoryPickupStation: Marked as disabled by officer");
        }

        /// <summary>
        /// Open storage interface directly without delays
        /// </summary>
        private void OpenStorageInterface(Player player)
        {
            ModLogger.Info($"Opening storage interface directly for {player.name}");

            // Prepare items for storage
            PrepareItemsForPickup(player);

            // Show contraband notification if any items were confiscated
            if (contrabandItems.Count > 0)
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"{contrabandItems.Count} illegal items confiscated permanently",
                        NotificationType.Warning
                    );
                }
            }

            // Populate and open storage
            if (storageEntity != null)
            {
                storageEntity.PopulateWithPlayerItems(player);

                try
                {
                    storageEntity.Open();
                    storageSessionActive = true;

                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            "Drag items between storage and inventory",
                            NotificationType.Instruction
                        );
                    }

                    ModLogger.Info("Storage interface opened successfully");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error opening storage interface: {ex.Message}");
                }
            }
            else
            {
                ModLogger.Error("StorageEntity not found");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessPrisonItemDropOffAndPickup(Player player)
        {
            isProcessing = true;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Processing...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }

            ModLogger.Info($"Starting complete inventory exchange for {player.name}");

            // Phase 1: Drop off prison items
#if MONO
            yield return StartCoroutine(RemovePrisonItems(player));
#else
            // In IL2CPP, directly yield the IEnumerator instead of using StartCoroutine
            yield return RemovePrisonItems(player);
#endif

            // Short delay between drop-off and pickup
            yield return new WaitForSeconds(1f);

            // Phase 2: Get items from persistent storage for pickup
            PrepareItemsForPickup(player);

            if (legalItems.Count == 0 && contrabandItems.Count == 0)
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "No personal items in storage - prison items removed",
                        NotificationType.Progress
                    );
                }
                CompletePickup();
                yield break;
            }

            // Phase 3: Open interactive storage for item retrieval
            yield return MelonCoroutines.Start(OpenInteractiveStorage(player));
            //yield return StartCoroutine(OpenInteractiveStorage(player));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessPrisonItemDropOff(Player player)
        {
            ModLogger.Info($"Starting prison item drop-off for {player.name}");

            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Dropping off prison items...",
                    NotificationType.Instruction
                );
            }

            // Get player inventory
            var inventory = PlayerSingleton<PlayerInventory>.Instance;
            if (inventory == null)
            {
                ModLogger.Error("PlayerInventory instance not found for prison item drop-off!");
                yield break;
            }

            // Remove prison items from inventory (prison uniform, etc.)
            var prisonItemsToRemove = new List<string> { "Prison Uniform", "Prison Shoes", "Prison Socks" };
            int itemsRemoved = 0;

            foreach (var itemName in prisonItemsToRemove)
            {
                bool itemRemoved = false;
                try
                {
                    // Try to remove prison items using reflection
                    var removeMethod = inventory.GetType().GetMethod("RemoveItem");
                    if (removeMethod != null)
                    {
                        removeMethod.Invoke(inventory, new object[] { itemName });
                        itemsRemoved++;
                        itemRemoved = true;
                        ModLogger.Info($"Removed prison item: {itemName}");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Could not remove {itemName}: {ex.Message}");
                }

                if (itemRemoved)
                {
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            $"Returned: {itemName}",
                            NotificationType.Progress
                        );
                    }
                    yield return new WaitForSeconds(0.3f); // Visual delay
                }
            }

            ModLogger.Info($"Prison item drop-off complete - removed {itemsRemoved} items");
            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>
        /// Prepare items for pickup - separate legal items from contraband
        /// </summary>
        private void PrepareItemsForPickup(Player player)
        {
            legalItems.Clear();
            contrabandItems.Clear();

            try
            {
                // Get items from persistent storage
                var persistentData = PersistentPlayerData.Instance;
                var allLegalItems = persistentData.GetLegalItemsForPlayer(player);
                var allContrabandItems = persistentData.GetContrabandItemsForPlayer(player);

                legalItems.AddRange(allLegalItems);
                contrabandItems.AddRange(allContrabandItems);

                ModLogger.Info($"Prepared {legalItems.Count} legal items and {contrabandItems.Count} contraband items for {player.name}");

                // Show contraband notification if any items are being kept
                if (contrabandItems.Count > 0)
                {
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            $"{contrabandItems.Count} illegal items confiscated permanently",
                            NotificationType.Warning
                        );
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error preparing items for pickup: {ex.Message}");
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator OpenInteractiveStorage(Player player)
        {
            ModLogger.Info($"Opening interactive storage for {player.name}");

            // Show initial notification with contraband info
            if (BehindBarsUIManager.Instance != null)
            {
                string message = contrabandItems.Count > 0
                    ? $"Storage contains {legalItems.Count} legal items ({contrabandItems.Count} illegal items confiscated)"
                    : $"Storage contains {legalItems.Count} personal items";

                BehindBarsUIManager.Instance.ShowNotification(message, NotificationType.Instruction);
            }

            // Populate storage entity with legal items
            if (storageEntity != null)
            {
                storageEntity.PopulateWithPlayerItems(player);

                // Wait a moment for population
                yield return new WaitForSeconds(0.5f);

                // Open storage menu
                try
                {
                    storageEntity.Open();
                    storageSessionActive = true;

                    // Update interaction message
                    if (interactableObject != null)
                    {
                        interactableObject.SetMessage("Storage open - drag items to retrieve them");
                        interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
                    }

                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            "Drag items from storage to your inventory. Close storage when done.",
                            NotificationType.Instruction
                        );
                    }

                    ModLogger.Info("Interactive storage opened successfully");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error opening storage menu: {ex.Message}");
                    // Fall back to completion
                    CompletePickupWithoutStorage(player);
                }
            }
            else
            {
                ModLogger.Error("StorageEntity not found - falling back to auto-completion");
                CompletePickupWithoutStorage(player);
            }
        }

        /// <summary>
        /// Direct item transfer fallback - give items back to player directly (no storage UI)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
#if MONO
        private System.Collections.IEnumerator DirectItemTransfer(Player player)
#else
        private IEnumerator DirectItemTransfer(Player player)
#endif
        {
            isProcessing = true;
            ModLogger.Info($"Starting direct item transfer for {player.name} - {legalItems.Count} items to return");

            var inventory = PlayerSingleton<PlayerInventory>.Instance;
            if (inventory == null)
            {
                ModLogger.Error("PlayerInventory not found!");
                CompletePickup();
                yield break;
            }

            // STEP 1: Remove prison items first
#if MONO
            yield return StartCoroutine(RemovePrisonItems(player));
#else
            // In IL2CPP, directly yield the IEnumerator instead of using StartCoroutine
            yield return RemovePrisonItems(player);
#endif

            yield return new WaitForSeconds(1f);

            // STEP 2: Unlock inventory
            InventoryProcessor.UnlockPlayerInventory(player);

            // CRITICAL: Wait for inventory system to fully re-enable before adding items
            yield return new WaitForSeconds(0.5f);

            // STEP 3: Transfer items using EXACT method from JailInventoryPickupStation.GivePrisonItem()
            int itemsTransferred = 0;

            foreach (var item in legalItems)
            {
                ModLogger.Info($"Attempting to return: {item.itemName} (ID: {item.itemId})");

#if !MONO
                var itemDef = Il2CppScheduleOne.Registry.GetItem(item.itemId);
#else
                var itemDef = ScheduleOne.Registry.GetItem(item.itemId);
#endif

                if (itemDef == null)
                {
                    ModLogger.Error($"Item definition not found in registry for: {item.itemId}");
                    continue;
                }

                // Create item instance using GetDefaultInstance
                var itemInstance = itemDef.GetDefaultInstance(item.stackCount);
                if (itemInstance == null)
                {
                    ModLogger.Error($"Failed to create item instance for: {item.itemId}");
                    continue;
                }

                // Special handling for CashInstance - restore the Balance
                if (item.itemType == "CashInstance" && item.cashBalance > 0f)
                {
#if !MONO
                    var cashInstance = itemInstance as Il2CppScheduleOne.ItemFramework.CashInstance;
#else
                    var cashInstance = itemInstance as ScheduleOne.ItemFramework.CashInstance;
#endif
                    if (cashInstance != null)
                    {
                        cashInstance.SetBalance(item.cashBalance);
                        ModLogger.Info($"✓ Restored cash balance: ${item.cashBalance:N2}");
                    }
                }

                // Add to player inventory using AddItemToInventory (the ACTUAL method that exists)
                inventory.AddItemToInventory(itemInstance);
                itemsTransferred++;

                ModLogger.Info($"✓ Successfully returned {item.itemName} (ID: {item.itemId})");

                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"Retrieved: {item.itemName}",
                        NotificationType.Progress
                    );
                }

                yield return new WaitForSeconds(0.3f);
            }

            ModLogger.Info($"Direct transfer complete - returned {itemsTransferred}/{legalItems.Count} items");

            // Clear stored items
            ClearStoredItemsForPlayer(player);

            // Notify completion
            if (ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.OnInventoryProcessingComplete(player);
            }

            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    $"Retrieved {itemsTransferred} personal items - tell guard when ready",
                    NotificationType.Progress
                );
            }

            CompletePickup();
        }

        /// <summary>
        /// Remove prison items from player inventory during release
        /// Includes all jail items: consumables (bedroll, sheets, cup, toothbrush) and clothing (uniform, shoes, socks)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
#if MONO
        private System.Collections.IEnumerator RemovePrisonItems(Player player)
#else
        private IEnumerator RemovePrisonItems(Player player)
#endif
        {
            ModLogger.Info("Removing all prison items from inventory");

            var inventory = PlayerSingleton<PlayerInventory>.Instance;
            if (inventory == null)
            {
                ModLogger.Error("PlayerInventory not found for prison item removal");
                yield break;
            }

            // All prison items to remove - consumables and clothing
            var prisonItemsToRemove = new List<string>
            {
                // Consumable items
                "Prison Bed Roll",
                "Prison Sheets and Pillow",
                "Prison Sheets & Pillow", // Alternative name
                "Prison Cup",
                "Prison Toothbrush",
                // Clothing items
                "Prison Uniform",
                "Prison Shoes",
                "Prison Socks"
            };

            int itemsRemoved = 0;

            foreach (var itemName in prisonItemsToRemove)
            {
                bool itemRemoved = false;
                
                // Try to remove via reflection
                var removeMethod = inventory.GetType().GetMethod("RemoveItemByName");
                if (removeMethod == null)
                {
                    removeMethod = inventory.GetType().GetMethod("RemoveItem");
                }

                if (removeMethod != null)
                {
                    try
                    {
                        removeMethod.Invoke(inventory, new object[] { itemName });
                        itemsRemoved++;
                        itemRemoved = true;
                        ModLogger.Info($"Removed prison item: {itemName}");
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"Could not remove {itemName}: {ex.Message}");
                    }
                }

                // Yield outside of try-catch block
                if (itemRemoved)
                {
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            $"Returned prison item: {itemName}",
                            NotificationType.Progress
                        );
                    }

                    yield return new WaitForSeconds(0.2f);
                }
            }

            ModLogger.Info($"Removed {itemsRemoved} prison items from inventory");
            
            // Also check inventory slots directly for any remaining prison items
            yield return new WaitForSeconds(0.5f);
            CheckAndRemoveRemainingPrisonItems(inventory);
        }

        /// <summary>
        /// Check inventory slots directly for any remaining prison items and remove them
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void CheckAndRemoveRemainingPrisonItems(PlayerInventory inventory)
        {
            try
            {
                ModLogger.Info("Checking inventory slots for remaining prison items...");
                
                // Get inventory slots using reflection
                var slotsProperty = inventory.GetType().GetProperty("ItemSlots");
                if (slotsProperty == null)
                {
                    slotsProperty = inventory.GetType().GetProperty("Slots");
                }
                
                if (slotsProperty == null)
                {
                    ModLogger.Debug("Could not find ItemSlots property - skipping slot-by-slot check");
                    return;
                }
                
                var slots = slotsProperty.GetValue(inventory);
                if (slots == null)
                {
                    ModLogger.Debug("ItemSlots is null - skipping slot-by-slot check");
                    return;
                }
                
                // Get count property/method
                int slotCount = 0;
                var countProperty = slots.GetType().GetProperty("Count");
                if (countProperty != null)
                {
                    slotCount = (int)countProperty.GetValue(slots);
                }
                else
                {
                    var lengthProperty = slots.GetType().GetProperty("Length");
                    if (lengthProperty != null)
                    {
                        slotCount = (int)lengthProperty.GetValue(slots);
                    }
                }
                
                int itemsFound = 0;
                var prisonItemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Prison Bed Roll",
                    "Prison Sheets and Pillow",
                    "Prison Sheets & Pillow",
                    "Prison Cup",
                    "Prison Toothbrush",
                    "Prison Uniform",
                    "Prison Shoes",
                    "Prison Socks"
                };
                
                // Check each slot
                for (int i = 0; i < slotCount; i++)
                {
                    try
                    {
                        // Get item from slot
                        var indexer = slots.GetType().GetProperty("Item", new[] { typeof(int) });
                        if (indexer == null)
                        {
                            var getMethod = slots.GetType().GetMethod("get_Item", new[] { typeof(int) });
                            if (getMethod != null)
                            {
                                var slotItem = getMethod.Invoke(slots, new object[] { i });
                                if (slotItem != null)
                                {
                                    // Check if item name contains "Prison"
                                    var nameProperty = slotItem.GetType().GetProperty("Name");
                                    if (nameProperty == null)
                                    {
                                        nameProperty = slotItem.GetType().GetProperty("ItemName");
                                    }
                                    
                                    if (nameProperty != null)
                                    {
                                        var itemName = nameProperty.GetValue(slotItem)?.ToString();
                                        if (!string.IsNullOrEmpty(itemName) && itemName.Contains("Prison", StringComparison.OrdinalIgnoreCase))
                                        {
                                            // Try to remove this item
                                            var removeMethod = inventory.GetType().GetMethod("RemoveItemByName");
                                            if (removeMethod == null)
                                            {
                                                removeMethod = inventory.GetType().GetMethod("RemoveItem");
                                            }
                                            
                                            if (removeMethod != null)
                                            {
                                                removeMethod.Invoke(inventory, new object[] { itemName });
                                                itemsFound++;
                                                ModLogger.Info($"Found and removed remaining prison item: {itemName}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"Error checking slot {i}: {ex.Message}");
                    }
                }
                
                if (itemsFound > 0)
                {
                    ModLogger.Info($"Found and removed {itemsFound} additional prison items from inventory slots");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error checking inventory slots: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback method when storage system fails
        /// </summary>
        private void CompletePickupWithoutStorage(Player player)
        {
            ModLogger.Info("Completing pickup without storage interface");

            // Clear stored items from persistent storage
            ClearStoredItemsForPlayer(player);

            // Unlock player inventory
            InventoryProcessor.UnlockPlayerInventory(player);
            ModLogger.Info("Player inventory unlocked");

            // Notify the ReleaseManager that inventory processing is complete
            if (ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.OnInventoryProcessingComplete(player);
            }

            // Show notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Storage system unavailable - process completed",
                    NotificationType.Warning
                );
            }

            CompletePickup();
        }

        /// <summary>
        /// Called by PrisonStorageEntity when storage session is completed
        /// </summary>
        public void OnStorageSessionComplete()
        {
            ModLogger.Info("Storage session completed by player - can re-open if needed");
            storageSessionActive = false;

            // Re-lock cursor for normal FPS gameplay
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
            ModLogger.Info("Cursor re-locked for FPS mode after storage close");

            // Get remaining items in storage (don't clear - allow re-opening)
            int remainingItems = 0;
            if (storageEntity != null)
            {
                remainingItems = storageEntity.GetRemainingItemCount();
                ModLogger.Info($"Storage closed with {remainingItems} items remaining");
            }

            // Final check: Remove any remaining jail items from inventory
            if (currentPlayer != null)
            {
                MelonCoroutines.Start(RemovePrisonItems(currentPlayer));
            }

            // DON'T clear items yet - player may want to re-open storage
            // DON'T notify ReleaseManager yet - player hasn't confirmed they're done
            // Items will be cleared when player confirms they're ready via guard interaction

            // Unlock player inventory so they can use items they took
            InventoryProcessor.UnlockPlayerInventory(currentPlayer);
            ModLogger.Info("Player inventory unlocked - can re-open storage or proceed");

            // Show notification that storage is closed but can be re-opened
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Storage closed - interact again to re-open, or tell guard when ready",
                    NotificationType.Progress
                );
            }

            ModLogger.Info("Storage closed - player can re-open or proceed to guard");
        }

        /// <summary>
        /// Legacy method - replaced by PrepareItemsForPickup using PersistentPlayerData
        /// </summary>
        
        private void AddItemToInventory(PlayerInventory inventory, PersistentPlayerData.StoredItem storedItem)
        {
            try
            {
                ModLogger.Info($"AddItemToInventory: Attempting to add item: {storedItem.itemName} (x{storedItem.stackCount})");
                ModLogger.Info($"AddItemToInventory: Item ID: {storedItem.itemId}");

                // Special handling for phone - re-enable it
                if (storedItem.itemName.Contains("Phone"))
                {
                    EnablePlayerPhone();
                }

                // Try to add items using stored information
                string itemId = storedItem.itemId;
                string itemName = storedItem.itemName;
                int stackCount = storedItem.stackCount;

                // Approach 1: Create proper ItemInstance from registry using stored ID
                try
                {
                    if (!string.IsNullOrEmpty(itemId) && itemId != "unknown")
                    {
                        // Get the item definition from registry
#if !MONO
                        var registry = Il2CppScheduleOne.Registry.Instance;
#else
                        var registry = ScheduleOne.Registry.Instance;
#endif
                        if (registry != null)
                        {
                            var getItemMethod = registry.GetType().GetMethod("GetItem");
                            if (getItemMethod != null)
                            {
                                var itemDef = getItemMethod.Invoke(registry, new object[] { itemId });
                                if (itemDef != null)
                                {
                                    // Create an ItemInstance from the definition
                                    var getDefaultInstanceMethod = itemDef.GetType().GetMethod("GetDefaultInstance");
                                    if (getDefaultInstanceMethod != null)
                                    {
                                        var itemInstance = getDefaultInstanceMethod.Invoke(itemDef, null);
                                        if (itemInstance != null)
                                        {
                                            // Set stack count if supported
                                            try
                                            {
                                                var stackCountProperty = itemInstance.GetType().GetProperty("StackCount");
                                                if (stackCountProperty != null && stackCount > 1)
                                                {
                                                    stackCountProperty.SetValue(itemInstance, stackCount);
                                                }
                                            }
                                            catch { } // Ignore stack count errors

                                            // Add the ItemInstance to inventory
                                            var addItemToInventoryMethod = inventory.GetType().GetMethod("AddItemToInventory");
                                            if (addItemToInventoryMethod != null)
                                            {
                                                addItemToInventoryMethod.Invoke(inventory, new object[] { itemInstance });
                                                ModLogger.Info($"Successfully added {itemName} (x{stackCount}) using ItemInstance from registry");
                                                return;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Registry-based addition failed: {ex.Message}");
                }

                // Approach 2: Try direct inventory methods with string (fallback)
                try
                {
                    // Try multiple times for stack count
                    for (int i = 0; i < stackCount; i++)
                    {
                        var addMethod = inventory.GetType().GetMethod("AddItem");
                        if (addMethod != null)
                        {
                            addMethod.Invoke(inventory, new object[] { itemName });
                        }
                        else
                        {
                            var addByNameMethod = inventory.GetType().GetMethod("AddItemByName");
                            if (addByNameMethod != null)
                            {
                                addByNameMethod.Invoke(inventory, new object[] { itemName });
                            }
                            else
                            {
                                var giveItemMethod = inventory.GetType().GetMethod("GiveItem");
                                if (giveItemMethod != null)
                                {
                                    giveItemMethod.Invoke(inventory, new object[] { itemName });
                                }
                            }
                        }
                    }
                    ModLogger.Info($"Successfully added {itemName} (x{stackCount}) using direct methods");
                    return;
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"String-based addition methods failed: {ex.Message}");
                }

                ModLogger.Warn($"Unable to add {itemName} (x{stackCount}) back to inventory - all methods failed");
                
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error adding item {storedItem.itemName}: {ex.Message}");
            }
        }
        
        private string GetItemIdFromName(string itemName)
        {
            try
            {
                // Check if it's one of our prison items
                foreach (var prisonItemId in PrisonItemRegistry.GetPrisonItemIds())
                {
                    // Try to get the item from registry to check its name
                    try
                    {
#if !MONO
                        var registry = Il2CppScheduleOne.Registry.Instance;
#else
                        var registry = ScheduleOne.Registry.Instance;
#endif
                        if (registry != null)
                        {
                            var getItemMethod = registry.GetType().GetMethod("GetItem");
                            if (getItemMethod != null)
                            {
                                var item = getItemMethod.Invoke(registry, new object[] { prisonItemId });
                                if (item != null)
                                {
                                    // Get the name from the item definition
                                    var nameField = item.GetType().GetField("Name");
                                    if (nameField != null)
                                    {
                                        var defName = nameField.GetValue(item)?.ToString();
                                        if (defName == itemName)
                                        {
                                            return prisonItemId;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"Error checking prison item {prisonItemId}: {ex.Message}");
                    }
                }
                
                // For common items, return the name as ID (fallback)
                return itemName.ToLower().Replace(" ", "");
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error getting item ID from name {itemName}: {ex.Message}");
                return null;
            }
        }
        
        private void EnablePlayerPhone()
        {
            try
            {
                ModLogger.Info("Re-enabling player phone...");
                
                // Try to re-enable phone through different approaches
                
                // Approach 1: Enable through PlayerInventory
                var playerInventory = PlayerSingleton<PlayerInventory>.Instance;
                if (playerInventory != null)
                {
                    // Try to enable phone interaction
                    var enablePhoneMethod = playerInventory.GetType().GetMethod("EnablePhone");
                    if (enablePhoneMethod != null)
                    {
                        enablePhoneMethod.Invoke(playerInventory, null);
                        ModLogger.Info("Phone enabled via EnablePhone method");
                        return;
                    }
                    
                    // Try to set phone enabled to true
                    var phoneEnabledField = playerInventory.GetType().GetField("phoneEnabled");
                    if (phoneEnabledField != null)
                    {
                        phoneEnabledField.SetValue(playerInventory, true);
                        ModLogger.Info("Phone enabled via phoneEnabled field");
                        return;
                    }
                }
                
                // Approach 2: Try to access phone directly
                try
                {
                    // Look for phone singleton or manager
                    var phoneTypes = new string[] { "PhoneManager", "Phone", "PlayerPhone" };
                    
                    foreach (var phoneTypeName in phoneTypes)
                    {
                        var phoneType = System.Type.GetType($"ScheduleOne.UI.Phone.{phoneTypeName}");
                        if (phoneType == null && !string.IsNullOrEmpty(phoneTypeName))
                        {
                            phoneType = System.Type.GetType($"Il2CppScheduleOne.UI.Phone.{phoneTypeName}");
                        }
                        
                        if (phoneType != null)
                        {
                            var instanceProperty = phoneType.GetProperty("Instance");
                            if (instanceProperty != null)
                            {
                                var phoneInstance = instanceProperty.GetValue(null);
                                if (phoneInstance != null)
                                {
                                    var setEnabledMethod = phoneType.GetMethod("SetEnabled");
                                    if (setEnabledMethod != null)
                                    {
                                        setEnabledMethod.Invoke(phoneInstance, new object[] { true });
                                        ModLogger.Info($"Phone enabled via {phoneTypeName}.SetEnabled");
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Direct phone access failed: {ex.Message}");
                }
                
                ModLogger.Info("Phone marked as returned (enable mechanism needs refinement)");
                
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error enabling phone: {ex.Message}");
            }
        }
        
        private void ClearStoredItemsForPlayer(Player player)
        {
            try
            {
                // Clear from persistent storage
                var persistentData = PersistentPlayerData.Instance;
                persistentData.ClearPlayerSnapshot(player);

                // Also clear from player handler for compatibility
                var playerHandler = Behind_Bars.Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    playerHandler.ClearConfiscatedItems();
                }

                ModLogger.Info("Cleared stored items from persistent storage and player record");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error clearing stored items: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore player's original clothing from PersistentPlayerData
        /// </summary>
        private void RestorePlayerClothing(Player player)
        {
            try
            {
                ModLogger.Info("Restoring player's original clothing from persistent data...");

                var persistentData = PersistentPlayerData.Instance;
                if (persistentData != null)
                {
                    persistentData.RestorePlayerClothing(player);
                    ModLogger.Info("Original clothing restored");
                }
                else
                {
                    ModLogger.Error("PersistentPlayerData not available for clothing restoration");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error restoring clothing: {ex.Message}");
            }
        }
        
        private void CompletePickup()
        {
            isProcessing = false;
            legalItems.Clear();
            contrabandItems.Clear();
            
            // Update interaction state
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Items retrieved");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
            }
            
            // Reset to default after a delay
            MelonCoroutines.Start(ResetInteractionDelay());
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ResetInteractionDelay()
        {
            yield return new WaitForSeconds(3f);
            
            if (interactableObject != null && !isProcessing && !storageSessionActive)
            {
                interactableObject.SetMessage("Retrieve personal belongings");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
        }
        
        // Cache for reducing repeated calls
        private Player lastCheckedPlayer = null;
        private bool lastPlayerHasItems = false;
        private float lastItemCheckTime = 0f;
        private const float ITEM_CHECK_INTERVAL = 5f; // Check every 5 seconds instead of every frame

        public bool HasItemsForPlayer(Player player)
        {
            if (player == null) return false;

            // Use cache to reduce repeated expensive calls
            if (lastCheckedPlayer == player && Time.time - lastItemCheckTime < ITEM_CHECK_INTERVAL)
            {
                return lastPlayerHasItems;
            }

            try
            {
                var persistentData = PersistentPlayerData.Instance;
                var legalItems = persistentData.GetLegalItemsForPlayer(player);
                var contrabandItems = persistentData.GetContrabandItemsForPlayer(player);

                bool hasItems = (legalItems != null && legalItems.Count > 0) ||
                               (contrabandItems != null && contrabandItems.Count > 0);

                // Update cache
                lastCheckedPlayer = player;
                lastPlayerHasItems = hasItems;
                lastItemCheckTime = Time.time;

                return hasItems;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error checking for items: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Enable the pickup station when a player is being released
        /// </summary>
        public void EnableForRelease(Player player)
        {
            ModLogger.Info($"Enabling inventory pickup station for {player.name}");
            gameObject.SetActive(true);

            if (interactableObject != null)
            {
                interactableObject.SetMessage("Access personal belongings storage");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }

            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Visit the storage station to retrieve your personal belongings",
                    NotificationType.Instruction
                );
            }
        }

        /// <summary>
        /// Note: Player teleportation is now handled by the ReleaseManager and ReleaseOfficer
        /// This method is no longer used in the new release workflow
        /// </summary>

        void Update()
        {
            // Update interaction state - allow access even if storage is empty (for clothing restoration)
            if (!isProcessing && !storageSessionActive && interactableObject != null)
            {
                // Always allow access to storage, even if empty - player needs to restore clothing
                interactableObject.SetMessage("Access personal belongings storage");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
            else if (storageSessionActive && interactableObject != null)
            {
                interactableObject.SetMessage("Collect Personal Belongings");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
            }
        }
    }
}