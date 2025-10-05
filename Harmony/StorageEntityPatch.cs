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
        public static bool SetStoredInstance_Prefix(StorageEntity __instance, NetworkConnection conn, int itemSlotIndex, ItemInstance instance)
        {
            // Only intercept for our custom PrisonStorageEntity
            if (__instance is Behind_Bars.Systems.Jail.PrisonStorageEntity)
            {
                ModLogger.Debug($"PrisonStorageEntity: Patched SetStoredInstance - slot {itemSlotIndex}, item: {(instance != null ? instance.Name : "null")}");

                // Manually set the slot locally (skip network RPCs)
                if (itemSlotIndex >= 0 && itemSlotIndex < __instance.ItemSlots.Count)
                {
                    __instance.ItemSlots[itemSlotIndex].SetStoredItem(instance, true); // true = internal/local
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
    }
}
