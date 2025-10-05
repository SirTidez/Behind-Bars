using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Newtonsoft.Json;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.ItemFramework;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.ItemFramework;
#endif

namespace Behind_Bars.Systems.Data
{
    /// <summary>
    /// Handles persistent storage of player data across saves and sessions
    /// Stores inventory snapshots, crime data, and arrest metadata
    /// </summary>
    public class PersistentPlayerData
    {
        #region Data Structures

        [System.Serializable]
        public class StoredItem
        {
            public string itemId;
            public string itemName;
            public int stackCount;
            public bool isContraband;
            public string itemType;
            public DateTime confiscationTime;
            public string specialHandling; // For special processing like empty weapons
            public float cashBalance; // For CashInstance - stores dollar amount

            public StoredItem(string id, string name, int count, bool contraband, string type)
            {
                itemId = id;
                itemName = name;
                stackCount = count;
                isContraband = contraband;
                itemType = type;
                confiscationTime = DateTime.Now;
                specialHandling = "";
                cashBalance = 0f;
            }
        }

        [System.Serializable]
        public class PlayerInventorySnapshot
        {
            public string playerId;
            public string playerName;
            public List<StoredItem> items = new List<StoredItem>();
            public Vector3 lastPosition;
            public DateTime arrestTime;
            public string arrestId;
            public object crimeData; // Serialized crime data
            public bool isActive; // Whether this data is still relevant
            public List<ClothingLayer> originalClothing = new List<ClothingLayer>(); // Player's civilian clothing

            public PlayerInventorySnapshot(string id, string name, string arrestGuid)
            {
                playerId = id;
                playerName = name;
                arrestTime = DateTime.Now;
                arrestId = arrestGuid;
                isActive = true;
            }
        }

        [System.Serializable]
        public class ClothingLayer
        {
            public string layerPath;
            public float[] colorRGBA; // Color as array for JSON serialization

            public ClothingLayer(string path, Color color)
            {
                layerPath = path;
                colorRGBA = new float[] { color.r, color.g, color.b, color.a };
            }

            public Color GetColor()
            {
                return new Color(colorRGBA[0], colorRGBA[1], colorRGBA[2], colorRGBA[3]);
            }
        }

        [System.Serializable]
        public class PersistentGameData
        {
            public List<PlayerInventorySnapshot> playerSnapshots = new List<PlayerInventorySnapshot>();
            public Dictionary<string, Vector3> storedExitPositions = new Dictionary<string, Vector3>();
            public DateTime lastSaveTime;
            public int version = 1;
        }

        #endregion

        #region Singleton Pattern

