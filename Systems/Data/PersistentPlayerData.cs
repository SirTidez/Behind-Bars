using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;

#if MONO
using Newtonsoft.Json;
using JsonSerialization = Newtonsoft.Json.Serialization;
#endif

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.ItemFramework;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.ItemFramework;
#endif

namespace Behind_Bars.Systems.Data
{
#if MONO
    /// <summary>
    /// Custom JSON converter for Unity Vector3 to avoid circular reference issues
    /// Serializes Vector3 as a simple object with x, y, z properties
    /// </summary>
    public class Vector3JsonConverter : JsonConverter
    {
        /// <inheritdoc />
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(Vector3);
        }

        /// <inheritdoc />
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is Vector3 vector)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("x");
                writer.WriteValue(vector.x);
                writer.WritePropertyName("y");
                writer.WriteValue(vector.y);
                writer.WritePropertyName("z");
                writer.WriteValue(vector.z);
                writer.WriteEndObject();
            }
        }

        /// <inheritdoc />
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            // Missing coordinates remain zero, matching the converter's legacy fallback.
            float x = 0, y = 0, z = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                    break;

                if (reader.TokenType == JsonToken.PropertyName)
                {
                    string propertyName = reader.Value.ToString();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "x":
                            x = Convert.ToSingle(reader.Value);
                            break;
                        case "y":
                            y = Convert.ToSingle(reader.Value);
                            break;
                        case "z":
                            z = Convert.ToSingle(reader.Value);
                            break;
                    }
                }
            }

            return new Vector3(x, y, z);
        }
    }

    /// <summary>
    /// Contract resolver to ignore problematic Vector3 properties
    /// </summary>
    public class Vector3ContractResolver : JsonSerialization.DefaultContractResolver
    {
        /// <inheritdoc />
        protected override JsonSerialization.JsonProperty CreateProperty(
            System.Reflection.MemberInfo member,
            Newtonsoft.Json.MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);

            // Ignore computed Vector3 properties that cause circular references
            if (property.DeclaringType == typeof(Vector3))
            {
                if (property.PropertyName == "normalized" ||
                    property.PropertyName == "magnitude" ||
                    property.PropertyName == "sqrMagnitude")
                {
                    property.ShouldSerialize = instance => false;
                }
            }

            return property;
        }
    }
