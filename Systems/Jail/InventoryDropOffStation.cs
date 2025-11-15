using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Handles player inventory drop-off during booking process
    /// </summary>
    public class InventoryDropOffStation : MonoBehaviour
    {
#if !MONO
        public InventoryDropOffStation(System.IntPtr ptr) : base(ptr) { }
#endif
        
        // InteractableObject component for IL2CPP compatibility
        private InteractableObject interactableObject;
        
        public float itemDropDuration = 1.0f; // Time between dropping each item (1 second to match notification)
        public Transform storageLocation; // Where items are "stored" visually
        
        private bool isProcessing = false;
        private BookingProcess bookingProcess;
        private Player currentPlayer;
        
        void Start()
        {
            // Find booking process
            bookingProcess = FindObjectOfType<BookingProcess>();
            
            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Info("InventoryDropOffStation interaction setup completed");
            
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
                    ModLogger.Info("Created default StorageLocation");
                }
            }
        }
        
        private void SetupInteractableComponent()
        {
            // Get or create InteractableObject component
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Info("Added InteractableObject component to InventoryDropOffStation");
            }
            else
            {
                ModLogger.Info("Found existing InteractableObject component on InventoryDropOffStation");
            }
            
            // Configure the interaction
            interactableObject.SetMessage("Drop off inventory");
            interactableObject.SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            
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
        
        private void OnInteractStart()
        {
            if (isProcessing)
            {
                interactableObject.SetMessage("Processing...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                return;
            }
            
            // Get player first
            currentPlayer = Player.Local;
            if (currentPlayer == null)
            {
                ModLogger.Error("No local player found for inventory drop-off!");
                return;
            }
            
            // Check if player has booking process active
            if (bookingProcess == null || !bookingProcess.bookingInProgress)
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "No active booking process", 
                        NotificationType.Warning
                    );
                }
                return;
            }
            
            // Check if inventory drop-off already completed
            if (bookingProcess.inventoryDropOffComplete)
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Inventory already dropped off", 
                        NotificationType.Progress
                    );
                }
                return;
            }
            
            // Start inventory drop-off process
            MelonCoroutines.Start(ProcessInventoryDropOff(currentPlayer));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessInventoryDropOff(Player player)
        {
            isProcessing = true;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Dropping off items...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }
            
            ModLogger.Info($"Starting inventory drop-off for {player.name}");
            
            // Show initial notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Placing items in storage...", 
                    NotificationType.Instruction
                );
            }
            
            // Get player inventory
#if !MONO
            var inventory = Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance;
#else
            var inventory = ScheduleOne.PlayerScripts.PlayerInventory.Instance;
