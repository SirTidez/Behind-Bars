using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
#endif

namespace Behind_Bars.Systems.Jail
{
#if MONO
    public sealed class JailCellManager : MonoBehaviour
#else
    public sealed class JailCellManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Cell Management")]
#endif
        public List<CellDetail> cells = new List<CellDetail>();
        public List<CellDetail> holdingCells = new List<CellDetail>();

        // IL2CPP can return a null wrapper from a same-frame GetComponent call
        // immediately after adding an injected component, even though
        // AddComponentSafe successfully created it. Keep the canonical component
        // reference supplied by the add call so inmate bed claims do not race
        // Unity's wrapper materialization.
        private static readonly Dictionary<int, PrisonBedInteractable> preparedBedComponents = new Dictionary<int, PrisonBedInteractable>();
        private readonly Dictionary<int, string> pendingNpcBunkClaims = new Dictionary<int, string>();
        private float nextPendingBunkClaimRetry;
        private int lastPendingBunkDiagnosticCount = -1;


        [System.Serializable]
        public class HoldingCellSpawnPoint
        {
            public int spawnIndex;
            public Transform spawnTransform;
            public bool isOccupied = false;
            public string occupantKey = "";
            public string occupantName = "";

            public string GetSpawnPointName()
            {
                return spawnTransform?.name ?? $"Spawn[{spawnIndex}]";
            }
        }