#endif

    /// <summary>
    /// Handles persistent storage of player data across saves and sessions
    /// Stores inventory snapshots, crime data, and arrest metadata
    /// </summary>
    public class PersistentPlayerData
    {
        #region Data Structures

        /// <summary>In-memory representation of one item held in custody.</summary>
        [System.Serializable]
        public class StoredItem
        {
            /// <summary>Runtime item identifier captured at arrest.</summary>
            public string itemId;
            /// <summary>Display name captured at arrest.</summary>
            public string itemName;
            /// <summary>Captured stack quantity.</summary>
            public int stackCount;
            /// <summary>Whether this item was classified as contraband.</summary>
            public bool isContraband;
            /// <summary>Runtime type name used for classification and diagnostics.</summary>
            public string itemType;
            /// <summary>Local timestamp at which the item entered custody.</summary>
            public DateTime confiscationTime;
            /// <summary>Special handling marker, such as an empty weapon.</summary>
            public string specialHandling; // For special processing like empty weapons
            /// <summary>Cash amount represented by a cash item; other items use zero.</summary>
            public float cashBalance; // For CashInstance - stores dollar amount

            /// <summary>Creates an in-memory confiscated-item record.</summary>
            /// <param name="id">Item identifier.</param>
            /// <param name="name">Display name.</param>
            /// <param name="count">Captured stack quantity.</param>
            /// <param name="contraband">Whether the item is contraband.</param>
            /// <param name="type">Runtime item type name.</param>
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

        /// <summary>In-memory custody snapshot for one player and arrest.</summary>
        [System.Serializable]
        public class PlayerInventorySnapshot
        {
            /// <summary>Stable player identifier used to find the active snapshot.</summary>
            public string playerId;
            /// <summary>Player name captured for display and legacy lookup.</summary>
            public string playerName;
            /// <summary>Items held in custody for this arrest.</summary>
            public List<StoredItem> items = new List<StoredItem>();
            /// <summary>Last known position at snapshot creation.</summary>
            public Vector3 lastPosition;
            /// <summary>Local timestamp at which the arrest snapshot was created.</summary>
            public DateTime arrestTime;
            /// <summary>Unique arrest/custody identifier returned to callers.</summary>
            public string arrestId;
            /// <summary>Opaque crime payload retained for compatibility with existing callers.</summary>
            public object crimeData; // Serialized crime data
            /// <summary>Whether this snapshot is eligible for active-player lookup.</summary>
            public bool isActive; // Whether this data is still relevant
            /// <summary>Civilian body layers captured before prison attire is applied.</summary>
            public List<ClothingLayer> originalClothing = new List<ClothingLayer>(); // Player's civilian clothing
            /// <summary>Civilian accessories captured before prison attire is applied.</summary>
            public List<ClothingAccessory> originalAccessories = new List<ClothingAccessory>(); // Civilian accessories, shoes, hair, and headwear

            /// <summary>Creates an active custody snapshot with a local arrest timestamp.</summary>
            /// <param name="id">Stable player identifier.</param>
            /// <param name="name">Player display name.</param>
            /// <param name="arrestGuid">Arrest/custody identifier.</param>
            public PlayerInventorySnapshot(string id, string name, string arrestGuid)
            {
                playerId = id;
                playerName = name;
                arrestTime = DateTime.Now;
                arrestId = arrestGuid;
                isActive = true;
            }
        }

        /// <summary>Serializable civilian avatar body-layer record.</summary>
        [System.Serializable]
        public class ClothingLayer
        {
            /// <summary>Avatar layer/resource path.</summary>
            public string layerPath;
            /// <summary>RGBA values in array form for save serialization.</summary>
            public float[] colorRGBA; // Color as array for JSON serialization

            /// <summary>Creates a serializable clothing layer from an avatar layer.</summary>
            /// <param name="path">Avatar layer/resource path.</param>
            /// <param name="color">Layer tint.</param>
            public ClothingLayer(string path, Color color)
            {
                layerPath = path;
                colorRGBA = new float[] { color.r, color.g, color.b, color.a };
            }

            /// <summary>Reconstructs the layer tint from its RGBA array.</summary>
            public Color GetColor()
            {
                return new Color(colorRGBA[0], colorRGBA[1], colorRGBA[2], colorRGBA[3]);
            }
        }

        /// <summary>Serializable civilian avatar accessory record.</summary>
        [System.Serializable]
        public class ClothingAccessory
        {
            /// <summary>Avatar accessory/resource path.</summary>
            public string path;
            /// <summary>RGBA values in array form for save serialization.</summary>
            public float[] colorRGBA;

            /// <summary>Creates a serializable accessory from an avatar accessory.</summary>
            /// <param name="accessoryPath">Avatar accessory/resource path.</param>
            /// <param name="color">Accessory tint.</param>
            public ClothingAccessory(string accessoryPath, Color color)
            {
                path = accessoryPath;
                colorRGBA = new[] { color.r, color.g, color.b, color.a };
            }

            /// <summary>
            /// Reconstructs the accessory tint, defaulting missing channels to opaque white
            /// for older or partially populated save data.
            /// </summary>
            public Color GetColor()
            {
                return new Color(
                    colorRGBA != null && colorRGBA.Length > 0 ? colorRGBA[0] : 1f,
                    colorRGBA != null && colorRGBA.Length > 1 ? colorRGBA[1] : 1f,
                    colorRGBA != null && colorRGBA.Length > 2 ? colorRGBA[2] : 1f,
                    colorRGBA != null && colorRGBA.Length > 3 ? colorRGBA[3] : 1f);
            }
        }

        /// <summary>Root in-memory data graph for persistent player records.</summary>
        [System.Serializable]
        public class PersistentGameData
        {
            /// <summary>All arrest snapshots retained by the mod.</summary>
            public List<PlayerInventorySnapshot> playerSnapshots = new List<PlayerInventorySnapshot>();
            /// <summary>Exit positions keyed by the current stable player identifier.</summary>
            public Dictionary<string, Vector3> storedExitPositions = new Dictionary<string, Vector3>();
            /// <summary>
            /// Wall-clock time recorded when a save starts. The value is updated before
            /// serialization, so it may advance even when the save later fails.
            /// </summary>
            public DateTime lastSaveTime;
            /// <summary>Current save schema version.</summary>
            public int version = 1;
        }

        #endregion

        /// <summary>
        /// Simple serializable Vector3 structure for JSON serialization
        /// Used when JsonSerializerSettings are not available (Mono mode)
        /// </summary>
        [System.Serializable]
        private class SerializableVector3
        {
            /// <summary>Serialized X coordinate.</summary>
            public float x;
            /// <summary>Serialized Y coordinate.</summary>
            public float y;
            /// <summary>Serialized Z coordinate.</summary>
            public float z;

            /// <summary>Creates an empty DTO for the serializer.</summary>
            public SerializableVector3() { }

            /// <summary>Copies Unity position components into the DTO.</summary>
            /// <param name="vector">Position to flatten.</param>
            public SerializableVector3(Vector3 vector)
            {
                x = vector.x;
                y = vector.y;
                z = vector.z;
            }

            /// <summary>Reconstructs the Unity position represented by this DTO.</summary>
            public Vector3 ToVector3()
            {
                return new Vector3(x, y, z);
            }
        }

        #region Singleton Pattern

        private static PersistentPlayerData _instance;
        /// <summary>
        /// Gets the process-wide data store, loading persisted state on first access.
        /// </summary>
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

        // Runtime object graph. Serialization is routed through SaveData/LoadData rather
        // than exposing this instance directly to the native save system.
        private PersistentGameData gameData = new PersistentGameData();
        // Legacy save key retained for compatibility with the original JsonHelper path.
        private const string SAVE_KEY = "BehindBars_PlayerData";
        // Unity real-time seconds between opportunistic AutoSave calls.
        private const float AUTO_SAVE_INTERVAL = 30f;
        private float lastAutoSave = 0f;

        #endregion

        #region Initialization

        private PersistentPlayerData()
        {
            // Construction is private so all callers share one loaded data set through
            // Instance; LoadData is deliberately deferred until first access.
            ModLogger.Info("PersistentPlayerData initialized");
        }

        #endregion

        #region Inventory Snapshot Management

        /// <summary>
        /// Creates or upgrades the active inventory snapshot for a player during arrest.
        /// </summary>
        /// <param name="player">Player whose inventory, appearance, and crime payload are captured.</param>
        /// <returns>The arrest identifier, or null when capture fails or <paramref name="player"/> is null.</returns>
        public string CreateInventorySnapshot(Player player)
        {
            if (player == null)
            {
                ModLogger.Error("Cannot create inventory snapshot for null player");
                return null;
            }

            try
            {
                string playerId = GetPlayerUniqueId(player);
                var existingSnapshot = GetActiveSnapshotByPlayerId(playerId);
                // Capture before deciding whether an existing arrest callback was the first
                // useful snapshot.  The native server/RPC callbacks can arrive while the
                // inventory collection is still empty on IL2CPP; blindly reusing that empty
                // snapshot permanently loses equippables such as a skateboard.
                var capturedItems = CapturePlayerInventory(player);
                var capturedClothing = CapturePlayerClothing(player);
                var capturedAccessories = CapturePlayerAccessories(player);
                if (existingSnapshot != null)
                {
                    // An active snapshot is reused by arrest ID. Empty item/appearance
                    // sections are upgraded from this capture, but non-empty sections are
                    // treated as authoritative to avoid duplicate callback accumulation.
                    bool upgradedAppearance = false;
                    if (existingSnapshot.items != null && existingSnapshot.items.Count > 0)
                    {
                        ModLogger.Debug($"Reusing captured personal property from active snapshot {existingSnapshot.arrestId} for {player.name}");
                    }
                    else if (capturedItems.Count > 0)
                    {
                        existingSnapshot.items ??= new List<StoredItem>();
                        existingSnapshot.items.AddRange(capturedItems);
                        ModLogger.Info($"Upgraded initially empty inventory snapshot {existingSnapshot.arrestId} with {capturedItems.Count} captured item(s)");
                    }

                    if ((existingSnapshot.originalClothing == null || existingSnapshot.originalClothing.Count == 0) && capturedClothing.Count > 0)
                    {
                        existingSnapshot.originalClothing = new List<ClothingLayer>(capturedClothing);
                        upgradedAppearance = true;
                    }

                    if ((existingSnapshot.originalAccessories == null || existingSnapshot.originalAccessories.Count == 0) && capturedAccessories.Count > 0)
                    {
                        existingSnapshot.originalAccessories = new List<ClothingAccessory>(capturedAccessories);
                        upgradedAppearance = true;
                    }

                    if (upgradedAppearance)
                    {
                        ModLogger.Info($"Upgraded active snapshot {existingSnapshot.arrestId} with {existingSnapshot.originalClothing?.Count ?? 0} civilian layers and {existingSnapshot.originalAccessories?.Count ?? 0} accessories");
                    }

                    SaveData();
                    return existingSnapshot.arrestId;
                }

                string arrestId = Guid.NewGuid().ToString();
                var legacySnapshotLookupKeys = GetLegacySnapshotLookupKeys(player, playerId);

                var snapshot = new PlayerInventorySnapshot(playerId, player.name, arrestId)
                {
                    lastPosition = player.transform.position,
                    crimeData = SerializeCrimeData(player.CrimeData)
                };

                // Capture all inventory items
                snapshot.items.AddRange(capturedItems);

                // Capture player's original clothing
                snapshot.originalClothing.AddRange(capturedClothing);
                snapshot.originalAccessories.AddRange(capturedAccessories);
                ModLogger.Info($"Captured {capturedClothing.Count} clothing layers and {capturedAccessories.Count} accessories for {player.name}");

                // Remove any existing active snapshots for this player
                gameData.playerSnapshots.RemoveAll(s => s.isActive &&
                    (s.playerId == playerId || legacySnapshotLookupKeys.Contains(s.playerId)));

                // Add new snapshot
                gameData.playerSnapshots.Add(snapshot);

                ModLogger.Info($"Created inventory snapshot for {player.name} with {capturedItems.Count} items (ID: {arrestId})");

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
        /// Retrieves items in the active snapshot that were not marked as contraband.
        /// </summary>
        /// <param name="player">Player whose active snapshot should be queried.</param>
        /// <returns>A new list of legal stored items, or an empty list when unavailable.</returns>
        public List<StoredItem> GetLegalItemsForPlayer(Player player)
        {
            if (player == null) return new List<StoredItem>();

            try
            {
                var snapshot = GetActiveSnapshotForPlayer(player);

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
        /// Retrieves items in the active snapshot that were marked as contraband.
        /// </summary>
        /// <param name="player">Player whose active snapshot should be queried.</param>
        /// <returns>A new list of contraband stored items, or an empty list when unavailable.</returns>
        public List<StoredItem> GetContrabandItemsForPlayer(Player player)
        {
            if (player == null) return new List<StoredItem>();

            try
            {
                var snapshot = GetActiveSnapshotForPlayer(player);

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
        /// Marks a player's active inventory snapshot inactive, normally on release.
        /// </summary>
        /// <param name="player">Player whose active snapshot should be deactivated.</param>
        public void ClearPlayerSnapshot(Player player)
        {
            if (player == null) return;

            try
            {
                var snapshot = GetActiveSnapshotForPlayer(player);

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
        /// Stores a player's exit position under the stable player key.
        /// </summary>
        /// <param name="player">Player whose exit position is being recorded.</param>
        /// <param name="position">World position to persist.</param>
        public void StorePlayerExitPosition(Player player, Vector3 position)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                string playerKey = GetPlayerUniqueId(player);
                if (string.IsNullOrEmpty(playerKey))
                {
                    return;
                }

                gameData.storedExitPositions[playerKey] = position;

                foreach (string legacyKey in GetLegacySnapshotLookupKeys(player, playerKey))
                {
                    gameData.storedExitPositions.Remove(legacyKey);
                }

                ModLogger.Info($"Stored exit position for {playerKey}: {position}");
                SaveData();
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error storing exit position: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets a player's stored exit position, migrating a legacy name-keyed entry when needed.
        /// </summary>
        /// <param name="player">Player whose position should be queried.</param>
        /// <returns>The stored position, or null when none is available.</returns>
        public Vector3? GetPlayerExitPosition(Player player)
        {
            if (player == null)
            {
                return null;
            }

            string playerKey = GetPlayerUniqueId(player);
            if (string.IsNullOrEmpty(playerKey))
            {
                return null;
            }

            var exitPosition = GetPlayerExitPositionByKey(playerKey);
            if (exitPosition.HasValue)
            {
                return exitPosition;
            }

            return TryMigrateLegacyExitPosition(player, playerKey);
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

        private List<ItemSlot> GetAllInventorySlots(PlayerInventory inventory)
        {
            var slots = new List<ItemSlot>();

            try
            {
                if (inventory == null)
                {
                    return slots;
                }

                // IL2CPP exposes GetAllInventorySlots as an
                // Il2CppSystem.Collections.Generic.List<ItemSlot>, which is
                // not assignable to managed System.Collections.IList. The old
                // reflection path therefore silently treated every inventory
                // (including equippable items such as the Golden Skateboard)
                // as empty. Enumerate the game's native collection directly.
                foreach (var slot in inventory.GetAllInventorySlots())
                {
                    if (slot != null)
                    {
                        slots.Add(slot);
                    }
                }

                ModLogger.Debug($"Captured {slots.Count} inventory slots through PlayerInventory.GetAllInventorySlots");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting inventory slots: {ex.Message}");
            }

            return slots;
        }

        private StoredItem ProcessInventorySlot(ItemSlot slot)
        {
            if (slot == null || slot.ItemInstance == null)
            {
                return null;
            }

            return ProcessItemInstance(slot.ItemInstance);
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

                return ProcessItemInstance(itemInstance);
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error processing inventory slot: {ex.Message}");
                return null;
            }
        }

        private StoredItem ProcessItemInstance(object itemInstance)
        {
            try
            {
                string itemId = GetItemId(itemInstance);
                string itemName = GetItemDisplayName(itemInstance);
                int stackCount = GetItemStackCount(itemInstance);
                bool isContraband = IsItemContraband(itemInstance);
                string itemType = GetItemType(itemInstance);

                ModLogger.Info($"ProcessInventorySlot: Extracted - Name: '{itemName}', ID: '{itemId}', Stack: {stackCount}, Type: {itemType}");

                // Cash stays with the player; all other property is represented by the
                // snapshot and physically secured by InventoryProcessor after arrest.
                if (itemType == "CashInstance" || itemName.Contains("Cash", StringComparison.OrdinalIgnoreCase))
                {
                    ModLogger.Info("Skipping cash - money is not confiscated during arrest");
                    return null;
                }

                if (IsWeaponItem(itemName, itemType))
                {
                    var weaponItem = new StoredItem(itemId, itemName, stackCount, isContraband, itemType)
                    {
                        specialHandling = "empty_weapon"
                    };
                    ModLogger.Info($"Captured weapon: {itemName} - will be returned empty");
                    return weaponItem;
                }

                if (IsAmmoItem(itemName, itemType))
                {
                    ModLogger.Info($"Confiscating ammo permanently: {itemName} (x{stackCount})");
                    return null;
                }

                return new StoredItem(itemId, itemName, stackCount, isContraband, itemType);
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error processing item instance: {ex.Message}");
                return null;
            }
        }

        private List<StoredItem> CaptureVehicleStorage(Player player)
        {
            var items = new List<StoredItem>();

            try
            {
                // Vehicle contents are included only for the 30-second window after
                // leaving a vehicle. The current reflection path requires ItemSlots to
                // implement managed IList; a native-only IL2CPP collection is skipped.
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
                // Product instances follow the dedicated product path below and are
                // currently always contraband. Other item types are classified only by
                // Definition.legalStatus; inability to read that shape defaults to legal.
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
                // AppliedPackaging is inspected for runtime compatibility, but the
                // current policy deliberately does not honor stealth level: every product
                // reaches the true return below. Exceptions also fail closed as contraband.
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
            // Core is the single source of truth for the stable key. This helper does
            // not fall back to the display name; callers explicitly decide when legacy
            // name lookup/migration is appropriate.
            return Behind_Bars.Core.ResolvePlayerKey(player);
        }

        private List<string> GetLegacySnapshotLookupKeys(Player player, string primaryKey = null)
        {
            // Older saves keyed snapshots and positions by player.name. Keep that name
            // as a read/migration candidate only when it differs from the stable key.
            var lookupKeys = new List<string>();

            if (player != null && !string.IsNullOrEmpty(player.name) && !lookupKeys.Contains(player.name))
            {
                if (string.IsNullOrEmpty(primaryKey) || !string.Equals(primaryKey, player.name, StringComparison.Ordinal))
                {
                    lookupKeys.Add(player.name);
                }
            }

            return lookupKeys;
        }

        private PlayerInventorySnapshot GetActiveSnapshotForPlayer(Player player)
        {
            if (player == null)
            {
                return null;
            }

            // Prefer the stable key; only if it has no active record do we search and
            // rewrite a legacy display-name keyed snapshot.
            string primaryKey = GetPlayerUniqueId(player);
            if (!string.IsNullOrEmpty(primaryKey))
            {
                var snapshot = GetActiveSnapshotByPlayerId(primaryKey);
                if (snapshot != null)
                {
                    return snapshot;
                }
            }

            return TryMigrateLegacySnapshot(player, primaryKey);
        }

        private PlayerInventorySnapshot GetActiveSnapshotByPlayerId(string playerId)
        {
            // There is at most one intended active snapshot per key, but Find preserves
            // the current first-match behavior if older saves contain duplicates.
            return gameData.playerSnapshots.Find(s => s.playerId == playerId && s.isActive);
        }

        private Vector3? GetPlayerExitPositionByKey(string playerKey)
        {
            // This is a pure key lookup. Legacy fallback and write-back are handled by
            // TryMigrateLegacyExitPosition so reads remain easy to reason about.
            try
            {
                if (!string.IsNullOrEmpty(playerKey) && gameData.storedExitPositions.ContainsKey(playerKey))
                {
                    return gameData.storedExitPositions[playerKey];
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting exit position: {ex.Message}");
            }

            return null;
        }

        private Vector3? TryMigrateLegacyExitPosition(Player player, string primaryKey)
        {
            if (player == null || string.IsNullOrEmpty(primaryKey))
            {
                return null;
            }

            // Migration is eager: copy the value to the stable key, remove the legacy
            // entry, and save immediately so the next load no longer depends on name.
            foreach (string legacyKey in GetLegacySnapshotLookupKeys(player, primaryKey))
            {
                var exitPosition = GetPlayerExitPositionByKey(legacyKey);
                if (!exitPosition.HasValue)
                {
                    continue;
                }

                gameData.storedExitPositions[primaryKey] = exitPosition.Value;
                gameData.storedExitPositions.Remove(legacyKey);
                SaveData();
                ModLogger.Info($"Migrated legacy exit position for {player.name} from {legacyKey} to {primaryKey}");
                return exitPosition;
            }

            return null;
        }

        private PlayerInventorySnapshot TryMigrateLegacySnapshot(Player player, string primaryKey)
        {
            if (player == null || string.IsNullOrEmpty(primaryKey))
            {
                return null;
            }

            // Update the existing object in place so any current caller keeps its
            // reference, then persist the stable identity and current display name.
            foreach (string legacyKey in GetLegacySnapshotLookupKeys(player, primaryKey))
            {
                var snapshot = GetActiveSnapshotByPlayerId(legacyKey);
                if (snapshot == null)
                {
                    continue;
                }

                snapshot.playerId = primaryKey;
                snapshot.playerName = player.name;
                SaveData();
                ModLogger.Info($"Migrated legacy inventory snapshot for {player.name} from {legacyKey} to {primaryKey}");
                return snapshot;
            }

            return null;
        }

        private object SerializeCrimeData(object crimeData)
        {
            try
            {
                if (crimeData != null)
                {
                    // Do not serialize the native crime graph directly: it contains Unity
                    // references/cycles. Persist only the current diagnostic projection
                    // (crime labels/counts, evasion, and pursuit level); failures return
                    // null and leave the rest of the inventory snapshot usable.
                    // Create a sanitized version without Unity object references
                    var sanitized = new
                    {
                        Crimes = ExtractCrimesData(crimeData),
                        EvadedArrest = GetPropertyValue<bool>(crimeData, "EvadedArrest"),
                        PursuitLevel = GetPropertyValue<int>(crimeData, "CurrentPursuitLevel")
                    };

                    // Serialize with settings to handle any remaining circular references
                    var settings = JsonHelper.GetSettingsWithReferenceLoopHandling(maxDepth: 5);

                    return JsonHelper.SerializeObject(sanitized, settings);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error serializing crime data: {ex.Message}");
            }

            return null;
        }

        private List<string> ExtractCrimesData(object crimeData)
        {
            var crimesList = new List<string>();
            try
            {
                // The native Crimes value is read by shape and flattened to display text;
                // this is intentionally not a round-trip representation of native crimes.
                var crimesProperty = crimeData.GetType().GetProperty("Crimes");
                if (crimesProperty != null)
                {
                    var crimes = crimesProperty.GetValue(crimeData);
                    if (crimes is System.Collections.IDictionary crimeDict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in crimeDict)
                        {
                            if (entry.Key != null)
                            {
                                // Get crime name/type as string
                                var crimeName = entry.Key.ToString();
                                var crimeCount = entry.Value?.ToString() ?? "1";
                                crimesList.Add($"{crimeName} (x{crimeCount})");
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error extracting crimes data: {ex.Message}");
            }
            return crimesList;
        }

        private T GetPropertyValue<T>(object obj, string propertyName)
        {
            try
            {
                // Reflection keeps this compatible with Mono/IL2CPP data shapes. Missing,
                // incompatible, or throwing properties intentionally resolve to T's default.
                var property = obj.GetType().GetProperty(propertyName);
                if (property != null)
                {
                    var value = property.GetValue(obj);
                    if (value is T typedValue)
                    {
                        return typedValue;
                    }
                    // Try to convert the value
                    return (T)System.Convert.ChangeType(value, typeof(T));
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error getting property {propertyName}: {ex.Message}");
            }
            return default(T);
        }

        #endregion

        #region Testing Methods

        /// <summary>
        /// Test method to verify crime data serialization works without errors
        /// Call this method after creating an inventory snapshot to verify the fix
        /// </summary>
        public void TestCrimeDataSerialization(Player player)
        {
            try
            {
                ModLogger.Info("=== Testing Crime Data Serialization ===");

                if (player == null)
                {
                    ModLogger.Error("Cannot test - player is null");
                    return;
                }

                var crimeData = player.CrimeData;
                if (crimeData == null)
                {
                    ModLogger.Info("Player has no CrimeData - test skipped");
                    return;
                }

                ModLogger.Info("Attempting to serialize crime data...");
                var serialized = SerializeCrimeData(crimeData);

                if (serialized != null)
                {
                    ModLogger.Info($"✓ SUCCESS: Crime data serialized successfully!");
                    ModLogger.Info($"Serialized data preview: {serialized.ToString().Substring(0, Math.Min(200, serialized.ToString().Length))}...");
                }
                else
                {
                    ModLogger.Warn("Serialization returned null - check if player has crime data");
                }

                ModLogger.Info("=== Test Complete ===");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"✗ FAILED: Test failed with error: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        #endregion

        #region Save/Load System

        private void SaveData()
        {
            try
            {
                // PlayerPrefs remains the outer persistence boundary. The payload is
                // flattened before serialization so Vector3/native object graphs do not
                // leak into JsonHelper; a failed save is logged without replacing memory.
                gameData.lastSaveTime = DateTime.Now;

#if MONO
                var settings = JsonHelper.GetSettingsWithConvertersAndResolver(
                    new List<JsonConverter> { new Vector3JsonConverter() },
                    new Vector3ContractResolver()
                );

                // Mono can serialize the runtime graph when converter settings exist; the
                // no-settings path uses the explicit DTO. IL2CPP uses the DTO path here as
                // well, keeping the on-disk shape consistent across runtimes.
                object dataToSerialize = gameData;
                if (settings == null)
                {
                    dataToSerialize = ConvertGameDataToSerializable(gameData);
                }
#else
                var settings = JsonHelper.GetDefaultSettings();
                object dataToSerialize = ConvertGameDataToSerializable(gameData);
#endif

                string jsonData = JsonHelper.SerializeObject(dataToSerialize, settings);
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
                // Read the legacy PlayerPrefs key and rebuild a fresh runtime graph on
                // missing, empty, incompatible, or failed data. Mono tries both the DTO
                // shape and the older direct graph shape; IL2CPP uses the DTO shape.
                if (PlayerPrefs.HasKey(SAVE_KEY))
                {
                    string jsonData = PlayerPrefs.GetString(SAVE_KEY);
                    if (!string.IsNullOrEmpty(jsonData))
                    {
#if MONO
                        var settings = JsonHelper.GetSettingsWithConvertersAndResolver(
                            new List<JsonConverter> { new Vector3JsonConverter() },
                            new Vector3ContractResolver()
                        );

                        // If settings is null (Mono mode), try the current explicit DTO
                        // shape first, then the older direct graph shape for saves written
                        // with Vector3JsonConverter.
                        if (settings == null)
                        {
                            try
                            {
                                var serializableData = JsonHelper.DeserializeObject<SerializablePersistentGameData>(jsonData, settings);
                                if (serializableData != null)
                                {
                                    gameData = ConvertSerializableToGameData(serializableData);
                                }
                                else
                                {
                                    gameData = new PersistentGameData();
                                }
                            }
                            catch
                            {
                                // If the DTO shape fails, try the older direct graph shape.
                                // This preserves saves written with Vector3JsonConverter.
                                try
                                {
                                    gameData = JsonHelper.DeserializeObject<PersistentGameData>(jsonData, settings);
                                    if (gameData == null)
                                    {
                                        gameData = new PersistentGameData();
                                    }
                                }
                                catch
                                {
                                    gameData = new PersistentGameData();
                                }
                            }
                        }
                        else
                        {
                            // A non-null settings object uses the runtime graph directly
                            // with the active converter configuration.
                            gameData = JsonHelper.DeserializeObject<PersistentGameData>(jsonData, settings);
                            if (gameData == null)
                            {
                                gameData = new PersistentGameData();
                            }
                        }
#else
                        var settings = JsonHelper.GetDefaultSettings();
                        var serializableData = JsonHelper.DeserializeObject<SerializablePersistentGameData>(jsonData, settings);
                        if (serializableData != null)
                        {
                            gameData = ConvertSerializableToGameData(serializableData);
                        }
                        else
                        {
                            gameData = new PersistentGameData();
                        }
#endif

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
                // Retention is wall-clock based and applies to all snapshots, active or
                // inactive. Position entries are not aged out by this cleanup pass.
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

        /// <summary>
        /// Flattens runtime persistent data into a Vector3-safe serialization DTO.
        /// </summary>
        /// <remarks>
        /// This conversion is used by the current IL2CPP save path and by the Mono
        /// fallback when converter settings are unavailable; the method is not Mono-only.
        /// </remarks>
        private SerializablePersistentGameData ConvertGameDataToSerializable(PersistentGameData data)
        {
            var serializableData = new SerializablePersistentGameData
            {
                playerSnapshots = new List<SerializablePlayerInventorySnapshot>(),
                storedExitPositions = new Dictionary<string, SerializableVector3>(),
                lastSaveTime = data.lastSaveTime,
                version = data.version
            };

            // Copy snapshot fields without changing the in-memory object graph. The
            // current DTO intentionally carries the same object-valued crime payload.
            foreach (var snapshot in data.playerSnapshots)
            {
                var serializableSnapshot = new SerializablePlayerInventorySnapshot
                {
                    playerId = snapshot.playerId,
                    playerName = snapshot.playerName,
                    items = snapshot.items,
                    lastPosition = new SerializableVector3(snapshot.lastPosition),
                    arrestTime = snapshot.arrestTime,
                    arrestId = snapshot.arrestId,
                    crimeData = snapshot.crimeData,
                    isActive = snapshot.isActive,
                    // Current compatibility DTO copies body layers only; accessories are
                    // absent from this legacy shape and therefore cannot be restored here.
                    originalClothing = snapshot.originalClothing
                };
                serializableData.playerSnapshots.Add(serializableSnapshot);
            }

            // Convert stored exit positions to Vector3 DTOs so dictionary values remain
            // primitive/serializer-safe.
            foreach (var kvp in data.storedExitPositions)
            {
                serializableData.storedExitPositions[kvp.Key] = new SerializableVector3(kvp.Value);
            }

            return serializableData;
        }

        /// <summary>
        /// Rehydrates runtime persistent data from the explicit serialization DTO.
        /// </summary>
        /// <param name="serializableData">DTO or already-runtime data returned by a loader.</param>
        /// <returns>A runtime data graph, or the input when it is already PersistentGameData.</returns>
        private PersistentGameData ConvertSerializableToGameData(object serializableData)
        {
            if (serializableData is SerializablePersistentGameData serializable)
            {
                var gameData = new PersistentGameData
                {
                    playerSnapshots = new List<PlayerInventorySnapshot>(),
                    storedExitPositions = new Dictionary<string, Vector3>(),
                    lastSaveTime = serializable.lastSaveTime,
                    version = serializable.version
                };

                // Recreate snapshots so Vector3 values become Unity structs again. Null
                // nested DTOs are not normalized here; the current loader relies on its
                // surrounding exception fallback for malformed payloads.
                foreach (var snapshot in serializable.playerSnapshots)
                {
                    var gameSnapshot = new PlayerInventorySnapshot(snapshot.playerId, snapshot.playerName, snapshot.arrestId)
                    {
                        items = snapshot.items,
                        lastPosition = snapshot.lastPosition.ToVector3(),
                        arrestTime = snapshot.arrestTime,
                        crimeData = snapshot.crimeData,
                        isActive = snapshot.isActive,
                        // The legacy DTO has no accessory collection to hydrate.
                        originalClothing = snapshot.originalClothing
                    };
                    gameData.playerSnapshots.Add(gameSnapshot);
                }

                // Recreate the dictionary keyed by the serialized player/exit key.
                foreach (var kvp in serializable.storedExitPositions)
                {
                    gameData.storedExitPositions[kvp.Key] = kvp.Value.ToVector3();
                }

                return gameData;
            }

            // If it's already PersistentGameData, return as-is
            return serializableData as PersistentGameData;
        }

        /// <summary>
        /// Serializable version of PersistentGameData for Mono mode serialization
        /// </summary>
        [System.Serializable]
        private class SerializablePersistentGameData
        {
            // Mirror of PersistentGameData with Vector3 values replaced by explicit DTOs.
            /// <summary>Serialized snapshot DTO collection.</summary>
            public List<SerializablePlayerInventorySnapshot> playerSnapshots = new List<SerializablePlayerInventorySnapshot>();
            /// <summary>Serialized exit-position DTO map.</summary>
            public Dictionary<string, SerializableVector3> storedExitPositions = new Dictionary<string, SerializableVector3>();
            /// <summary>Round-trip save timestamp.</summary>
            public DateTime lastSaveTime;
            /// <summary>Save schema version.</summary>
            public int version = 1;
        }

        /// <summary>
        /// Serializable version of PlayerInventorySnapshot for Mono mode serialization
        /// </summary>
        [System.Serializable]
        private class SerializablePlayerInventorySnapshot
        {
            // Mirror of PlayerInventorySnapshot used only at the serializer boundary.
            // The current legacy DTO has no originalAccessories field, so accessories do
            // not round-trip through ConvertGameDataToSerializable/ConvertSerializableToGameData.
            /// <summary>Stable player identifier.</summary>
            public string playerId;
            /// <summary>Player display name.</summary>
            public string playerName;
            /// <summary>Stored item records.</summary>
            public List<StoredItem> items = new List<StoredItem>();
            /// <summary>Flattened last known position.</summary>
            public SerializableVector3 lastPosition;
            /// <summary>Arrest timestamp.</summary>
            public DateTime arrestTime;
            /// <summary>Arrest/custody identifier.</summary>
            public string arrestId;
            /// <summary>Opaque crime payload.</summary>
            public object crimeData;
            /// <summary>Whether this snapshot remains active.</summary>
            public bool isActive;
            /// <summary>Civilian body layers captured at arrest.</summary>
            public List<ClothingLayer> originalClothing = new List<ClothingLayer>();
        }

        /// <summary>
        /// Saves data at most once per configured Unity <c>Time.time</c> interval
        /// (normally seconds; subject to the engine's time scale).
        /// </summary>
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
        /// Gets a compact count summary of active snapshots and stored exit positions.
        /// </summary>
        /// <returns>Human-readable counts from the current in-memory data.</returns>
        public string GetDataStats()
        {
            var activeSnapshots = gameData.playerSnapshots.FindAll(s => s.isActive);
            return $"Active snapshots: {activeSnapshots.Count}, Stored positions: {gameData.storedExitPositions.Count}";
        }

        /// <summary>
        /// Forces the current in-memory data to the PlayerPrefs save boundary.
        /// </summary>
        public void ForceSave()
        {
            SaveData();
        }

        /// <summary>
        /// Replaces all in-memory persistent data with an empty schema and saves it.
        /// Primarily intended for test/reset flows.
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

        private List<ClothingAccessory> CapturePlayerAccessories(Player player)
        {
            var accessories = new List<ClothingAccessory>();

            try
            {
#if !MONO
                var playerAvatar = player.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var playerAvatar = player.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
#endif
                var settings = playerAvatar?.CurrentSettings;
                if (settings?.AccessorySettings == null)
                {
                    ModLogger.Warn("Player avatar accessory settings are null");
                    return accessories;
                }

                foreach (var accessory in settings.AccessorySettings)
                {
                    if (accessory != null && !string.IsNullOrWhiteSpace(accessory.path))
                    {
                        accessories.Add(new ClothingAccessory(accessory.path, accessory.color));
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error capturing player accessories: {ex.Message}");
            }

            return accessories;
        }

        /// <summary>
        /// Restores the active snapshot's civilian body layers and accessories to a player.
        /// The full avatar settings object is committed through LoadAvatarSettings.
        /// </summary>
        /// <param name="player">Player whose active custody appearance should be restored.</param>
        public void RestorePlayerClothing(Player player)
        {
            try
            {
                // Find the player's active snapshot using stable key with legacy-name fallback
                var snapshot = GetActiveSnapshotForPlayer(player);
                if (snapshot == null ||
                    ((snapshot.originalClothing == null || snapshot.originalClothing.Count == 0) &&
                     (snapshot.originalAccessories == null || snapshot.originalAccessories.Count == 0)))
                {
                    ModLogger.Warn("No clothing data saved for player");
                    return;
                }

                ModLogger.Info($"Restoring {snapshot.originalClothing?.Count ?? 0} clothing layers and {snapshot.originalAccessories?.Count ?? 0} accessories for {player.name}");

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

                // Match the booking attire handoff: replace the complete avatar surface,
                // including accessories, then commit once through LoadAvatarSettings.
                settings.BodyLayerSettings.Clear();
                settings.AccessorySettings.Clear();

                // Restore original clothing layers
                foreach (var clothingLayer in snapshot.originalClothing ?? new List<ClothingLayer>())
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

                foreach (var accessory in snapshot.originalAccessories ?? new List<ClothingAccessory>())
                {
                    if (accessory == null || string.IsNullOrWhiteSpace(accessory.path))
                    {
                        continue;
                    }

                    settings.AccessorySettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#endif
                    {
                        path = accessory.path,
                        color = accessory.GetColor()
                    });
                }

                // The booking path replaces attire through LoadAvatarSettings.
                // Use the same full-avatar application path on release: the
                // lighter body-layer refresh can log success on IL2CPP while
                // leaving the authored prison outfit rendered in place.
                playerAvatar.LoadAvatarSettings(settings);
                player.SetVisibleToLocalPlayer(false);
                ModLogger.Info($"✓ Restored original clothing for {player.name} - changed back from prison attire");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error restoring player clothing: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns a detached view of the active custody record's civilian appearance.
        /// Accessories are represented as ClothingLayer entries for the legacy caller shape.
        /// </summary>
        /// <param name="player">Player whose active snapshot should be queried.</param>
        /// <returns>Copied body layers followed by accessory paths converted to layers.</returns>
        public List<ClothingLayer> GetOriginalClothingForPlayer(Player player)
        {
            var snapshot = GetActiveSnapshotForPlayer(player);
            var appearance = snapshot?.originalClothing != null
                ? new List<ClothingLayer>(snapshot.originalClothing)
                : new List<ClothingLayer>();

            if (snapshot?.originalAccessories != null)
            {
                foreach (var accessory in snapshot.originalAccessories)
                {
                    if (accessory != null && !string.IsNullOrWhiteSpace(accessory.path))
                    {
                        appearance.Add(new ClothingLayer(accessory.path, accessory.GetColor()));
                    }
                }
            }

            return appearance;
        }

        #endregion
    }
}