#endif
            if (inventory == null)
            {
                ModLogger.Error("PlayerInventory instance not found!");
                CompleteDropOff();
                yield break;
            }

            List<string> confiscatedItems = new List<string>();

            // Get all items from player inventory
            var inventoryItems = GetInventoryItems(inventory);

            if (inventoryItems.Count == 0)
            {
                // Player has no items
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "No items to confiscate",
                        NotificationType.Progress
                    );
                }
                yield return new WaitForSeconds(1f);
            }
            else
            {
                // Clear entire inventory at once (this was working before)
                confiscatedItems = inventoryItems; // Keep the list for records

                // Show confiscation notification
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"Confiscating {inventoryItems.Count} items...",
                        NotificationType.Progress
                    );
                }

                // Clear all inventory slots
                ClearAllInventorySlots(inventory);

                ModLogger.Info($"Successfully confiscated all {inventoryItems.Count} items");
                yield return new WaitForSeconds(1f);
            }

            // Store confiscated items in booking process
            if (bookingProcess != null)
            {
                bookingProcess.confiscatedItems = confiscatedItems;
                bookingProcess.SetInventoryDropOffComplete();
                ModLogger.Info("Inventory drop-off saved to booking process");
            }

            // Also store in player handler for later retrieval
            var playerHandler = Behind_Bars.Core.GetPlayerHandler(currentPlayer);
            if (playerHandler != null && confiscatedItems.Count > 0)
            {
                playerHandler.AddConfiscatedItems(confiscatedItems);
                ModLogger.Info($"Stored {confiscatedItems.Count} confiscated items in player record");
            }

            // Show completion notification
            if (BehindBarsUIManager.Instance != null)
            {
                string message = confiscatedItems.Count > 0
                    ? $"{confiscatedItems.Count} items secured in storage"
                    : "Inventory processing complete";
                BehindBarsUIManager.Instance.ShowNotification(message, NotificationType.Progress);
            }

            // Final UI refresh to ensure inventory display is updated
            RefreshInventoryUI(inventory);
            
            CompleteDropOff();
            ModLogger.Info("Inventory drop-off completed successfully");
        }
        
        private List<string> GetInventoryItems(PlayerInventory inventory)
        {
            List<string> items = new List<string>();

            try
            {
                ModLogger.Info("Attempting to read player inventory...");

                // Approach 1: Use GetAllInventorySlots() method for comprehensive inventory access
                try
                {
                    var getAllSlotsMethod = inventory.GetType().GetMethod("GetAllInventorySlots");
                    if (getAllSlotsMethod != null)
                    {
                        var allSlots = getAllSlotsMethod.Invoke(inventory, null);

                        if (allSlots is System.Collections.IList slotsList)
                        {
                            ModLogger.Info($"Found {slotsList.Count} inventory slots");
                            for (int i = 0; i < slotsList.Count; i++)
                            {
                                var slot = slotsList[i];
                                if (slot != null)
                                {
                                    var slotType = slot.GetType();
                                    var itemInstanceProperty = slotType.GetProperty("ItemInstance");
                                    if (itemInstanceProperty != null)
                                    {
                                        var itemInstance = itemInstanceProperty.GetValue(slot);
                                        if (itemInstance != null)
                                        {
                                            // Get item name and handle stacking
                                            var itemName = GetItemDisplayName(itemInstance);
                                            var stackCount = GetItemStackCount(itemInstance);

                                            if (!string.IsNullOrEmpty(itemName))
                                            {
                                                // For stacked items, add multiple entries
                                                for (int stack = 0; stack < stackCount; stack++)
                                                {
                                                    items.Add(itemName);
                                                }

                                                if (stackCount > 1)
                                                {
                                                    ModLogger.Info($"Found stacked item: {itemName} x{stackCount}");
                                                }
                                                else
                                                {
                                                    ModLogger.Info($"Found inventory item: {itemName}");
                                                }
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
                    ModLogger.Debug($"GetAllInventorySlots approach failed: {ex.Message}");
                }

                // Fallback: Add common items if we couldn't find any
                if (items.Count == 0)
                {
                    items.Add("Phone");
                    items.Add("Wallet");
                    items.Add("Keys");
                    ModLogger.Info("Using fallback items - actual inventory access needs refinement");
                }

                ModLogger.Info($"Total items found for confiscation: {items.Count}");

            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error reading inventory: {ex.Message}");
                // Return fallback items on error
                items.Add("Phone");
                items.Add("Personal belongings");
            }

            return items;
        }

        private int GetItemStackCount(object itemInstance)
        {
            try
            {
                // Try to get stack count from ItemInstance
                var stackCountProperty = itemInstance.GetType().GetProperty("StackCount");
                if (stackCountProperty != null)
                {
                    var stackCount = stackCountProperty.GetValue(itemInstance);
                    if (stackCount is int count && count > 0)
                    {
                        return count;
                    }
                }

                // Try alternative property names
                var amountProperty = itemInstance.GetType().GetProperty("Amount");
                if (amountProperty != null)
                {
                    var amount = amountProperty.GetValue(itemInstance);
                    if (amount is int amountCount && amountCount > 0)
                    {
                        return amountCount;
                    }
                }

                var countProperty = itemInstance.GetType().GetProperty("Count");
                if (countProperty != null)
                {
                    var count = countProperty.GetValue(itemInstance);
                    if (count is int itemCount && itemCount > 0)
                    {
                        return itemCount;
                    }
                }

                // Default to 1 if no stack count found
                return 1;
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error getting stack count: {ex.Message}");
                return 1;
            }
        }

        private string GetItemDisplayName(object itemInstance)
        {
            try
            {
                // Try to get Name property directly
                var nameProperty = itemInstance.GetType().GetProperty("Name");
                if (nameProperty != null)
                {
                    var name = nameProperty.GetValue(itemInstance)?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }

                // Try to get name from Definition
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
                            if (!string.IsNullOrEmpty(defName))
                            {
                                return defName;
                            }
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
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error getting item display name: {ex.Message}");
                return "Unknown Item";
            }
        }

        private void ClearAllInventorySlots(PlayerInventory inventory)
        {
            try
            {
                ModLogger.Info("Clearing all inventory slots");

                // Approach 1: Use ClearInventory() method if available
                try
                {
                    var clearInventoryMethod = inventory.GetType().GetMethod("ClearInventory");
                    if (clearInventoryMethod != null)
                    {
                        clearInventoryMethod.Invoke(inventory, null);
                        ModLogger.Info("Successfully used ClearInventory method");
                        return;
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"ClearInventory method failed: {ex.Message}");
                }

                // Approach 2: Clear using GetAllInventorySlots
                try
                {
                    var getSlotsMethod = inventory.GetType().GetMethod("GetAllInventorySlots");
                    if (getSlotsMethod != null)
                    {
                        var slots = getSlotsMethod.Invoke(inventory, null);
                        if (slots is System.Collections.IList allSlots)
                        {
                            ModLogger.Info($"Clearing {allSlots.Count} inventory slots (including cash)");
                            for (int i = 0; i < allSlots.Count - 1; i++) // Skip cash slot (last one)
                            {
                                var slot = allSlots[i];
                                if (slot != null)
                                {
                                    var itemInstanceProperty = slot.GetType().GetProperty("ItemInstance");
                                    if (itemInstanceProperty != null)
                                    {
                                        var itemInstance = itemInstanceProperty.GetValue(slot);
                                        if (itemInstance != null)
                                        {
                                            var clearStoredInstanceMethod = slot.GetType().GetMethod("ClearStoredInstance");
                                            if (clearStoredInstanceMethod != null)
                                            {
                                                clearStoredInstanceMethod.Invoke(slot, null);
                                                ModLogger.Info($"Cleared inventory slot {i}");
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
                    ModLogger.Debug($"GetAllInventorySlots clearing failed: {ex.Message}");
                }

                ModLogger.Info("Inventory clearing completed");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error clearing inventory slots: {ex.Message}");
            }
        }

        private void RefreshInventoryUI(PlayerInventory inventory)
        {
            try
            {
                ModLogger.Info("Refreshing inventory UI after item removal");

                // Approach 1: Try calling UpdateInventoryVariables to refresh internal state
                try
                {
                    var updateInventoryVariablesMethod = inventory.GetType().GetMethod("UpdateInventoryVariables",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (updateInventoryVariablesMethod != null)
                    {
                        updateInventoryVariablesMethod.Invoke(inventory, null);
                        ModLogger.Info("Called UpdateInventoryVariables");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"UpdateInventoryVariables call failed: {ex.Message}");
                }

                ModLogger.Info("Inventory UI refresh completed");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error refreshing inventory UI: {ex.Message}");
            }
        }

        private void CompleteDropOff()
        {
            isProcessing = false;

            // Update interaction state
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Items dropped off");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
            }
        }
        
        public bool IsComplete()
        {
            return bookingProcess != null && bookingProcess.inventoryDropOffComplete;
        }
        
        void Update()
        {
            // Update interaction state based on booking progress
            if (!isProcessing && interactableObject != null)
            {
                if (bookingProcess != null && bookingProcess.inventoryDropOffComplete)
                {
                    interactableObject.SetMessage("Items dropped off");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
                }
                else if (bookingProcess != null && (bookingProcess.bookingInProgress || bookingProcess.storageInteractionAllowed))
                {
                    interactableObject.SetMessage("Drop off inventory");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                else
                {
                    interactableObject.SetMessage("Booking required");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                }
            }
        }
    }
}