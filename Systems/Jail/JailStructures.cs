using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Systems.Jail;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

/// <summary>
/// Runtime state for one authored jail door, including prefab instance, lock state,
/// animation target, and completion callbacks.
/// </summary>
[System.Serializable]
public class JailDoor
{
    // Authored holder/operation points and the instantiated door are scene references;
    // they are rebuilt on scene load rather than persisted in custody data.
    public Transform doorHolder;
    public GameObject doorInstance;
    public Transform doorHinge;
    public Transform doorPoint;  // Guard position point for safe door operation

    public string doorName;
    public DoorType doorType;
    public DoorInteractionType interactionType;
    public DoorState currentState = DoorState.Closed;
    public bool isLocked = false;

    public float openAngle = -135f;  // Door opens to -135 degrees on Z axis
    public float closedAngle = 0f;
    public float animationSpeed = 2f;
    public bool reverseDirection = false;  // If true, flips the open angle direction

    // Private animation state. currentAngle is the last applied hinge angle; targetAngle
    // is selected by OpenDoor/CloseDoor while isAnimating gates UpdateDoorAnimation.
    private float targetAngle;
    private float currentAngle;
    private bool isAnimating = false;

    /// <summary>
    /// Raised after this door reaches its fully open position.
    /// </summary>
    public event Action<JailDoor> Opened;

    /// <summary>
    /// Raised after this door reaches its fully closed position, including the
    /// closed-and-locked state.
    /// </summary>
    public event Action<JailDoor> Closed;

    /// <summary>Classifies the physical role of a jail door.</summary>
    public enum DoorType
    {
        CellDoor,
        HoldingCellDoor,
        EntryDoor,
        GuardDoor,
        AreaDoor
    }

    /// <summary>Describes whether a guard passes through a door or only operates it.</summary>
    public enum DoorInteractionType
    {
        PassThrough,    // Guard moves through door (Inner, Entry, Guard doors)
        OperationOnly   // Guard only opens/closes door (Cell, Holding doors)
    }

    /// <summary>Transient animation/lock state reported by a jail door.</summary>
    public enum DoorState
    {
        Closed,
        Opening,
        Open,
        Closing,
        Locked
    }

    /// <summary>Returns whether the authored door holder exists.</summary>
    public bool IsValid()
    {
        return doorHolder != null;
    }

    /// <summary>Returns whether a runtime door prefab has been assigned.</summary>
    public bool IsInstantiated()
    {
        return doorInstance != null;
    }

    /// <summary>Returns true only for the fully open state.</summary>
    public bool IsOpen()
    {
        return currentState == DoorState.Open;
    }

    /// <summary>Returns true for both closed and closed-and-locked states.</summary>
    public bool IsClosed()
    {
        return currentState == DoorState.Closed || currentState == DoorState.Locked;
    }

    /// <summary>Returns whether the hinge is moving toward its current target.</summary>
    public bool IsAnimating()
    {
        return isAnimating;
    }

    /// <summary>
    /// Begins opening the door unless it is locked or already opening/open. Completion
    /// and the <see cref="Opened"/> event occur on a later animation update.
    /// </summary>
    public void OpenDoor()
    {
        if (isLocked || currentState == DoorState.Open || currentState == DoorState.Opening)
            return;

        currentState = DoorState.Opening;
        targetAngle = GetEffectiveOpenAngle();
        isAnimating = true;

        Debug.Log($"{doorName}: Opening door (direction: {(reverseDirection ? "reversed" : "normal")})");
    }

    /// <summary>
    /// Begins closing the door unless it is already closed/closing/locked. Locking is a
    /// separate operation performed by LockDoor or the owning controller.
    /// </summary>
    public void CloseDoor()
    {
        if (currentState == DoorState.Closed || currentState == DoorState.Closing || currentState == DoorState.Locked)
            return;

        currentState = DoorState.Closing;
        targetAngle = closedAngle;
        isAnimating = true;

        Debug.Log($"{doorName}: Closing door");
    }

