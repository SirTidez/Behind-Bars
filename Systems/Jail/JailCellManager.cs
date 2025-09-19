using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Jail
{
#if MONO
    public sealed class JailCellManager : MonoBehaviour
#else
    public sealed class JailCellManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        [Header("Cell Management")]
        public List<CellDetail> cells = new List<CellDetail>();
        public List<CellDetail> holdingCells = new List<CellDetail>();

        [System.Serializable]
        public class HoldingCellSpawnPoint
        {
            public int spawnIndex;
            public Transform spawnTransform;
            public bool isOccupied = false;
            public string occupantName = "";

            public string GetSpawnPointName()
            {
                return spawnTransform?.name ?? $"Spawn[{spawnIndex}]";
            }
        }

        public void Initialize(Transform jailRoot)
        {
            DiscoverCells(jailRoot);
            DiscoverHoldingCells(jailRoot);
            SetupCellBeds();
            InitializeHoldingCellSpawnPoints();

            ModLogger.Info($"✓ Cell Manager initialized: {cells.Count} prison cells, {holdingCells.Count} holding cells");
        }

        void DiscoverCells(Transform jailRoot)
        {
            cells.Clear();
            Transform cellsParent = jailRoot.Find("Cells");
            if (cellsParent == null)
            {
                ModLogger.Warn("Cells parent folder not found!");
                return;
            }

            ModLogger.Info($"Found Cells parent with {cellsParent.childCount} children");

            for (int j = 0; j < cellsParent.childCount; j++)
            {
                Transform cellTransform = cellsParent.GetChild(j);

                if (!cellTransform.name.Contains("Cell"))
                {
                    continue;
                }

                ModLogger.Debug($"Processing cell: {cellTransform.name}");

                CellDetail cell = new CellDetail();
                cell.cellTransform = cellTransform;
                cell.cellIndex = j;
                cell.cellName = cellTransform.name.Replace("_", " ");

                cell.cellDoor = new JailDoor();
                cell.cellDoor.doorHolder = FindDoorHolder(cellTransform, "DoorHolder");
                cell.cellDoor.doorPoint = cell.cellDoor.doorHolder?.Find("DoorPoint");
                cell.cellDoor.doorName = $"{cell.cellName} Door";
                cell.cellDoor.doorType = JailDoor.DoorType.CellDoor;

                cell.cellBounds = FindChildContaining(cellTransform, "CellBounds");
                cell.cellBedBottom = FindChildContaining(cellTransform, "CellBedBottom");
                cell.cellBedTop = FindChildContaining(cellTransform, "CellBedTop");
                cell.spawnPoints = FindAllChildrenContaining(cellTransform, "Spawn");

                ModLogger.Debug($"Cell setup: DoorHolder={cell.cellDoor.doorHolder != null}, Bounds={cell.cellBounds != null}, Beds={cell.cellBedBottom != null}/{cell.cellBedTop != null}, SpawnPoints={cell.spawnPoints.Count}");

                if (cell.IsValid())
                {
                    cells.Add(cell);
                    ModLogger.Info($"✓ Successfully added {cell.cellName}");
                }
                else
                {
                    ModLogger.Warn($"✗ Cell {cellTransform.name} is not valid - missing door holder");
                }
            }

            ModLogger.Info($"Discovered {cells.Count} prison cells total");
        }

        void DiscoverHoldingCells(Transform jailRoot)
        {
            holdingCells.Clear();
            Transform holdingCellsParent = jailRoot.Find("HoldingCells");
            if (holdingCellsParent == null)
            {
                ModLogger.Warn("HoldingCells parent not found!");
                return;
            }

            ModLogger.Info($"Found HoldingCells parent with {holdingCellsParent.childCount} children");

            for (int j = 0; j < holdingCellsParent.childCount; j++)
            {
                Transform holdingCellTransform = holdingCellsParent.GetChild(j);

                if (!holdingCellTransform.name.Contains("HoldingCell"))
                {
                    ModLogger.Debug($"Skipping {holdingCellTransform.name} - doesn't contain 'HoldingCell'");
                    continue;
                }

                ModLogger.Debug($"Processing potential holding cell: {holdingCellTransform.name}");

                CellDetail holdingCell = new CellDetail();
                holdingCell.cellTransform = holdingCellTransform;
                holdingCell.cellIndex = j;
                holdingCell.cellName = holdingCellTransform.name.Replace("_", " ");

                holdingCell.cellDoor = new JailDoor();
                holdingCell.cellDoor.doorHolder = FindDoorHolder(holdingCellTransform, "DoorHolder");
                holdingCell.cellDoor.doorPoint = holdingCell.cellDoor.doorHolder?.Find("DoorPoint");
                holdingCell.cellDoor.doorName = $"{holdingCell.cellName} Door";
                holdingCell.cellDoor.doorType = JailDoor.DoorType.HoldingCellDoor;

                holdingCell.cellBounds = FindChildContaining(holdingCellTransform, "HoldingCellBounds");

                holdingCell.spawnPoints.Clear();
                for (int spawnIndex = 0; spawnIndex < 3; spawnIndex++)
                {
                    Transform spawnPoint = holdingCellTransform.Find($"HoldingCellSpawn[{spawnIndex}]");
                    if (spawnPoint != null)
                    {
                        holdingCell.spawnPoints.Add(spawnPoint);
                        ModLogger.Debug($"  Found spawn point {spawnIndex}: {spawnPoint.name}");
                    }
                    else
                    {
                        ModLogger.Warn($"  Missing spawn point {spawnIndex} for {holdingCellTransform.name}");
                    }
                }

                holdingCell.maxOccupants = 3;
                holdingCell.InitializeSpawnPointOccupancy();

                ModLogger.Debug($"Holding cell setup: DoorHolder={holdingCell.cellDoor.doorHolder != null}, Bounds={holdingCell.cellBounds != null}, SpawnPoints={holdingCell.spawnPoints.Count}/3");

                if (holdingCell.IsValid())
                {
                    holdingCells.Add(holdingCell);
                    ModLogger.Info($"✓ Successfully added {holdingCell.cellName} with {holdingCell.spawnPoints.Count} spawn points");
                }
                else
                {
                    ModLogger.Warn($"✗ Holding cell {holdingCellTransform.name} is not valid - missing door holder");
                }
            }

            ModLogger.Info($"Pattern-based discovery completed. Found {holdingCells.Count} holding cells.");

            if (holdingCells.Count == 0)
            {
                ModLogger.Info("No holding cells found via patterns. Trying fallback search...");
                for (int j = 0; j < holdingCellsParent.childCount; j++)
                {
                    Transform child = holdingCellsParent.GetChild(j);
                    if (child.name.Contains("HoldingCell"))
                    {
                        ModLogger.Debug($"Fallback: examining {child.name}");

                        Transform actualCell = null;
                        string[] cellNames = { "HoldingCell", "Cell", "Holding" };
                        foreach (string cellName in cellNames)
                        {
                            for (int k = 0; k < 10; k++)
                            {
                                actualCell = child.Find($"{cellName}[{k}]");
                                if (actualCell != null) break;
                            }
                            if (actualCell != null) break;

                            actualCell = child.Find(cellName);
                            if (actualCell != null) break;
                        }

                        if (actualCell == null)
                        {
                            actualCell = child;
                            ModLogger.Debug($"Using {child.name} directly as holding cell");
                        }

                        if (actualCell != null)
                        {
                            CellDetail holdingCell = new CellDetail();
                            holdingCell.cellTransform = actualCell;
                            holdingCell.cellIndex = holdingCells.Count;
                            holdingCell.cellName = $"Holding Cell {holdingCells.Count}";

                            holdingCell.cellDoor = new JailDoor();
                            holdingCell.cellDoor.doorHolder = FindDoorHolder(actualCell, "DoorHolder");
                            if (holdingCell.cellDoor.doorHolder == null)
                            {
                                holdingCell.cellDoor.doorHolder = FindDoorHolder(child, "DoorHolder");
                            }
                            holdingCell.cellDoor.doorName = $"Holding Cell {holdingCells.Count} Door";
                            holdingCell.cellDoor.doorType = JailDoor.DoorType.HoldingCellDoor;

                            holdingCells.Add(holdingCell);
                            ModLogger.Info($"Fallback: Added holding cell from {child.name}");
                        }
                    }
                }
            }

            ModLogger.Info($"DiscoverHoldingCells completed. Found {holdingCells.Count} holding cells.");
        }

        Transform FindDoorHolder(Transform parent, string holderName)
        {
            Transform[] allChildren = parent.GetComponentsInChildren<Transform>();
            foreach (Transform child in allChildren)
            {
                if (child.name.Contains(holderName))
                {
                    return child;
                }
            }
            return null;
        }

        Transform FindChildContaining(Transform parent, string namePart)
        {
            foreach (Transform child in parent)
            {
                if (child.name.Contains(namePart))
                {
                    return child;
                }

                Transform found = FindChildContaining(child, namePart);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        List<Transform> FindAllChildrenContaining(Transform parent, string namePart)
        {
            List<Transform> foundChildren = new List<Transform>();
            FindAllChildrenContainingRecursive(parent, namePart, foundChildren);
            return foundChildren;
        }

        void FindAllChildrenContainingRecursive(Transform parent, string namePart, List<Transform> foundChildren)
        {
            foreach (Transform child in parent)
            {
                if (child.name.Contains(namePart))
                {
                    foundChildren.Add(child);
                }

                FindAllChildrenContainingRecursive(child, namePart, foundChildren);
            }
        }

        void SetupCellBeds()
        {
            foreach (var cell in cells)
            {
                SetupCellBed(cell.cellBedBottom, "Bottom", cell);
                SetupCellBed(cell.cellBedTop, "Top", cell);
            }
        }

        void SetupCellBed(Transform bedTransform, string bedType, CellDetail cell)
        {
            if (bedTransform == null) return;

            // Remove any existing JailBed component (for backwards compatibility)
            JailBed existingBed = bedTransform.GetComponent<JailBed>();
            if (existingBed != null)
            {
                DestroyImmediate(existingBed);
                ModLogger.Debug($"Removed existing JailBed component from {bedTransform.name}");
            }

            // 1. Instantiate the PrisonBedInteractable prefab for visuals
            GameObject instantiatedPrefab = InstantiatePrisonBedPrefab(bedTransform);

            // 2. Add PrisonBedInteractable script component to the bed for interaction logic
            PrisonBedInteractable bedInteractable = bedTransform.GetComponent<PrisonBedInteractable>();
            if (bedInteractable == null)
            {
                bedInteractable = bedTransform.gameObject.AddComponent<PrisonBedInteractable>();
                ModLogger.Debug($"Added PrisonBedInteractable component to {bedTransform.name}");
            }

            // Configure bed settings
            bedInteractable.isTopBunk = bedType.Equals("Top", System.StringComparison.OrdinalIgnoreCase);
            bedInteractable.cellName = cell.cellName;

            // 3. Find bed component transforms from the instantiated prefab
            if (instantiatedPrefab != null)
            {
                Transform prisonBedContainer = instantiatedPrefab.transform.Find("PrisonBed");
                if (prisonBedContainer != null)
                {
                    bedInteractable.bedMat = prisonBedContainer.Find("BedMat");
                    bedInteractable.whiteSheet = prisonBedContainer.Find("WhiteSheet");
                    bedInteractable.bedSheet = prisonBedContainer.Find("BedSheet");
                    bedInteractable.pillow = prisonBedContainer.Find("Pillow");

                    ModLogger.Info($"✓ Found bed components in PrisonBed container");
                }
                else
                {
                    ModLogger.Warn($"Could not find PrisonBed container in instantiated prefab");
                }
            }
            else
            {
                ModLogger.Warn($"Failed to instantiate PrisonBedInteractable prefab for {bedTransform.name}");
            }

            // Update the cell's bed component references
            if (bedType.Equals("Bottom", System.StringComparison.OrdinalIgnoreCase))
            {
                cell.bedBottomComponent = null;
            }
            else if (bedType.Equals("Top", System.StringComparison.OrdinalIgnoreCase))
            {
                cell.bedTopComponent = null;
            }

            ModLogger.Info($"✓ Setup {bedType} bed with PrisonBedInteractable: {bedTransform.name} in {cell.cellName}");
        }

        GameObject InstantiatePrisonBedPrefab(Transform bedTransform)
        {
            // Try to load the PrisonBedInteractable prefab from asset bundle
            if (Behind_Bars.Core.CachedJailBundle != null)
            {
                GameObject prefab = null;

#if MONO
                prefab = Behind_Bars.Core.CachedJailBundle.LoadAsset<GameObject>("PrisonBedInteractable");
#else
                prefab = Behind_Bars.Core.CachedJailBundle.LoadAsset("PrisonBedInteractable", Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif

                if (prefab != null)
                {
                    GameObject instance = UnityEngine.Object.Instantiate(prefab, bedTransform);
                    instance.name = "PrisonBedInteractable";
                    instance.transform.localPosition = Vector3.zero;
                    instance.transform.localRotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;

                    ModLogger.Info($"✓ Instantiated PrisonBedInteractable prefab");
                    return instance;
                }
                else
                {
                    ModLogger.Error($"Could not load PrisonBedInteractable prefab from asset bundle");
                }
            }
            else
            {
                ModLogger.Error($"Asset bundle not available for PrisonBedInteractable prefab");
            }

            return null;
        }


        void InitializeHoldingCellSpawnPoints()
        {
            foreach (var holdingCell in holdingCells)
            {
                holdingCell.spawnPointOccupancy.Clear();

                for (int i = 0; i < holdingCell.spawnPoints.Count; i++)
                {
                    Transform spawnPoint = holdingCell.spawnPoints[i];
                    if (spawnPoint != null)
                    {
                        SpawnPointOccupancy spawnData = new SpawnPointOccupancy
                        {
                            spawnIndex = i,
                            spawnPoint = spawnPoint,
                            isOccupied = false,
                            occupantName = ""
                        };

                        holdingCell.spawnPointOccupancy.Add(spawnData);
                    }
                }

                ModLogger.Info($"✓ Initialized {holdingCell.spawnPointOccupancy.Count} spawn points for {holdingCell.cellName}");
            }
        }

        public Transform AssignPlayerToHoldingCell(string playerName)
        {
            foreach (var holdingCell in holdingCells)
            {
                var availableSpawn = holdingCell.spawnPointOccupancy.FirstOrDefault(sp => !sp.isOccupied);
                if (availableSpawn != null)
                {
                    availableSpawn.isOccupied = true;
                    availableSpawn.occupantName = playerName;

                    ModLogger.Info($"✓ Assigned {playerName} to {holdingCell.cellName} spawn point {availableSpawn.spawnIndex}");
                    return availableSpawn.spawnPoint;
                }
            }

            ModLogger.Warn($"⚠️ No available spawn points in holding cells for {playerName}");
            return null;
        }

        public void ReleasePlayerFromHoldingCell(string playerName)
        {
            foreach (var holdingCell in holdingCells)
            {
                var occupiedSpawn = holdingCell.spawnPointOccupancy.FirstOrDefault(sp => sp.occupantName == playerName);
                if (occupiedSpawn != null)
                {
                    occupiedSpawn.isOccupied = false;
                    occupiedSpawn.occupantName = "";

                    ModLogger.Info($"✓ Released {playerName} from {holdingCell.cellName} spawn point {occupiedSpawn.spawnIndex}");
                    return;
                }
            }

            ModLogger.Warn($"⚠️ Player {playerName} not found in any holding cell");
        }

        public CellDetail GetAvailableJailCell()
        {
            return cells.FirstOrDefault(c => c.IsAvailable());
        }

        public CellDetail GetAvailableHoldingCell()
        {
            return holdingCells.FirstOrDefault(c => c.HasAvailableSpace());
        }

        public (int totalSpawns, int available, int occupied, int totalCells) GetHoldingCellStatus()
        {
            int totalSpawns = holdingCells.Sum(hc => hc.spawnPointOccupancy.Count);
            int occupied = holdingCells.Sum(hc => hc.spawnPointOccupancy.Count(sp => sp.isOccupied));
            int available = totalSpawns - occupied;

            return (totalSpawns, available, occupied, holdingCells.Count);
        }

        public CellDetail GetCellByIndex(int cellIndex)
        {
            return cells.FirstOrDefault(c => c.cellIndex == cellIndex);
        }

        public CellDetail GetHoldingCellByIndex(int cellIndex)
        {
            return holdingCells.FirstOrDefault(c => c.cellIndex == cellIndex);
        }

        /// <summary>
        /// Find which holding cell contains the specified player
        /// </summary>
        /// <param name="player">Player to search for</param>
        /// <returns>Holding cell index (0-based) or -1 if not found</returns>
        public int FindPlayerHoldingCell(Player player)
        {
            if (player == null) return -1;

            Vector3 playerPosition = player.transform.position;

            // Check each holding cell bounds to see which contains the player
            for (int i = 0; i < holdingCells.Count; i++)
            {
                var holdingCell = holdingCells[i];
                if (IsPlayerInHoldingCellBounds(player, i))
                {
                    ModLogger.Info($"Player {player.name} found in holding cell {i}");
                    return i;
                }
            }

            ModLogger.Warn($"Player {player.name} not found in any holding cell bounds");
            return -1;
        }

        /// <summary>
        /// Check if player is currently in specified holding cell bounds
        /// </summary>
        /// <param name="player">Player to check</param>
        /// <param name="holdingCellIndex">Index of holding cell to check (0-based)</param>
        /// <returns>True if player is within the holding cell bounds</returns>
        public bool IsPlayerInHoldingCellBounds(Player player, int holdingCellIndex)
        {
            if (player == null || holdingCellIndex < 0 || holdingCellIndex >= holdingCells.Count)
            {
                return false;
            }

            var holdingCell = holdingCells[holdingCellIndex];
            if (holdingCell?.cellBounds == null)
            {
                return false;
            }

            var boundsCollider = holdingCell.cellBounds.GetComponent<BoxCollider>();
            if (boundsCollider == null)
            {
                return false;
            }

            // Calculate world position of bounds manually
            Vector3 playerPos = player.transform.position;
            Transform boundsTransform = boundsCollider.transform;
            Vector3 boundsWorldCenter = boundsTransform.TransformPoint(boundsCollider.center);
            Vector3 boundsWorldSize = Vector3.Scale(boundsCollider.size, boundsTransform.lossyScale);

            // Manual bounds checking
            Vector3 min = boundsWorldCenter - boundsWorldSize * 0.5f;
            Vector3 max = boundsWorldCenter + boundsWorldSize * 0.5f;

            bool contains = (playerPos.x >= min.x && playerPos.x <= max.x) &&
                           (playerPos.y >= min.y && playerPos.y <= max.y) &&
                           (playerPos.z >= min.z && playerPos.z <= max.z);

            return contains;
        }

        /// <summary>
        /// Check if player has exited the specified holding cell bounds
        /// </summary>
        /// <param name="player">Player to check</param>
        /// <param name="holdingCellIndex">Index of holding cell (0-based)</param>
        /// <returns>True if player is outside the holding cell bounds</returns>
        public bool HasPlayerExitedHoldingCell(Player player, int holdingCellIndex)
        {
            return !IsPlayerInHoldingCellBounds(player, holdingCellIndex);
        }

        /// <summary>
        /// Check if player is currently in specified jail cell bounds
        /// </summary>
        /// <param name="player">Player to check</param>
        /// <param name="cellIndex">Index of jail cell to check (0-based)</param>
        /// <returns>True if player is within the jail cell bounds</returns>
        public bool IsPlayerInJailCellBounds(Player player, int cellIndex)
        {
            if (player == null || cellIndex < 0 || cellIndex >= cells.Count)
                return false;

            var cell = cells[cellIndex];
            if (cell?.cellBounds == null)
                return false;

            var boundsCollider = cell.cellBounds.GetComponent<BoxCollider>();
            if (boundsCollider == null)
                return false;

            // Use same manual world-space calculation as holding cells to avoid Unity bounds issues
            Vector3 playerPos = player.transform.position;
            Transform boundsTransform = boundsCollider.transform;
            Vector3 boundsWorldCenter = boundsTransform.TransformPoint(boundsCollider.center);
            Vector3 boundsWorldSize = Vector3.Scale(boundsCollider.size, boundsTransform.lossyScale);

            // Manual bounds checking
            Vector3 min = boundsWorldCenter - boundsWorldSize * 0.5f;
            Vector3 max = boundsWorldCenter + boundsWorldSize * 0.5f;

            bool contains = (playerPos.x >= min.x && playerPos.x <= max.x) &&
                           (playerPos.y >= min.y && playerPos.y <= max.y) &&
                           (playerPos.z >= min.z && playerPos.z <= max.z);

            return contains;
        }

        public void TestHoldingCellDiscovery()
        {
            ModLogger.Info("=== TESTING HOLDING CELL DISCOVERY ===");
            holdingCells.Clear();
            DiscoverHoldingCells(transform.parent ?? transform);
            ModLogger.Info($"Discovery completed. Found {holdingCells.Count} holding cells.");

            TestHoldingCellSpawnSystem();
        }

        public void TestHoldingCellSpawnSystem()
        {
            ModLogger.Info("=== TESTING HOLDING CELL SPAWN SYSTEM ===");

            var (totalSpawns, available, occupied, totalCells) = GetHoldingCellStatus();
            ModLogger.Info($"Holding Cell Status: {totalCells} cells, {totalSpawns} total spawn points, {available} available, {occupied} occupied");

            ModLogger.Info("Testing player assignments:");
            var spawn1 = AssignPlayerToHoldingCell("TestPlayer1");
            var spawn2 = AssignPlayerToHoldingCell("TestPlayer2");
            var spawn3 = AssignPlayerToHoldingCell("TestPlayer3");
            var spawn4 = AssignPlayerToHoldingCell("TestPlayer4");

            var (totalAfter, availableAfter, occupiedAfter, totalCellsAfter) = GetHoldingCellStatus();
            ModLogger.Info($"Status after assignments: {totalCellsAfter} cells, {totalAfter} total spawn points, {availableAfter} available, {occupiedAfter} occupied");

            ModLogger.Info("Testing player releases:");
            ReleasePlayerFromHoldingCell("TestPlayer2");
            ReleasePlayerFromHoldingCell("TestPlayer4");

            var (totalFinal, availableFinal, occupiedFinal, totalCellsFinal) = GetHoldingCellStatus();
            ModLogger.Info($"Final status: {totalCellsFinal} cells, {totalFinal} total spawn points, {availableFinal} available, {occupiedFinal} occupied");

            foreach (var holdingCell in holdingCells)
            {
                var (current, max, availableCell) = holdingCell.GetOccupancyStatus();
                ModLogger.Info($"  {holdingCell.cellName}: {current}/{max} occupied, {availableCell} available");

                foreach (var spawn in holdingCell.spawnPointOccupancy)
                {
                    string status = spawn.isOccupied ? $"occupied by {spawn.occupantName}" : "available";
                    ModLogger.Info($"    Spawn {spawn.spawnIndex}: {status}");
                }
            }
        }
    }
}