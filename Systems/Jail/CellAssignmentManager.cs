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
        public Dictionary<int, CellOccupancy> cellOccupancy = new Dictionary<int, CellOccupancy>();
        public Dictionary<string, int> playerCellAssignments = new Dictionary<string, int>(); // playerId -> cellNumber
        public Dictionary<string, int> npcCellAssignments = new Dictionary<string, int>(); // npcId -> cellNumber

        // Configuration
        public int totalCells = 36; // Total cells available (0-35)
        public int maxOccupantsPerCell = 1; // NPCs should have individual cells since there are plenty
        private const int MAX_PLAYER_CELLS = 12; // Players restricted to cells 0-11
        private const int MAX_USABLE_CELLS = 36; // NPCs can use all cells 0-35

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                ModLogger.Debug("CellAssignmentManager initialized");
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
                int detectedCells = jailController.cells.Count;
                // Use all available cells up to 36
                totalCells = Math.Min(detectedCells, MAX_USABLE_CELLS);
                ModLogger.Debug($"Found {detectedCells} cells in jail structure, using {totalCells} (0-{totalCells-1}) total cells");
                ModLogger.Debug($"Players restricted to cells 0-{MAX_PLAYER_CELLS-1}, NPCs can use all cells 0-{totalCells-1}");
            }

            // Initialize all cells as empty
            for (int i = 0; i < totalCells; i++)
            {
                // Player cells (0-11) can have 2 occupants, NPC cells (12+) should be individual
                int maxOccupants = (i <= 11) ? 2 : 1;

                cellOccupancy[i] = new CellOccupancy
                {
                    cellNumber = i,
                    occupants = new List<string>(),
                    maxOccupants = maxOccupants,
                    isAvailable = true
                };
            }

            ModLogger.Debug($"✓ Initialized cell tracking for {totalCells} cells");
        }

        /// <summary>
        /// Assign a cell to a player (restricted to cells 0-11)
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
                ModLogger.Debug($"Player {player.name} already assigned to cell {existingCell}");
                return existingCell;
            }

            // Find available cell in player range (0-11)
            int cellNumber = FindAvailablePlayerCell();
            if (cellNumber == -1)
            {
                ModLogger.Error($"No available cells for player {player.name} in range 0-11");
                return -1;
            }

            // Assign player to cell
            if (AssignOccupantToCell(cellNumber, playerId, $"Player: {player.name}"))
            {
                playerCellAssignments[playerId] = cellNumber;
                ModLogger.Debug($"✓ Assigned player {player.name} to cell {cellNumber}");
                return cellNumber;
            }

            return -1;
        }

        /// <summary>
        /// Assign a cell to an NPC (can use any available cell)
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
                ModLogger.Debug($"NPC {npcName} already assigned to cell {existingCell}");
                return existingCell;
            }

            // Find available cell (NPCs can use any cell)
            int cellNumber = FindAvailableNPCCell();
            if (cellNumber == -1)
            {
                ModLogger.Error($"No available cells for NPC {npcName}");
                return -1;
            }

            // Assign NPC to cell
            if (AssignOccupantToCell(cellNumber, npcId, $"NPC: {npcName}"))
            {
                npcCellAssignments[npcId] = cellNumber;
                ModLogger.Debug($"✓ Assigned NPC {npcName} to cell {cellNumber}");
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
                ModLogger.Debug($"✓ Released player {player.name} from cell {cellNumber}");
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
                ModLogger.Debug($"✓ Released NPC {npcName} from cell {cellNumber}");
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
        /// Find an available cell for players (restricted to cells 0-11)
        /// </summary>
        private int FindAvailablePlayerCell()
        {
            var availableCells = new List<int>();

            // Only check cells 0-11 for players
            for (int i = 0; i < MAX_PLAYER_CELLS; i++)
            {
                if (cellOccupancy.ContainsKey(i) && cellOccupancy[i].HasSpace())
                {
                    availableCells.Add(i);
                }
            }

            if (availableCells.Count == 0)
            {
                return -1; // No available player cells
            }

            // Random selection from available player cells
            int selectedCell = availableCells[UnityEngine.Random.Range(0, availableCells.Count)];
            ModLogger.Debug($"Selected player cell {selectedCell} from {availableCells.Count} available player cells (0-11)");

            return selectedCell;
        }

        /// <summary>
        /// Find an available cell for NPCs (can use cells 0-35, preferring empty cells)
        /// </summary>
        private int FindAvailableNPCCell()
        {
            var availableCells = new List<int>();
            var emptyCells = new List<int>();

            // Check all available cells (0-35)
            for (int i = 0; i < totalCells; i++)
            {
                if (cellOccupancy.ContainsKey(i) && cellOccupancy[i].HasSpace())
                {
                    availableCells.Add(i);

                    // Track completely empty cells for preference
                    if (cellOccupancy[i].IsEmpty())
                    {
                        emptyCells.Add(i);
                    }
                }
            }

            if (availableCells.Count == 0)
            {
                return -1; // No available cells
            }

            // Prefer empty cells first (since we have plenty of cells for few inmates)
            if (emptyCells.Count > 0)
            {
                int selectedCell = emptyCells[UnityEngine.Random.Range(0, emptyCells.Count)];
                ModLogger.Debug($"Selected empty NPC cell {selectedCell} from {emptyCells.Count} empty cells");
                return selectedCell;
            }

            // Fallback to any available cell if no empty ones
            int fallbackCell = availableCells[UnityEngine.Random.Range(0, availableCells.Count)];
            ModLogger.Debug($"Selected shared NPC cell {fallbackCell} from {availableCells.Count} available cells");
            return fallbackCell;
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
            ModLogger.Debug("=== CURRENT CELL ASSIGNMENTS ===");

            int occupiedCells = cellOccupancy.Values.Count(c => c.occupants.Count > 0);
            int totalOccupants = cellOccupancy.Values.Sum(c => c.occupants.Count);

            // Count assignments by category
            int playerCellsUsed = cellOccupancy.Where(c => c.Key <= 11 && c.Value.occupants.Count > 0).Count();
            int npcCellsUsed = cellOccupancy.Where(c => c.Key > 11 && c.Value.occupants.Count > 0).Count();
            int floorLevelCellsUsed = cellOccupancy.Where(c => c.Key >= 18 && c.Value.occupants.Count > 0).Count();

            ModLogger.Debug($"Occupied cells: {occupiedCells}/{totalCells}, Total occupants: {totalOccupants}");
            ModLogger.Debug($"Player cells (0-11) used: {playerCellsUsed}, NPC cells (12+) used: {npcCellsUsed}");
            ModLogger.Debug($"Floor-level cells (18+) used: {floorLevelCellsUsed}");

            foreach (var kvp in cellOccupancy.Where(c => c.Value.occupants.Count > 0))
            {
                var cell = kvp.Value;
                string occupantList = string.Join(", ", cell.occupantNames);
                string cellType = cell.cellNumber <= 11 ? "[PLAYER]" : (cell.cellNumber >= 18 ? "[FLOOR]" : "[MID]");
                ModLogger.Debug($"Cell {cell.cellNumber} {cellType}: {occupantList} ({cell.occupants.Count}/{cell.maxOccupants})");
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