    /// <summary>
    /// Marks the door locked and closes it first when necessary. A closing-and-locking
    /// transition raises <see cref="Closed"/> only after the hinge reaches its target.
    /// </summary>
    public void LockDoor()
    {
        isLocked = true;

        // If door is open or opening, close it first
        if (currentState == DoorState.Open || currentState == DoorState.Opening)
        {
            currentState = DoorState.Closing;
            targetAngle = closedAngle;
            isAnimating = true;
            Debug.Log($"{doorName}: Closing and locking door");
        }
        else
        {
            // Door is already closed, just lock it
            currentState = DoorState.Locked;
            Debug.Log($"{doorName}: Door locked");
        }
    }

    /// <summary>Clears the lock and returns a locked door to the closed state.</summary>
    public void UnlockDoor()
    {
        if (isLocked)
        {
            isLocked = false;
            currentState = DoorState.Closed;
            Debug.Log($"{doorName}: Door unlocked");
        }
    }

    /// <summary>
    /// Advances the hinge toward its target and raises the corresponding completion event
    /// when the door reaches its open or closed position.
    /// </summary>
    /// <param name="deltaTime">Frame interval used for animation interpolation.</param>
    public void UpdateDoorAnimation(float deltaTime)
    {
        if (!isAnimating || doorHinge == null)
            return;

        // Lerp current angle towards target angle
        currentAngle = Mathf.LerpAngle(currentAngle, targetAngle, animationSpeed * deltaTime);

        // Apply rotation to hinge (on Z axis for your doors)
        doorHinge.localEulerAngles = new Vector3(0, 0, currentAngle);

        // Lerp approaches its target asymptotically. Waiting for the last fraction of a
        // degree leaves a visually complete door in Opening/Closing for an extra beat and
        // stalls an escort waiting on the completion event. Snap the imperceptible final
        // two degrees for both directions so the event matches the visible animation.
        const float completionTolerance = 2f;
        if (Mathf.Abs(Mathf.DeltaAngle(currentAngle, targetAngle)) < completionTolerance)
        {
            currentAngle = targetAngle;
            doorHinge.localEulerAngles = new Vector3(0, 0, currentAngle);
            isAnimating = false;

            // Update state based on final position
            if (Mathf.Approximately(currentAngle, GetEffectiveOpenAngle()))
            {
                currentState = DoorState.Open;
                Debug.Log($"{doorName}: Door opened");
                RaiseDoorEvent(Opened, "opened");
            }
            else if (Mathf.Approximately(currentAngle, closedAngle))
            {
                if (isLocked)
                {
                    currentState = DoorState.Locked;
                    Debug.Log($"{doorName}: Door closed and locked");
                }
                else
                {
                    currentState = DoorState.Closed;
                    Debug.Log($"{doorName}: Door closed");
                }

                RaiseDoorEvent(Closed, "closed");
            }
        }
    }

    private float GetEffectiveOpenAngle()
    {
        return reverseDirection ? -openAngle : openAngle;
    }

    // Invoke each listener independently so one subscriber cannot prevent the door state
    // transition or the remaining listeners from observing completion.
    private void RaiseDoorEvent(Action<JailDoor> listeners, string completedState)
    {
        if (listeners == null)
        {
            return;
        }

        foreach (Delegate listener in listeners.GetInvocationList())
        {
            try
            {
                ((Action<JailDoor>)listener)?.Invoke(this);
            }
            catch (Exception exception)
            {
                Debug.LogError($"{doorName}: {completedState} listener failed: {exception}");
            }
        }
    }

    /// <summary>
    /// Resets the authored hinge to its closed angle and derives the interaction mode from
    /// the configured door type. It does not instantiate a prefab.
    /// </summary>
    public void InitializeDoor()
    {
        if (doorHinge != null)
        {
            currentAngle = closedAngle;
            doorHinge.localEulerAngles = new Vector3(0, 0, currentAngle);
        }

        // Auto-determine interaction type based on door type
        SetInteractionTypeFromDoorType();
    }