        public void Initialize(Transform jailRoot)
        {
            preparedBedComponents.Clear();
            RemoveLegacyNpcBunkVisuals(jailRoot);
            DiscoverCells(jailRoot);
            DiscoverHoldingCells(jailRoot);
            SetupCellBeds();
            InitializeHoldingCellSpawnPoints();
            RetryPendingNpcBunkClaims();

            ModLogger.Debug($"✓ Cell Manager initialized: {cells.Count} prison cells, {holdingCells.Count} holding cells");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void RemoveLegacyNpcBunkVisuals(Transform root)
        {
            if (root == null)
            {
                return;
            }

            // A previous runtime fallback synthesized bunk anchors from cell
            // bounds.  Those bounds are not an authoring coordinate system and
            // could put a whole bed in a doorway.  Remove only objects created
            // by that fallback; real prefab beds and player-owned buildables
            // are deliberately left alone.
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                RemoveLegacyNpcBunkVisuals(child);

                string childName = child.name;
                if (childName.StartsWith("BehindBars_Npc", System.StringComparison.Ordinal) ||
                    childName.StartsWith("BehindBars_RuntimeCellBed", System.StringComparison.Ordinal))
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
        }

        private void Update()
        {
            if (pendingNpcBunkClaims.Count == 0 || Time.unscaledTime < nextPendingBunkClaimRetry)
            {
                return;
            }

            nextPendingBunkClaimRetry = Time.unscaledTime + 0.5f;
            RetryPendingNpcBunkClaims();
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

            ModLogger.Debug($"Found Cells parent with {cellsParent.childCount} children");

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

                ValidateAuthoredBedMarkers(cell);

                ModLogger.Debug($"Cell setup: DoorHolder={cell.cellDoor.doorHolder != null}, Bounds={cell.cellBounds != null}, Beds={cell.cellBedBottom != null}/{cell.cellBedTop != null}, SpawnPoints={cell.spawnPoints.Count}");

                if (cell.IsValid())
                {
                    cells.Add(cell);
                    ModLogger.Debug($"✓ Successfully added {cell.cellName}");
                }
                else
                {
                    ModLogger.Warn($"✗ Cell {cellTransform.name} is not valid - missing door holder");
                }
            }

            ModLogger.Debug($"Discovered {cells.Count} prison cells total");
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

            ModLogger.Debug($"Found HoldingCells parent with {holdingCellsParent.childCount} children");

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
                    ModLogger.Debug($"✓ Successfully added {holdingCell.cellName} with {holdingCell.spawnPoints.Count} spawn points");
                }
                else
                {
                    ModLogger.Warn($"✗ Holding cell {holdingCellTransform.name} is not valid - missing door holder");
                }
            }

            ModLogger.Debug($"Pattern-based discovery completed. Found {holdingCells.Count} holding cells.");

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

            ModLogger.Debug($"DiscoverHoldingCells completed. Found {holdingCells.Count} holding cells.");
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
            // IL2CPP fix: Use GetChild instead of foreach to avoid casting issues
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void ValidateAuthoredBedMarkers(CellDetail cell)
        {
            if (cell == null)
            {
                return;
            }

            // CellBedBottom and CellBedTop are authored directly on the bunk
            // surfaces in the jail prefab.  Their position, rotation, and
            // scale are part of the bed prefab's layout contract: overwriting
            // them from a cell-bound estimate moves the completed bedding off
            // the metal bunk.  Keep these anchors read-only at runtime.
            ValidateAuthoredBedMarker(cell.cellBedBottom, cell.cellName, "bottom");
            ValidateAuthoredBedMarker(cell.cellBedTop, cell.cellName, "top");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void ValidateAuthoredBedMarker(Transform marker, string cellName, string bunkType)
        {
            if (marker == null)
            {
                return;
            }

            if (marker.lossyScale.sqrMagnitude < 0.0001f)
            {
                ModLogger.Warn(
                    $"[JAIL LIFECYCLE] Authored {bunkType} bunk marker in {cellName} has a near-zero scale; " +
                    "leaving it unchanged so the authored layout can be repaired in the asset bundle.");
            }
        }

        List<Transform> FindAllChildrenContaining(Transform parent, string namePart)
        {
            List<Transform> foundChildren = new List<Transform>();
            FindAllChildrenContainingRecursive(parent, namePart, foundChildren);
            return foundChildren;
        }

        void FindAllChildrenContainingRecursive(Transform parent, string namePart, List<Transform> foundChildren)
        {
            // IL2CPP fix: Use GetChild instead of foreach to avoid casting issues
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
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

#if !MONO
        [HideFromIl2Cpp]
#endif
        void SetupCellBed(Transform bedTransform, string bedType, CellDetail cell)
        {
            if (bedTransform == null || cell == null)
            {
                return;
            }

            // Remove any existing JailBed component (for backwards compatibility)
            JailBed existingBed = BBHelpers.GetComponentSafe<JailBed>(bedTransform.gameObject);
            if (existingBed != null)
            {
                DestroyImmediate(existingBed);
                ModLogger.Debug($"Removed existing JailBed component from {bedTransform.name}");
            }

            // These are the real progressive player-bed interaction surfaces.
            // An NPC claim completes and invalidates this same component; do
            // not replace it with a display-only clone or the player's
            // bedroll/sheets can no longer resolve a valid placement target.
            //
            // The complete staged hierarchy is serialized under each anchor
            // in Jail.prefab. Do not create it dynamically here: IL2CPP can
            // materialize nested prefab transforms differently after bundle
            // loading, which detached the NPC mattress/sheets/pillow from the
            // metal bunk even though the player path later looked correct.
            GameObject instantiatedPrefab = FindExistingPrisonBedPrefab(bedTransform);
            if (instantiatedPrefab == null)
            {
                ModLogger.Error(
                    $"Missing serialized PrisonBedInteractable under {cell.cellName}/{bedTransform.name}. " +
                    "The loaded jail asset bundle is stale or was built without authored bunk surfaces.");
                return;
            }

            // Keep the player and NPC paths on the same authored bed prefab.
            // Its local transform is part of the staged placement contract; only
            // repair material bindings here, not its authored geometry transform.
            JailMaterialCompatibility.RepairForScheduleOne(instantiatedPrefab);

            PrisonBedInteractable bedInteractable = GetPreparedBed(bedTransform);
            if (bedInteractable == null)
            {
                bedInteractable = BBHelpers.AddComponentSafe<PrisonBedInteractable>(bedTransform.gameObject);
                if (bedInteractable != null)
                {
                    preparedBedComponents[bedTransform.GetInstanceID()] = bedInteractable;
                }
            }

            if (bedInteractable == null)
            {
                ModLogger.Warn($"Could not add PrisonBedInteractable to {bedTransform.name}");
                return;
            }

            bedInteractable.isTopBunk = bedType.Equals("Top", System.StringComparison.OrdinalIgnoreCase);
            bedInteractable.cellName = cell.cellName;
            BindBedDressing(bedTransform, bedInteractable, cell.cellName, bedType);

            SetPreparedBed(cell, bedType, bedInteractable);
            ModLogger.Debug($"✓ Setup {bedType} prison bed interaction: {bedTransform.name} in {cell.cellName}");
        }

        /// <summary>
        /// Completes and reserves one bunk for a spawned inmate.  Cell
        /// assignment remains the source of truth; this only represents that
        /// assignment visibly and prevents a player from using the bunk.
        /// </summary>
        public void ClaimBedForNpc(int cellIndex, string inmateName)
        {
            if (cellIndex >= 0 && !string.IsNullOrWhiteSpace(inmateName))
            {
                pendingNpcBunkClaims[cellIndex] = inmateName;
            }

            CellDetail cell = GetCellByIndex(cellIndex);
            if (cell == null)
            {
                ModLogger.Warn($"Could not claim a bunk for NPC {inmateName}: cell {cellIndex} was unavailable");
                return;
            }

            // Pick one of the two finished bunks per inmate.  A stable hash
            // avoids reassigning a visibly different bunk every scene restore,
            // while still distributing inmates between upper and lower beds.
            bool preferTop = SelectTopBunk(cellIndex, inmateName);
            string bunkType = preferTop ? "top" : "bottom";
            PrisonBedInteractable bunk = GetOrPrepareBed(cell, preferTop);

            if (bunk == null)
            {
                preferTop = !preferTop;
                bunkType = preferTop ? "top" : "bottom";
                bunk = GetOrPrepareBed(cell, preferTop);
            }

            if (bunk == null)
            {
                LogPendingBunkClaim(cell, cellIndex, inmateName);
                return;
            }

            // Rebind immediately before the claim. This guarantees that a
            // late IL2CPP materialization cannot leave an NPC with a
            // partial/detached visual while the player uses the complete
            // authored bed hierarchy.
            Transform bunkAnchor = preferTop ? cell.cellBedTop : cell.cellBedBottom;
            if (!BindBedDressing(bunkAnchor, bunk, cell.cellName, bunkType))
            {
                ModLogger.Warn(
                    $"[JAIL LIFECYCLE] Could not finalize {bunkType} bunk dressing in cell {cellIndex} for NPC {inmateName}; retrying when the authored bed hierarchy is ready.");
                return;
            }

            // NPCs now complete the exact same authored visual hierarchy as a
            // player. The existing hierarchy is the only one proven to honor
            // the prison-bed prefab's nested scale/rotation contract; creating
            // a second visual root produces an offset, incomplete bed.
            bunk.ClaimForNpc(inmateName);
            pendingNpcBunkClaims.Remove(cellIndex);
            ModLogger.Info(
                $"[JAIL LIFECYCLE] Claimed completed {bunkType ?? "assigned"} bunk in cell {cellIndex} for NPC {inmateName}: " +
                $"mat={bunk.bedMat != null}, whiteSheet={bunk.whiteSheet != null}, bedSheet={bunk.bedSheet != null}, pillow={bunk.pillow != null}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void RetryPendingNpcBunkClaims()
        {
            if (pendingNpcBunkClaims.Count == 0)
            {
                return;
            }

            var pending = pendingNpcBunkClaims.ToArray();
            foreach (var claim in pending)
            {
                // The jail prefab finishes materializing its nested marker
                // hierarchy over several frames on IL2CPP.  The original
                // retry only attempted to reuse the null references captured
                // during DiscoverCells, so it could never recover when the
                // authored CellBedBottom/CellBedTop transforms became
                // available shortly after the inmate spawned.
                CellDetail cell = GetCellByIndex(claim.Key);
                RefreshCellBedAnchors(cell);
                ClaimBedForNpc(claim.Key, claim.Value);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void RefreshCellBedAnchors(CellDetail cell)
        {
            if (cell?.cellTransform == null)
            {
                return;
            }

            bool resolvedBottom = false;
            bool resolvedTop = false;

            if (cell.cellBedBottom == null)
            {
                cell.cellBedBottom = FindChildContaining(cell.cellTransform, "CellBedBottom");
                resolvedBottom = cell.cellBedBottom != null;
            }

            if (cell.cellBedTop == null)
            {
                cell.cellBedTop = FindChildContaining(cell.cellTransform, "CellBedTop");
                resolvedTop = cell.cellBedTop != null;
            }

            if (!resolvedBottom && !resolvedTop)
            {
                return;
            }

            // In IL2CPP the authored bunk markers can materialize after the
            // initial cell discovery pass.  Validate the late markers, but do
            // not rewrite their authored transforms: those transforms are the
            // coordinate system for the completed player and NPC bed layout.
            ValidateAuthoredBedMarkers(cell);

            ModLogger.Info(
                $"[JAIL LIFECYCLE] Resolved authored bunk anchor(s) for {cell.cellName}: " +
                $"bottom={resolvedBottom}, top={resolvedTop}");

            if (resolvedBottom)
            {
                SetupCellBed(cell.cellBedBottom, "Bottom", cell);
            }

            if (resolvedTop)
            {
                SetupCellBed(cell.cellBedTop, "Top", cell);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void LogPendingBunkClaim(CellDetail cell, int cellIndex, string inmateName)
        {
            if (lastPendingBunkDiagnosticCount == pendingNpcBunkClaims.Count)
            {
                return;
            }

            lastPendingBunkDiagnosticCount = pendingNpcBunkClaims.Count;
            ModLogger.Warn(
                $"[JAIL LIFECYCLE] Bunk claim pending for NPC {inmateName} in cell {cellIndex}: " +
                $"bottomAnchor={cell.cellBedBottom != null}, topAnchor={cell.cellBedTop != null}, " +
                $"bundleReady={Behind_Bars.Core.CachedJailBundle != null}. Retrying once the cell visuals are available.");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private PrisonBedInteractable GetPreparedBed(Transform bedTransform)
        {
            if (bedTransform == null)
            {
                return null;
            }

            int instanceId = bedTransform.GetInstanceID();
            if (preparedBedComponents.TryGetValue(instanceId, out PrisonBedInteractable prepared) && prepared != null)
            {
                return prepared;
            }

            PrisonBedInteractable resolved = BBHelpers.GetComponentSafe<PrisonBedInteractable>(bedTransform.gameObject);
            if (resolved != null)
            {
                preparedBedComponents[instanceId] = resolved;
            }

            return resolved;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private PrisonBedInteractable GetPreparedBed(CellDetail cell, string bedType)
        {
            if (cell == null)
            {
                return null;
            }

            bool isTop = bedType.Equals("Top", System.StringComparison.OrdinalIgnoreCase);
            PrisonBedInteractable prepared = isTop ? cell.preparedTopBunk : cell.preparedBottomBunk;
            if (prepared != null)
            {
                return prepared;
            }

            Transform anchor = isTop ? cell.cellBedTop : cell.cellBedBottom;
            if (anchor == null)
            {
                return null;
            }

            prepared = GetPreparedBed(anchor);
            if (prepared != null)
            {
                SetPreparedBed(cell, bedType, prepared);
            }

            return prepared;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private PrisonBedInteractable GetOrPrepareBed(CellDetail cell, bool isTop)
        {
            string bedType = isTop ? "Top" : "Bottom";
            PrisonBedInteractable prepared = GetPreparedBed(cell, bedType);
            if (prepared != null)
            {
                return prepared;
            }

            Transform anchor = isTop ? cell.cellBedTop : cell.cellBedBottom;
            if (anchor == null)
            {
                return null;
            }

            SetupCellBed(anchor, bedType, cell);
            return GetPreparedBed(cell, bedType);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void SetPreparedBed(CellDetail cell, string bedType, PrisonBedInteractable bed)
        {
            if (bedType.Equals("Top", System.StringComparison.OrdinalIgnoreCase))
            {
                cell.preparedTopBunk = bed;
            }
            else
            {
                cell.preparedBottomBunk = bed;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static bool SelectTopBunk(int cellIndex, string inmateName)
        {
            unchecked
            {
                int hash = cellIndex * 397;
                if (!string.IsNullOrEmpty(inmateName))
                {
                    for (int i = 0; i < inmateName.Length; i++)
                    {
                        hash = (hash * 31) + inmateName[i];
                    }
                }

                return (hash & 1) != 0;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static GameObject FindExistingPrisonBedPrefab(Transform bedAnchor)
        {
            if (bedAnchor == null)
            {
                return null;
            }

            for (int i = 0; i < bedAnchor.childCount; i++)
            {
                Transform child = bedAnchor.GetChild(i);
                if (child != null && child.name == "PrisonBedInteractable")
                {
                    return child.gameObject;
                }
            }

            return null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static bool BindBedDressing(Transform bedAnchor, PrisonBedInteractable bed, string cellName, string bedType)
        {
            if (bedAnchor == null || bed == null)
            {
                return false;
            }

            GameObject bedPrefab = FindExistingPrisonBedPrefab(bedAnchor);
            Transform prisonBedContainer = bedPrefab != null ? bedPrefab.transform.Find("PrisonBed") : null;
            if (prisonBedContainer == null)
            {
                ModLogger.Warn($"Prison bed dressing was unavailable for {bedType} bunk in {cellName}");
                return false;
            }

            // These authored children contain the exact local positions,
            // rotations, and scales used by the verified player-bed path.
            // Do not apply transform compensation here.
            bed.bedMat = prisonBedContainer.Find("BedMat");
            bed.whiteSheet = prisonBedContainer.Find("WhiteSheet");
            bed.bedSheet = prisonBedContainer.Find("BedSheet");
            bed.pillow = prisonBedContainer.Find("Pillow");

            return bed.bedMat != null && bed.whiteSheet != null &&
                   bed.bedSheet != null && bed.pillow != null;
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
                            occupantKey = "",
                            occupantName = ""
                        };

                        holdingCell.spawnPointOccupancy.Add(spawnData);
                    }
                }

                ModLogger.Debug($"✓ Initialized {holdingCell.spawnPointOccupancy.Count} spawn points for {holdingCell.cellName}");
            }
        }

        public Transform AssignPlayerToHoldingCell(Player player)
        {
            if (player == null)
            {
                return null;
            }

            return AssignPlayerToHoldingCellInternal(GetPlayerRuntimeKey(player), player.name);
        }

        /// <summary>
        /// Assigns a player to a named holding cell. This is used for the disciplinary
        /// hold path so the reserved HoldingCell_01 is selected independently of prefab order.
        /// </summary>
        public Transform AssignPlayerToHoldingCellByName(Player player, string holdingCellName)
        {
            if (player == null || string.IsNullOrEmpty(holdingCellName))
            {
                return null;
            }

            string occupantKey = GetPlayerRuntimeKey(player);
            var holdingCell = holdingCells.FirstOrDefault(cell =>
                cell != null && string.Equals(cell.cellTransform?.name, holdingCellName, StringComparison.Ordinal));
            if (holdingCell == null)
            {
                ModLogger.Error($"Holding cell '{holdingCellName}' was not found for disciplinary hold");
                return null;
            }

            var existingSpawn = holdingCell.spawnPointOccupancy.FirstOrDefault(sp => sp.occupantKey == occupantKey);
            if (existingSpawn != null)
            {
                return existingSpawn.spawnPoint;
            }

            var availableSpawn = holdingCell.spawnPointOccupancy.FirstOrDefault(sp => !sp.isOccupied);
            if (availableSpawn == null)
            {
                ModLogger.Error($"Holding cell '{holdingCellName}' has no free disciplinary spawn point for {player.name}");
                return null;
            }

            availableSpawn.isOccupied = true;
            availableSpawn.occupantKey = occupantKey;
            availableSpawn.occupantName = player.name;
            ModLogger.Info($"Assigned {player.name} to reserved disciplinary cell {holdingCellName} spawn point {availableSpawn.spawnIndex}");
            return availableSpawn.spawnPoint;
        }

        private Transform AssignPlayerToHoldingCellByNameForDiagnostics(string playerName)
        {
            return AssignPlayerToHoldingCellInternal(playerName, playerName);
        }

        private Transform AssignPlayerToHoldingCellInternal(string occupantKey, string occupantDisplayName)
        {
            foreach (var holdingCell in holdingCells)
            {
                var availableSpawn = holdingCell.spawnPointOccupancy.FirstOrDefault(sp => !sp.isOccupied);
                if (availableSpawn != null)
                {
                    availableSpawn.isOccupied = true;
                    availableSpawn.occupantKey = occupantKey;
                    availableSpawn.occupantName = occupantDisplayName;

                    ModLogger.Info($"✓ Assigned {occupantDisplayName} to {holdingCell.cellName} spawn point {availableSpawn.spawnIndex}");
                    return availableSpawn.spawnPoint;
                }
            }

            ModLogger.Warn($"⚠️ No available spawn points in holding cells for {occupantDisplayName}");
            return null;
        }

        public void ReleasePlayerFromHoldingCell(Player player)
        {
            if (player == null)
            {
                return;
            }

            ReleasePlayerFromHoldingCellInternal(GetPlayerRuntimeKey(player), player.name);
        }

        private void ReleasePlayerFromHoldingCellByNameForDiagnostics(string playerName)
        {
            ReleasePlayerFromHoldingCellInternal(playerName, playerName);
        }

        private void ReleasePlayerFromHoldingCellInternal(string occupantKey, string occupantDisplayName)
        {
            foreach (var holdingCell in holdingCells)
            {
                var occupiedSpawn = holdingCell.spawnPointOccupancy.FirstOrDefault(sp =>
                    sp.occupantKey == occupantKey || sp.occupantName == occupantDisplayName);
                if (occupiedSpawn != null)
                {
                    occupiedSpawn.isOccupied = false;
                    occupiedSpawn.occupantKey = "";
                    occupiedSpawn.occupantName = "";

                    ModLogger.Info($"✓ Released {occupantDisplayName} from {holdingCell.cellName} spawn point {occupiedSpawn.spawnIndex}");
                    return;
                }
            }

            ModLogger.Warn($"⚠️ Player {occupantDisplayName} not found in any holding cell");
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

        public CellDetail GetHoldingCellByName(string holdingCellName)
        {
            if (string.IsNullOrEmpty(holdingCellName))
            {
                return null;
            }

            return holdingCells.FirstOrDefault(cell =>
                cell != null && string.Equals(cell.cellTransform?.name, holdingCellName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Gets the runtime list index used by both holding-cell bounds checks and the door
        /// controller. This intentionally differs from CellDetail.cellIndex, which preserves
        /// the authored child index in the prison prefab.
        /// </summary>
        public int GetHoldingCellRuntimeIndexByName(string holdingCellName)
        {
            if (string.IsNullOrEmpty(holdingCellName))
            {
                return -1;
            }

            for (int i = 0; i < holdingCells.Count; i++)
            {
                var holdingCell = holdingCells[i];
                if (holdingCell != null && string.Equals(holdingCell.cellTransform?.name, holdingCellName, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
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

            // BoxCollider.center and size are expressed in the collider transform's local
            // space.  Testing against a world-aligned box loses the transform rotation and
            // can incorrectly report that a prisoner has left a rotated holding cell.
            Vector3 localPlayerPosition = boundsCollider.transform.InverseTransformPoint(player.transform.position);
            Vector3 halfSize = boundsCollider.size * 0.5f;
            Vector3 min = boundsCollider.center - halfSize;
            Vector3 max = boundsCollider.center + halfSize;

            return localPlayerPosition.x >= min.x && localPlayerPosition.x <= max.x &&
                   localPlayerPosition.y >= min.y && localPlayerPosition.y <= max.y &&
                   localPlayerPosition.z >= min.z && localPlayerPosition.z <= max.z;
        }

        /// <summary>
        /// Check if player has exited the specified holding cell bounds
        /// </summary>
        /// <param name="player">Player to check</param>
        /// <param name="holdingCellIndex">Index of holding cell (0-based)</param>
        /// <returns>True if player is outside the holding cell bounds</returns>
        public bool HasPlayerExitedHoldingCell(Player player, int holdingCellIndex)
        {
            if (player == null || holdingCellIndex < 0 || holdingCellIndex >= holdingCells.Count)
            {
                return false;
            }

            var holdingCell = holdingCells[holdingCellIndex];
            var boundsCollider = holdingCell?.cellBounds != null
                ? holdingCell.cellBounds.GetComponent<BoxCollider>()
                : null;
            // doorPoint is an authored guard-operation point and can sit well out in the
            // corridor.  It is not the physical threshold.  Use the actual door holder
            // first so the handoff occurs as the player clears the doorway.
            Transform doorway = holdingCell?.cellDoor?.doorHolder ?? holdingCell?.cellDoor?.doorPoint;

            // HoldingCellBounds is intentionally generous in the authored prefab.  Using it
            // alone means the prisoner has to walk down the corridor before we recognize the
            // exit.  Treat a short crossing beyond the actual door plane as the primary exit
            // signal, while keeping the bounds check as a safe fallback for incomplete assets.
            if (boundsCollider != null && doorway != null)
            {
                Vector3 cellCenter = boundsCollider.transform.TransformPoint(boundsCollider.center);
                Vector3 outward = doorway.position - cellCenter;
                outward.y = 0f;

                if (outward.sqrMagnitude > 0.01f)
                {
                    outward.Normalize();
                    Vector3 playerFromDoor = player.transform.position - doorway.position;
                    playerFromDoor.y = 0f;

                    // A small positive clearance prevents a player standing in the door
                    // jamb from advancing the escort, without making them walk down the
                    // corridor to the old guard-operation point.
                    const float doorClearance = 0.05f;
                    if (Vector3.Dot(playerFromDoor, outward) >= doorClearance)
                    {
                        return true;
                    }
                }
            }

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
            {
                ModLogger.Debug($"IsPlayerInJailCellBounds: Invalid parameters - player={player != null}, cellIndex={cellIndex}, cells.Count={cells.Count}");
                return false;
            }

            var cell = cells[cellIndex];
            if (cell?.cellBounds == null)
            {
                ModLogger.Debug($"IsPlayerInJailCellBounds: Cell {cellIndex} or cellBounds is null");
                return false;
            }

            var boundsCollider = cell.cellBounds.GetComponent<BoxCollider>();
            if (boundsCollider == null)
            {
                ModLogger.Debug($"IsPlayerInJailCellBounds: Cell {cellIndex} boundsCollider is null");
                return false;
            }

            // Use same manual world-space calculation as holding cells to avoid Unity bounds issues
            Vector3 playerPos = player.transform.position;
            Transform boundsTransform = boundsCollider.transform;
            Vector3 boundsWorldCenter = boundsTransform.TransformPoint(boundsCollider.center);
            Vector3 boundsWorldSize = Vector3.Scale(boundsCollider.size, boundsTransform.lossyScale);

            // Manual bounds checking with slight margin for edge cases
            Vector3 min = boundsWorldCenter - boundsWorldSize * 0.5f;
            Vector3 max = boundsWorldCenter + boundsWorldSize * 0.5f;

            // Add small margin (0.1m) to account for floating point precision and edge cases
            const float margin = 0.1f;
            Vector3 marginVector = new Vector3(margin, margin, margin);
            min -= marginVector;
            max += marginVector;

            bool containsX = playerPos.x >= min.x && playerPos.x <= max.x;
            bool containsY = playerPos.y >= min.y && playerPos.y <= max.y;
            bool containsZ = playerPos.z >= min.z && playerPos.z <= max.z;
            bool contains = containsX && containsY && containsZ;

            // Debug logging when player should be in cell but isn't detected
            if (!contains)
            {
                ModLogger.Debug($"IsPlayerInJailCellBounds: Player {player.name} at ({playerPos.x:F2}, {playerPos.y:F2}, {playerPos.z:F2}) is NOT in cell {cellIndex}");
                ModLogger.Debug($"  Cell bounds: center=({boundsWorldCenter.x:F2}, {boundsWorldCenter.y:F2}, {boundsWorldCenter.z:F2}), size=({boundsWorldSize.x:F2}, {boundsWorldSize.y:F2}, {boundsWorldSize.z:F2})");
                ModLogger.Debug($"  Bounds min=({min.x:F2}, {min.y:F2}, {min.z:F2}), max=({max.x:F2}, {max.y:F2}, {max.z:F2})");
                ModLogger.Debug($"  Checks: X={containsX}, Y={containsY}, Z={containsZ}");
                
                // Also try using Unity's built-in bounds check as fallback
                Bounds worldBounds = new Bounds(boundsWorldCenter, boundsWorldSize);
                if (worldBounds.Contains(playerPos))
                {
                    ModLogger.Debug($"  Unity Bounds.Contains returns TRUE (using fallback)");
                    return true;
                }
            }

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
            var spawn1 = AssignPlayerToHoldingCellByNameForDiagnostics("TestPlayer1");
            var spawn2 = AssignPlayerToHoldingCellByNameForDiagnostics("TestPlayer2");
            var spawn3 = AssignPlayerToHoldingCellByNameForDiagnostics("TestPlayer3");
            var spawn4 = AssignPlayerToHoldingCellByNameForDiagnostics("TestPlayer4");

            var (totalAfter, availableAfter, occupiedAfter, totalCellsAfter) = GetHoldingCellStatus();
            ModLogger.Info($"Status after assignments: {totalCellsAfter} cells, {totalAfter} total spawn points, {availableAfter} available, {occupiedAfter} occupied");

            ModLogger.Info("Testing player releases:");
            ReleasePlayerFromHoldingCellByNameForDiagnostics("TestPlayer2");
            ReleasePlayerFromHoldingCellByNameForDiagnostics("TestPlayer4");

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

        private static string GetPlayerRuntimeKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Core.ResolvePlayerKey(player);
        }
    }
}
