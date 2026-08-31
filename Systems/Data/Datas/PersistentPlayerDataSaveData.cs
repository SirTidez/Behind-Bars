using System;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;

#if !MONO
using Il2CppScheduleOne.Persistence.Datas;
#else
using ScheduleOne.Persistence.Datas;
#endif

namespace Behind_Bars.Systems.Data.Datas
{
    /// <summary>
    /// Serializable representation of Vector3 for Unity JsonUtility
    /// </summary>
    [Serializable]
    public class Vector3SaveData
    {
        /// <summary>Serialized X coordinate.</summary>
        public float x;
        /// <summary>Serialized Y coordinate.</summary>
        public float y;
        /// <summary>Serialized Z coordinate.</summary>
        public float z;

        /// <summary>Reconstructs the Unity position represented by this DTO.</summary>
        public Vector3 ToVector3() => new Vector3(x, y, z);

        /// <summary>Flattens a Unity position into fields supported by JsonUtility.</summary>
        /// <param name="v">Position to serialize.</param>
        public static Vector3SaveData FromVector3(Vector3 v) => new Vector3SaveData { x = v.x, y = v.y, z = v.z };
    }

    /// <summary>
    /// Serializable representation of a stored exit position
    /// </summary>
    [Serializable]
    public class StoredExitPositionSaveData
    {
        /// <summary>Stable dictionary key for the stored exit.</summary>
        public string key;
        /// <summary>Serialized world position associated with <see cref="key"/>.</summary>
        public Vector3SaveData position;

        /// <summary>Creates a list-friendly DTO for one stored-exit dictionary entry.</summary>
        /// <param name="key">Exit key; null is persisted as an empty string.</param>
        /// <param name="position">Exit world position.</param>
        public static StoredExitPositionSaveData FromKeyValue(string key, Vector3 position)
        {
            return new StoredExitPositionSaveData
            {
                key = key ?? "",
                position = Vector3SaveData.FromVector3(position)
            };
        }
    }

    /// <summary>
    /// Serializable representation of a clothing layer
    /// </summary>
    [Serializable]
    public class ClothingLayerSaveData
    {
        /// <summary>Addressable/resource path for the clothing layer.</summary>
        public string layerPath;
        /// <summary>RGBA channels in order; shorter arrays hydrate with opaque-white defaults.</summary>
        public float[] colorRGBA = new float[4]; // r, g, b, a

        /// <summary>Flattens a runtime clothing layer into primitive save fields.</summary>
        /// <param name="layer">Layer to serialize, or null to omit.</param>
        public static ClothingLayerSaveData FromClothingLayer(PersistentPlayerData.ClothingLayer layer)
        {
            if (layer == null)
                return null;

            var color = layer.GetColor();
            return new ClothingLayerSaveData
            {
                layerPath = layer.layerPath ?? "",
                colorRGBA = new float[] { color.r, color.g, color.b, color.a }
            };
        }

        /// <summary>
        /// Reconstructs a clothing layer, applying white/opaque defaults for truncated
        /// or legacy color arrays.
        /// </summary>
        public PersistentPlayerData.ClothingLayer ToClothingLayer()
        {
            Color color = new Color(
                colorRGBA.Length > 0 ? colorRGBA[0] : 1f,
                colorRGBA.Length > 1 ? colorRGBA[1] : 1f,
                colorRGBA.Length > 2 ? colorRGBA[2] : 1f,
                colorRGBA.Length > 3 ? colorRGBA[3] : 1f
            );
            return new PersistentPlayerData.ClothingLayer(layerPath, color);
        }
    }

    /// <summary>Serializable representation of a player-avatar accessory.</summary>
    [Serializable]
    public class ClothingAccessorySaveData
    {
        /// <summary>Addressable/resource path for the avatar accessory.</summary>
        public string path;
        /// <summary>RGBA channels in order; shorter or null arrays hydrate with opaque-white defaults.</summary>
        public float[] colorRGBA = new float[4];

