using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Data;
using System;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
using Registry = Il2CppScheduleOne.Registry;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
#else
using ScheduleOne.Storage;
using ScheduleOne.ItemFramework;
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
using Registry = ScheduleOne.Registry;
using FishNet.Connection;
using FishNet.Object;
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
        private HashSet<string> failedItemsCache = new HashSet<string>(); // Cache failed items to prevent log spam

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

            // CRITICAL: Add NetworkObject component for StorageEntity.Open() to work
            // StorageEntity expects a NetworkObject for network RPCs
            var networkObject = GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                networkObject = gameObject.AddComponent<NetworkObject>();
                ModLogger.Info("Added NetworkObject component to PrisonStorageEntity");
            }

            // Set as local-only (no actual networking)
            try
            {
                networkObject.enabled = true;
                ModLogger.Info("NetworkObject configured for local-only storage");
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"NetworkObject setup: {ex.Message}");
            }

            // Ensure ItemSlots list is properly initialized
            if (ItemSlots == null)
            {
#if MONO
                ItemSlots = new List<ItemSlot>();
#else
                ItemSlots = new Il2CppSystem.Collections.Generic.List<ItemSlot>();
#endif
            }

            // Create slots if they don't exist
            while (ItemSlots.Count < SlotCount)
            {
                ItemSlot itemSlot = new ItemSlot(SlotsAreFilterable);
#if MONO
                itemSlot.onItemDataChanged += ContentsChanged;
#else
                itemSlot.onItemDataChanged += new System.Action(ContentsChanged);
#endif
#if MONO
                itemSlot.SetSlotOwner(this);
#else
                itemSlot.SetSlotOwner(this.Cast<Il2CppScheduleOne.ItemFramework.IItemSlotOwner>());
#endif
                ItemSlots.Add(itemSlot);
            }

            // Subscribe to onClosed event
            if (onClosed != null)
            {
#if MONO
                onClosed += HandleStorageClosed;
#else
                onClosed.AddListener(new System.Action(HandleStorageClosed));
#endif
            }

            ModLogger.Info($"PrisonStorageEntity initialized with {ItemSlots.Count} slots (local-only mode)");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void HandleStorageClosed()
        {
            ModLogger.Info("Storage closed by player");

            var pickupStation = GetComponentInParent<InventoryPickupStation>();
            if (pickupStation != null)
            {
                pickupStation.OnStorageSessionComplete();
            }
        }

        /// <summary>
        /// Populate storage with player's legal items for retrieval
        /// </summary>
        /// <summary>
        /// Reset storage for a new release (clear all items and flags)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void ResetForNewRelease()
        {
            ModLogger.Info("PrisonStorageEntity: Resetting for new release");

            // Clear all contents
            ClearContents();

            // Reset all flags
            isPopulated = false;
            targetPlayer = null;
            playerLegalItems = new List<PersistentPlayerData.StoredItem>();
            failedItemsCache.Clear();

            // Close storage if it's open
            if (IsOpened)
            {
                Close();
            }

            ModLogger.Info("PrisonStorageEntity: Reset complete");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        public void PopulateWithPlayerItems(Player player)
        {
            // Prevent repeated population for the same player
            if (isPopulated && targetPlayer == player)
            {
                ModLogger.Debug($"Storage already populated for {player.name}, skipping redundant population");
                return;
            }

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
#if !MONO
        [HideFromIl2Cpp]
#endif
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

                var itemInstance = CreateItemInstanceFromStoredItem(storedItem);
                if (itemInstance != null)
                {
                    try
                    {
                        // Use the game's InsertItem method instead of directly setting slots
                        InsertItem(itemInstance, false);
                        ModLogger.Info($"✓ Inserted {storedItem.itemName} x{storedItem.stackCount} into storage");
                        slotIndex++;
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Error inserting {storedItem.itemName} into storage: {ex.Message}\n{ex.StackTrace}");
                    }
                }
                else
                {
                    ModLogger.Warn($"CreateItemInstanceFromStoredItem returned null for {storedItem.itemName}");
                }
            }

            ModLogger.Info($"Populated {slotIndex} storage slots with player items");
        }

        /// <summary>
        /// Create ItemInstance from stored item data
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private ItemInstance CreateItemInstanceFromStoredItem(PersistentPlayerData.StoredItem storedItem)
        {
            ModLogger.Info($"Attempting to create ItemInstance for: {storedItem.itemName} (ID: {storedItem.itemId}, Count: {storedItem.stackCount})");

            try
            {
                if (!string.IsNullOrEmpty(storedItem.itemId) && storedItem.itemId != "unknown")
                {
                    ModLogger.Debug($"Trying registry lookup for item ID: {storedItem.itemId}");

                    // Use static Registry.GetItem() directly - NO REFLECTION (same as working GivePrisonItem)
#if !MONO
                    var itemDef = Il2CppScheduleOne.Registry.GetItem(storedItem.itemId);
#else
                    var itemDef = ScheduleOne.Registry.GetItem(storedItem.itemId);
#endif

                    if (itemDef != null)
                    {
                        ModLogger.Debug($"Found item definition for {storedItem.itemId}");

                        // Create ItemInstance using GetDefaultInstance - NO REFLECTION
                        var itemInstance = itemDef.GetDefaultInstance(storedItem.stackCount);
                        if (itemInstance != null)
                        {
                            // Special handling for CashInstance - restore the Balance
                            if (storedItem.itemType == "CashInstance" && storedItem.cashBalance > 0f)
                            {
#if !MONO
                                var cashInstance = itemInstance as Il2CppScheduleOne.ItemFramework.CashInstance;
#else
                                var cashInstance = itemInstance as ScheduleOne.ItemFramework.CashInstance;
#endif
                                if (cashInstance != null)
                                {
                                    cashInstance.SetBalance(storedItem.cashBalance);
                                    ModLogger.Info($"✓ Set cash balance to ${storedItem.cashBalance:N2}");
                                }
                            }

                            // CRITICAL: Weapons should always be returned EMPTY (Value = 0)
                            // IntegerItemInstance stores gun ammo in the Value field
                            if (storedItem.itemType == "IntegerItemInstance")
                            {
#if !MONO
                                var integerInstance = itemInstance as Il2CppScheduleOne.ItemFramework.IntegerItemInstance;
#else
                                var integerInstance = itemInstance as ScheduleOne.ItemFramework.IntegerItemInstance;
#endif
                                if (integerInstance != null)
                                {
                                    integerInstance.SetValue(0); // Empty the gun
                                    ModLogger.Info($"✓ Set weapon Value to 0 (empty gun)");
                                }
                            }

                            ModLogger.Info($"✓ Successfully created ItemInstance for {storedItem.itemName} in storage");
                            return itemInstance;
                        }
                        else
                        {
                            ModLogger.Warn($"GetDefaultInstance returned null for {storedItem.itemId}");
                        }
                    }
                    else
                    {
                        ModLogger.Warn($"Registry.GetItem() returned null for ID: {storedItem.itemId}");
                    }
                }
                else
                {
                    ModLogger.Warn($"Invalid item ID for {storedItem.itemName}: '{storedItem.itemId}' - trying name-based lookup");
                }

                // Fallback: Search all items in Registry by matching name
                ModLogger.Info($"Attempting name-based Registry search for '{storedItem.itemName}'");
                if (TryFindItemInRegistryByName(storedItem.itemName, storedItem.stackCount, out ItemInstance registryItem))
                {
                    ModLogger.Info($"Successfully created {storedItem.itemName} via Registry name search");
                    return registryItem;
                }

                // Last resort: Try old name pattern matching
                if (TryCreateItemByName(storedItem, out ItemInstance fallbackItem))
                {
                    ModLogger.Info($"Successfully created {storedItem.itemName} using legacy name-based fallback");
                    return fallbackItem;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Exception creating item instance for {storedItem.itemName}: {ex.Message}");
            }

            // Only log the first failure for each item to prevent spam
            if (!failedItemsCache.Contains(storedItem.itemName))
            {
                failedItemsCache.Add(storedItem.itemName);
                ModLogger.Warn($"Failed to create ItemInstance for {storedItem.itemName}");
            }
            return null;
        }

        /// <summary>
        /// Search the entire Registry by item name to find matching ItemDefinition
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool TryFindItemInRegistryByName(string itemName, int quantity, out ItemInstance itemInstance)
        {
            itemInstance = null;

            try
            {
                // Try direct static call with lowercase name (common pattern)
#if !MONO
                var itemDef = Il2CppScheduleOne.Registry.GetItem(itemName.ToLower().Replace(" ", ""));
#else
                var itemDef = ScheduleOne.Registry.GetItem(itemName.ToLower().Replace(" ", ""));
#endif
                if (itemDef != null)
                {
                    itemInstance = itemDef.GetDefaultInstance(quantity);
                    if (itemInstance != null)
                    {
                        ModLogger.Info($"Created ItemInstance for '{itemName}' using direct Registry call");
                        return true;
                    }
                }

                // Manual search of ItemRegistry as fallback
#if !MONO
                var registry = Il2CppScheduleOne.Registry.Instance;
#else
                var registry = ScheduleOne.Registry.Instance;
#endif
                if (registry == null)
                {
                    ModLogger.Error("Registry instance is null");
                    return false;
                }

                // If that didn't work, try searching ItemRegistry field manually
                var itemRegistryField = registry.GetType().GetField("ItemRegistry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (itemRegistryField != null)
                {
                    var itemRegistry = itemRegistryField.GetValue(registry) as System.Collections.IList;
                    if (itemRegistry != null)
                    {
                        ModLogger.Info($"Searching {itemRegistry.Count} items in Registry for '{itemName}'");

                        // Search through all registered items
                        foreach (var itemRegister in itemRegistry)
                        {
                            if (itemRegister == null) continue;

                            var definitionField = itemRegister.GetType().GetField("Definition");
                            if (definitionField == null) continue;

                            var definition = definitionField.GetValue(itemRegister);
                            if (definition == null) continue;

                            var nameProperty = definition.GetType().GetProperty("Name");
                            if (nameProperty == null) continue;

                            var defName = nameProperty.GetValue(definition)?.ToString();
                            if (string.IsNullOrEmpty(defName)) continue;

                            if (defName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                            {
                                ModLogger.Info($"Found matching item: {defName}");

                                var getDefaultInstanceMethod = definition.GetType().GetMethod("GetDefaultInstance", new System.Type[] { typeof(int) });
                                if (getDefaultInstanceMethod != null)
                                {
                                    itemInstance = getDefaultInstanceMethod.Invoke(definition, new object[] { quantity }) as ItemInstance;
                                    if (itemInstance != null)
                                    {
                                        ModLogger.Info($"Created ItemInstance for '{itemName}' via manual Registry search");
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }

                ModLogger.Warn($"No matching item found in Registry for '{itemName}'");
                return false;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error searching Registry by name: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Try to create item by name pattern matching when ID is unknown
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
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
                // Only log the first failure for each item to prevent spam
                if (!failedItemsCache.Contains(storedItem.itemName))
                {
                    failedItemsCache.Add(storedItem.itemName);
                    ModLogger.Debug($"Name-based fallback failed for {storedItem.itemName}: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Called when storage is opened by player
        /// </summary>
        public override void OnOpened()
        {
            base.OnOpened();
            ModLogger.Info($"Prison storage opened by {CurrentPlayerAccessor?.name ?? "unknown player"}");
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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
#if !MONO
        [HideFromIl2Cpp]
#endif
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