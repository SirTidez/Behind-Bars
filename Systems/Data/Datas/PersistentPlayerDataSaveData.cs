using System;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;

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
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3() => new Vector3(x, y, z);
        public static Vector3SaveData FromVector3(Vector3 v) => new Vector3SaveData { x = v.x, y = v.y, z = v.z };
    }

    /// <summary>
    /// Serializable representation of a stored exit position
    /// </summary>
    [Serializable]
    public class StoredExitPositionSaveData
    {
        public string key;
        public Vector3SaveData position;

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
        public string layerPath;
        public float[] colorRGBA = new float[4]; // r, g, b, a

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

    /// <summary>
    /// Serializable representation of a stored item
    /// </summary>
    [Serializable]
    public class StoredItemSaveData
    {
        public string itemId;
        public string itemName;
        public int stackCount;
        public bool isContraband;
        public string itemType;
        public string confiscationTime;         // DateTime as ISO 8601 string
        public string specialHandling;
        public float cashBalance;

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
        public string playerId;
        public string playerName;
        public List<StoredItemSaveData> items = new List<StoredItemSaveData>();
        public Vector3SaveData lastPosition;
        public string arrestTime;              // DateTime as ISO 8601 string
        public string arrestId;
        public string crimeData;                // Serialized crime data (as string)
        public bool isActive;
        public List<ClothingLayerSaveData> originalClothing = new List<ClothingLayerSaveData>();

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

            return saveData;
        }

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
        // Key constants for consistent key naming
        private const string KEY_LAST_SAVE_TIME = "lastSaveTime";
        private const string KEY_VERSION = "version";
        private const string KEY_PLAYER_SNAPSHOTS_JSON = "playerSnapshotsJson";
        private const string KEY_STORED_EXIT_POSITIONS_JSON = "storedExitPositionsJson";

        /// <summary>
        /// Creates a new PersistentPlayerDataSaveData with a GUID
        /// </summary>
        public PersistentPlayerDataSaveData(string guid) : base(guid)
        {
        }

        /// <summary>
        /// Default constructor (required for serialization)
        /// </summary>
        public PersistentPlayerDataSaveData() : base(Guid.NewGuid().ToString())
        {
        }

        /// <summary>
        /// Creates a PersistentPlayerDataSaveData from PersistentGameData
        /// </summary>
        public static PersistentPlayerDataSaveData FromGameData(PersistentPlayerData.PersistentGameData gameData)
        {
            if (gameData == null)
                return null;

            string guid = $"persistentplayerdata_{Guid.NewGuid()}";
            var saveData = new PersistentPlayerDataSaveData(guid);

            // Store simple fields as key-value pairs
            saveData.Add(KEY_LAST_SAVE_TIME, gameData.lastSaveTime.ToString("O"));
            saveData.Add(KEY_VERSION, gameData.version);

            // Convert snapshots to JSON string (complex nested data)
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
                string snapshotsJson = JsonUtility.ToJson(snapshotsList);
                saveData.Add(KEY_PLAYER_SNAPSHOTS_JSON, snapshotsJson);
            }

            // Convert exit positions to JSON string (Dictionary to List)
            if (gameData.storedExitPositions != null && gameData.storedExitPositions.Count > 0)
            {
                var positionsList = new List<StoredExitPositionSaveData>();
                foreach (var kvp in gameData.storedExitPositions)
                {
                    var positionSaveData = StoredExitPositionSaveData.FromKeyValue(kvp.Key, kvp.Value);
                    positionsList.Add(positionSaveData);
                }
                string positionsJson = JsonUtility.ToJson(positionsList);
                saveData.Add(KEY_STORED_EXIT_POSITIONS_JSON, positionsJson);
            }

            return saveData;
        }

        /// <summary>
        /// Converts this SaveData back to PersistentGameData
        /// </summary>
        public PersistentPlayerData.PersistentGameData ToGameData()
        {
            var gameData = new PersistentPlayerData.PersistentGameData();

            // Parse DateTime from key-value storage
            string lastSaveTimeStr = GetString(KEY_LAST_SAVE_TIME, "");
            if (!string.IsNullOrEmpty(lastSaveTimeStr))
            {
                if (DateTime.TryParse(lastSaveTimeStr, out DateTime parsedDate))
                {
                    gameData.lastSaveTime = parsedDate;
                }
            }

            gameData.version = GetInt(KEY_VERSION, 1);

            // Deserialize snapshots from JSON string
            string snapshotsJson = GetString(KEY_PLAYER_SNAPSHOTS_JSON, "");
            if (!string.IsNullOrEmpty(snapshotsJson))
            {
                try
                {
                    var snapshotsList = JsonUtility.FromJson<List<PlayerInventorySnapshotSaveData>>(snapshotsJson);
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

            // Deserialize exit positions from JSON string (List to Dictionary)
            string positionsJson = GetString(KEY_STORED_EXIT_POSITIONS_JSON, "");
            if (!string.IsNullOrEmpty(positionsJson))
            {
                try
                {
                    var positionsList = JsonUtility.FromJson<List<StoredExitPositionSaveData>>(positionsJson);
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