    /// <summary>
    /// Automatically set interaction type based on door type
    /// </summary>
    public void SetInteractionTypeFromDoorType()
    {
        switch (doorType)
        {
            case DoorType.CellDoor:
            case DoorType.HoldingCellDoor:
                interactionType = DoorInteractionType.OperationOnly;
                break;
            case DoorType.EntryDoor:
            case DoorType.GuardDoor:
            case DoorType.AreaDoor:
                interactionType = DoorInteractionType.PassThrough;
                break;
            default:
                interactionType = DoorInteractionType.OperationOnly;
                break;
        }
    }

    // Legacy compatibility wrapper; prefer the explicit OpenDoor/CloseDoor/LockDoor APIs
    // when the caller needs to reason about asynchronous animation state.
    public void SetDoorState(bool open, bool locked = false)
    {
        if (locked)
        {
            LockDoor();
        }
        else if (open)
        {
            OpenDoor();
        }
        else
        {
            CloseDoor();
        }
    }

    /// <summary>Returns whether the door is currently locked.</summary>
    public bool IsLocked()
    {
        return isLocked;
    }
}

/// <summary>
/// Runtime reservation for one holding-cell spawn point. Occupant key and display name
/// are cleared together when the reservation is released.
/// </summary>
[System.Serializable]
public class SpawnPointOccupancy
{
    /// <summary>Authored transform used as the spawn destination.</summary>
    public Transform spawnPoint;
    /// <summary>Zero-based spawn index within the holding cell.</summary>
    public int spawnIndex;
    /// <summary>Whether this point is reserved by an occupant.</summary>
    public bool isOccupied;
    /// <summary>Stable runtime key for the reserved occupant.</summary>
    public string occupantKey;
    /// <summary>Display name retained for diagnostics.</summary>
    public string occupantName;
}

/// <summary>
/// Authored cell geometry plus scene-local door, bed, spawn, and occupancy state used by
/// booking, recreation, and custody flows.
/// </summary>
[System.Serializable]
public class CellDetail
{
    // cellIndex is the authored child index under Cells. It is intentionally distinct
    // from a compact runtime list position used by some holding-cell APIs.
    public Transform cellTransform;
    public Transform cellBounds;
    public JailDoor cellDoor;
    
    // Bed references for sleeping functionality
    public Transform cellBedBottom;
    public Transform cellBedTop;
    public JailBed bedBottomComponent;
    public JailBed bedTopComponent;

    // Runtime ownership surfaces for the current progressive-bed component.
    // Both player and NPC bunks use this same authored surface; an NPC claim
    // completes it and disables interaction rather than using a display clone.
    // Never persist Unity object references in the cell save model.
    [System.NonSerialized]
    public PrisonBedInteractable preparedBottomBunk;

    [System.NonSerialized]
    public PrisonBedInteractable preparedTopBunk;

    // Spawn points for arrested players
    public List<Transform> spawnPoints = new List<Transform>();
    
    // Individual spawn point occupancy tracking (up to 3 per holding cell)
    public List<SpawnPointOccupancy> spawnPointOccupancy = new List<SpawnPointOccupancy>();

    public int cellIndex;
    public string cellName;
    public bool isOccupied = false;
    public string occupantKey = "";
    public string occupantName = "";
    
    // Maximum occupants for this cell (3 for holding cells, 1 for regular cells)
    public int maxOccupants = 1;

    /// <summary>Returns whether the cell transform and its door record are usable.</summary>
    public bool IsValid()
    {
        return cellTransform != null && cellDoor.IsValid();
    }
    
    /// <summary>
    /// Initialize spawn point occupancy tracking. Holding cells use this list as the
    /// authoritative per-occupant reservation; regular cells may use the cell-level
    /// fallback when no spawn points were authored.
    /// </summary>
    public void InitializeSpawnPointOccupancy()
    {
        spawnPointOccupancy.Clear();
        
        for (int i = 0; i < spawnPoints.Count && i < maxOccupants; i++)
        {
            spawnPointOccupancy.Add(new SpawnPointOccupancy
            {
                spawnPoint = spawnPoints[i],
                spawnIndex = i,
                isOccupied = false,
                occupantKey = null,
                occupantName = null
            });
        }
        
        Debug.Log($"Initialized {spawnPointOccupancy.Count} spawn points for {cellName}");
    }