        /// <summary>Flattens a runtime accessory into primitive save fields.</summary>
        /// <param name="accessory">Accessory to serialize, or null to omit.</param>
        public static ClothingAccessorySaveData FromClothingAccessory(PersistentPlayerData.ClothingAccessory accessory)
        {
            if (accessory == null)
                return null;

            var color = accessory.GetColor();
            return new ClothingAccessorySaveData
            {
                path = accessory.path ?? "",
                colorRGBA = new[] { color.r, color.g, color.b, color.a }
            };
        }

        /// <summary>
        /// Reconstructs an accessory, applying white/opaque defaults for truncated or
        /// legacy color arrays.
        /// </summary>
        public PersistentPlayerData.ClothingAccessory ToClothingAccessory()
        {
            var color = new Color(
                colorRGBA != null && colorRGBA.Length > 0 ? colorRGBA[0] : 1f,
                colorRGBA != null && colorRGBA.Length > 1 ? colorRGBA[1] : 1f,
                colorRGBA != null && colorRGBA.Length > 2 ? colorRGBA[2] : 1f,
                colorRGBA != null && colorRGBA.Length > 3 ? colorRGBA[3] : 1f);
            return new PersistentPlayerData.ClothingAccessory(path, color);
        }
    }

    /// <summary>
    /// Serializable representation of a stored item
    /// </summary>
    [Serializable]
    public class StoredItemSaveData
    {
        /// <summary>Persisted item identifier, if the source item exposed one.</summary>
        public string itemId;
        /// <summary>Display name captured at confiscation time.</summary>
        public string itemName;
        /// <summary>Captured stack quantity.</summary>
        public int stackCount;
        /// <summary>Whether the item was classified as contraband at capture time.</summary>
        public bool isContraband;
        /// <summary>Runtime item-type name used for display and restoration diagnostics.</summary>
        public string itemType;
        /// <summary>ISO-8601 serialization of the confiscation timestamp.</summary>
        public string confiscationTime;         // DateTime as ISO 8601 string
        /// <summary>Additional handling note retained with the item.</summary>
        public string specialHandling;
        /// <summary>Cash value associated with this stored item.</summary>
        public float cashBalance;

        /// <summary>Flattens a stored item into save-safe primitive fields.</summary>
        /// <param name="item">Stored item to serialize, or null to omit.</param>
        public static StoredItemSaveData FromStoredItem(PersistentPlayerData.StoredItem item)
        {
            if (item == null)
                return null;

            return new StoredItemSaveData
            {
                itemId = item.itemId ?? "",
                itemName = item.itemName ?? "",
                stackCount = item.stackCount,
                isContraband = item.isContraband,
                itemType = item.itemType ?? "",
                confiscationTime = item.confiscationTime.ToString("O"),
                specialHandling = item.specialHandling ?? "",
                cashBalance = item.cashBalance
            };
        }

        /// <summary>
        /// Reconstructs a stored item. Malformed or missing timestamps fall back to the
        /// current local time so a legacy item remains loadable.
        /// </summary>
        public PersistentPlayerData.StoredItem ToStoredItem()
        {
            DateTime parsedTime;
            if (!DateTime.TryParse(confiscationTime, out parsedTime))
            {
                parsedTime = DateTime.Now;
            }

            return new PersistentPlayerData.StoredItem(itemId, itemName, stackCount, isContraband, itemType)
            {
                confiscationTime = parsedTime,
                specialHandling = specialHandling ?? "",
                cashBalance = cashBalance
            };
        }
    }

