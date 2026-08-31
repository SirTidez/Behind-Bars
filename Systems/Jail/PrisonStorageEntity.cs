using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Data;
using System;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Registry = Il2CppScheduleOne.Registry;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
#else
using ScheduleOne.Storage;
using ScheduleOne.ItemFramework;
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
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

        // Population state is a local presentation snapshot for the current storage session.  The
        // item slots are rebuilt from persisted legal-item data; failedItemsCache only suppresses
        // repeated diagnostics and is deliberately retained until a full release reset.
        private Player targetPlayer;
        private List<PersistentPlayerData.StoredItem> playerLegalItems;
        private bool isPopulated = false;
        private HashSet<string> failedItemsCache = new HashSet<string>(); // Cache failed items to prevent log spam
        private bool _awakeInitialized;

        /// <summary>
        /// Configure the storage entity once, create its local item slots, and attach the close
        /// callback used to notify the owning pickup station.  The Mono and IL2CPP base-Awake
        /// paths intentionally differ; see the runtime-specific comments below.
        /// </summary>
        public override void Awake()
        {
            if (_awakeInitialized)
                return;

            _awakeInitialized = true;

            // Configure storage entity BEFORE base.Awake() which creates slots
            StorageEntityName = "Personal Belongings Storage";
            StorageEntitySubtitle = "Retrieve your stored items";
            SlotCount = 8; // Match player inventory size
            AccessSettings = EAccessSettings.SinglePlayerOnly;
            MaxAccessDistance = 3f;
            DisplayRowCount = 2; // Show in 2 rows (4x2 grid)

#if MONO
            base.Awake();
#else
            // IL2CPP: calling base.Awake() on injected StorageEntity derivatives can recurse
            // through IL2CPP runtime invoke and cause stack overflow.
#endif

            // CRITICAL: Add NetworkObject component for StorageEntity.Open() to work
            // StorageEntity expects a NetworkObject for network RPCs
            var networkObject = GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                networkObject = gameObject.AddComponent<NetworkObject>();
                ModLogger.Debug("Added NetworkObject component to PrisonStorageEntity");
            }

            // The component must remain enabled for StorageEntity's local Open/Close API, but this
            // entity is used as a local-only pickup surface; no multiplayer storage synchronization
            // is established here.
            try
            {
                networkObject.enabled = true;
                ModLogger.Debug("NetworkObject configured for local-only storage");
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
#if MONO
            onClosed += HandleStorageClosed;
#else
            onClosed += new System.Action(HandleStorageClosed);
#endif

            ModLogger.Debug($"PrisonStorageEntity initialized with {ItemSlots.Count} slots (local-only mode)");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void HandleStorageClosed()
        {
            ModLogger.Info("Storage closed by player");

            Transform cursor = transform;
            while (cursor != null)
            {
                var pickupStation = BBHelpers.GetComponentSafe<InventoryPickupStation>(cursor.gameObject);
                if (pickupStation != null)
                {
                    pickupStation.OnStorageSessionComplete();
                    break;
                }

                cursor = cursor.parent;
            }
        }

        /// <summary>
        /// Reset the storage for a new release.  This clears slot contents, population ownership,
        /// the persisted-item snapshot, failed-item diagnostics, and an open storage menu when the
        /// game's storage-menu singleton is available.
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
                Singleton<StorageMenu>.Instance?.Close();
            }

            ModLogger.Info("PrisonStorageEntity: Reset complete");
        }

        /// <summary>
        /// Populate the local storage view from the player's currently persisted legal-item
        /// snapshot.  Repeated calls for the same player are ignored; a different player clears
        /// the previous contents.  An empty or null item result still marks the view as
        /// populated, while item-conversion failures are logged and skipped.
        /// </summary>
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
                var persistentData = Core.ResolvePersistentPlayerData();
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

                object itemInstance = CreateItemInstanceFromStoredItem(storedItem);
                if (itemInstance != null)
                {
                    try
                    {
                        // Use reflection to invoke InsertItem without hard-binding the IL2CPP item types
                        var insertItemMethod = GetType().GetMethod("InsertItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (insertItemMethod != null)
                        {
                            insertItemMethod.Invoke(this, new object[] { itemInstance, false });
                        }
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
        private object CreateItemInstanceFromStoredItem(PersistentPlayerData.StoredItem storedItem)
        {
            ModLogger.Info($"Attempting to create ItemInstance for: {storedItem.itemName} (ID: {storedItem.itemId}, Count: {storedItem.stackCount})");

            try
            {
                if (!string.IsNullOrEmpty(storedItem.itemId) && storedItem.itemId != "unknown")
                {
                    ModLogger.Debug($"Trying registry lookup for item ID: {storedItem.itemId}");

                    // Use reflection to keep the IL2CPP item framework surface isolated
#if !MONO
                    var registry = Il2CppScheduleOne.Registry.Instance;
#else
                    var registry = ScheduleOne.Registry.Instance;
#endif
                    var itemDef = PrisonItemRegistry.GetRegistryItemDefinition(registry, storedItem.itemId);

                    if (itemDef != null)
                    {
                        ModLogger.Debug($"Found item definition for {storedItem.itemId}");

                        // Create ItemInstance using GetDefaultInstance - NO REFLECTION
                        var getDefaultInstanceMethod = itemDef.GetType().GetMethod("GetDefaultInstance", new System.Type[] { typeof(int) });
                        var itemInstance = getDefaultInstanceMethod != null
                            ? getDefaultInstanceMethod.Invoke(itemDef, new object[] { storedItem.stackCount })
                            : null;
                        if (itemInstance != null)
                        {
                            // Special handling for CashInstance - restore the Balance
                            if (storedItem.itemType == "CashInstance" && storedItem.cashBalance > 0f)
                            {
                                var setBalanceMethod = itemInstance.GetType().GetMethod("SetBalance");
                                if (setBalanceMethod != null)
                                {
                                    setBalanceMethod.Invoke(itemInstance, new object[] { storedItem.cashBalance });
                                    ModLogger.Info($"✓ Set cash balance to ${storedItem.cashBalance:N2}");
                                }
                            }

                            // CRITICAL: Weapons should always be returned EMPTY (Value = 0)
                            // IntegerItemInstance stores gun ammo in the Value field
                            if (storedItem.itemType == "IntegerItemInstance")
                            {
                                var setValueMethod = itemInstance.GetType().GetMethod("SetValue");
                                if (setValueMethod != null)
                                {
                                    setValueMethod.Invoke(itemInstance, new object[] { 0 });
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
                if (TryFindItemInRegistryByName(storedItem.itemName, storedItem.stackCount, out object registryItem))
                {
                    ModLogger.Info($"Successfully created {storedItem.itemName} via Registry name search");
                    return registryItem;
                }

                // Last resort: Try old name pattern matching
                if (TryCreateItemByName(storedItem, out object fallbackItem))
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
        private bool TryFindItemInRegistryByName(string itemName, int quantity, out object itemInstance)
        {
            itemInstance = null;

            try
            {
                // Try direct registry lookup with lowercase name (common pattern)
#if !MONO
                var registry = Il2CppScheduleOne.Registry.Instance;
#else
                var registry = ScheduleOne.Registry.Instance;
#endif
                var itemDef = PrisonItemRegistry.GetRegistryItemDefinition(registry, itemName.ToLower().Replace(" ", ""));
                if (itemDef != null)
                {
                    var getDefaultInstanceMethod = itemDef.GetType().GetMethod("GetDefaultInstance", new System.Type[] { typeof(int) });
                    itemInstance = getDefaultInstanceMethod != null
                        ? getDefaultInstanceMethod.Invoke(itemDef, new object[] { quantity })
                        : null;
                    if (itemInstance != null)
                    {
                        ModLogger.Info($"Created ItemInstance for '{itemName}' using direct Registry call");
                        return true;
                    }
                }

                // Manual search of ItemRegistry as fallback
#if !MONO
                var registry2 = Il2CppScheduleOne.Registry.Instance;
#else
                var registry2 = ScheduleOne.Registry.Instance;
#endif
                if (registry2 == null)
                {
                    ModLogger.Error("Registry instance is null");
                    return false;
                }

                // If that didn't work, try searching ItemRegistry field manually
                var itemRegistryField = registry2.GetType().GetField("ItemRegistry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (itemRegistryField != null)
                {
                    var itemRegistry = itemRegistryField.GetValue(registry2) as System.Collections.IList;
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
                                    itemInstance = getDefaultInstanceMethod.Invoke(definition, new object[] { quantity });
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
        private bool TryCreateItemByName(PersistentPlayerData.StoredItem storedItem, out object itemInstance)
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
                    var itemDef = PrisonItemRegistry.GetRegistryItemDefinition(registry, itemId);
                    if (itemDef != null)
                    {
                        var getDefaultInstanceMethod = itemDef.GetType().GetMethod("GetDefaultInstance");
                        if (getDefaultInstanceMethod != null)
                        {
                            itemInstance = getDefaultInstanceMethod.Invoke(itemDef, null);
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
        /// Return the sum of quantities in occupied storage slots, rather than the number of
        /// occupied slots.  Slots whose item instance cannot be read are not counted.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public int GetRemainingItemCount()
        {
            int count = 0;
            foreach (var slot in ItemSlots)
            {
                var itemInstanceProperty = slot.GetType().GetProperty("ItemInstance");
                if (itemInstanceProperty != null && itemInstanceProperty.GetValue(slot) != null)
                {
                    count += slot.Quantity;
                }
            }
            return count;
        }

        /// <summary>
        /// Clear the current slot contents and population snapshot.  This reduced reset leaves the
        /// failed-item diagnostic cache and any open storage menu untouched; use
        /// <see cref="ResetForNewRelease"/> for the complete release-session reset.
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
