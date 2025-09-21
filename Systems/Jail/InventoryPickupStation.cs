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
        private List<string> storedItems = new List<string>();
        
        void Start()
        {
            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Info("InventoryPickupStation interaction setup completed");
            
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
                ModLogger.Info("Added InteractableObject component to InventoryPickupStation");
            }
            else
            {
                ModLogger.Info("Found existing InteractableObject component on InventoryPickupStation");
            }
            
            // Configure the interaction
            interactableObject.SetMessage("Retrieve personal items");
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
            
            ModLogger.Info("InteractableObject component configured with event listeners");
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
                ModLogger.Error("No local player found for inventory pickup!");
                return;
            }
            
            // Check if player has items to retrieve
            var playerHandler = Behind_Bars.Core.GetPlayerHandler(currentPlayer);
            if (playerHandler == null)
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "No player record found", 
                        NotificationType.Warning
                    );
                }
                return;
            }
            
            // Get stored items from player's jail record
            storedItems = GetStoredItemsForPlayer(playerHandler);
            
            if (storedItems.Count == 0)
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "No items in storage", 
                        NotificationType.Progress
                    );
                }
                return;
            }
            
            // Start inventory pickup process
            MelonCoroutines.Start(ProcessInventoryPickup(currentPlayer));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessInventoryPickup(Player player)
        {
            isProcessing = true;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Retrieving items...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }
            
            ModLogger.Info($"Starting inventory pickup for {player.name}");
            
            // Show initial notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Retrieving your personal items...", 
                    NotificationType.Instruction
                );
            }
            
            // Get player inventory
            var inventory = PlayerSingleton<PlayerInventory>.Instance;
            if (inventory == null)
            {
                ModLogger.Error("PlayerInventory instance not found!");
                CompletePickup();
                yield break;
            }
            
            // Animate picking up each item
            for (int i = 0; i < storedItems.Count; i++)
            {
                string item = storedItems[i];
                
                // Show item return notification
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"Returned {item}", 
                        NotificationType.Progress
                    );
                }
                
                // Add item back to player inventory
                AddItemToInventory(inventory, item);
                
                ModLogger.Info($"Returned {item}");
                
                // Wait between items for visual effect
                yield return new WaitForSeconds(itemPickupDuration);
            }
            
            // Clear stored items from player record
            ClearStoredItemsForPlayer(currentPlayer);

            // NOW unlock the player's inventory after retrieving their items
            InventoryProcessor.UnlockPlayerInventory(player);
            ModLogger.Info("Player inventory fully unlocked after retrieving belongings");

            // Show completion notification
            if (BehindBarsUIManager.Instance != null)
            {
                string message = storedItems.Count > 0
                    ? $"{storedItems.Count} items returned - you are free to go!"
                    : "Pickup complete - you are free to go!";
                BehindBarsUIManager.Instance.ShowNotification(message, NotificationType.Progress);
            }

            // Wait a moment for the notification
            yield return new WaitForSeconds(2f);

            // Now teleport player to freedom (jail exit location)
            TeleportPlayerToFreedom(player);

            CompletePickup();
            ModLogger.Info("Inventory pickup completed successfully - player is free");
        }
        
        private List<string> GetStoredItemsForPlayer(Behind_Bars.Players.PlayerHandler playerHandler)
        {
            try
            {
                // Get confiscated items from player's jail record
                // This would typically be stored when the player was booked
                var items = playerHandler.GetConfiscatedItems();
                if (items != null && items.Count > 0)
                {
                    return new List<string>(items);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting stored items: {ex.Message}");
            }
            
            // Fallback - simulate some items for animation purposes
            return new List<string>();
        }
        
        private void AddItemToInventory(PlayerInventory inventory, string itemName)
        {
            try
            {
                ModLogger.Info($"Attempting to add item: {itemName}");
                
                // Special handling for phone - re-enable it
                if (itemName.Contains("Phone"))
                {
                    EnablePlayerPhone();
                }
                
                // Approach 1: Create proper ItemInstance from registry
                try
                {
                    string itemId = GetItemIdFromName(itemName);
                    if (!string.IsNullOrEmpty(itemId))
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
                                            // Now add the ItemInstance to inventory
                                            var addItemToInventoryMethod = inventory.GetType().GetMethod("AddItemToInventory");
                                            if (addItemToInventoryMethod != null)
                                            {
                                                addItemToInventoryMethod.Invoke(inventory, new object[] { itemInstance });
                                                ModLogger.Info($"Successfully added {itemName} using ItemInstance from registry");
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
                    var addMethod = inventory.GetType().GetMethod("AddItem");
                    if (addMethod != null)
                    {
                        addMethod.Invoke(inventory, new object[] { itemName });
                        ModLogger.Info($"Successfully added {itemName} using AddItem method");
                        return;
                    }
                    
                    var addByNameMethod = inventory.GetType().GetMethod("AddItemByName");
                    if (addByNameMethod != null)
                    {
                        addByNameMethod.Invoke(inventory, new object[] { itemName });
                        ModLogger.Info($"Successfully added {itemName} using AddItemByName method");
                        return;
                    }
                    
                    var giveItemMethod = inventory.GetType().GetMethod("GiveItem");
                    if (giveItemMethod != null)
                    {
                        giveItemMethod.Invoke(inventory, new object[] { itemName });
                        ModLogger.Info($"Successfully added {itemName} using GiveItem method");
                        return;
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"String-based addition methods failed: {ex.Message}");
                }
                
                // Approach 3: Try to equip items (last resort)
                try
                {
                    var equipMethod = inventory.GetType().GetMethod("EquipItem");
                    if (equipMethod != null)
                    {
                        equipMethod.Invoke(inventory, new object[] { itemName });
                        ModLogger.Info($"Successfully equipped {itemName}");
                        return;
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Equip failed: {ex.Message}");
                }
                
                ModLogger.Warn($"Unable to add {itemName} back to inventory - all methods failed");
                
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error adding item {itemName}: {ex.Message}");
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
                var playerHandler = Behind_Bars.Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    playerHandler.ClearConfiscatedItems();
                    ModLogger.Info("Cleared stored items from player record");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error clearing stored items: {ex.Message}");
            }
        }
        
        private void CompletePickup()
        {
            isProcessing = false;
            storedItems.Clear();
            
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
            
            if (interactableObject != null && !isProcessing)
            {
                interactableObject.SetMessage("Retrieve personal items");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
        }
        
        public bool HasItemsForPlayer(Player player)
        {
            try
            {
                var playerHandler = Behind_Bars.Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    var items = playerHandler.GetConfiscatedItems();
                    return items != null && items.Count > 0;
                }
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
                interactableObject.SetMessage("Collect your belongings");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }

            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Visit the storage station to collect your belongings",
                    NotificationType.Instruction
                );
            }
        }

        /// <summary>
        /// Teleport the player to freedom (outside jail) after completing pickup
        /// </summary>
        private void TeleportPlayerToFreedom(Player player)
        {
            try
            {
                ModLogger.Info($"Teleporting {player.name} to freedom");

                // Get the jail exit position from JailSystem
                var core = Behind_Bars.Core.Instance;
                if (core?.JailSystem != null)
                {
                    // Try to get the stored exit position
                    var exitPosition = core.JailSystem.GetPlayerExitPosition(player.name);
                    if (exitPosition.HasValue)
                    {
                        player.transform.position = exitPosition.Value;
                        ModLogger.Info($"Teleported {player.name} to stored exit position");
                        return;
                    }
                }

                // Fallback: teleport to a safe location outside the jail
                // This should be set to a proper exit location in your jail setup
                Vector3 jailExitPosition = new Vector3(0, 1, 0); // Replace with actual exit coordinates
                player.transform.position = jailExitPosition;
                ModLogger.Info($"Teleported {player.name} to default jail exit position");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error teleporting player to freedom: {ex.Message}");
            }
        }

        void Update()
        {
            // Update interaction state based on whether player has items to retrieve
            if (!isProcessing && interactableObject != null && currentPlayer != null)
            {
                if (HasItemsForPlayer(Player.Local))
                {
                    interactableObject.SetMessage("Retrieve personal items");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                else
                {
                    interactableObject.SetMessage("No items in storage");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                }
            }
        }
    }
}