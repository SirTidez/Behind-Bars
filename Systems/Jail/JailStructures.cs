using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Systems.Jail;

[System.Serializable]
public class JailDoor
{
    public Transform doorHolder;
    public GameObject doorInstance;
    public Transform doorHinge;

    public string doorName;
    public DoorType doorType;
    public DoorInteractionType interactionType;
    public DoorState currentState = DoorState.Closed;
    public bool isLocked = false;

    public float openAngle = -135f;  // Door opens to -135 degrees on Z axis
    public float closedAngle = 0f;
    public float animationSpeed = 2f;

    // Private animation state
    private float targetAngle;
    private float currentAngle;
    private bool isAnimating = false;

    public enum DoorType
    {
        CellDoor,
        HoldingCellDoor,
        EntryDoor,
        GuardDoor,
        AreaDoor
    }

    public enum DoorInteractionType
    {
        PassThrough,    // Guard moves through door (Inner, Entry, Guard doors)
        OperationOnly   // Guard only opens/closes door (Cell, Holding doors)
    }

    public enum DoorState
    {
        Closed,
        Opening,
        Open,
        Closing,
        Locked
    }

    public bool IsValid()
    {
        return doorHolder != null;
    }

    public bool IsInstantiated()
    {
        return doorInstance != null;
    }

    public bool IsOpen()
    {
        return currentState == DoorState.Open;
    }

    public bool IsClosed()
    {
        return currentState == DoorState.Closed || currentState == DoorState.Locked;
    }

    public bool IsAnimating()
    {
        return isAnimating;
    }

    public void OpenDoor()
    {
        if (isLocked || currentState == DoorState.Open || currentState == DoorState.Opening)
            return;

        currentState = DoorState.Opening;
        targetAngle = openAngle;
        isAnimating = true;

        Debug.Log($"{doorName}: Opening door");
    }

    public void CloseDoor()
    {
        if (currentState == DoorState.Closed || currentState == DoorState.Closing || currentState == DoorState.Locked)
            return;

        currentState = DoorState.Closing;
        targetAngle = closedAngle;
        isAnimating = true;

        Debug.Log($"{doorName}: Closing door");
    }

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

    public void UnlockDoor()
    {
        if (isLocked)
        {
            isLocked = false;
            currentState = DoorState.Closed;
            Debug.Log($"{doorName}: Door unlocked");
        }
    }

    public void UpdateDoorAnimation(float deltaTime)
    {
        if (!isAnimating || doorHinge == null)
            return;

        // Lerp current angle towards target angle
        currentAngle = Mathf.LerpAngle(currentAngle, targetAngle, animationSpeed * deltaTime);

        // Apply rotation to hinge (on Z axis for your doors)
        doorHinge.localEulerAngles = new Vector3(0, 0, currentAngle);

        // Check if animation is complete
        if (Mathf.Abs(Mathf.DeltaAngle(currentAngle, targetAngle)) < 0.1f)
        {
            currentAngle = targetAngle;
            doorHinge.localEulerAngles = new Vector3(0, 0, currentAngle);
            isAnimating = false;

            // Update state based on final position
            if (Mathf.Approximately(currentAngle, openAngle))
            {
                currentState = DoorState.Open;
                Debug.Log($"{doorName}: Door opened");
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
            }
        }
    }

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

    // Legacy method for compatibility
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

    public bool IsLocked()
    {
        return isLocked;
    }
}

[System.Serializable]
public class SpawnPointOccupancy
{
    public Transform spawnPoint;
    public int spawnIndex;
    public bool isOccupied;
    public string occupantName;
}

[System.Serializable]
public class CellDetail
{
    public Transform cellTransform;
    public Transform cellBounds;
    public JailDoor cellDoor;
    
    // Bed references for sleeping functionality
    public Transform cellBedBottom;
    public Transform cellBedTop;
    public JailBed bedBottomComponent;
    public JailBed bedTopComponent;
    
    // Spawn points for arrested players
    public List<Transform> spawnPoints = new List<Transform>();
    
    // Individual spawn point occupancy tracking (up to 3 per holding cell)
    public List<SpawnPointOccupancy> spawnPointOccupancy = new List<SpawnPointOccupancy>();

    public int cellIndex;
    public string cellName;
    public bool isOccupied = false;
    public string occupantName = "";
    
