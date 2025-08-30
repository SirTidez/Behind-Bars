using System;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class JailDoor
{
    public Transform doorHolder;
    public GameObject doorInstance;
    public Transform doorHinge;

    public string doorName;
    public DoorType doorType;
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
}

[System.Serializable]
public class CellDetail
{
    public Transform cellTransform;
    public Transform cellBounds;
    public JailDoor cellDoor;

    public int cellIndex;
    public string cellName;
    public bool isOccupied = false;
    public string occupantName = "";

    public bool IsValid()
    {
        return cellTransform != null && cellDoor.IsValid();
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
}