    /// <summary>
    /// Serializable representation of a player inventory snapshot
    /// </summary>
    [Serializable]
    public class PlayerInventorySnapshotSaveData
    {
        /// <summary>Player identifier captured when the arrest snapshot was created.</summary>
        public string playerId;
        /// <summary>Player display name captured with the snapshot.</summary>
        public string playerName;
        /// <summary>
        /// Confiscated item DTOs. Null source entries are omitted while flattening; a null
        /// element encountered during hydration can abort the remaining snapshot conversion.
        /// </summary>
        public List<StoredItemSaveData> items = new List<StoredItemSaveData>();
        /// <summary>Last known player position at snapshot time.</summary>
        public Vector3SaveData lastPosition;
        /// <summary>ISO-8601 serialization of the arrest timestamp.</summary>
        public string arrestTime;              // DateTime as ISO 8601 string
        /// <summary>Stable arrest/incident identifier.</summary>
        public string arrestId;
        /// <summary>Crime payload preserved as a string for compatibility with current saves.</summary>
        public string crimeData;                // Serialized crime data (as string)
        /// <summary>Whether this snapshot is still active in the persistent state.</summary>
        public bool isActive;
        /// <summary>Original clothing layers captured before custody.</summary>
        public List<ClothingLayerSaveData> originalClothing = new List<ClothingLayerSaveData>();
        /// <summary>Original accessories captured before custody.</summary>
        public List<ClothingAccessorySaveData> originalAccessories = new List<ClothingAccessorySaveData>();

        /// <summary>
        /// Converts a runtime inventory snapshot to the JSON-friendly DTO shape. Complex
        /// collections are copied entry-by-entry and null source entries are omitted.
        /// </summary>
        /// <param name="snapshot">Snapshot to serialize, or null to omit.</param>
        public static PlayerInventorySnapshotSaveData FromSnapshot(PersistentPlayerData.PlayerInventorySnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            var saveData = new PlayerInventorySnapshotSaveData
            {
                playerId = snapshot.playerId ?? "",
                playerName = snapshot.playerName ?? "",
                arrestTime = snapshot.arrestTime.ToString("O"),
                arrestId = snapshot.arrestId ?? "",
                isActive = snapshot.isActive
            };

            // Convert items
            if (snapshot.items != null)
            {
                foreach (var item in snapshot.items)
                {
                    var itemSaveData = StoredItemSaveData.FromStoredItem(item);
                    if (itemSaveData != null)
                    {
                        saveData.items.Add(itemSaveData);
                    }
                }
            }

            // Convert position
            saveData.lastPosition = Vector3SaveData.FromVector3(snapshot.lastPosition);

            // Convert crime data (already a string or object - serialize if needed)
            if (snapshot.crimeData != null)
            {
                // If it's already a string, use it; otherwise serialize
                saveData.crimeData = snapshot.crimeData is string str ? str : snapshot.crimeData.ToString();
            }

            // Convert clothing
            if (snapshot.originalClothing != null)
            {
                foreach (var clothing in snapshot.originalClothing)
                {
                    var clothingSaveData = ClothingLayerSaveData.FromClothingLayer(clothing);
                    if (clothingSaveData != null)
                    {
                        saveData.originalClothing.Add(clothingSaveData);
                    }
                }
            }

            if (snapshot.originalAccessories != null)
            {
                foreach (var accessory in snapshot.originalAccessories)
                {
                    var accessorySaveData = ClothingAccessorySaveData.FromClothingAccessory(accessory);
                    if (accessorySaveData != null)
                    {
                        saveData.originalAccessories.Add(accessorySaveData);
                    }
                }
            }

            return saveData;
        }

