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
    /// Handles giving new prison inmates their starter items (bed mat, sheets, cup, toothbrush, etc.)
    /// </summary>
    public class JailInventoryPickupStation : MonoBehaviour
    {
#if !MONO
        public JailInventoryPickupStation(System.IntPtr ptr) : base(ptr) { }
#endif

        // InteractableObject component for IL2CPP compatibility
        private InteractableObject interactableObject;
        
        public float itemGiveDuration = 1.0f; // Time between giving each item
        public Transform storageLocation; // Where items are "retrieved" from visually
        
        private bool isProcessing = false;
        private Player currentPlayer;
        private bool itemsCurrentlyTaken = false; // Track if items are visually taken
        
        // Prison starter items that inmates receive (using registered item IDs)
        private List<string> prisonStarterItems = new List<string>
        {
            "behindbars.bedroll",
            "behindbars.sheetsnpillows", // Fixed to match PrisonItemRegistry
            "behindbars.cup",
            "behindbars.toothbrush"
        };
        
        void Start()
        {
            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Info("JailInventoryPickupStation interaction setup completed");
            
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
                    ModLogger.Info("Created default StorageLocation for prison items");
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
                ModLogger.Info("Added InteractableObject component to JailInventoryPickupStation");
            }
            else
            {
                ModLogger.Info("Found existing InteractableObject component on JailInventoryPickupStation");
            }
            
            // Configure the interaction
            interactableObject.SetMessage("Collect prison items");
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
            
            ModLogger.Info("JailInventoryPickupStation InteractableObject component configured with event listeners");
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
                ModLogger.Error("No local player found for jail item pickup!");
                return;
            }
            
            // Check if player is in jail/booking process
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
            
            // Check if player already received prison items
            if (HasReceivedPrisonItems(playerHandler))
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Prison items already received", 
                        NotificationType.Progress
                    );
                }
                return;
            }
            
            // Start prison item pickup process
            MelonCoroutines.Start(ProcessJailItemPickup(currentPlayer));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessJailItemPickup(Player player)
        {
            isProcessing = true;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Collecting prison items...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }
            
            ModLogger.Info($"Starting prison item pickup for {player.name}");
            
            // Show initial notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Collecting your prison essentials...", 
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
            
            // Give all prison items at once
            List<string> itemNames = new List<string>();
            
            foreach (string item in prisonStarterItems)
            {
                // Get the item definition to show proper name
                string displayName = item;
#if !MONO
                var itemDef = Il2CppScheduleOne.Registry.GetItem(item);
#else
                var itemDef = ScheduleOne.Registry.GetItem(item);
#endif
                if (itemDef != null)
                {
                    displayName = itemDef.Name;
                }
                
                itemNames.Add(displayName);
                
                // Add item to player inventory
                GivePrisonItem(inventory, item);
                
                ModLogger.Info($"Gave prison item: {item}");
            }
            
            // Show single notification for all items
            if (BehindBarsUIManager.Instance != null)
            {
                string itemList = string.Join(", ", itemNames);
                BehindBarsUIManager.Instance.ShowNotification(
                    $"Received: {itemList}", 
                    NotificationType.Progress
                );
            }
            
            yield return new WaitForSeconds(1f);
            
            // Mark that player has received prison items
            MarkPrisonItemsReceived(currentPlayer);
            
            // Show completion notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    $"{prisonStarterItems.Count} prison items received", 
                    NotificationType.Progress
                );
            }
            
            CompletePickup();
            ModLogger.Info("Prison item pickup completed successfully");
        }
        
        private void GivePrisonItem(PlayerInventory inventory, string itemId)
        {
            try
            {
                ModLogger.Info($"Attempting to add prison item: {itemId}");
                
#if !MONO
                // Get item definition from registry
                var itemDef = Il2CppScheduleOne.Registry.GetItem(itemId);
#else
                // Get item definition from registry
                var itemDef = ScheduleOne.Registry.GetItem(itemId);
#endif
                
                if (itemDef == null)
                {
                    ModLogger.Error($"Item definition not found in registry for: {itemId}");
                    return;
                }
                
                // Create item instance
                var itemInstance = itemDef.GetDefaultInstance(1);
                if (itemInstance == null)
                {
                    ModLogger.Error($"Failed to create item instance for: {itemId}");
                    return;
                }
                
                // Add to player inventory using the proper method
                inventory.AddItemToInventory(itemInstance);
                ModLogger.Info($"✓ Successfully added prison item to inventory: {itemDef.Name} ({itemId})");
                
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error adding prison item {itemId}: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }
        
        
        private bool HasReceivedPrisonItems(Behind_Bars.Players.PlayerHandler playerHandler)
        {
            try
            {
                // Check if player has a flag indicating they received prison items
                // For now, we'll use a simple approach - check if they have any prison items in their record
                var confiscatedItems = playerHandler.GetConfiscatedItems();
                return confiscatedItems != null && confiscatedItems.Contains("PRISON_ITEMS_RECEIVED");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error checking prison items status: {ex.Message}");
                return false;
            }
        }
        
        private void MarkPrisonItemsReceived(Player player)
        {
            try
            {
                var playerHandler = Behind_Bars.Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    // Add a flag to indicate prison items were received
                    var items = new List<string> { "PRISON_ITEMS_RECEIVED" };
                    playerHandler.AddConfiscatedItems(items);
                    ModLogger.Info("Marked prison items as received for player");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error marking prison items as received: {ex.Message}");
            }
        }
        
        private void CompletePickup()
        {
            isProcessing = false;
            itemsCurrentlyTaken = true;
            
            // Disable the visual item prefabs
            DisableItemPrefabs();
            
            // Update interaction state to show items are taken
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Items taken");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }
            
            ModLogger.Info("Prison item pickup station disabled - items have been taken and prefabs hidden");
        }

        
        /// <summary>
        /// Disable all item prefabs to show they've been taken
        /// </summary>
        private void DisableItemPrefabs()
        {
            try
            {
                ModLogger.Info("Disabling prison item prefabs");
                
                // Find and disable all item prefabs under this pickup station
                var itemNames = new string[] { "ClothingPile", "PillowAndSheets", "BedRoll", "JailCup", "JailToothBrush" };
                
                foreach (string itemName in itemNames)
                {
                    var itemObject = transform.Find(itemName);
                    if (itemObject != null)
                    {
                        itemObject.gameObject.SetActive(false);
                        ModLogger.Info($"Disabled item prefab: {itemName}");
                    }
                    else
                    {
                        ModLogger.Debug($"Item prefab not found: {itemName}");
                    }
                }
                
                ModLogger.Info("All available prison item prefabs disabled");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error disabling item prefabs: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Enable all item prefabs to show they're available for pickup
        /// </summary>
        private void EnableItemPrefabs()
        {
            try
            {
                ModLogger.Info("Enabling prison item prefabs");
                
                // Find and enable all item prefabs under this pickup station
                var itemNames = new string[] { "ClothingPile", "PillowAndSheets", "BedRoll", "JailCup", "JailToothBrush" };
                
                foreach (string itemName in itemNames)
                {
                    var itemObject = transform.Find(itemName);
                    if (itemObject != null)
                    {
                        itemObject.gameObject.SetActive(true);
                        ModLogger.Info($"Enabled item prefab: {itemName}");
                    }
                    else
                    {
                        ModLogger.Debug($"Item prefab not found: {itemName}");
                    }
                }
                
                ModLogger.Info("All available prison item prefabs enabled");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error enabling item prefabs: {ex.Message}");
            }
        }
        
        public bool NeedsPrisonItems(Player player)
        {
            try
            {
                var playerHandler = Behind_Bars.Core.GetPlayerHandler(player);
                if (playerHandler != null)
                {
                    // Player needs prison items if they haven't received them yet
                    return !HasReceivedPrisonItems(playerHandler);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error checking if player needs prison items: {ex.Message}");
            }
            
            return false;
        }
        
        void Update()
        {
            // Update interaction state based on whether player needs prison items
            if (!isProcessing && interactableObject != null)
            {
                var localPlayer = Player.Local;
                if (localPlayer != null && NeedsPrisonItems(localPlayer))
                {
                    // New inmate needs items - enable interaction and show items
                    if (itemsCurrentlyTaken)
                    {
                        // Re-enable item prefabs for new inmate
                        EnableItemPrefabs();
                        itemsCurrentlyTaken = false;
                        ModLogger.Info("New inmate detected - re-enabled prison item station");
                    }
                    
                    interactableObject.SetMessage("Collect prison items");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                else
                {
                    // Items have been taken or player doesn't need them
                    interactableObject.SetMessage("Items taken");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                }
            }
        }
    }
}