    // Maximum occupants for this cell (3 for holding cells, 1 for regular cells)
    public int maxOccupants = 1;

    public bool IsValid()
    {
        return cellTransform != null && cellDoor.IsValid();
    }
    
    /// <summary>
    /// Initialize spawn point occupancy tracking
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
                occupantName = null
            });
        }
        
        Debug.Log($"Initialized {spawnPointOccupancy.Count} spawn points for {cellName}");
    }

    /// <summary>
    /// Gets the next available spawn point in this cell
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
    /// Assigns a player to the first available spawn point
    /// </summary>
    /// <param name="playerName">Player to assign</param>
    /// <returns>The spawn point assigned, or null if cell is full</returns>
    public Transform AssignPlayerToSpawnPoint(string playerName)
    {
        if (spawnPointOccupancy.Count > 0)
        {
            // Find first available spawn point
            var availableSpawn = spawnPointOccupancy.Find(sp => !sp.isOccupied);
            if (availableSpawn != null)
            {
                availableSpawn.isOccupied = true;
                availableSpawn.occupantName = playerName;
                
                // Update cell-level occupancy
                UpdateCellOccupancy();
                
                Debug.Log($"Assigned {playerName} to {cellName} spawn point {availableSpawn.spawnIndex}");
                return availableSpawn.spawnPoint;
            }
        }
        else
        {
            // Regular cell behavior
            if (!isOccupied)
            {
                isOccupied = true;
                occupantName = playerName;
                return GetAvailableSpawnPoint();
            }
        }
        
        return null; // Cell is full
    }
    
    /// <summary>
    /// Releases a player from their spawn point
    /// </summary>
    /// <param name="playerName">Player to release</param>
    public void ReleasePlayerFromSpawnPoint(string playerName)
    {
        if (spawnPointOccupancy.Count > 0)
        {
            var occupiedSpawn = spawnPointOccupancy.Find(sp => sp.occupantName == playerName);
            if (occupiedSpawn != null)
            {
                occupiedSpawn.isOccupied = false;
                occupiedSpawn.occupantName = null;
                
                // Update cell-level occupancy
                UpdateCellOccupancy();
                
                Debug.Log($"Released {playerName} from {cellName} spawn point {occupiedSpawn.spawnIndex}");
            }
        }
        else
        {
            // Regular cell behavior
            if (occupantName == playerName)
            {
                isOccupied = false;
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
                occupantName = occupiedSpawns[0].occupantName;
            }
            else
            {
                occupantName = "";
            }
        }
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

    public void OpenCell()
    {
        if (cellDoor.IsValid())
        {
            cellDoor.OpenDoor();
        }
    }

    public void CloseCell()
    {
        if (cellDoor.IsValid())
        {
            cellDoor.CloseDoor();
        }
    }

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
                var prisonBed = cellBedBottom.GetComponent<PrisonBedInteractable>();
                if (prisonBed != null && prisonBed.IsComplete)
                {
                    var jailBed = cellBedBottom.GetComponent<JailBed>();
                    if (jailBed != null)
                        beds.Add(jailBed);
                }
            }

            if (cellBedTop != null)
            {
                var prisonBed = cellBedTop.GetComponent<PrisonBedInteractable>();
                if (prisonBed != null && prisonBed.IsComplete)
                {
                    var jailBed = cellBedTop.GetComponent<JailBed>();
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
            var prisonBed = cellBedBottom.GetComponent<PrisonBedInteractable>();
            if (prisonBed != null && prisonBed.IsComplete)
            {
                var jailBed = cellBedBottom.GetComponent<JailBed>();
                if (jailBed != null)
                    return jailBed;
            }
        }

        if (cellBedTop != null)
        {
            var prisonBed = cellBedTop.GetComponent<PrisonBedInteractable>();
            if (prisonBed != null && prisonBed.IsComplete)
            {
                var jailBed = cellBedTop.GetComponent<JailBed>();
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
            var prisonBed = cellBedBottom.GetComponent<PrisonBedInteractable>();
            if (prisonBed != null)
                beds.Add(prisonBed);
        }

        if (cellBedTop != null)
        {
            var prisonBed = cellBedTop.GetComponent<PrisonBedInteractable>();
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