        /// <summary>
        /// Reconstructs a runtime snapshot. Invalid arrest timestamps use local now,
        /// absent positions use <see cref="Vector3.zero"/>, and crime data remains text.
        /// </summary>
        /// <remarks>
        /// The current payload loader catches an exception around the whole snapshot list,
        /// so a malformed nested item or clothing DTO can stop later snapshots in that payload.
        /// </remarks>
        public PersistentPlayerData.PlayerInventorySnapshot ToSnapshot()
        {
            DateTime parsedTime;
            if (!DateTime.TryParse(arrestTime, out parsedTime))
            {
                parsedTime = DateTime.Now;
            }

            var snapshot = new PersistentPlayerData.PlayerInventorySnapshot(playerId, playerName, arrestId)
            {
                isActive = isActive
            };

            // Convert items
            if (items != null)
            {
                foreach (var itemSaveData in items)
                {
                    var item = itemSaveData.ToStoredItem();
                    if (item != null)
                    {
                        snapshot.items.Add(item);
                    }
                }
            }

            // Convert position
            snapshot.lastPosition = lastPosition?.ToVector3() ?? Vector3.zero;
            snapshot.arrestTime = parsedTime;

            // Convert crime data (keep as string for now)
            snapshot.crimeData = crimeData;

            // Convert clothing
            if (originalClothing != null)
            {
                foreach (var clothingSaveData in originalClothing)
                {
                    var clothing = clothingSaveData.ToClothingLayer();
                    if (clothing != null)
                    {
                        snapshot.originalClothing.Add(clothing);
                    }
                }
            }

            if (originalAccessories != null)
            {
                foreach (var accessorySaveData in originalAccessories)
                {
                    var accessory = accessorySaveData?.ToClothingAccessory();
                    if (accessory != null)
                    {
                        snapshot.originalAccessories.Add(accessory);
                    }
                }
            }

            return snapshot;
        }
    }

    /// <summary>
    /// Serializable representation of PersistentGameData using GenericSaveData
    /// Uses key-value storage for simple fields and JSON strings for complex nested data
    /// </summary>
    [Serializable]
    public class PersistentPlayerDataSaveData : GenericSaveData
    {
        // Simple values use GenericSaveData's key/value store; nested collections use
        // JSON strings because the native save surface cannot represent dictionaries
        // and object graphs directly. These keys are part of the persisted schema.
        private const string KEY_LAST_SAVE_TIME = "lastSaveTime";
        private const string KEY_VERSION = "version";
        private const string KEY_PLAYER_SNAPSHOTS_JSON = "playerSnapshotsJson";
        private const string KEY_STORED_EXIT_POSITIONS_JSON = "storedExitPositionsJson";

        /// <summary>
        /// Creates a new save DTO with the supplied GenericSaveData identifier.
        /// </summary>
        /// <param name="guid">Identifier assigned to the native save-data wrapper.</param>
        public PersistentPlayerDataSaveData(string guid) : base(guid)
        {
        }

        /// <summary>
        /// Creates a save DTO with a new identifier for serializers that require a default constructor.
        /// </summary>
        public PersistentPlayerDataSaveData() : base(Guid.NewGuid().ToString())
        {
        }

        /// <summary>
        /// Flattens runtime persistent game data into the current GenericSaveData schema.
        /// </summary>
        /// <param name="gameData">Runtime data to convert, or null to omit.</param>
        /// <returns>A save DTO, or null when <paramref name="gameData"/> is null.</returns>
        public static PersistentPlayerDataSaveData FromGameData(PersistentPlayerData.PersistentGameData gameData)
        {
            if (gameData == null)
                return null;

            string guid = $"persistentplayerdata_{Guid.NewGuid()}";
            var saveData = new PersistentPlayerDataSaveData(guid);

            // Store simple fields as key-value pairs. DateTime uses round-trip text;
            // version is retained for migration decisions made by the owning system.
            saveData.Add(KEY_LAST_SAVE_TIME, gameData.lastSaveTime.ToString("O"));
            saveData.Add(KEY_VERSION, gameData.version);

            // Convert snapshots to JSON string (complex nested data). Empty collections
            // are omitted, and ToGameData supplies initialized empty lists on read.
            if (gameData.playerSnapshots != null && gameData.playerSnapshots.Count > 0)
            {
                var snapshotsList = new List<PlayerInventorySnapshotSaveData>();
                foreach (var snapshot in gameData.playerSnapshots)
                {
                    var snapshotSaveData = PlayerInventorySnapshotSaveData.FromSnapshot(snapshot);
                    if (snapshotSaveData != null)
                    {
                        snapshotsList.Add(snapshotSaveData);
                    }
                }
                string snapshotsJson = JsonHelper.SerializeObject(snapshotsList);
                saveData.Add(KEY_PLAYER_SNAPSHOTS_JSON, snapshotsJson);
            }

            // Convert exit positions to JSON string (Dictionary to List), because JSON
            // object keys are less portable across the native serializer boundary.
            if (gameData.storedExitPositions != null && gameData.storedExitPositions.Count > 0)
            {
                var positionsList = new List<StoredExitPositionSaveData>();
                foreach (var kvp in gameData.storedExitPositions)
                {
                    var positionSaveData = StoredExitPositionSaveData.FromKeyValue(kvp.Key, kvp.Value);
                    positionsList.Add(positionSaveData);
                }
                string positionsJson = JsonHelper.SerializeObject(positionsList);
                saveData.Add(KEY_STORED_EXIT_POSITIONS_JSON, positionsJson);
            }

            return saveData;
        }

