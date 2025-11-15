using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Systems.NPCs;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Clothing;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Clothing;
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
        private bool forceEnabledForNewInmate = false; // Force enabled state after reset, bypass Update checks
        private float resetTime = 0f; // Time when reset occurred

        // Store player's original clothing
        private Dictionary<string, object> originalPlayerClothing = new Dictionary<string, object>();
        
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
            ModLogger.Debug("JailInventoryPickupStation interaction setup completed");

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
                    ModLogger.Debug("Created default StorageLocation for prison items");
                }
            }

            // Disable conflicting InventoryDropOff GameObject if it exists
            DisableConflictingInventoryDropOff();
        }
        
        private void SetupInteractableComponent()
        {
            // Get or create InteractableObject component
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Debug("Added InteractableObject component to JailInventoryPickupStation");
            }
            else
            {
                ModLogger.Debug("Found existing InteractableObject component on JailInventoryPickupStation");
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
            
            ModLogger.Debug("JailInventoryPickupStation InteractableObject component configured with event listeners");
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

            var releaseManager = ReleaseManager.Instance;
            bool isDuringRelease = false;

            // Check if this is during a release process
            if (releaseManager != null)
            {
                var releaseStatus = releaseManager.GetReleaseStatus(currentPlayer);
                isDuringRelease = releaseStatus != ReleaseManager.ReleaseStatus.NotStarted &&
                                 releaseStatus != ReleaseManager.ReleaseStatus.Completed &&
                                 releaseStatus != ReleaseManager.ReleaseStatus.Failed;
            }

            if (isDuringRelease)
            {
                ModLogger.Info($"JailInventoryPickupStation: Release mode interaction for {currentPlayer.name}");
                // During release: Process item exchange (remove prison items, give back personal items)
                MelonCoroutines.Start(ProcessReleaseItemExchange(currentPlayer));
                return;
            }

            // ORIGINAL BOOKING LOGIC BELOW
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

            // Check if player already received prison items (SKIP if force-enabled for new inmate)
            if (!forceEnabledForNewInmate && HasReceivedPrisonItems(playerHandler))
            {
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Prison items already received",
                        NotificationType.Progress
                    );
                }
                ModLogger.Info("JailInventoryPickupStation: Player already has prison items");
                return;
            }

            // If force-enabled, log that we're bypassing the check
            if (forceEnabledForNewInmate)
            {
                ModLogger.Info("JailInventoryPickupStation: Force-enabled for new arrest - bypassing 'already received' check");
            }

            // Start prison item pickup process
            MelonCoroutines.Start(ProcessJailItemPickup(currentPlayer));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessReleaseItemExchange(Player player)
        {
            isProcessing = true;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Exchanging items...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }

            ModLogger.Info($"Starting release item exchange for {player.name}");

            // Show initial notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Returning prison items and retrieving personal belongings...",
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

            // Phase 1: Remove prison items from player inventory
            var prisonItemsToRemove = new List<string> { "Prison Uniform", "Prison Shoes", "Prison Socks" };
            int itemsRemoved = 0;

            foreach (var itemName in prisonItemsToRemove)
            {
                bool itemRemoved = false;
                try
                {
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

                if (itemRemoved && BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"Returned: {itemName}",
                        NotificationType.Progress
                    );
                    yield return new WaitForSeconds(0.3f);
                }
            }

            yield return new WaitForSeconds(1f);

            // Phase 2: Give back personal items using PersistentPlayerData
            var persistentData = Behind_Bars.Systems.Data.PersistentPlayerData.Instance;
            if (persistentData != null)
            {
                var legalItems = persistentData.GetLegalItemsForPlayer(player);
                var contrabandItems = persistentData.GetContrabandItemsForPlayer(player);

                ModLogger.Info($"Found {legalItems.Count} legal items and {contrabandItems.Count} contraband items for {player.name}");

                // Return legal items
                foreach (var item in legalItems)
                {
                    bool itemGiven = false;
                    try
                    {
                        // Since stored items have "unknown" IDs, use direct inventory methods instead of registry lookup
                        // Try multiple approaches to add the item back
                        ModLogger.Info($"Attempting to return: {item.itemName} (ID: {item.itemId})");

                        // Try multiple inventory addition approaches
                        bool addSuccess = false;

                        // Approach 1: Try AddItem with string parameter
                        try
                        {
                            var methods = inventory.GetType().GetMethods();
                            foreach (var method in methods)
                            {
                                if (method.Name == "AddItem" && method.GetParameters().Length == 1)
                                {
                                    var paramType = method.GetParameters()[0].ParameterType;
                                    if (paramType == typeof(string))
                                    {
                                        for (int i = 0; i < item.stackCount; i++)
                                        {
                                            method.Invoke(inventory, new object[] { item.itemName });
                                        }
                                        addSuccess = true;
                                        ModLogger.Info($"Successfully returned {item.itemName} using AddItem(string)");
                                        break;
                                    }
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            ModLogger.Debug($"AddItem(string) failed for {item.itemName}: {ex.Message}");
                        }

                        // Approach 2: Try GiveItem methods
                        if (!addSuccess)
                        {
                            try
                            {
                                var giveItemMethod = inventory.GetType().GetMethod("GiveItem");
                                if (giveItemMethod != null)
                                {
                                    for (int i = 0; i < item.stackCount; i++)
                                    {
                                        giveItemMethod.Invoke(inventory, new object[] { item.itemName });
                                    }
                                    addSuccess = true;
                                    ModLogger.Info($"Successfully returned {item.itemName} using GiveItem");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                ModLogger.Debug($"GiveItem failed for {item.itemName}: {ex.Message}");
                            }
                        }

                        // Approach 3: Try to use the same method that gives prison items but with civilian items
                        if (!addSuccess)
                        {
                            try
                            {
                                // Try to add using registry lookup with item name as ID
                                GivePrisonItem(inventory, item.itemName.ToLower().Replace(" ", ""));
                                addSuccess = true;
                                ModLogger.Info($"Successfully returned {item.itemName} using GivePrisonItem fallback");
                            }
                            catch (System.Exception ex)
                            {
                                ModLogger.Debug($"GivePrisonItem fallback failed for {item.itemName}: {ex.Message}");
                            }
                        }

                        if (addSuccess)
                        {
                            itemGiven = true;
                        }
                        else
                        {
                            ModLogger.Warn($"All methods failed to return {item.itemName} - item not added to inventory");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Error returning item {item.itemName}: {ex.Message}");
                    }

                    if (itemGiven && BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification(
                            $"Returned: {item.itemName}",
                            NotificationType.Progress
                        );
                        yield return new WaitForSeconds(0.3f);
                    }
                }

                // Clear stored items
                persistentData.ClearPlayerSnapshot(player);
                ModLogger.Info($"Cleared stored items for {player.name}");
            }

            // Complete the exchange
            CompletePickup();
            ModLogger.Info($"Release item exchange completed for {player.name}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessJailItemPickup(Player player)
        {
            // Clear force-enabled flag now that player is interacting
            forceEnabledForNewInmate = false;
            ModLogger.Info($"JailInventoryPickupStation: Cleared force-enabled flag - player interacting");

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
            
            // First, change player to prison clothing
            yield return ChangePlayerToPrisonClothing(player);

            yield return new WaitForSeconds(1f);

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

            // Mark prison gear pickup as complete in booking process
            ModLogger.Info("Attempting to find BookingProcess to mark gear pickup complete...");
            var bookingProcess = FindObjectOfType<BookingProcess>();
            if (bookingProcess != null)
            {
                ModLogger.Info($"Found BookingProcess! Current state - Mugshot: {bookingProcess.mugshotComplete}, Fingerprint: {bookingProcess.fingerprintComplete}, Prison Gear: {bookingProcess.prisonGearPickupComplete}");
                bookingProcess.SetPrisonGearPickupComplete();
                ModLogger.Info($"Prison gear pickup marked as complete! New state - Mugshot: {bookingProcess.mugshotComplete}, Fingerprint: {bookingProcess.fingerprintComplete}, Prison Gear: {bookingProcess.prisonGearPickupComplete}");
                ModLogger.Info($"IsBookingComplete: {bookingProcess.IsBookingComplete()}");
            }
            else
            {
                ModLogger.Error("BookingProcess not found! Cannot mark prison gear pickup as complete");
            }

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
        
        /// <summary>
        /// Change player's appearance to prison attire
        /// Uses third-person camera view to fix visual bug when applying uniform
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ChangePlayerToPrisonClothing(Player player)
        {
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            
            // Freeze player movement during uniform application
            bool wasMovable = false;
            try
            {
#if MONO
                wasMovable = PlayerSingleton<PlayerMovement>.Instance.CanMove;
                PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
                wasMovable = PlayerSingleton<PlayerMovement>.Instance.canMove;
                PlayerSingleton<PlayerMovement>.Instance.canMove = false;
#endif
                ModLogger.Debug("Froze player movement during uniform application");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error freezing player movement: {ex.Message}");
            }
            
            // Enter third-person view to fix visual bug
            // TEMPORARILY COMMENTED OUT FOR TESTING
            /*
            if (playerCamera != null)
            {
                try
                {
                    playerCamera.ViewAvatar();
                    ModLogger.Debug("Entered third-person view for uniform application");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Could not enter third-person view: {ex.Message}");
                }
            }
            */

            // Wait a moment for camera transition to complete
            // yield return new WaitForSeconds(0.2f);

            // Get player's avatar component and apply uniform
            try
            {
                ModLogger.Info($"Changing {player.name} to prison clothing...");

                // Get player's avatar component
#if !MONO
                var playerAvatar = player.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var playerAvatar = player.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
#endif
                if (playerAvatar == null)
                {
                    ModLogger.Error("Could not find player's Avatar component");
                    // Exit third-person view before breaking
                    // TEMPORARILY COMMENTED OUT FOR TESTING
                    /*
                    if (playerCamera != null)
                    {
                        try { playerCamera.StopViewingAvatar(); } catch { }
                    }
                    */
                    // Restore movement before breaking
                    try
                    {
#if MONO
                        PlayerSingleton<PlayerMovement>.Instance.CanMove = wasMovable;
#else
                        PlayerSingleton<PlayerMovement>.Instance.canMove = wasMovable;
#endif
                    }
                    catch { }
                    yield break;
                }

                // Get current avatar settings
                var currentSettings = playerAvatar.CurrentSettings;
                if (currentSettings == null)
                {
                    ModLogger.Error("Could not get player's current avatar settings");
                    // Exit third-person view before breaking
                    // TEMPORARILY COMMENTED OUT FOR TESTING
                    /*
                    if (playerCamera != null)
                    {
                        try { playerCamera.StopViewingAvatar(); } catch { }
                    }
                    */
                    // Restore movement before breaking
                    try
                    {
#if MONO
                        PlayerSingleton<PlayerMovement>.Instance.CanMove = wasMovable;
#else
                        PlayerSingleton<PlayerMovement>.Instance.canMove = wasMovable;
#endif
                    }
                    catch { }
                    yield break;
                }

                // Save original clothing (body layers and accessories)
                SaveOriginalClothing(currentSettings);

                // Clear current clothing
                currentSettings.BodyLayerSettings.Clear();
                currentSettings.AccessorySettings.Clear();

                // Apply prison uniform (similar to inmates)
                bool isMale = currentSettings.Gender < 0.5f;

                // Add underwear first
                string underwearPath = isMale ? "Avatar/Layers/Bottom/MaleUnderwear" : "Avatar/Layers/Bottom/FemaleUnderwear";
                currentSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = underwearPath,
                    layerTint = Color.white
                });

                // Add orange jumpsuit top
                currentSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = AvatarResourcePaths.Tops.TShirt,
                    layerTint = new Color(1f, 0.5f, 0f) // Orange
                });

                // Add orange jumpsuit pants
                currentSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = AvatarResourcePaths.Bottoms.Jeans,
                    layerTint = new Color(1f, 0.5f, 0f) // Orange
                });

                // Add prison sandals/flip-flops
                currentSettings.AccessorySettings.Clear();
                currentSettings.AccessorySettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#endif
                {
                    path = AvatarResourcePaths.Footwear.Sandals,
                    color = new Color(0.3f, 0.3f, 0.3f) // Gray sandals
                });

                // Apply the changes
                playerAvatar.LoadAvatarSettings(currentSettings);

                ModLogger.Info($"✓ Changed {player.name} to prison attire");
                
                // CRITICAL: Ensure player visibility is reset to false after uniform application
                // This fixes visual bugs that can occur if SetVisibleToLocalPlayer was set to true during mugshot
                try
                {
                    // Reset player visibility to false
                    player.SetVisibleToLocalPlayer(false);
                    ModLogger.Debug("Reset player visibility to false after uniform application");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Could not reset player visibility: {ex.Message}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error changing player to prison clothing: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
                
                // Ensure visibility is reset even on error
                try
                {
                    if (player != null)
                    {
                        player.SetVisibleToLocalPlayer(false);
                    }
                }
                catch { }
            }

            // Hold third-person view for 1 second while player is frozen to see the uniform change
            // Player remains frozen and in third-person during this wait
            yield return new WaitForSeconds(1f);

            // Exit third-person view
            // TEMPORARILY COMMENTED OUT FOR TESTING
            /*
            if (playerCamera != null)
            {
                try
                {
                    playerCamera.StopViewingAvatar();
                    ModLogger.Debug("Exited third-person view after uniform application");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Could not exit third-person view: {ex.Message}");
                }
            }
            */

            // Restore player movement after uniform application and third-person view
            try
            {
#if MONO
                PlayerSingleton<PlayerMovement>.Instance.CanMove = wasMovable;
#else
                PlayerSingleton<PlayerMovement>.Instance.canMove = wasMovable;
#endif
                ModLogger.Debug("Restored player movement after uniform application");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error restoring player movement: {ex.Message}");
            }

            // Show notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Changed into prison uniform",
                    NotificationType.Progress
                );
            }
        }

        /// <summary>
        /// Save player's original clothing for later restoration
        /// </summary>
        private void SaveOriginalClothing(AvatarSettings settings)
        {
            try
            {
                ModLogger.Info("Saving player's original clothing...");

                // Clear any existing saved clothing
                originalPlayerClothing.Clear();

                // Save body layers
                var bodyLayers = new List<object>();
                foreach (var layer in settings.BodyLayerSettings)
                {
                    bodyLayers.Add(new Dictionary<string, object>
                    {
                        { "path", layer.layerPath },
                        { "tint", layer.layerTint }
                    });
                }
                originalPlayerClothing["BodyLayers"] = bodyLayers;

                // Save accessories
                var accessories = new List<object>();
                foreach (var accessory in settings.AccessorySettings)
                {
                    accessories.Add(new Dictionary<string, object>
                    {
                        { "path", accessory.path },
                        { "color", accessory.color }
                    });
                }
                originalPlayerClothing["Accessories"] = accessories;

                ModLogger.Info($"Saved {bodyLayers.Count} body layers and {accessories.Count} accessories");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error saving original clothing: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore player's original clothing (for when they're released)
        /// </summary>
        public void RestoreOriginalClothing(Player player)
        {
            try
            {
                if (originalPlayerClothing.Count == 0)
                {
                    ModLogger.Warn("No original clothing saved to restore");
                    return;
                }

                ModLogger.Info($"Restoring {player.name}'s original clothing...");

#if !MONO
                var playerAvatar = player.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var playerAvatar = player.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
#endif
                if (playerAvatar == null)
                {
                    ModLogger.Error("Could not find player's Avatar component for restoration");
                    return;
                }

                var currentSettings = playerAvatar.CurrentSettings;
                if (currentSettings == null)
                {
                    ModLogger.Error("Could not get player's current avatar settings for restoration");
                    return;
                }

                // Clear prison clothing
                currentSettings.BodyLayerSettings.Clear();
                currentSettings.AccessorySettings.Clear();

                // Restore body layers
                if (originalPlayerClothing.ContainsKey("BodyLayers"))
                {
                    var bodyLayers = originalPlayerClothing["BodyLayers"] as List<object>;
                    foreach (var layerObj in bodyLayers)
                    {
                        var layer = layerObj as Dictionary<string, object>;
                        if (layer != null)
                        {
                            currentSettings.BodyLayerSettings.Add(new
#if !MONO
                                Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                                ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                            {
                                layerPath = layer["path"] as string,
                                layerTint = (Color)layer["tint"]
                            });
                        }
                    }
                }

                // Restore accessories
                if (originalPlayerClothing.ContainsKey("Accessories"))
                {
                    var accessories = originalPlayerClothing["Accessories"] as List<object>;
                    foreach (var accessoryObj in accessories)
                    {
                        var accessory = accessoryObj as Dictionary<string, object>;
                        if (accessory != null)
                        {
                            currentSettings.AccessorySettings.Add(new
#if !MONO
                                Il2CppScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#else
                                ScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#endif
                            {
                                path = accessory["path"] as string,
                                color = (Color)accessory["color"]
                            });
                        }
                    }
                }

                // Apply the restored settings
                playerAvatar.LoadAvatarSettings(currentSettings);

                ModLogger.Info($"✓ Restored {player.name}'s original clothing");

                // Clear saved clothing
                originalPlayerClothing.Clear();
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error restoring original clothing: {ex.Message}");
            }
        }

        /// <summary>
        /// Disable conflicting InventoryDropOff GameObject to prevent duplicate interactions
        /// </summary>
        private void DisableConflictingInventoryDropOff()
        {
            try
            {
                // Look for InventoryDropOff in the parent storage area
                var jailController = Core.JailController;
                if (jailController?.storage?.inventoryDropOff != null)
                {
                    var dropOffObj = jailController.storage.inventoryDropOff.gameObject;
                    if (dropOffObj.activeSelf)
                    {
                        dropOffObj.SetActive(false);
                        ModLogger.Info("Disabled conflicting InventoryDropOff GameObject to prevent duplicate interactions");
                    }
                }

                // Also look for any GameObject named "InventoryDropOff" in the scene
                var dropOffObjects = FindObjectsOfType<GameObject>();
                foreach (var obj in dropOffObjects)
                {
                    if (obj.name == "InventoryDropOff" && obj.activeSelf)
                    {
                        obj.SetActive(false);
                        ModLogger.Info($"Disabled conflicting GameObject: {obj.name}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Could not disable InventoryDropOff objects: {ex.Message}");
            }
        }

        private void CompletePickup()
        {
            isProcessing = false;

            // Notify the ReleaseManager that inventory processing is complete
            if (currentPlayer != null && ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.OnInventoryProcessingComplete(currentPlayer);
                ModLogger.Info($"JailInventoryPickupStation: Notified ReleaseManager that inventory pickup is complete for {currentPlayer.name}");
            }
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
                var itemNames = new string[] { "ClothingPile (1)", "PillowAndSheets", "BedRoll", "JailCup", "JailToothBrush" };
                
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
                var itemNames = new string[] { "ClothingPile (1)", "PillowAndSheets", "BedRoll", "JailCup", "JailToothBrush" };
                
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
        
        /// <summary>
        /// Reset the station for a new inmate (called from BookingProcess on re-arrest)
        /// </summary>
        public void ResetForNewInmate()
        {
            ModLogger.Info("JailInventoryPickupStation: Resetting for new inmate");

            // CRITICAL: Re-enable the GameObject itself (it gets disabled during release)
            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
                ModLogger.Info("JailInventoryPickupStation: Re-enabled GameObject");
            }

            // Re-enable all item prefabs
            EnableItemPrefabs();
            itemsCurrentlyTaken = false;
            isProcessing = false;
            currentPlayer = null;

            // CRITICAL: Force enabled state and prevent Update() from disabling it
            forceEnabledForNewInmate = true;
            resetTime = Time.time;

            // Reset interaction state
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Collect prison items");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                ModLogger.Info("JailInventoryPickupStation: Interaction FORCED to enabled for new inmate");
            }

            ModLogger.Info("JailInventoryPickupStation: Reset complete and ready for new inmate");
        }

        void Update()
        {
            // PRIORITY: Check force-enabled flag first (overrides all other logic)
            if (forceEnabledForNewInmate && interactableObject != null)
            {
                // Keep enabled state for new inmate, don't let Update() disable it
                interactableObject.SetMessage("Collect prison items");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);

                // Log once per second to debug
                if (Time.frameCount % 60 == 0)
                {
                    ModLogger.Debug($"JailInventoryPickupStation: FORCE ENABLED for new inmate (time since reset: {Time.time - resetTime:F1}s)");
                }

                return; // Skip normal Update logic while force-enabled
            }

            // Update interaction state based on whether player needs prison items or is being released
            if (!isProcessing && interactableObject != null)
            {
                var localPlayer = Player.Local;
                var releaseManager = ReleaseManager.Instance;

                // Check if this is during a release process
                bool isDuringRelease = false;
                if (releaseManager != null && localPlayer != null)
                {
                    var releaseStatus = releaseManager.GetReleaseStatus(localPlayer);
                    isDuringRelease = releaseStatus != ReleaseManager.ReleaseStatus.NotStarted &&
                                     releaseStatus != ReleaseManager.ReleaseStatus.Completed &&
                                     releaseStatus != ReleaseManager.ReleaseStatus.Failed;
                }

                if (isDuringRelease)
                {
                    // During release: Allow interaction to exchange prison items for personal belongings
                    interactableObject.SetMessage("Exchange prison items for personal belongings");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                    // Removed spam debug log: ModLogger.Debug("JailInventoryPickupStation: Release mode - allowing interaction");
                }
                else if (localPlayer != null && NeedsPrisonItems(localPlayer))
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

                    // Debug logging for interaction state
                    if (Time.frameCount % 300 == 0) // Log every ~5 seconds
                    {
                        ModLogger.Debug($"JailInventoryPickupStation: Enabled for {localPlayer.name} - NeedsPrisonItems=true, State=Default");
                    }
                }
                else
                {
                    // Items have been taken or player doesn't need them
                    interactableObject.SetMessage("Items taken");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);

                    // Debug why not enabled
                    if (localPlayer != null && Time.frameCount % 300 == 0)
                    {
                        var playerHandler = Behind_Bars.Core.GetPlayerHandler(localPlayer);
                        bool hasReceived = playerHandler != null && HasReceivedPrisonItems(playerHandler);
                        ModLogger.Debug($"JailInventoryPickupStation: Disabled - Player exists: true, NeedsPrisonItems: false, HasReceivedPrisonItems: {hasReceived}");
                    }
                }
            }
        }
    }
}