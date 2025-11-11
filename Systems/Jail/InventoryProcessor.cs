using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Vehicles;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.ItemFramework;
using ScheduleOne.Vehicles;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Handles proper inventory processing during arrest - confiscates contraband and locks legal items
    /// Uses the game's actual contraband detection system for accurate identification
    /// </summary>
    public class InventoryProcessor
    {
        /// <summary>
        /// Result of inventory processing
        /// </summary>
        public class InventoryProcessResult
        {
            public List<string> ConfiscatedItems { get; set; } = new List<string>();
            public List<string> LockedItems { get; set; } = new List<string>();
            public int TotalItemsProcessed { get; set; } = 0;
            public bool Success { get; set; } = true;
            public string ErrorMessage { get; set; } = "";
        }

        private const float VEHICLE_POSSESSION_TIMEOUT = 30f; // Same as game's ArrestNoticeScreen


        /// <summary>
        /// Get all inventory slots from the player's inventory
        /// </summary>
        private static List<object> GetAllInventorySlots(PlayerInventory inventory)
        {
            var slots = new List<object>();

            try
            {
                // Use the game's GetAllInventorySlots method
                var getAllSlotsMethod = inventory.GetType().GetMethod("GetAllInventorySlots");
                if (getAllSlotsMethod != null)
                {
                    var allSlots = getAllSlotsMethod.Invoke(inventory, null);
                    if (allSlots is System.Collections.IList slotsList)
                    {
                        for (int i = 0; i < slotsList.Count; i++)
                        {
                            var slot = slotsList[i];
                            if (slot != null)
                            {
                                slots.Add(slot);
                            }
                        }
                        ModLogger.Debug($"[INVENTORY] Retrieved {slots.Count} slots using GetAllInventorySlots");
                    }
                }
                else
                {
                    ModLogger.Warn("[INVENTORY] GetAllInventorySlots method not found, using fallback");
                    // Fallback: try to get hotbar slots directly
                    var hotbarSlotsField = inventory.GetType().GetField("hotbarSlots");
                    if (hotbarSlotsField != null)
                    {
                        var hotbarSlots = hotbarSlotsField.GetValue(inventory);
                        if (hotbarSlots is System.Collections.IList hotbarList)
                        {
                            for (int i = 0; i < hotbarList.Count; i++)
                            {
                                var slot = hotbarList[i];
                                if (slot != null)
                                {
                                    slots.Add(slot);
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error getting inventory slots: {ex.Message}");
            }

            return slots;
        }

        /// <summary>
        /// Get vehicle storage slots if player recently exited a vehicle
        /// </summary>
        private static List<object> GetVehicleStorageSlots(Player player)
        {
            var slots = new List<object>();

            try
            {
                // Check if player recently exited a vehicle (same logic as ArrestNoticeScreen)
                if (player.LastDrivenVehicle != null && player.TimeSinceVehicleExit < VEHICLE_POSSESSION_TIMEOUT)
                {
                    ModLogger.Info($"[INVENTORY] Player recently exited vehicle - including storage in search");

                    var vehicle = player.LastDrivenVehicle;
                    var storageProperty = vehicle.GetType().GetProperty("Storage");
                    if (storageProperty != null)
                    {
                        var storage = storageProperty.GetValue(vehicle);
                        if (storage != null)
                        {
                            var itemSlotsProperty = storage.GetType().GetProperty("ItemSlots");
                            if (itemSlotsProperty != null)
                            {
                                var itemSlots = itemSlotsProperty.GetValue(storage);
                                if (itemSlots is System.Collections.IList vehicleSlotsList)
                                {
                                    for (int i = 0; i < vehicleSlotsList.Count; i++)
                                    {
                                        var slot = vehicleSlotsList[i];
                                        if (slot != null)
                                        {
                                            slots.Add(slot);
                                        }
                                    }
                                    ModLogger.Debug($"[INVENTORY] Retrieved {slots.Count} vehicle storage slots");
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"[INVENTORY] Error getting vehicle storage slots: {ex.Message}");
            }

            return slots;
        }

        /// <summary>
        /// Process a single inventory slot for contraband detection and confiscation
        /// </summary>
        private static void ProcessInventorySlot(object slot, InventoryProcessResult result)
        {
            try
            {
                // Get the ItemInstance from the slot
                var itemInstanceProperty = slot.GetType().GetProperty("ItemInstance");
                if (itemInstanceProperty == null) return;

                var itemInstance = itemInstanceProperty.GetValue(slot);
                if (itemInstance == null) return;

                result.TotalItemsProcessed++;

                // Check if this item is contraband
                if (IsItemContraband(itemInstance))
                {
                    // Get item name for logging
                    string itemName = GetItemDisplayName(itemInstance);
                    int stackCount = GetItemStackCount(itemInstance);

                    ModLogger.Info($"[CONTRABAND] Found illegal item: {itemName} (x{stackCount})");

                    // Confiscate the item by clearing the slot
                    var clearMethod = slot.GetType().GetMethod("ClearStoredInstance");
                    if (clearMethod != null)
                    {
                        clearMethod.Invoke(slot, null);
                        ModLogger.Info($"[CONTRABAND] Confiscated: {itemName} (x{stackCount})");

                        // Add to confiscated items list (multiple entries for stacked items)
                        for (int i = 0; i < stackCount; i++)
                        {
                            result.ConfiscatedItems.Add(itemName);
                        }
                    }
                    else
                    {
                        // Fallback: set ItemInstance to null
                        itemInstanceProperty.SetValue(slot, null);
                        ModLogger.Info($"[CONTRABAND] Confiscated (fallback): {itemName} (x{stackCount})");

                        for (int i = 0; i < stackCount; i++)
                        {
                            result.ConfiscatedItems.Add(itemName);
                        }
                    }
                }
                else
                {
                    // Legal item - will be locked but not confiscated
                    string itemName = GetItemDisplayName(itemInstance);
                    int stackCount = GetItemStackCount(itemInstance);

                    ModLogger.Debug($"[INVENTORY] Legal item will be locked: {itemName} (x{stackCount})");

                    for (int i = 0; i < stackCount; i++)
                    {
                        result.LockedItems.Add(itemName);
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error processing inventory slot: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if an item is contraband using the game's legal status system
        /// </summary>
        private static bool IsItemContraband(object itemInstance)
        {
            try
            {
                // Check if it's a product (drug) with packaging stealth
                if (IsProductInstance(itemInstance))
                {
                    return IsProductContraband(itemInstance);
                }

                // For regular items, check the Definition.legalStatus
                var definitionProperty = itemInstance.GetType().GetProperty("Definition");
                if (definitionProperty != null)
                {
                    var definition = definitionProperty.GetValue(itemInstance);
                    if (definition != null)
                    {
                        var legalStatusField = definition.GetType().GetField("legalStatus");
                        if (legalStatusField != null)
                        {
                            var legalStatus = legalStatusField.GetValue(definition);

                            // Convert to int for comparison (ELegalStatus: Legal = 0, anything else = illegal)
                            if (legalStatus is System.Enum enumValue)
                            {
                                int statusValue = System.Convert.ToInt32(enumValue);
                                return statusValue != 0; // 0 = Legal, anything else = illegal
                            }
                        }
                    }
                }

                return false; // Default to legal if we can't determine status
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"[INVENTORY] Error checking contraband status: {ex.Message}");
                return false; // Default to legal on error
            }
        }

        /// <summary>
        /// Check if an item instance is a product (drug)
        /// </summary>
        private static bool IsProductInstance(object itemInstance)
        {
            try
            {
                // Check if it's a ProductItemInstance
                var typeName = itemInstance.GetType().Name;
                return typeName.Contains("ProductItemInstance");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if a product (drug) is contraband based on packaging stealth
        /// </summary>
        private static bool IsProductContraband(object productInstance)
        {
            try
            {
                // Check the AppliedPackaging stealth level
                var appliedPackagingProperty = productInstance.GetType().GetProperty("AppliedPackaging");
                if (appliedPackagingProperty != null)
                {
                    var appliedPackaging = appliedPackagingProperty.GetValue(productInstance);
                    if (appliedPackaging == null)
                    {
                        // No packaging = visible contraband
                        return true;
                    }

                    // Check stealth level - police can detect up to a certain level
                    var stealthLevelProperty = appliedPackaging.GetType().GetProperty("StealthLevel");
                    if (stealthLevelProperty != null)
                    {
                        var stealthLevel = stealthLevelProperty.GetValue(appliedPackaging);

                        // For arrest processing, assume police can detect all stealth levels
                        // (in body searches, this would be different based on officer skill)
                        return true; // Products are always contraband during arrest
                    }
                }

                return true; // Products default to contraband
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"[INVENTORY] Error checking product contraband status: {ex.Message}");
                return true; // Default to contraband for products
            }
        }

        /// <summary>
        /// Get display name for an item
        /// </summary>
        private static string GetItemDisplayName(object itemInstance)
        {
            try
            {
                // Try Name property first
                var nameProperty = itemInstance.GetType().GetProperty("Name");
                if (nameProperty != null)
                {
                    var name = nameProperty.GetValue(itemInstance)?.ToString();
                    if (!string.IsNullOrEmpty(name)) return name;
                }

                // Try Definition.Name
                var definitionProperty = itemInstance.GetType().GetProperty("Definition");
                if (definitionProperty != null)
                {
                    var definition = definitionProperty.GetValue(itemInstance);
                    if (definition != null)
                    {
                        var defNameField = definition.GetType().GetField("Name");
                        if (defNameField != null)
                        {
                            var defName = defNameField.GetValue(definition)?.ToString();
                            if (!string.IsNullOrEmpty(defName)) return defName;
                        }
                    }
                }

                // Fallback to ID
                var idProperty = itemInstance.GetType().GetProperty("ID");
                if (idProperty != null)
                {
                    return idProperty.GetValue(itemInstance)?.ToString() ?? "Unknown Item";
                }

                return "Unknown Item";
            }
            catch
            {
                return "Unknown Item";
            }
        }

        /// <summary>
        /// Get stack count for an item
        /// </summary>
        private static int GetItemStackCount(object itemInstance)
        {
            try
            {
                var stackCountProperty = itemInstance.GetType().GetProperty("StackCount");
                if (stackCountProperty != null)
                {
                    var stackCount = stackCountProperty.GetValue(itemInstance);
                    if (stackCount is int count && count > 0) return count;
                }

                var amountProperty = itemInstance.GetType().GetProperty("Amount");
                if (amountProperty != null)
                {
                    var amount = amountProperty.GetValue(itemInstance);
                    if (amount is int amountCount && amountCount > 0) return amountCount;
                }

                return 1; // Default stack count
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// Lock the player's inventory to prevent use during jail time
        /// COMPLETELY disable inventory access - no weapons or items should be usable in jail
        /// </summary>
        public static void LockPlayerInventory(Player player)
        {
            try
            {
                ModLogger.Info("[INVENTORY] Processing inventory for arrest - removing ammo and locking access");

                var playerInventory = player.GetComponent<PlayerInventory>();
                if (playerInventory == null)
                {
#if !MONO
                    playerInventory = Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance;
#else
                    playerInventory = ScheduleOne.PlayerScripts.PlayerInventory.Instance;
#endif
                }

                if (playerInventory == null)
                {
                    ModLogger.Error("[INVENTORY] PlayerInventory not found!");
                    return;
                }

                // STEP 0: Remove ALL ammo from inventory (safety - prevent incidents in prison)
                // Note: This also runs in Harmony patch BEFORE snapshot, but we call again here as safety
                RemoveAllAmmo(playerInventory);

                // STEP 1: FIRST unequip any currently held item using multiple approaches
                try
                {
                    ModLogger.Info("[INVENTORY] Checking for equipped items to unequip...");

                    // Approach 1: Check EquippedSlotIndex property
                    var equippedSlotIndexProperty = playerInventory.GetType().GetProperty("EquippedSlotIndex");
                    if (equippedSlotIndexProperty != null)
                    {
                        int equippedIndex = (int)equippedSlotIndexProperty.GetValue(playerInventory);
                        ModLogger.Info($"[INVENTORY] EquippedSlotIndex: {equippedIndex}");

                        if (equippedIndex != -1)
                        {
                            ModLogger.Info($"[INVENTORY] Player has item equipped in slot {equippedIndex} - unequipping...");

                            // Player has something equipped - unequip it using the game's IndexAllSlots method
                            var indexAllSlotsMethod = playerInventory.GetType().GetMethod("IndexAllSlots");
                            if (indexAllSlotsMethod != null)
                            {
                                var hotbarSlot = indexAllSlotsMethod.Invoke(playerInventory, new object[] { equippedIndex });
                                if (hotbarSlot != null)
                                {
                                    var unequipMethod = hotbarSlot.GetType().GetMethod("Unequip");
                                    if (unequipMethod != null)
                                    {
                                        unequipMethod.Invoke(hotbarSlot, null);
                                        ModLogger.Info($"[INVENTORY] Successfully unequipped item from slot {equippedIndex} using game's Unequip method");
                                    }
                                    else
                                    {
                                        ModLogger.Warn("[INVENTORY] Unequip method not found on HotbarSlot");
                                    }
                                }
                                else
                                {
                                    ModLogger.Warn($"[INVENTORY] Could not get HotbarSlot for index {equippedIndex}");
                                }
                            }
                            else
                            {
                                ModLogger.Warn("[INVENTORY] IndexAllSlots method not found on PlayerInventory");
                            }

                            // Also set EquippedSlotIndex to -1 to ensure it's cleared
                            equippedSlotIndexProperty.SetValue(playerInventory, -1);
                            ModLogger.Info("[INVENTORY] Set EquippedSlotIndex to -1");
                        }
                        else
                        {
                            ModLogger.Info("[INVENTORY] No item currently equipped according to EquippedSlotIndex (-1)");
                        }
                    }
                    else
                    {
                        ModLogger.Warn("[INVENTORY] EquippedSlotIndex property not found on PlayerInventory");
                    }

                    // Approach 2: Force unequip ALL slots that have items (brute force approach)
                    ModLogger.Info("[INVENTORY] Attempting brute force unequip of all slots...");
                    try
                    {
                        var indexAllSlotsMethod = playerInventory.GetType().GetMethod("IndexAllSlots");
                        if (indexAllSlotsMethod != null)
                        {
                            // Check all 8 hotbar slots (0-7) plus cash slot (8)
                            for (int i = 0; i < 9; i++)
                            {
                                try
                                {
                                    var hotbarSlot = indexAllSlotsMethod.Invoke(playerInventory, new object[] { i });
                                    if (hotbarSlot != null)
                                    {
                                        // Check if this slot has an item and is equipped
                                        var isEquippedProperty = hotbarSlot.GetType().GetProperty("IsEquipped");
                                        if (isEquippedProperty != null)
                                        {
                                            bool isEquipped = (bool)isEquippedProperty.GetValue(hotbarSlot);
                                            if (isEquipped)
                                            {
                                                ModLogger.Info($"[INVENTORY] Found equipped item in slot {i} - force unequipping...");
                                                var unequipMethod = hotbarSlot.GetType().GetMethod("Unequip");
                                                if (unequipMethod != null)
                                                {
                                                    unequipMethod.Invoke(hotbarSlot, null);
                                                    ModLogger.Info($"[INVENTORY] Force unequipped item from slot {i}");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    ModLogger.Debug($"[INVENTORY] Error checking slot {i}: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            ModLogger.Warn("[INVENTORY] IndexAllSlots method not found for brute force unequip");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"[INVENTORY] Error in brute force unequip: {ex.Message}");
                    }

                    // Approach 3: Try to simulate the holster key press
                    ModLogger.Info("[INVENTORY] Attempting to simulate holster button press...");
                    try
                    {
                        // Look for methods that might handle holstering
                        var methods = playerInventory.GetType().GetMethods();
                        foreach (var method in methods)
                        {
                            if (method.Name.Contains("Holster") || method.Name.Contains("PutAway") || method.Name.Contains("Clear"))
                            {
                                ModLogger.Info($"[INVENTORY] Found potential holster method: {method.Name}");
                                try
                                {
                                    if (method.GetParameters().Length == 0)
                                    {
                                        method.Invoke(playerInventory, null);
                                        ModLogger.Info($"[INVENTORY] Called {method.Name}");
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    ModLogger.Debug($"[INVENTORY] Error calling {method.Name}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"[INVENTORY] Error in holster simulation: {ex.Message}");
                    }

                    // STEP 1.5: Switch to an empty slot after unequipping to ensure visual updates
                    // This ensures the character model doesn't show holding a gun after arrest
                    try
                    {
                        ModLogger.Info("[INVENTORY] Switching to empty slot to ensure visual update...");
                        var indexAllSlotsMethodForEmpty = playerInventory.GetType().GetMethod("IndexAllSlots");
                        
                        if (indexAllSlotsMethodForEmpty != null && equippedSlotIndexProperty != null)
                        {
                            // Find the first empty slot (0-7 are hotbar slots)
                            int emptySlotIndex = -1;
                            for (int i = 0; i < 8; i++)
                            {
                                try
                                {
                                    var hotbarSlot = indexAllSlotsMethodForEmpty.Invoke(playerInventory, new object[] { i });
                                    if (hotbarSlot != null)
                                    {
                                        var itemInstanceProperty = hotbarSlot.GetType().GetProperty("ItemInstance");
                                        if (itemInstanceProperty != null)
                                        {
                                            var itemInstance = itemInstanceProperty.GetValue(hotbarSlot);
                                            if (itemInstance == null)
                                            {
                                                emptySlotIndex = i;
                                                break;
                                            }
                                        }
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    ModLogger.Debug($"[INVENTORY] Error checking slot {i} for empty slot: {ex.Message}");
                                }
                            }
                            
                            if (emptySlotIndex != -1)
                            {
                                // CRITICAL: Set equipped slot to empty slot FIRST to trigger visual refresh
                                equippedSlotIndexProperty.SetValue(playerInventory, emptySlotIndex);
                                ModLogger.Info($"[INVENTORY] Switched active slot to empty slot {emptySlotIndex} to refresh visual state");
                                
                                // Try to use a method to switch slots if available (for proper visual update)
                                var switchSlotMethod = playerInventory.GetType().GetMethod("SwitchSlot");
                                if (switchSlotMethod != null)
                                {
                                    try
                                    {
                                        switchSlotMethod.Invoke(playerInventory, new object[] { emptySlotIndex });
                                        ModLogger.Info($"[INVENTORY] Called SwitchSlot({emptySlotIndex}) to refresh visual state");
                                    }
                                    catch (System.Exception ex)
                                    {
                                        ModLogger.Debug($"[INVENTORY] SwitchSlot method failed: {ex.Message}");
                                    }
                                }
                                
                                // Then set back to -1 to ensure nothing is equipped
                                // This double-update ensures the visual state is properly refreshed
                                equippedSlotIndexProperty.SetValue(playerInventory, -1);
                                ModLogger.Info("[INVENTORY] Set EquippedSlotIndex to -1 after visual refresh");
                            }
                            else
                            {
                                ModLogger.Info("[INVENTORY] No empty slots found, setting EquippedSlotIndex to -1");
                                equippedSlotIndexProperty.SetValue(playerInventory, -1);
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"[INVENTORY] Error switching to empty slot: {ex.Message}");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"[INVENTORY] Error unequipping current item: {ex.Message}");
                }

                // STEP 2: THEN disable all inventory functionality
                try
                {
                    // Disable inventory system
                    var setInventoryEnabledMethod = playerInventory.GetType().GetMethod("SetInventoryEnabled");
                    if (setInventoryEnabledMethod != null)
                    {
                        setInventoryEnabledMethod.Invoke(playerInventory, new object[] { false });
                        ModLogger.Info("[INVENTORY] Inventory system completely disabled");
                    }

                    // Disable equipping
                    var setEquippingEnabledMethod = playerInventory.GetType().GetMethod("SetEquippingEnabled");
                    if (setEquippingEnabledMethod != null)
                    {
                        setEquippingEnabledMethod.Invoke(playerInventory, new object[] { false });
                        ModLogger.Info("[INVENTORY] Equipping completely disabled");
                    }

                    // Disable the entire component
                    playerInventory.enabled = false;
                    ModLogger.Info("[INVENTORY] PlayerInventory component disabled");

                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"[INVENTORY] Error completely disabling inventory: {ex.Message}");
                }

                ModLogger.Info("[INVENTORY] Inventory COMPLETELY DISABLED - player cannot access any items in jail");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error locking inventory: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback method to lock individual inventory slots
        /// </summary>
        private static void LockIndividualSlots(PlayerInventory inventory)
        {
            try
            {
                var allSlots = GetAllInventorySlots(inventory);
                foreach (var slot in allSlots)
                {
                    try
                    {
                        // Try to set removal and add locks on individual slots
                        var setRemovalLockedMethod = slot.GetType().GetMethod("SetIsRemovalLocked");
                        if (setRemovalLockedMethod != null)
                        {
                            setRemovalLockedMethod.Invoke(slot, new object[] { true });
                        }

                        var setAddLockedMethod = slot.GetType().GetMethod("SetIsAddLocked");
                        if (setAddLockedMethod != null)
                        {
                            setAddLockedMethod.Invoke(slot, new object[] { true });
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"[INVENTORY] Error locking individual slot: {ex.Message}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error in fallback slot locking: {ex.Message}");
            }
        }

        /// <summary>
        /// Unlock the player's inventory when released from jail
        /// Restores full inventory functionality
        /// </summary>
        public static void UnlockPlayerInventory(Player player)
        {
            try
            {
                ModLogger.Info($"[INVENTORY] RESTORING inventory for released player: {player.name}");

                var playerInventory = player.GetComponent<PlayerInventory>();
                if (playerInventory == null)
                {
#if !MONO
                    playerInventory = Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance;
#else
                    playerInventory = ScheduleOne.PlayerScripts.PlayerInventory.Instance;
#endif
                }

                if (playerInventory == null)
                {
                    ModLogger.Error("[INVENTORY] PlayerInventory instance not found during unlock!");
                    return;
                }

                // Re-enable the component first
                playerInventory.enabled = true;
                ModLogger.Info("[INVENTORY] PlayerInventory component re-enabled");

                // Re-enable inventory system
                try
                {
                    var setInventoryEnabledMethod = playerInventory.GetType().GetMethod("SetInventoryEnabled");
                    if (setInventoryEnabledMethod != null)
                    {
                        setInventoryEnabledMethod.Invoke(playerInventory, new object[] { true });
                        ModLogger.Info("[INVENTORY] Inventory system re-enabled");
                    }

                    var setEquippingEnabledMethod = playerInventory.GetType().GetMethod("SetEquippingEnabled");
                    if (setEquippingEnabledMethod != null)
                    {
                        setEquippingEnabledMethod.Invoke(playerInventory, new object[] { true });
                        ModLogger.Info("[INVENTORY] Equipping re-enabled");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"[INVENTORY] Error re-enabling inventory system: {ex.Message}");
                }

                ModLogger.Info("[INVENTORY] Inventory FULLY RESTORED - player can access items again");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error unlocking inventory: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback method to unlock individual inventory slots
        /// </summary>
        private static void UnlockIndividualSlots(PlayerInventory inventory)
        {
            try
            {
                var allSlots = GetAllInventorySlots(inventory);
                foreach (var slot in allSlots)
                {
                    try
                    {
                        // Remove locks from individual slots
                        var setRemovalLockedMethod = slot.GetType().GetMethod("SetIsRemovalLocked");
                        if (setRemovalLockedMethod != null)
                        {
                            setRemovalLockedMethod.Invoke(slot, new object[] { false });
                        }

                        var setAddLockedMethod = slot.GetType().GetMethod("SetIsAddLocked");
                        if (setAddLockedMethod != null)
                        {
                            setAddLockedMethod.Invoke(slot, new object[] { false });
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"[INVENTORY] Error unlocking individual slot: {ex.Message}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error in fallback slot unlocking: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove ALL ammunition from player inventory for safety
        /// Prevents weapons from being loaded/fired in prison
        /// Public so it can be called from Harmony patch BEFORE inventory snapshot
        /// </summary>
        public static void RemoveAllAmmo(PlayerInventory inventory)
        {
            try
            {
                ModLogger.Info("[INVENTORY] Removing all ammunition and unloading weapons for safety");

                var allSlots = GetAllInventorySlots(inventory);
                int ammoRemoved = 0;
                int weaponsUnloaded = 0;

                foreach (var slot in allSlots)
                {
                    try
                    {
                        var itemInstanceProperty = slot.GetType().GetProperty("ItemInstance");
                        if (itemInstanceProperty == null) continue;

                        var itemInstance = itemInstanceProperty.GetValue(slot);
                        if (itemInstance == null) continue;

                        // Check if this is ammo
                        var itemType = itemInstance.GetType().Name;
                        var itemName = itemInstance.GetType().GetProperty("Name")?.GetValue(itemInstance)?.ToString() ?? "";

                        bool isAmmo = itemType.Contains("Ammo", StringComparison.OrdinalIgnoreCase) ||
                                     itemType.Contains("Magazine", StringComparison.OrdinalIgnoreCase) ||
                                     itemName.Contains("Magazine", StringComparison.OrdinalIgnoreCase) ||
                                     itemName.Contains("Ammo", StringComparison.OrdinalIgnoreCase) ||
                                     itemName.Contains("Round", StringComparison.OrdinalIgnoreCase) ||
                                     itemName.Contains("Cartridge", StringComparison.OrdinalIgnoreCase);

                        // Check if this is a weapon - IntegerItemInstance that's NOT ammo is usually a gun
                        bool isWeapon = (itemType == "IntegerItemInstance" && !isAmmo) ||
                                       itemType.Contains("Weapon", StringComparison.OrdinalIgnoreCase) ||
                                       itemType.Contains("Gun", StringComparison.OrdinalIgnoreCase) ||
                                       itemName.Contains("Pistol", StringComparison.OrdinalIgnoreCase) ||
                                       itemName.Contains("Rifle", StringComparison.OrdinalIgnoreCase) ||
                                       itemName.Contains("Shotgun", StringComparison.OrdinalIgnoreCase) ||
                                       itemName.Contains("M1911", StringComparison.OrdinalIgnoreCase);

                        if (isAmmo)
                        {
                            // Clear ammo/magazine slots completely
                            var clearMethod = slot.GetType().GetMethod("ClearStoredInstance");
                            if (clearMethod != null)
                            {
                                clearMethod.Invoke(slot, new object[] { true }); // true = internal
                                ammoRemoved++;
                                ModLogger.Info($"[INVENTORY] Confiscated ammo: {itemName}");
                            }
                        }
                        else if (isWeapon)
                        {
                            // Unload the weapon - set internal ammo to 0
                            UnloadWeapon(itemInstance);
                            weaponsUnloaded++;
                            ModLogger.Info($"[INVENTORY] Unloaded weapon: {itemName}");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"[INVENTORY] Error checking slot: {ex.Message}");
                    }
                }

                ModLogger.Info($"[INVENTORY] Removed {ammoRemoved} ammunition items, unloaded {weaponsUnloaded} weapons");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[INVENTORY] Error removing ammo: {ex.Message}");
            }
        }

        /// <summary>
        /// Empty a weapon's loaded ammo
        /// IntegerItemInstance uses the "Value" field to store loaded ammo count
        /// </summary>
        private static void UnloadWeapon(object weaponInstance)
        {
            try
            {
                var weaponType = weaponInstance.GetType().Name;

                // IntegerItemInstance stores ammo in the "Value" field
                if (weaponType.Contains("Integer", StringComparison.OrdinalIgnoreCase))
                {
                    var valueField = weaponInstance.GetType().GetField("Value");
                    if (valueField != null)
                    {
                        int currentValue = (int)valueField.GetValue(weaponInstance);
                        valueField.SetValue(weaponInstance, 0);
                        ModLogger.Info($"[INVENTORY] Unloaded weapon - removed {currentValue} rounds");
                        return;
                    }
                }

                // Fallback: Try SetValue method if it exists
                var setValueMethod = weaponInstance.GetType().GetMethod("SetValue");
                if (setValueMethod != null)
                {
                    setValueMethod.Invoke(weaponInstance, new object[] { 0 });
                    ModLogger.Info($"[INVENTORY] Unloaded weapon via SetValue(0)");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"[INVENTORY] Could not unload weapon: {ex.Message}");
            }
        }
    }
}