        /// <summary>
        /// Rehydrates runtime persistent data from key/value fields and nested JSON.
        /// </summary>
        /// <returns>Initialized game data; malformed optional JSON is skipped with a warning.</returns>
        public PersistentPlayerData.PersistentGameData ToGameData()
        {
            var gameData = new PersistentPlayerData.PersistentGameData();

            // Parse DateTime from key-value storage. A missing or malformed timestamp
            // leaves the runtime default rather than invalidating the whole save.
            string lastSaveTimeStr = GetString(KEY_LAST_SAVE_TIME, "");
            if (!string.IsNullOrEmpty(lastSaveTimeStr))
            {
                if (DateTime.TryParse(lastSaveTimeStr, out DateTime parsedDate))
                {
                    gameData.lastSaveTime = parsedDate;
                }
            }

            gameData.version = GetInt(KEY_VERSION, 1);

            // Deserialize snapshots from JSON string. Payload-level failure is contained
            // so exit positions and the rest of the save can still be restored; a malformed
            // entry currently aborts the remaining snapshot entries in this payload.
            string snapshotsJson = GetString(KEY_PLAYER_SNAPSHOTS_JSON, "");
            if (!string.IsNullOrEmpty(snapshotsJson))
            {
                try
                {
                    var snapshotsList = JsonHelper.DeserializeObject<List<PlayerInventorySnapshotSaveData>>(snapshotsJson);
                    if (snapshotsList != null)
                    {
                        foreach (var snapshotSaveData in snapshotsList)
                        {
                            var snapshot = snapshotSaveData.ToSnapshot();
                            if (snapshot != null)
                            {
                                gameData.playerSnapshots.Add(snapshot);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"Error deserializing player snapshots: {ex.Message}");
                }
            }

            // Deserialize exit positions from JSON string (List to Dictionary). Entries
            // without keys are ignored; duplicate keys use normal dictionary overwrite.
            // The current loop does not null-check position, so a null position aborts
            // the remaining position payload through the surrounding catch.
            string positionsJson = GetString(KEY_STORED_EXIT_POSITIONS_JSON, "");
            if (!string.IsNullOrEmpty(positionsJson))
            {
                try
                {
                    var positionsList = JsonHelper.DeserializeObject<List<StoredExitPositionSaveData>>(positionsJson);
                    if (positionsList != null)
                    {
                        foreach (var positionSaveData in positionsList)
                        {
                            if (positionSaveData != null && !string.IsNullOrEmpty(positionSaveData.key))
                            {
                                gameData.storedExitPositions[positionSaveData.key] = positionSaveData.position.ToVector3();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"Error deserializing exit positions: {ex.Message}");
                }
            }

            return gameData;
        }
    }
}

