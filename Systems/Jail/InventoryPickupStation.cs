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
            interactableObject.SetMessage("Access personal belongings storage");
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
            
            ModLogger.Info("InteractableObject component configured with event listeners");
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
                ModLogger.Info("Added PrisonStorageEntity component to InventoryPickupStation");
            }
            else
            {
                ModLogger.Info("Found existing PrisonStorageEntity component");
            }

            // Also add StorageEntityInteractable for proper interaction
#if !MONO
            var storageInteractable = GetComponent<Il2CppScheduleOne.Storage.StorageEntityInteractable>();
            if (storageInteractable == null)
            {
                storageInteractable = gameObject.AddComponent<Il2CppScheduleOne.Storage.StorageEntityInteractable>();
                ModLogger.Info("Added StorageEntityInteractable component");
            }
#else
            var storageInteractable = GetComponent<ScheduleOne.Storage.StorageEntityInteractable>();
            if (storageInteractable == null)
            {
                storageInteractable = gameObject.AddComponent<ScheduleOne.Storage.StorageEntityInteractable>();
                ModLogger.Info("Added StorageEntityInteractable component");
            }
#endif
        }
        
        private void OnInteractStart()
        {
            // This method is now handled by StorageEntityInteractable
            // Just populate storage if not already done
            if (!storageSessionActive && Player.Local != null)
            {
                PrepareStorageForPlayer(Player.Local);
            }
        }

        /// <summary>
        /// Prepare storage with player's items for the storage interface
        /// </summary>
        public void PrepareStorageForPlayer(Player player)
        {
            if (storageEntity == null)
            {
                ModLogger.Error("StorageEntity not found when preparing storage");
                return;
            }

            currentPlayer = player;

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

            // Populate storage entity with player's legal items
            storageEntity.PopulateWithPlayerItems(player);

            ModLogger.Info($"Storage prepared for {player.name} with {legalItems.Count} legal items");
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
            yield return StartCoroutine(ProcessPrisonItemDropOff(player));

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
            yield return StartCoroutine(OpenInteractiveStorage(player));
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
            ModLogger.Info("Storage session completed by player");
            storageSessionActive = false;

            // Get remaining items in storage
            int remainingItems = 0;
            if (storageEntity != null)
            {
                remainingItems = storageEntity.GetRemainingItemCount();
            }

            // Log contraband items that were kept
            foreach (var contrabandItem in contrabandItems)
            {
                ModLogger.Info($"Contraband permanently confiscated: {contrabandItem.itemName} (x{contrabandItem.stackCount})");
            }

            // Clear stored items from persistent storage
            ClearStoredItemsForPlayer(currentPlayer);

            // Reset storage entity
            if (storageEntity != null)
            {
                storageEntity.ResetStorage();
            }

            // Unlock player inventory
            InventoryProcessor.UnlockPlayerInventory(currentPlayer);
            ModLogger.Info("Player inventory fully unlocked after storage session");

            // Notify the ReleaseManager that inventory processing is complete
            if (ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.OnInventoryProcessingComplete(currentPlayer);
            }

            // Show completion notification
            if (BehindBarsUIManager.Instance != null)
            {
                int retrievedItems = legalItems.Count - remainingItems;
                string message = retrievedItems > 0
                    ? $"Retrieved {retrievedItems}/{legalItems.Count} personal items"
                    : "No items retrieved from storage";

                if (contrabandItems.Count > 0)
                {
                    message += $" ({contrabandItems.Count} illegal items confiscated)";
                }

                BehindBarsUIManager.Instance.ShowNotification(message, NotificationType.Progress);
            }

            // Complete the pickup process
            CompletePickup();
            ModLogger.Info($"Storage session completed - player processed");
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
                interactableObject.SetMessage("Access personal belongings storage");
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
            // Update interaction state based on whether player has items to retrieve
            if (!isProcessing && !storageSessionActive && interactableObject != null)
            {
                if (HasItemsForPlayer(Player.Local))
                {
                    interactableObject.SetMessage("Access personal belongings storage");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                else
                {
                    interactableObject.SetMessage("No items in storage");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                }
            }
            else if (storageSessionActive && interactableObject != null)
            {
                interactableObject.SetMessage("Storage open - close when finished");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
            }
        }
    }
}