    /// <summary>
    /// Gets the next available spawn point in this cell. Holding cells search their
    /// occupancy records first; regular cells fall back to bounds/transform or the first
    /// authored point when no per-spawn records exist.
    /// </summary>
    /// <returns>Transform of available spawn point, or null if all are occupied</returns>
    public Transform GetAvailableSpawnPoint()
    {
        // For holding cells with multiple spawn points, find first available
        if (spawnPointOccupancy.Count > 0)
        {
            var availableSpawn = spawnPointOccupancy.Find(sp => !sp.isOccupied);
            return availableSpawn?.spawnPoint;
        }
        
        // Fallback for regular cells or if no occupancy tracking
        if (spawnPoints.Count == 0)
        {
            return cellBounds != null ? cellBounds : cellTransform;
        }
        
        return isOccupied ? null : spawnPoints[0];
    }
    
    /// <summary>
    /// Assigns a player to the first available spawn point and records the stable key/name
    /// pair. Holding cells update cell-level occupancy from their per-spawn records.
    /// </summary>
    /// <param name="player">Player to assign</param>
    /// <returns>The spawn point assigned, or null if cell is full</returns>
    public Transform AssignPlayerToSpawnPoint(Player player)
    {
        if (player == null)
        {
            return null;
        }

        return AssignPlayerToSpawnPoint(GetPlayerRuntimeKey(player), player.name);
    }

    private Transform AssignPlayerToSpawnPoint(string playerKey, string playerDisplayName)
    {
        if (spawnPointOccupancy.Count > 0)
        {
            // Find first available spawn point
            var availableSpawn = spawnPointOccupancy.Find(sp => !sp.isOccupied);
            if (availableSpawn != null)
            {
                availableSpawn.isOccupied = true;
                availableSpawn.occupantKey = playerKey;
                availableSpawn.occupantName = playerDisplayName;
                
                // Update cell-level occupancy
                UpdateCellOccupancy();
                
                Debug.Log($"Assigned {playerDisplayName} to {cellName} spawn point {availableSpawn.spawnIndex}");
                return availableSpawn.spawnPoint;
            }
        }
        else
        {
            // Regular cell behavior
            if (!isOccupied)
            {
                isOccupied = true;
                occupantKey = playerKey;
                occupantName = playerDisplayName;
                return GetAvailableSpawnPoint();
            }
        }
        
        return null; // Cell is full
    }
    
    /// <summary>
    /// Releases a player from their spawn point
    /// </summary>
    /// <param name="player">Player to release</param>
    public void ReleasePlayerFromSpawnPoint(Player player)
    {
        if (player == null)
        {
            return;
        }

        ReleasePlayerFromSpawnPoint(GetPlayerRuntimeKey(player), player.name);
    }

    private void ReleasePlayerFromSpawnPoint(string playerKey, string playerDisplayName)
    {
        if (spawnPointOccupancy.Count > 0)
        {
            var occupiedSpawn = spawnPointOccupancy.Find(sp => sp.occupantKey == playerKey || sp.occupantName == playerDisplayName);
            if (occupiedSpawn != null)
            {
                occupiedSpawn.isOccupied = false;
                occupiedSpawn.occupantKey = null;
                occupiedSpawn.occupantName = null;
                
                // Update cell-level occupancy
                UpdateCellOccupancy();
                
                Debug.Log($"Released {playerDisplayName} from {cellName} spawn point {occupiedSpawn.spawnIndex}");
            }
        }
        else
        {
            // Regular cell behavior
            if (occupantKey == playerKey || occupantName == playerDisplayName)
            {
                isOccupied = false;
                occupantKey = "";
                occupantName = "";
            }
        }
    }
    
