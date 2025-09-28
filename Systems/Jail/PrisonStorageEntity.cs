using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Data;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.Storage;
using ScheduleOne.ItemFramework;
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Interactive storage entity for prison inventory pickup station
    /// Allows players to manually retrieve their stored belongings
    /// </summary>
    public class PrisonStorageEntity : StorageEntity
    {
#if !MONO
        public PrisonStorageEntity(System.IntPtr ptr) : base(ptr) { }
#endif

        private Player targetPlayer;
        private List<PersistentPlayerData.StoredItem> playerLegalItems;
        private bool isPopulated = false;

        public override void Awake()
        {
            // Configure storage entity BEFORE base.Awake() which creates slots
            StorageEntityName = "Personal Belongings Storage";
            StorageEntitySubtitle = "Retrieve your stored items";
            SlotCount = 8; // Match player inventory size
            AccessSettings = EAccessSettings.SinglePlayerOnly;
            MaxAccessDistance = 3f;
            DisplayRowCount = 2; // Show in 2 rows (4x2 grid)

            base.Awake();

            // Ensure ItemSlots list is properly initialized
            if (ItemSlots == null)
            {
                ItemSlots = new List<ItemSlot>();
            }

            // Create slots if they don't exist
            while (ItemSlots.Count < SlotCount)
            {
                ItemSlot itemSlot = new ItemSlot(SlotsAreFilterable);
                itemSlot.onItemDataChanged += ContentsChanged;
                itemSlot.SetSlotOwner(this);
                ItemSlots.Add(itemSlot);
            }

            ModLogger.Info($"PrisonStorageEntity initialized with {ItemSlots.Count} slots");
        }

        /// <summary>
        /// Populate storage with player's legal items for retrieval
        /// </summary>
        public void PopulateWithPlayerItems(Player player)
        {
            if (isPopulated)
            {
                ModLogger.Debug("Storage already populated, clearing first");
                ClearContents();
            }

            targetPlayer = player;
            playerLegalItems = new List<PersistentPlayerData.StoredItem>();

            try
            {
                // Get legal items from persistent storage
                var persistentData = PersistentPlayerData.Instance;
                var legalItems = persistentData.GetLegalItemsForPlayer(player);

                if (legalItems != null && legalItems.Count > 0)
                {
                    playerLegalItems.AddRange(legalItems);
                    ModLogger.Info($"Found {legalItems.Count} legal items to populate in storage");

                    // Convert stored items to ItemInstances and add to storage slots
                    PopulateStorageSlots();
                    isPopulated = true;
                }
                else
                {
                    ModLogger.Info("No legal items found for player");
                    isPopulated = true; // Still mark as populated even if empty
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error populating storage with player items: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert stored items to ItemInstances and populate storage slots
        /// </summary>
        private void PopulateStorageSlots()
        {
            int slotIndex = 0;

            foreach (var storedItem in playerLegalItems)
            {
                if (slotIndex >= ItemSlots.Count)
                {
                    ModLogger.Warn("Not enough storage slots for all items");
                    break;
                }

                try
                {
                    var itemInstance = CreateItemInstanceFromStoredItem(storedItem);
                    if (itemInstance != null)
                    {
                        ItemSlots[slotIndex].SetStoredItem(itemInstance, false);
                        ModLogger.Debug($"Populated slot {slotIndex} with {storedItem.itemName} x{storedItem.stackCount}");
                        slotIndex++;
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error creating item instance for {storedItem.itemName}: {ex.Message}");
                }
            }

            ModLogger.Info($"Populated {slotIndex} storage slots with player items");
        }

        /// <summary>
        /// Create ItemInstance from stored item data
        /// </summary>
        private ItemInstance CreateItemInstanceFromStoredItem(PersistentPlayerData.StoredItem storedItem)
        {
            ModLogger.Info($"Attempting to create ItemInstance for: {storedItem.itemName} (ID: {storedItem.itemId}, Count: {storedItem.stackCount})");

            try
            {
                if (!string.IsNullOrEmpty(storedItem.itemId) && storedItem.itemId != "unknown")
                {
                    ModLogger.Debug($"Trying registry lookup for item ID: {storedItem.itemId}");

                    // Try to get item from registry using stored ID
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
                            var itemDef = getItemMethod.Invoke(registry, new object[] { storedItem.itemId });
                            if (itemDef != null)
                            {
                                ModLogger.Debug($"Found item definition for {storedItem.itemId}");

                                var getDefaultInstanceMethod = itemDef.GetType().GetMethod("GetDefaultInstance");
                                if (getDefaultInstanceMethod != null)
                                {
                                    var itemInstance = getDefaultInstanceMethod.Invoke(itemDef, null) as ItemInstance;
                                    if (itemInstance != null)
                                    {
                                        ModLogger.Debug($"Created ItemInstance for {storedItem.itemName}");

                                        // Set quantity if greater than 1
                                        if (storedItem.stackCount > 1)
                                        {
                                            try
                                            {
                                                var quantityProperty = itemInstance.GetType().GetProperty("Quantity");
                                                if (quantityProperty != null)
                                                {
                                                    quantityProperty.SetValue(itemInstance, storedItem.stackCount);
                                                    ModLogger.Debug($"Set quantity to {storedItem.stackCount}");
                                                }
                                            }
                                            catch (System.Exception qex)
                                            {
                                                ModLogger.Debug($"Could not set quantity: {qex.Message}");
                                            }
                                        }

                                        ModLogger.Info($"Successfully created ItemInstance for {storedItem.itemName}");
                                        return itemInstance;
                                    }
                                    else
                                    {
                                        ModLogger.Warn($"GetDefaultInstance returned null for {storedItem.itemId}");
                                    }
                                }
                                else
                                {
                                    ModLogger.Warn($"No GetDefaultInstance method found for {storedItem.itemId}");
                                }
                            }
                            else
                            {
                                ModLogger.Warn($"No item definition found for ID: {storedItem.itemId}");
                            }
                        }
                        else
                        {
                            ModLogger.Warn("No GetItem method found on Registry");
                        }
                    }
                    else
                    {
                        ModLogger.Warn("Registry instance is null");
                    }
                }
                else
                {
                    ModLogger.Warn($"Invalid item ID for {storedItem.itemName}: '{storedItem.itemId}' - trying name-based lookup");
                }

                // Fallback: Try to find item by name pattern matching
                if (TryCreateItemByName(storedItem, out ItemInstance fallbackItem))
                {
                    ModLogger.Info($"Successfully created {storedItem.itemName} using name-based fallback");
                    return fallbackItem;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Exception creating item instance for {storedItem.itemName}: {ex.Message}");
            }

            ModLogger.Warn($"Failed to create ItemInstance for {storedItem.itemName}");
            return null;
        }

        /// <summary>
        /// Try to create item by name pattern matching when ID is unknown
        /// </summary>
        private bool TryCreateItemByName(PersistentPlayerData.StoredItem storedItem, out ItemInstance itemInstance)
        {
            itemInstance = null;

            try
            {
                // Common item name to ID mappings
                string itemId = storedItem.itemName.ToLower().Replace(" ", "") switch
                {
                    "m1911" => "m1911",
                    "wateringcan" => "wateringcan",
                    "m1911magazine" => "m1911magazine",
                    "cash" => "cash",
                    "phone" => "phone",
                    _ => null
                };

                if (itemId == null)
                {
                    ModLogger.Debug($"No known ID mapping for item name: {storedItem.itemName}");
                    return false;
                }

                ModLogger.Debug($"Trying fallback item ID '{itemId}' for '{storedItem.itemName}'");

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
                            var getDefaultInstanceMethod = itemDef.GetType().GetMethod("GetDefaultInstance");
                            if (getDefaultInstanceMethod != null)
                            {
                                itemInstance = getDefaultInstanceMethod.Invoke(itemDef, null) as ItemInstance;
                                if (itemInstance != null)
                                {
                                    // Set quantity
                                    if (storedItem.stackCount > 1)
                                    {
                                        try
                                        {
                                            var quantityProperty = itemInstance.GetType().GetProperty("Quantity");
                                            quantityProperty?.SetValue(itemInstance, storedItem.stackCount);
                                        }
                                        catch { }
                                    }

                                    ModLogger.Info($"Created {storedItem.itemName} using fallback ID: {itemId}");
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Name-based fallback failed for {storedItem.itemName}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Called when storage is opened by player
        /// </summary>
        public override void OnOpened()
        {
            base.OnOpened();
            ModLogger.Info($"Prison storage opened by {CurrentAccessor?.name ?? "unknown player"}");
        }

        /// <summary>
        /// Called when storage is closed
        /// </summary>
        public override void OnClosed()
        {
            base.OnClosed();
            ModLogger.Info("Prison storage closed");

            // Storage session complete - notify inventory pickup station
            var pickupStation = GetComponentInParent<InventoryPickupStation>();
            if (pickupStation != null)
            {
                pickupStation.OnStorageSessionComplete();
            }
        }

        /// <summary>
        /// Check if player can access this storage
        /// </summary>
        public override bool CanBeOpened()
        {
            if (!base.CanBeOpened())
                return false;

            // Only allow access if storage has been populated
            if (!isPopulated)
            {
                ModLogger.Debug("Storage not populated yet");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Get count of items still in storage
        /// </summary>
        public int GetRemainingItemCount()
        {
            int count = 0;
            foreach (var slot in ItemSlots)
            {
                if (slot.ItemInstance != null)
                    count += slot.Quantity;
            }
            return count;
        }

        /// <summary>
        /// Clear all storage and reset state
        /// </summary>
        public void ResetStorage()
        {
            ClearContents();
            isPopulated = false;
            targetPlayer = null;
            playerLegalItems?.Clear();
            ModLogger.Info("Prison storage reset");
        }
    }
}