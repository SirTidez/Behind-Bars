using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Manages cell assignments and occupancy tracking for the prison system
    /// IL2CPP-safe implementation
    /// </summary>
    public class CellAssignmentManager : MonoBehaviour
    {
#if !MONO
        public CellAssignmentManager(System.IntPtr ptr) : base(ptr) { }
#endif

        public static CellAssignmentManager Instance { get; private set; }

        // Cell tracking
        private Dictionary<int, CellOccupancy> cellOccupancy = new Dictionary<int, CellOccupancy>();
        private Dictionary<string, int> playerCellAssignments = new Dictionary<string, int>(); // playerId -> cellNumber
        private Dictionary<string, int> npcCellAssignments = new Dictionary<string, int>(); // npcId -> cellNumber

        // Configuration
        public int totalCells = 12; // Cells 0-11
        public int maxOccupantsPerCell = 2; // Most cells have 2 bunks

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                ModLogger.Info("CellAssignmentManager initialized");
            }
            else
            {
                Destroy(this);
            }
        }

        private void Start()
        {
            InitializeCellTracking();
        }

        /// <summary>
        /// Initialize cell occupancy tracking
        /// </summary>
        private void InitializeCellTracking()
        {
            cellOccupancy.Clear();
            playerCellAssignments.Clear();
            npcCellAssignments.Clear();

            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("JailController not found - using default cell count");
            }
            else
            {
                totalCells = jailController.cells.Count;
                ModLogger.Info($"Found {totalCells} cells in jail structure");
            }

            // Initialize all cells as empty
            for (int i = 0; i < totalCells; i++)
            {
                cellOccupancy[i] = new CellOccupancy
                {
                    cellNumber = i,
                    occupants = new List<string>(),
                    maxOccupants = maxOccupantsPerCell,
                    isAvailable = true
                };
            }

            ModLogger.Info($"✓ Initialized cell tracking for {totalCells} cells");
        }

        /// <summary>
        /// Assign a cell to a player
        /// </summary>
        public int AssignPlayerToCell(Player player)
        {
            if (player == null)
            {
                ModLogger.Error("Cannot assign null player to cell");
                return -1;
            }

            string playerId = GetPlayerId(player);

            // Check if player already has a cell assigned
            if (playerCellAssignments.ContainsKey(playerId))
            {
                int existingCell = playerCellAssignments[playerId];
                ModLogger.Info($"Player {player.name} already assigned to cell {existingCell}");
                return existingCell;
            }

            // Find available cell
            int cellNumber = FindAvailableCell();
            if (cellNumber == -1)
            {
                ModLogger.Error($"No available cells for player {player.name}");
                return -1;
            }

            // Assign player to cell
            if (AssignOccupantToCell(cellNumber, playerId, $"Player: {player.name}"))
            {
                playerCellAssignments[playerId] = cellNumber;
                ModLogger.Info($"✓ Assigned player {player.name} to cell {cellNumber}");
                return cellNumber;
            }

            return -1;
        }

        /// <summary>
        /// Assign a cell to an NPC
        /// </summary>
        public int AssignNPCToCell(string npcId, string npcName)
        {
            if (string.IsNullOrEmpty(npcId))
            {
                ModLogger.Error("Cannot assign NPC with empty ID to cell");
                return -1;
            }

            // Check if NPC already has a cell assigned
            if (npcCellAssignments.ContainsKey(npcId))
            {
                int existingCell = npcCellAssignments[npcId];
                ModLogger.Info($"NPC {npcName} already assigned to cell {existingCell}");
                return existingCell;
            }

            // Find available cell
            int cellNumber = FindAvailableCell();
            if (cellNumber == -1)
            {
                ModLogger.Error($"No available cells for NPC {npcName}");
                return -1;
            }

            // Assign NPC to cell
            if (AssignOccupantToCell(cellNumber, npcId, $"NPC: {npcName}"))
            {
                npcCellAssignments[npcId] = cellNumber;
                ModLogger.Info($"✓ Assigned NPC {npcName} to cell {cellNumber}");
                return cellNumber;
            }

            return -1;
        }

        /// <summary>
        /// Release a player from their cell
        /// </summary>
        public void ReleasePlayerFromCell(Player player)
        {
            if (player == null) return;

            string playerId = GetPlayerId(player);
            if (playerCellAssignments.ContainsKey(playerId))
            {
                int cellNumber = playerCellAssignments[playerId];
                RemoveOccupantFromCell(cellNumber, playerId);
                playerCellAssignments.Remove(playerId);
                ModLogger.Info($"✓ Released player {player.name} from cell {cellNumber}");
            }
        }

        /// <summary>
        /// Release an NPC from their cell
        /// </summary>
        public void ReleaseNPCFromCell(string npcId, string npcName)
        {
            if (string.IsNullOrEmpty(npcId)) return;

            if (npcCellAssignments.ContainsKey(npcId))
            {
                int cellNumber = npcCellAssignments[npcId];
                RemoveOccupantFromCell(cellNumber, npcId);
                npcCellAssignments.Remove(npcId);
                ModLogger.Info($"✓ Released NPC {npcName} from cell {cellNumber}");
            }
        }

        /// <summary>
        /// Get the cell number assigned to a player
        /// </summary>
        public int GetPlayerCellNumber(Player player)
        {
            if (player == null) return -1;

            string playerId = GetPlayerId(player);
            return playerCellAssignments.ContainsKey(playerId) ? playerCellAssignments[playerId] : -1;
        }

        /// <summary>
        /// Get the cell number assigned to an NPC
        /// </summary>
        public int GetNPCCellNumber(string npcId)
        {
            return npcCellAssignments.ContainsKey(npcId) ? npcCellAssignments[npcId] : -1;
        }

        /// <summary>
        /// Get cell position from jail controller
        /// </summary>
        public Vector3? GetCellPosition(int cellNumber)
        {
            var jailController = Core.JailController;
            if (jailController == null || cellNumber < 0 || cellNumber >= jailController.cells.Count)
            {
                return null;
            }

            var cell = jailController.cells[cellNumber];
            return cell.cellTransform?.position;
        }

        /// <summary>
        /// Get cell spawn points for escorting
        /// </summary>
        public List<Transform> GetCellSpawnPoints(int cellNumber)
        {
            var jailController = Core.JailController;
            if (jailController == null || cellNumber < 0 || cellNumber >= jailController.cells.Count)
            {
                return new List<Transform>();
            }

            var cell = jailController.cells[cellNumber];
            return cell.spawnPoints ?? new List<Transform>();
        }

        /// <summary>
        /// Find an available cell
        /// </summary>
        private int FindAvailableCell()
        {
            // Find cell with space
            for (int i = 0; i < totalCells; i++)
            {
                if (cellOccupancy.ContainsKey(i) && cellOccupancy[i].HasSpace())
                {
                    return i;
                }
            }

            return -1; // No available cells
        }

        /// <summary>
        /// Assign an occupant to a specific cell
        /// </summary>
        private bool AssignOccupantToCell(int cellNumber, string occupantId, string displayName)
        {
            if (!cellOccupancy.ContainsKey(cellNumber))
            {
                ModLogger.Error($"Cell {cellNumber} does not exist");
                return false;
            }

            var cell = cellOccupancy[cellNumber];
            if (!cell.HasSpace())
            {
                ModLogger.Error($"Cell {cellNumber} is full");
                return false;
            }

            cell.occupants.Add(occupantId);
            cell.occupantNames.Add(displayName);
            cell.isAvailable = cell.HasSpace();

            // Close and lock the cell door for security
            CloseCellDoor(cellNumber);

            ModLogger.Debug($"Added {displayName} to cell {cellNumber} ({cell.occupants.Count}/{cell.maxOccupants})");
            return true;
        }

        /// <summary>
        /// Remove an occupant from a cell
        /// </summary>
        private void RemoveOccupantFromCell(int cellNumber, string occupantId)
        {
            if (!cellOccupancy.ContainsKey(cellNumber))
            {
                ModLogger.Error($"Cell {cellNumber} does not exist");
                return;
            }

            var cell = cellOccupancy[cellNumber];
            int index = cell.occupants.IndexOf(occupantId);
            if (index >= 0)
            {
                cell.occupants.RemoveAt(index);
                if (index < cell.occupantNames.Count)
                {
                    cell.occupantNames.RemoveAt(index);
                }
                cell.isAvailable = cell.HasSpace();
                ModLogger.Debug($"Removed occupant from cell {cellNumber} ({cell.occupants.Count}/{cell.maxOccupants})");
            }
        }

        /// <summary>
        /// Get a unique player ID for tracking
        /// </summary>
        private string GetPlayerId(Player player)
        {
            // Use a combination of name and instance ID for uniqueness
            return $"{player.name}_{player.GetInstanceID()}";
        }

        /// <summary>
        /// Get occupancy status for all cells
        /// </summary>
        public Dictionary<int, CellOccupancy> GetAllCellOccupancy()
        {
            return new Dictionary<int, CellOccupancy>(cellOccupancy);
        }

        /// <summary>
        /// Get occupancy status for a specific cell
        /// </summary>
        public CellOccupancy GetCellOccupancy(int cellNumber)
        {
            return cellOccupancy.ContainsKey(cellNumber) ? cellOccupancy[cellNumber] : null;
        }

        /// <summary>
        /// Debug method to log current cell assignments
        /// </summary>
        public void LogCellAssignments()
        {
            ModLogger.Info("=== CURRENT CELL ASSIGNMENTS ===");
            
            int occupiedCells = cellOccupancy.Values.Count(c => c.occupants.Count > 0);
            int totalOccupants = cellOccupancy.Values.Sum(c => c.occupants.Count);
            
            ModLogger.Info($"Occupied cells: {occupiedCells}/{totalCells}, Total occupants: {totalOccupants}");
            
            foreach (var kvp in cellOccupancy.Where(c => c.Value.occupants.Count > 0))
            {
                var cell = kvp.Value;
                string occupantList = string.Join(", ", cell.occupantNames);
                ModLogger.Info($"Cell {cell.cellNumber}: {occupantList} ({cell.occupants.Count}/{cell.maxOccupants})");
            }
        }

        /// <summary>
        /// Close and lock the cell door for security
        /// </summary>
        private void CloseCellDoor(int cellNumber)
        {
            var jailController = Core.JailController;
            if (jailController?.cellManager == null) return;

            var cellDetail = jailController.GetCellByIndex(cellNumber);
            if (cellDetail?.cellDoor == null)
            {
                ModLogger.Debug($"No door found for cell {cellNumber}");
                return;
            }

            // Close and lock the door
            if (!cellDetail.cellDoor.IsClosed())
            {
                cellDetail.cellDoor.CloseDoor();
                ModLogger.Info($"Closed door for cell {cellNumber}");
            }

            if (!cellDetail.cellDoor.IsLocked())
            {
                cellDetail.cellDoor.LockDoor();
                ModLogger.Debug($"Locked door for cell {cellNumber}");
            }
        }
    }

    /// <summary>
    /// Represents the occupancy status of a single cell
    /// </summary>
    [System.Serializable]
    public class CellOccupancy
    {
        public int cellNumber;
        public List<string> occupants = new List<string>(); // List of occupant IDs
        public List<string> occupantNames = new List<string>(); // Display names for debugging
        public int maxOccupants = 2;
        public bool isAvailable = true;

        public bool HasSpace()
        {
            return occupants.Count < maxOccupants;
        }

        public bool IsEmpty()
        {
            return occupants.Count == 0;
        }

        public int GetAvailableSpace()
        {
            return maxOccupants - occupants.Count;
        }
    }
}