        private static PersistentPlayerData _instance;
        public static PersistentPlayerData Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new PersistentPlayerData();
                    _instance.LoadData();
                }
                return _instance;
            }
        }

        #endregion

        #region Fields

        private PersistentGameData gameData = new PersistentGameData();
        private const string SAVE_KEY = "BehindBars_PlayerData";
        private const float AUTO_SAVE_INTERVAL = 30f; // Auto-save every 30 seconds
        private float lastAutoSave = 0f;

        #endregion

        #region Initialization

        private PersistentPlayerData()
        {
            ModLogger.Info("PersistentPlayerData initialized");
        }

        #endregion

        #region Inventory Snapshot Management

        /// <summary>
        /// Create a complete inventory snapshot for a player during arrest
        /// </summary>
        public string CreateInventorySnapshot(Player player)
        {
            if (player == null)
            {
                ModLogger.Error("Cannot create inventory snapshot for null player");
                return null;
            }

            try
            {
                string arrestId = Guid.NewGuid().ToString();
                string playerId = GetPlayerUniqueId(player);

                var snapshot = new PlayerInventorySnapshot(playerId, player.name, arrestId)
                {
                    lastPosition = player.transform.position,
                    crimeData = SerializeCrimeData(player.CrimeData)
                };

                // Capture all inventory items
                var items = CapturePlayerInventory(player);
                snapshot.items.AddRange(items);

                // Capture player's original clothing
                var clothing = CapturePlayerClothing(player);
                snapshot.originalClothing.AddRange(clothing);
                ModLogger.Info($"Captured {clothing.Count} clothing layers for {player.name}");

                // Remove any existing active snapshots for this player
                gameData.playerSnapshots.RemoveAll(s => s.playerId == playerId && s.isActive);

                // Add new snapshot
                gameData.playerSnapshots.Add(snapshot);

                ModLogger.Info($"Created inventory snapshot for {player.name} with {items.Count} items (ID: {arrestId})");

                // Save immediately
                SaveData();

                return arrestId;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating inventory snapshot: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Retrieve legal items for a player (filters out contraband)
        /// </summary>
        public List<StoredItem> GetLegalItemsForPlayer(Player player)
        {
            if (player == null) return new List<StoredItem>();

            try
            {
                string playerId = GetPlayerUniqueId(player);
                var snapshot = GetActiveSnapshotForPlayer(playerId);

                if (snapshot != null)
                {
                    var legalItems = snapshot.items.FindAll(item => !item.isContraband);
                    ModLogger.Info($"Retrieved {legalItems.Count} legal items for {player.name} (out of {snapshot.items.Count} total)");
                    return legalItems;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting legal items for player: {ex.Message}");
            }

            return new List<StoredItem>();
        }

        /// <summary>
        /// Get contraband items for a player (for evidence/records)
        /// </summary>
        public List<StoredItem> GetContrabandItemsForPlayer(Player player)
        {
            if (player == null) return new List<StoredItem>();

            try
            {
                string playerId = GetPlayerUniqueId(player);
                var snapshot = GetActiveSnapshotForPlayer(playerId);

                if (snapshot != null)
                {
                    var contrabandItems = snapshot.items.FindAll(item => item.isContraband);
                    ModLogger.Info($"Retrieved {contrabandItems.Count} contraband items for {player.name}");
                    return contrabandItems;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting contraband items for player: {ex.Message}");
            }

            return new List<StoredItem>();
        }

        /// <summary>
        /// Clear a player's inventory snapshot (on release)
        /// </summary>
        public void ClearPlayerSnapshot(Player player)
        {
            if (player == null) return;

            try
            {
                string playerId = GetPlayerUniqueId(player);
                var snapshot = GetActiveSnapshotForPlayer(playerId);

                if (snapshot != null)
                {
                    snapshot.isActive = false;
                    ModLogger.Info($"Cleared inventory snapshot for {player.name}");
                    SaveData();
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error clearing player snapshot: {ex.Message}");
            }
        }

        #endregion

        #region Position Storage

        /// <summary>
        /// Store a player's exit position
        /// </summary>
        public void StorePlayerExitPosition(string playerName, Vector3 position)
        {
            try
            {
                gameData.storedExitPositions[playerName] = position;
                ModLogger.Info($"Stored exit position for {playerName}: {position}");
                SaveData();
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error storing exit position: {ex.Message}");
            }
        }

        /// <summary>
        /// Get a player's stored exit position
        /// </summary>
        public Vector3? GetPlayerExitPosition(string playerName)
        {
            try
            {
                if (gameData.storedExitPositions.ContainsKey(playerName))
                {
                    return gameData.storedExitPositions[playerName];
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting exit position: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Inventory Processing

        private List<StoredItem> CapturePlayerInventory(Player player)
        {
            var items = new List<StoredItem>();

            try
            {
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
                    ModLogger.Warn("Could not find PlayerInventory to capture");
                    return items;
                }

                // Get all inventory slots
                var allSlots = GetAllInventorySlots(playerInventory);
                ModLogger.Info($"Found {allSlots.Count} inventory slots to check");

                foreach (var slot in allSlots)
                {
                    var storedItem = ProcessInventorySlot(slot);
                    if (storedItem != null)
                    {
                        items.Add(storedItem);
                        ModLogger.Info($"Captured item: {storedItem.itemName} (ID: {storedItem.itemId}, Stack: {storedItem.stackCount})");
                    }
                    else
                    {
                        ModLogger.Debug("Empty slot found during inventory capture");
                    }
                }

                // Also check vehicle storage if recently exited
                var vehicleItems = CaptureVehicleStorage(player);
                items.AddRange(vehicleItems);

                ModLogger.Info($"Captured {items.Count} items from {player.name}'s inventory");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error capturing player inventory: {ex.Message}");
            }

            return items;
        }

        private List<object> GetAllInventorySlots(PlayerInventory inventory)
        {
            var slots = new List<object>();

            try
            {
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
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting inventory slots: {ex.Message}");
            }

            return slots;
        }

        private StoredItem ProcessInventorySlot(object slot)
        {
            try
            {
                ModLogger.Debug($"ProcessInventorySlot: Processing slot of type {slot.GetType().Name}");

                // Get the ItemInstance from the slot
                var itemInstanceProperty = slot.GetType().GetProperty("ItemInstance");
                if (itemInstanceProperty == null)
                {
                    ModLogger.Debug("ProcessInventorySlot: No ItemInstance property found");
                    return null;
                }

                var itemInstance = itemInstanceProperty.GetValue(slot);
                if (itemInstance == null)
                {
                    ModLogger.Debug("ProcessInventorySlot: ItemInstance is null (empty slot)");
                    return null;
                }

                ModLogger.Debug($"ProcessInventorySlot: Got ItemInstance of type {itemInstance.GetType().Name}");

                // Get item details
                string itemId = GetItemId(itemInstance);
                string itemName = GetItemDisplayName(itemInstance);
                int stackCount = GetItemStackCount(itemInstance);
                bool isContraband = IsItemContraband(itemInstance);
                string itemType = GetItemType(itemInstance);

                ModLogger.Info($"ProcessInventorySlot: Extracted - Name: '{itemName}', ID: '{itemId}', Stack: {stackCount}, Type: {itemType}");

                // Skip cash entirely - don't confiscate money
                if (itemType == "CashInstance" || itemName.Contains("Cash", StringComparison.OrdinalIgnoreCase))
                {
                    ModLogger.Info($"Skipping cash - money is not confiscated during arrest");
                    return null; // Don't store cash
                }

                // Special handling for weapons and ammo
                if (IsWeaponItem(itemName, itemType))
                {
                    // Weapons are returned but emptied (no ammo)
                    var weaponItem = new StoredItem(itemId, itemName, stackCount, isContraband, itemType);
                    weaponItem.specialHandling = "empty_weapon"; // Mark for special processing
                    ModLogger.Info($"Captured weapon: {itemName} - will be returned empty");
                    return weaponItem;
                }
                else if (IsAmmoItem(itemName, itemType))
                {
                    // Ammo is permanently confiscated
                    ModLogger.Info($"Confiscating ammo permanently: {itemName} (x{stackCount})");
                    return null; // Don't store ammo - it's lost forever
                }

                return new StoredItem(itemId, itemName, stackCount, isContraband, itemType);
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error processing inventory slot: {ex.Message}");
                return null;
            }
        }

        private List<StoredItem> CaptureVehicleStorage(Player player)
        {
            var items = new List<StoredItem>();

            try
            {
                // Check if player recently exited a vehicle
                if (player.LastDrivenVehicle != null && player.TimeSinceVehicleExit < 30f)
                {
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
                                            var storedItem = ProcessInventorySlot(slot);
                                            if (storedItem != null)
                                            {
                                                items.Add(storedItem);
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
                ModLogger.Debug($"Error capturing vehicle storage: {ex.Message}");
            }

            return items;
        }

        #endregion

        #region Item Analysis

        private bool IsItemContraband(object itemInstance)
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
                ModLogger.Debug($"Error checking contraband status: {ex.Message}");
                return false; // Default to legal on error
            }
        }

        private bool IsProductInstance(object itemInstance)
        {
            try
            {
                var typeName = itemInstance.GetType().Name;
                return typeName.Contains("ProductItemInstance");
            }
            catch
            {
                return false;
            }
        }

        private bool IsProductContraband(object productInstance)
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

                    // Products are always contraband during arrest processing
                    return true;
                }

                return true; // Products default to contraband
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error checking product contraband status: {ex.Message}");
                return true; // Default to contraband for products
            }
        }

        private string GetItemId(object itemInstance)
        {
            try
            {
                // ItemInstance has a public field "ID" (not property)
                var idField = itemInstance.GetType().GetField("ID");
                if (idField != null)
                {
                    var idValue = idField.GetValue(itemInstance)?.ToString();
                    if (!string.IsNullOrEmpty(idValue))
                    {
                        ModLogger.Debug($"Got item ID via field: {idValue}");
                        return idValue;
                    }
                }

                // Fallback: Try property
                var idProperty = itemInstance.GetType().GetProperty("ID");
                if (idProperty != null)
                {
                    var idValue = idProperty.GetValue(itemInstance)?.ToString();
                    if (!string.IsNullOrEmpty(idValue))
                    {
                        ModLogger.Debug($"Got item ID via property: {idValue}");
                        return idValue;
                    }
                }

                // Last resort: Get ID from Definition
                var definitionProperty = itemInstance.GetType().GetProperty("Definition");
                if (definitionProperty != null)
                {
                    var definition = definitionProperty.GetValue(itemInstance);
                    if (definition != null)
                    {
                        var defIdProperty = definition.GetType().GetProperty("ID");
                        if (defIdProperty != null)
                        {
                            var idValue = defIdProperty.GetValue(definition)?.ToString();
                            if (!string.IsNullOrEmpty(idValue))
                            {
                                ModLogger.Debug($"Got item ID via Definition.ID: {idValue}");
                                return idValue;
                            }
                        }
                    }
                }

                ModLogger.Warn($"Could not extract ID from ItemInstance - all methods failed");
                return "unknown";
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Exception in GetItemId: {ex.Message}");
                return "unknown";
            }
        }

        private string GetItemDisplayName(object itemInstance)
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

                return GetItemId(itemInstance);
            }
            catch
            {
                return "Unknown Item";
            }
        }

        private int GetItemStackCount(object itemInstance)
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

        private string GetItemType(object itemInstance)
        {
            try
            {
                return itemInstance.GetType().Name;
            }
            catch
            {
                return "Unknown";
            }
        }

        #endregion

        #region Utility Methods

        private string GetPlayerUniqueId(Player player)
        {
            // Try to get a unique identifier for the player
            // This could be Steam ID, network ID, or fallback to name
            try
            {
                // Try to get network ID or similar unique identifier
                var networkIdProperty = player.GetType().GetProperty("NetworkId");
                if (networkIdProperty != null)
                {
                    var networkId = networkIdProperty.GetValue(player);
                    if (networkId != null) return networkId.ToString();
                }

                // Fallback to player name (not ideal for multiplayer)
                return player.name;
            }
            catch
            {
                return player.name;
            }
        }

        private PlayerInventorySnapshot GetActiveSnapshotForPlayer(string playerId)
        {
            return gameData.playerSnapshots.Find(s => s.playerId == playerId && s.isActive);
        }

        private object SerializeCrimeData(object crimeData)
        {
            try
            {
                if (crimeData != null)
                {
                    return JsonConvert.SerializeObject(crimeData);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error serializing crime data: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Save/Load System

        private void SaveData()
        {
            try
            {
                gameData.lastSaveTime = DateTime.Now;
                string jsonData = JsonConvert.SerializeObject(gameData, Formatting.Indented);
                PlayerPrefs.SetString(SAVE_KEY, jsonData);
                PlayerPrefs.Save();

                ModLogger.Debug("Player data saved successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error saving player data: {ex.Message}");
            }
        }

        private void LoadData()
        {
            try
            {
                if (PlayerPrefs.HasKey(SAVE_KEY))
                {
                    string jsonData = PlayerPrefs.GetString(SAVE_KEY);
                    if (!string.IsNullOrEmpty(jsonData))
                    {
                        gameData = JsonConvert.DeserializeObject<PersistentGameData>(jsonData);
                        if (gameData == null)
                        {
                            gameData = new PersistentGameData();
                        }

                        ModLogger.Info($"Loaded player data - {gameData.playerSnapshots.Count} snapshots, {gameData.storedExitPositions.Count} positions");
                        CleanupOldData();
                    }
                }
                else
                {
                    gameData = new PersistentGameData();
                    ModLogger.Info("No existing player data found - starting fresh");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error loading player data: {ex.Message}");
                gameData = new PersistentGameData();
            }
        }

        private void CleanupOldData()
        {
            try
            {
                // Remove snapshots older than 7 days
                var cutoffTime = DateTime.Now.AddDays(-7);
                int removedCount = gameData.playerSnapshots.RemoveAll(s => s.arrestTime < cutoffTime);

                if (removedCount > 0)
                {
                    ModLogger.Info($"Cleaned up {removedCount} old inventory snapshots");
                    SaveData();
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error cleaning up old data: {ex.Message}");
            }
        }

        public void AutoSave()
        {
            if (Time.time - lastAutoSave > AUTO_SAVE_INTERVAL)
            {
                SaveData();
                lastAutoSave = Time.time;
            }
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Get statistics about stored data
        /// </summary>
        public string GetDataStats()
        {
            var activeSnapshots = gameData.playerSnapshots.FindAll(s => s.isActive);
            return $"Active snapshots: {activeSnapshots.Count}, Stored positions: {gameData.storedExitPositions.Count}";
        }

        /// <summary>
        /// Force save all data
        /// </summary>
        public void ForceSave()
        {
            SaveData();
        }

        /// <summary>
        /// Clear all stored data (for testing)
        /// </summary>
        public void ClearAllData()
        {
            gameData = new PersistentGameData();
            SaveData();
            ModLogger.Info("All persistent player data cleared");
        }

        #endregion

        #region Weapon and Ammo Detection

        private bool IsWeaponItem(string itemName, string itemType)
        {
            if (string.IsNullOrEmpty(itemName)) return false;

            string name = itemName.ToLower();
            string type = itemType?.ToLower() ?? "";

            // Common weapon patterns
            return name.Contains("pistol") ||
                   name.Contains("gun") ||
                   name.Contains("rifle") ||
                   name.Contains("shotgun") ||
                   name.Contains("weapon") ||
                   name.Contains("firearm") ||
                   type.Contains("weapon") ||
                   type.Contains("gun");
        }

        private bool IsAmmoItem(string itemName, string itemType)
        {
            if (string.IsNullOrEmpty(itemName)) return false;

            string name = itemName.ToLower();
            string type = itemType?.ToLower() ?? "";

            // Common ammo patterns - INCLUDING MAGAZINES!
            return name.Contains("ammo") ||
                   name.Contains("ammunition") ||
                   name.Contains("bullet") ||
                   name.Contains("round") ||
                   name.Contains("cartridge") ||
                   name.Contains("shell") ||
                   name.Contains("magazine") ||
                   name.Contains("mag") ||
                   name.Contains("clip") ||
                   type.Contains("ammo") ||
                   type.Contains("ammunition") ||
                   type.Contains("magazine");
        }

        #endregion

        #region Clothing Capture and Restoration

        private List<ClothingLayer> CapturePlayerClothing(Player player)
        {
            var clothingLayers = new List<ClothingLayer>();

            try
            {
#if !MONO
                var playerAvatar = player.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var playerAvatar = player.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
#endif

                if (playerAvatar == null)
                {
                    ModLogger.Warn("Could not find player Avatar for clothing capture");
                    return clothingLayers;
                }

                var settings = playerAvatar.CurrentSettings;
                if (settings == null || settings.BodyLayerSettings == null)
                {
                    ModLogger.Warn("Player avatar settings are null");
                    return clothingLayers;
                }

                // Capture all body layers (clothing)
                foreach (var layer in settings.BodyLayerSettings)
                {
                    clothingLayers.Add(new ClothingLayer(layer.layerPath, layer.layerTint));
                }

                ModLogger.Info($"Captured {clothingLayers.Count} clothing layers from player");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error capturing player clothing: {ex.Message}");
            }

            return clothingLayers;
        }

        public void RestorePlayerClothing(Player player)
        {
            try
            {
                // Find the player's active snapshot using player ID
                string playerId = GetPlayerUniqueId(player);
                var snapshot = GetActiveSnapshotForPlayer(playerId);
                if (snapshot == null || snapshot.originalClothing == null || snapshot.originalClothing.Count == 0)
                {
                    ModLogger.Warn("No clothing data saved for player");
                    return;
                }

                ModLogger.Info($"Restoring {snapshot.originalClothing.Count} clothing layers for {player.name}");

#if !MONO
                var playerAvatar = player.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var playerAvatar = player.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
#endif

                if (playerAvatar == null)
                {
                    ModLogger.Error("Could not find player Avatar for clothing restoration");
                    return;
                }

                var settings = playerAvatar.CurrentSettings;
                if (settings == null)
                {
                    ModLogger.Error("Player avatar settings are null");
                    return;
                }

                // Clear prison clothing
                settings.BodyLayerSettings.Clear();

                // Restore original clothing layers
                foreach (var clothingLayer in snapshot.originalClothing)
                {
                    settings.BodyLayerSettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                    {
                        layerPath = clothingLayer.layerPath,
                        layerTint = clothingLayer.GetColor()
                    });
                }

                // Apply the clothing changes
                playerAvatar.ApplyBodyLayerSettings(settings);
                ModLogger.Info($"✓ Restored original clothing for {player.name} - changed back from prison attire");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error restoring player clothing: {ex.Message}");
            }
        }

        #endregion
    }
}