    /// <summary>
    /// Updates cell-level occupancy based on spawn point occupancy
    /// </summary>
    void UpdateCellOccupancy()
    {
        if (spawnPointOccupancy.Count > 0)
        {
            var occupiedSpawns = spawnPointOccupancy.FindAll(sp => sp.isOccupied);
            isOccupied = occupiedSpawns.Count > 0;
            
            // Set occupant name to first occupant (for compatibility)
            if (occupiedSpawns.Count > 0)
            {
                occupantKey = occupiedSpawns[0].occupantKey;
                occupantName = occupiedSpawns[0].occupantName;
            }
            else
            {
                occupantKey = "";
                occupantName = "";
            }
        }
    }

    private static string GetPlayerRuntimeKey(Player player)
    {
        if (player == null)
        {
            return string.Empty;
        }

        return Behind_Bars.Core.ResolvePlayerKey(player);
    }
    
    /// <summary>
    /// Gets current occupancy status
    /// </summary>
    /// <returns>(current occupants, max occupants, available spaces)</returns>
    public (int current, int max, int available) GetOccupancyStatus()
    {
        if (spawnPointOccupancy.Count > 0)
        {
            int current = spawnPointOccupancy.Count(sp => sp.isOccupied);
            int max = maxOccupants;
            int available = max - current;
            return (current, max, available);
        }
        else
        {
            // Regular cell
            return (isOccupied ? 1 : 0, 1, isOccupied ? 0 : 1);
        }
    }

    /// <summary>
    /// Gets a random spawn point in this cell
    /// </summary>
    /// <returns>Transform of random spawn point</returns>
    public Transform GetRandomSpawnPoint()
    {
        if (spawnPoints.Count == 0)
        {
            return cellBounds != null ? cellBounds : cellTransform;
        }
        
        int randomIndex = UnityEngine.Random.Range(0, spawnPoints.Count);
        return spawnPoints[randomIndex];
    }

    /// <summary>Begins opening this cell's door when its door record is valid.</summary>
    public void OpenCell()
    {
        if (cellDoor.IsValid())
        {
            cellDoor.OpenDoor();
        }
    }

    /// <summary>Begins closing this cell's door when its door record is valid.</summary>
    public void CloseCell()
    {
        if (cellDoor.IsValid())
        {
            cellDoor.CloseDoor();
        }
    }

    /// <summary>Locks or unlocks this cell's door without changing occupancy.</summary>
    /// <param name="locked">Whether the cell door should be locked.</param>
    public void LockCell(bool locked)
    {
        if (cellDoor.IsValid())
        {
            if (locked)
                cellDoor.LockDoor();
            else
                cellDoor.UnlockDoor();
        }
    }

    /// <summary>
    /// Check if this cell has beds available
    /// </summary>
    /// <returns>True if cell has at least one bed</returns>
    public bool HasBeds()
    {
        return cellBedBottom != null || cellBedTop != null;
    }

    /// <summary>
    /// Get all beds in this cell
    /// </summary>
    /// <returns>List of JailBed components</returns>
    public List<JailBed> GetAllBeds()
    {
        var beds = new List<JailBed>();

        // Check for JailBed components first (backwards compatibility)
        if (bedBottomComponent != null)
            beds.Add(bedBottomComponent);
        if (bedTopComponent != null)
            beds.Add(bedTopComponent);

        // If no JailBed components found, check for completed PrisonBedInteractable
        if (beds.Count == 0)
        {
            if (cellBedBottom != null)
            {
                var prisonBed = BBHelpers.GetComponentSafe<PrisonBedInteractable>(cellBedBottom.gameObject);
                if (prisonBed != null && prisonBed.IsComplete)
                {
                    var jailBed = BBHelpers.GetComponentSafe<JailBed>(cellBedBottom.gameObject);
                    if (jailBed != null)
                        beds.Add(jailBed);
                }
            }

            if (cellBedTop != null)
            {
                var prisonBed = BBHelpers.GetComponentSafe<PrisonBedInteractable>(cellBedTop.gameObject);
                if (prisonBed != null && prisonBed.IsComplete)
                {
                    var jailBed = BBHelpers.GetComponentSafe<JailBed>(cellBedTop.gameObject);
                    if (jailBed != null)
                        beds.Add(jailBed);
                }
            }
        }

        return beds;
    }

