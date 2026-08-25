using HarmonyLib;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.ItemFramework;
using Il2CppFishNet.Connection;
#else
using ScheduleOne.Storage;
using ScheduleOne.ItemFramework;
using FishNet.Connection;
#endif

namespace Behind_Bars.Harmony
{
#if MONO
    /// <summary>
    /// Patches StorageEntity to make PrisonStorageEntity work locally without network sync
    /// </summary>
    [HarmonyPatch(typeof(StorageEntity))]
    public class StorageEntityPatch
    {
        // NOTE: Open() patch removed - the exception was being caught properly and storage was working
        // The Harmony prefix was preventing the UI from opening

        /// <summary>
        /// Patch SetStoredInstance to work locally for PrisonStorageEntity
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(StorageEntity.SetStoredInstance))]
        public static bool SetStoredInstance_Prefix(StorageEntity __instance, NetworkConnection conn, int itemSlotIndex, object instance)
        {
            // Only intercept for our custom PrisonStorageEntity
            if (__instance is Behind_Bars.Systems.Jail.PrisonStorageEntity)
            {
                ModLogger.Debug($"PrisonStorageEntity: Patched SetStoredInstance - slot {itemSlotIndex}, item: {(instance != null ? GetItemName(instance) : "null")}");

                // Manually set the slot locally (skip network RPCs)
                if (itemSlotIndex >= 0 && itemSlotIndex < __instance.ItemSlots.Count)
                {
                    SetStoredItemLocally(__instance.ItemSlots[itemSlotIndex], instance);
                    ModLogger.Info($"Locally updated storage slot {itemSlotIndex}");
                }

                return false; // Skip original method (prevent network RPC)
            }

            return true; // Allow normal behavior for other StorageEntity types
        }

        /// <summary>
        /// Patch SetItemSlotQuantity to work locally for PrisonStorageEntity
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(StorageEntity.SetItemSlotQuantity))]
        public static bool SetItemSlotQuantity_Prefix(StorageEntity __instance, int itemSlotIndex, int quantity)
        {
            // Only intercept for our custom PrisonStorageEntity
            if (__instance is Behind_Bars.Systems.Jail.PrisonStorageEntity)
            {
                ModLogger.Debug($"PrisonStorageEntity: Patched SetItemSlotQuantity - slot {itemSlotIndex}, quantity: {quantity}");

                // Manually set quantity locally (skip network RPCs)
                if (itemSlotIndex >= 0 && itemSlotIndex < __instance.ItemSlots.Count && __instance.ItemSlots[itemSlotIndex] != null)
                {
                    __instance.ItemSlots[itemSlotIndex].SetQuantity(quantity, true); // true = internal/local
                    ModLogger.Info($"Locally updated slot {itemSlotIndex} quantity to {quantity}");
                }

                return false; // Skip original method
            }

            return true; // Allow normal behavior for other StorageEntity types
        }

        private static void SetStoredItemLocally(object slot, object instance)
        {
            if (slot == null)
            {
                return;
            }

            try
            {
                var method = slot.GetType().GetMethod("SetStoredItem");
                if (method != null)
                {
                    method.Invoke(slot, new object[] { instance, true });
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to set stored item locally: {ex.Message}");
            }
        }

        private static string GetItemName(object itemInstance)
        {
            if (itemInstance == null)
            {
                return "null";
            }

            try
            {
                var nameProperty = itemInstance.GetType().GetProperty("Name");
                if (nameProperty != null)
                {
                    var value = nameProperty.GetValue(itemInstance) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                var definitionProperty = itemInstance.GetType().GetProperty("Definition");
                if (definitionProperty != null)
                {
                    var definition = definitionProperty.GetValue(itemInstance);
                    if (definition != null)
                    {
                        var defType = definition.GetType();
                        var defNameProperty = defType.GetProperty("name") ?? defType.GetProperty("Name");
                        if (defNameProperty != null)
                        {
                            var value = defNameProperty.GetValue(definition) as string;
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                return value;
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Failed to read item name from {itemInstance.GetType().Name}: {ex.Message}");
            }

            return itemInstance.GetType().Name;
        }
    }
#endif
}