    /// <summary>
    /// Get the first bed in this cell (bottom bunk preferred)
    /// </summary>
    /// <returns>JailBed component or null if no beds</returns>
    public JailBed GetFirstBed()
    {
        // Check for JailBed components first (backwards compatibility)
        if (bedBottomComponent != null)
            return bedBottomComponent;
        if (bedTopComponent != null)
            return bedTopComponent;

        // Check for completed PrisonBedInteractable with JailBed (bottom preferred)
        if (cellBedBottom != null)
        {
            var prisonBed = BBHelpers.GetComponentSafe<PrisonBedInteractable>(cellBedBottom.gameObject);
            if (prisonBed != null && prisonBed.IsComplete)
            {
                var jailBed = BBHelpers.GetComponentSafe<JailBed>(cellBedBottom.gameObject);
                if (jailBed != null)
                    return jailBed;
            }
        }

        if (cellBedTop != null)
        {
            var prisonBed = BBHelpers.GetComponentSafe<PrisonBedInteractable>(cellBedTop.gameObject);
            if (prisonBed != null && prisonBed.IsComplete)
            {
                var jailBed = BBHelpers.GetComponentSafe<JailBed>(cellBedTop.gameObject);
                if (jailBed != null)
                    return jailBed;
            }
        }

        return null;
    }

    /// <summary>
    /// Get all PrisonBedInteractable components in this cell
    /// </summary>
    /// <returns>List of PrisonBedInteractable components</returns>
    public List<PrisonBedInteractable> GetAllPrisonBeds()
    {
        var beds = new List<PrisonBedInteractable>();

        if (cellBedBottom != null)
        {
            var prisonBed = BBHelpers.GetComponentSafe<PrisonBedInteractable>(cellBedBottom.gameObject);
            if (prisonBed != null)
                beds.Add(prisonBed);
        }

        if (cellBedTop != null)
        {
            var prisonBed = BBHelpers.GetComponentSafe<PrisonBedInteractable>(cellBedTop.gameObject);
            if (prisonBed != null)
                beds.Add(prisonBed);
        }

        return beds;
    }

    /// <summary>
    /// Reset all beds in this cell to unmade state
    /// </summary>
    public void ResetAllBeds()
    {
        var prisonBeds = GetAllPrisonBeds();
        foreach (var bed in prisonBeds)
        {
            bed.ResetBed();
        }
    }

    /// <summary>
    /// Check if this cell is available for occupation
    /// </summary>
    /// <returns>True if cell is not occupied</returns>
    public bool IsAvailable()
    {
        if (spawnPointOccupancy.Count > 0)
        {
            return spawnPointOccupancy.Any(sp => !sp.isOccupied);
        }
        return !isOccupied;
    }

    /// <summary>
    /// Check if this cell has available space (for holding cells)
    /// </summary>
    /// <returns>True if cell has available space</returns>
    public bool HasAvailableSpace()
    {
        if (spawnPointOccupancy.Count > 0)
        {
            return spawnPointOccupancy.Any(sp => !sp.isOccupied);
        }
        return !isOccupied;
    }
}

/// <summary>
/// Authored storage-area references and the two inventory stations used by booking and
/// release. Component references are resolved lazily through the IL2CPP-safe helpers.
/// </summary>
[System.Serializable]
public class JailStorageArea
{
#if MONO
    [Header("Storage Area Components")]
#endif
    public Transform storageArea;
    public Transform guardPoint;

#if MONO
    [Header("Door Controls")]
#endif
    public JailDoor storageHallDoor;
    public JailDoor bookingStorageDoor;

#if MONO
    [Header("Inventory Stations")]
#endif
    public Transform jailInventoryPickup;        // Prison items station (JailInventoryPickupStation)
    public Transform inventoryDropOff;           // Personal items drop-off station
    public Transform inventoryPickup;            // Personal items pickup station (InventoryPickupStation)

#if MONO
    [Header("Storage Components")]
#endif
    public Transform cubbies;
    public Transform bounds;
    public Transform desktop;
    public Transform equipJailSuit;
    public Transform storageWalls;

    // Component references for the stations
    private JailInventoryPickupStation jailInventoryComponent;
    private InventoryPickupStation inventoryPickupComponent;

    /// <summary>Returns whether the storage root and guard point are both assigned.</summary>
    public bool IsValid()
    {
        return storageArea != null && guardPoint != null;
    }

    /// <summary>
    /// Initialize the storage area components
    /// </summary>
    public void InitializeStorageArea()
    {
        if (!IsValid())
        {
            Debug.LogError("Storage area is not valid - missing required components");
            return;
        }

        // Initialize jail inventory pickup station (prison items)
        if (jailInventoryPickup != null)
        {
            jailInventoryComponent = BBHelpers.GetComponentSafe<JailInventoryPickupStation>(jailInventoryPickup.gameObject);
            if (jailInventoryComponent == null)
            {
                jailInventoryComponent = BBHelpers.AddComponentSafe<JailInventoryPickupStation>(jailInventoryPickup.gameObject);
                Debug.Log("Added JailInventoryPickupStation component to JailInventoryPickup");
            }
        }

        // Initialize inventory pickup station (personal items return)
        if (inventoryPickup != null)
        {
            inventoryPickupComponent = BBHelpers.GetComponentSafe<InventoryPickupStation>(inventoryPickup.gameObject);
            if (inventoryPickupComponent == null)
            {
                inventoryPickupComponent = BBHelpers.AddComponentSafe<InventoryPickupStation>(inventoryPickup.gameObject);
                Debug.Log("Added InventoryPickupStation component to InventoryPickup");
            }
        }

        Debug.Log("Storage area components initialized successfully");
    }

    /// <summary>
    /// Get the jail inventory pickup station component (for prison items)
    /// </summary>
    public JailInventoryPickupStation GetJailInventoryPickupStation()
    {
        if (jailInventoryComponent == null && jailInventoryPickup != null)
        {
            jailInventoryComponent = BBHelpers.GetComponentSafe<JailInventoryPickupStation>(jailInventoryPickup.gameObject);
        }
        return jailInventoryComponent;
    }

    /// <summary>
    /// Get the inventory pickup station component (for personal items return)
    /// </summary>
    public InventoryPickupStation GetInventoryPickupStation()
    {
        if (inventoryPickupComponent == null && inventoryPickup != null)
        {
            inventoryPickupComponent = BBHelpers.GetComponentSafe<InventoryPickupStation>(inventoryPickup.gameObject);
        }
        return inventoryPickupComponent;
    }

    /// <summary>
    /// Enable jail inventory pickup for new inmates
    /// </summary>
    public void EnableJailInventoryPickup(Player player)
    {
        var station = GetJailInventoryPickupStation();
        if (station != null)
        {
            station.gameObject.SetActive(true);
            Debug.Log($"Enabled jail inventory pickup for {player.name}");
        }
    }

    /// <summary>
    /// Enable inventory pickup for released inmates
    /// </summary>
    public void EnableInventoryPickup(Player player)
    {
        var station = GetInventoryPickupStation();
        if (station != null)
        {
            station.EnableForRelease(player);
            Debug.Log($"Enabled inventory pickup for release of {player.name}");
        }
    }

    /// <summary>
    /// Check if a player needs prison items
    /// </summary>
    public bool PlayerNeedsPrisonItems(Player player)
    {
        var station = GetJailInventoryPickupStation();
        return station != null && station.NeedsPrisonItems(player);
    }

    /// <summary>
    /// Check if a player has personal items to retrieve
    /// </summary>
    public bool PlayerHasPersonalItems(Player player)
    {
        var station = GetInventoryPickupStation();
        return station != null && station.HasItemsForPlayer(player);
    }
}


