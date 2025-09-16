using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BehindBars.Areas;
using MelonLoader;
using UnityEngine.UI;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;



#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
#endif


#if MONO
public sealed class JailController : MonoBehaviour
#else
public sealed class JailController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
{
    public GameObject jailDoorPrefab;
    public GameObject steelDoorPrefab;

    public GameObject securityCameraPrefab;
    public List<CellDetail> cells = new List<CellDetail>();
    public List<CellDetail> holdingCells = new List<CellDetail>();

    public List<SecurityCamera> securityCameras = new List<SecurityCamera>();

    // Patrol Points - registered once on initialization
    public List<Transform> patrolPoints = new List<Transform>();
    
    /// <summary>
    /// Initialize patrol points by finding them explicitly by name
    /// </summary>
    public void InitializePatrolPoints()
    {
        patrolPoints.Clear();
        
        // Known patrol point names from the jail structure
        string[] patrolPointNames = {
            "Patrol_Upper_Right",
            "Patrol_Upper_Left", 
            "Patrol_Lower_Left",
            "Patrol_Kitchen",
            "Patrol_Laundry"
        };
        
        foreach (string pointName in patrolPointNames)
        {
            Transform patrolPoint = transform.Find(pointName);
            if (patrolPoint == null)
            {
                // Try searching in PatrolPoints container
                Transform patrolContainer = transform.Find("PatrolPoints");
                if (patrolContainer != null)
                {
                    patrolPoint = patrolContainer.Find(pointName);
                }
            }
            
            if (patrolPoint != null)
            {
                patrolPoints.Add(patrolPoint);
                ModLogger.Info($"✓ Registered patrol point: {pointName}");
            }
            else
            {
                ModLogger.Warn($"⚠️  Could not find patrol point: {pointName}");
            }
        }
        
        ModLogger.Info($"✓ Initialized {patrolPoints.Count} patrol points in JailController");
    }

    public KitchenArea kitchen = new KitchenArea();
    public LaundryArea laundry = new LaundryArea();
    public PhoneArea phoneArea = new PhoneArea();
    public BookingArea booking = new BookingArea();
    public GuardRoomArea guardRoom = new GuardRoomArea();
    public MainRecArea mainRec = new MainRecArea();
    public ShowerArea showers = new ShowerArea();

    // Holding cell doors for easy access and testing
    public Transform holdingCellDoor0;     // Alt+4
    public Transform holdingCellDoor1;     // Alt+5

    public List<AreaLighting> areaLights = new List<AreaLighting>();
    public LightingState currentLightingState = LightingState.Normal;

    public bool enableLightingLOD = true;
    public float lightCullingDistance = 50f;
    public int maxRealTimeLights = 20;
    public bool preferBakedLighting = true;

    // Emissive Material Control
    public Material emissiveMaterial;
    public List<Material> allEmissiveMaterials = new List<Material>();
    public string emissiveMaterialName = "M_LightEmissive";
    public bool enableEmissiveControl = true;
    
    // Emissive colors for different lighting states
    public Color emissiveNormalColor = Color.white;
    public Color emissiveEmergencyColor = Color.red;
    public Color emissiveBlackoutColor = Color.black;
    
    // Emissive intensities for different lighting states
    public float emissiveNormalIntensity = 1.0f;
    public float emissiveEmergencyIntensity = 0.8f;
    public float emissiveBlackoutIntensity = 0.0f;

    [System.Serializable]
    public class AreaLighting
    {
        public string areaName;
        public Transform lightsParent;

        public List<Light> lights = new List<Light>();

        public bool isOn = true;
        public float normalIntensity = 1f;
        public float emergencyIntensity = 0.3f;
        public Color normalColor = Color.white;
        public Color emergencyColor = Color.red;

        public List<Light> realTimeLights = new List<Light>();
        public List<Light> bakedLights = new List<Light>();
        public bool isPlayerNearby = true;

        public void SetLightingState(LightingState state)
        {
            switch (state)
            {
                case LightingState.Normal:
                    SetLights(true, normalIntensity, normalColor);
                    break;
                case LightingState.Emergency:
                    SetLights(true, emergencyIntensity, emergencyColor);
                    break;
                case LightingState.Blackout:
                    SetLights(false, 0f, normalColor);
                    break;
            }
        }

        public void SetLights(bool enabled, float intensity, Color color)
        {
            isOn = enabled;

            // Only update real-time lights if player is nearby or lights are essential
            foreach (var light in realTimeLights)
            {
                if (light != null && (isPlayerNearby || enabled))
                {
                    light.enabled = enabled;
                    light.intensity = intensity;
                    light.color = color;
                }
            }

            // Legacy support - update all lights in main list
            foreach (var light in lights)
            {
                if (light != null && !realTimeLights.Contains(light) && !bakedLights.Contains(light))
                {
                    light.enabled = enabled;
                    light.intensity = intensity;
                    light.color = color;
                }
            }
        }

        public void ToggleLights()
        {
            isOn = !isOn;
            foreach (var light in lights)
            {
                if (light != null)
                {
                    light.enabled = isOn;
                }
            }
        }
    }

    public enum LightingState
    {
        Normal,      // Full lighting
        Emergency,   // Dim red lighting  
        Blackout     // All lights off
    }

    public List<MonitorAssignment> monitorAssignments = new List<MonitorAssignment>();

    [System.Serializable]
    public class MonitorAssignment
    {
        public MonitorController monitor;

        public List<SecurityCamera> availableCameras = new List<SecurityCamera>();

        public bool autoRotate = false;
        public float rotationInterval = 10f;


        public int currentCameraIndex = 0;
        public float lastRotationTime = 0f;

        public SecurityCamera GetCurrentCamera()
        {
            if (availableCameras.Count == 0) return null;
            currentCameraIndex = Mathf.Clamp(currentCameraIndex, 0, availableCameras.Count - 1);
            return availableCameras[currentCameraIndex];
        }

        public SecurityCamera GetNextCamera()
        {
            if (availableCameras.Count == 0) return null;
            currentCameraIndex = (currentCameraIndex + 1) % availableCameras.Count;
            return availableCameras[currentCameraIndex];
        }

        public SecurityCamera GetNextAvailableCamera(List<SecurityCamera> camerasInUse)
        {
            if (availableCameras.Count == 0) return null;

            // Find a camera not currently in use by other monitors
            int attempts = 0;
            int startIndex = currentCameraIndex;

            do
            {
                currentCameraIndex = (currentCameraIndex + 1) % availableCameras.Count;
                SecurityCamera candidate = availableCameras[currentCameraIndex];

                // If this camera is not in use by other monitors, use it
                if (!camerasInUse.Contains(candidate))
                {
                    return candidate;
                }

                attempts++;
            } while (attempts < availableCameras.Count && currentCameraIndex != startIndex);

            // Fallback: if all cameras are in use, just rotate normally
            return availableCameras[currentCameraIndex];
        }

        public SecurityCamera GetPreviousCamera()
        {
            if (availableCameras.Count == 0) return null;
            currentCameraIndex = (currentCameraIndex - 1 + availableCameras.Count) % availableCameras.Count;
            return availableCameras[currentCameraIndex];
        }
    }

    public bool guardsCanControlDoors = true;
    public KeyCode emergencyLockdownKey = KeyCode.L;
    public KeyCode unlockAllKey = KeyCode.U;
    public KeyCode openAllCellsKey = KeyCode.O;
    public KeyCode closeAllCellsKey = KeyCode.C;
    public KeyCode blackoutKey = KeyCode.B;

    public float cameraDownwardAngle = 15f;

    public bool showDebugInfo = false;

    private bool testAllCellsOpen = false;
    private bool testAllCellsLocked = false;
    private bool testHoldingAreaLocked = false;
    private bool testEmergencyLockdown = false;

    void Start()
    {
        InitializeJail();
    }

    void Update()
    {
        // Update door animations
        UpdateDoorAnimations();

        // Update monitor rotations
        UpdateMonitorRotations();

        // Update lighting LOD system
        if (enableLightingLOD)
        {
            UpdateLightingLOD();
        }

        // Guard control inputs (for testing)
        if (Input.GetKeyDown(emergencyLockdownKey))
        {
            EmergencyLockdown();
        }

        if (Input.GetKeyDown(unlockAllKey))
        {
            UnlockAll();
        }

        if (Input.GetKeyDown(openAllCellsKey))
        {
            OpenAllCells();
        }

        if (Input.GetKeyDown(closeAllCellsKey))
        {
            CloseAllCells();
        }
        
        if (Input.GetKeyDown(blackoutKey))
        {
            Blackout();
        }
        
        // Door testing keyboard shortcuts
        HandleDoorKeyboardShortcuts();
    }
    
    /// <summary>
    /// Handle keyboard shortcuts for door testing
    /// </summary>
    void HandleDoorKeyboardShortcuts()
    {
        // Alt+1: Prison Enter Door
        if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.Alpha1))
        {
            ToggleBookingDoor(booking.prisonEntryDoor, "Prison Enter Door");
        }
        
        // Alt+2: Booking Inner Door  
        if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.Alpha2))
        {
            ToggleBookingDoor(booking.bookingInnerDoor, "Booking Inner Door");
        }
        
        // Alt+3: Guard Door
        if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.Alpha3))
        {
            ToggleBookingDoor(booking.guardDoor, "Guard Door");
        }
        
        // Alt+4: Holding Cell Door 0
        if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.Alpha4))
        {
            ToggleDoor(holdingCellDoor0, "Holding Cell Door 0");
        }
        
        // Alt+5: Holding Cell Door 1
        if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.Alpha5))
        {
            ToggleDoor(holdingCellDoor1, "Holding Cell Door 1");
        }
    }
    
    /// <summary>
    /// Toggle a booking area door open/closed with logging
    /// </summary>
    void ToggleBookingDoor(JailDoor jailDoor, string doorName)
    {
        if (jailDoor == null)
        {
            ModLogger.Warn($"{doorName} not found in booking area - cannot toggle");
            return;
        }
        
        if (!jailDoor.IsInstantiated())
        {
            ModLogger.Warn($"{doorName} not instantiated - cannot toggle");
            return;
        }
        
        if (jailDoor.IsOpen())
        {
            jailDoor.CloseDoor();
            ModLogger.Info($"Closed {doorName}");
        }
        else
        {
            jailDoor.OpenDoor();
            ModLogger.Info($"Opened {doorName}");
        }
    }
    
    /// <summary>
    /// Toggle a door open/closed with logging
    /// </summary>
    void ToggleDoor(Transform doorTransform, string doorName)
    {
        if (doorTransform == null)
        {
            ModLogger.Warn($"{doorName} not assigned - cannot toggle");
            return;
        }
        
        // First, try to find the door in our door collections
        JailDoor jailDoor = FindJailDoor(doorTransform);
        if (jailDoor != null)
        {
            if (jailDoor.IsOpen())
            {
                jailDoor.CloseDoor();
                ModLogger.Info($"Closed {doorName} via JailDoor");
            }
            else
            {
                jailDoor.OpenDoor();
                ModLogger.Info($"Opened {doorName} via JailDoor");
            }
            return;
        }
        
        ModLogger.Warn($"{doorName} not found in door collections - logging position for manual assignment");
        ModLogger.Info($"{doorName} located at: {doorTransform.position} (Path: {GetGameObjectPath(doorTransform)})");
    }
    
    /// <summary>
    /// Find a JailDoor that matches the given transform
    /// </summary>
    JailDoor FindJailDoor(Transform doorTransform)
    {
        // Check all cell doors
        foreach (var cell in cells)
        {
            if (cell.cellDoor?.doorHolder == doorTransform || 
                cell.cellDoor?.doorInstance?.transform == doorTransform)
            {
                return cell.cellDoor;
            }
        }
        
        // Check all holding cell doors  
        foreach (var holdingCell in holdingCells)
        {
            if (holdingCell.cellDoor?.doorHolder == doorTransform || 
                holdingCell.cellDoor?.doorInstance?.transform == doorTransform)
            {
                return holdingCell.cellDoor;
            }
        }
        
        // Check area doors (booking, guard room, etc)
        var allAreaDoors = new List<JailDoor>();
        if (booking.doors != null) allAreaDoors.AddRange(booking.doors);
        if (guardRoom.doors != null) allAreaDoors.AddRange(guardRoom.doors);
        
        foreach (var door in allAreaDoors)
        {
            if (door?.doorHolder == doorTransform || 
                door?.doorInstance?.transform == doorTransform)
            {
                return door;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Get the full hierarchy path of a GameObject for debugging
    /// </summary>
    string GetGameObjectPath(Transform transform)
    {
        string path = transform.name;
        while (transform.parent != null)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }
        return path;
    }
    
    /// <summary>
    /// Initialize holding cell door references for keyboard shortcuts
    /// </summary>
    void InitializeHoldingCellDoorReferences()
    {
        // Find holding cell doors for keyboard shortcuts
        Transform holdingCells = transform.Find("HoldingCells");
        if (holdingCells != null)
        {
            Transform holdingCell0 = holdingCells.Find("HoldingCell_00");
            if (holdingCell0 != null)
                holdingCellDoor0 = holdingCell0.Find("HoldingDoorHolder[0]");
                
            Transform holdingCell1 = holdingCells.Find("HoldingCell_01");  
            if (holdingCell1 != null)
                holdingCellDoor1 = holdingCell1.Find("HoldingDoorHolder[1]");
        }
        
        ModLogger.Info($"Holding Cell Door 0: {(holdingCellDoor0 != null ? "✓ Found" : "✗ Missing")}");
        ModLogger.Info($"Holding Cell Door 1: {(holdingCellDoor1 != null ? "✓ Found" : "✗ Missing")}");
    }
    
    Transform FindDoorByName(params string[] possibleNames)
    {
        // Search in the entire jail hierarchy
        Transform[] allTransforms = GetComponentsInChildren<Transform>();
        
        foreach (string doorName in possibleNames)
        {
            foreach (Transform t in allTransforms)
            {
                // Exact name match for precision
                if (t.name == doorName)
                {
                    ModLogger.Debug($"Found door by exact name match: {doorName}");
                    return t;
                }
            }
        }
        
        // If exact match fails, log what we did find for debugging
        ModLogger.Debug($"Exact name match failed. Available transforms containing searched terms:");
        foreach (string doorName in possibleNames)
        {
            foreach (Transform t in allTransforms)
            {
                if (t.name.ToLower().Contains(doorName.ToLower()))
                {
                    ModLogger.Debug($"  Similar: {t.name} (contains '{doorName}')");
                }
            }
        }
        
        return null;
    }
    
    Transform FindDoorInHoldingCell(int holdingCellIndex)
    {
        var holdingCell = holdingCells.FirstOrDefault(c => c.cellIndex == holdingCellIndex);
        if (holdingCell?.cellTransform != null)
        {
            // Look for door in the holding cell structure
            Transform door = holdingCell.cellTransform.Find("HoldingDoorHolder[0]");
            if (door != null) return door;
            
            // Alternative door names
            string[] doorNames = {
                "Door",
                "HoldingDoor", 
                "CellDoor",
                $"HoldingCell_{holdingCellIndex}_Door"
            };
            
            foreach (string doorName in doorNames)
            {
                door = FindChildRecursive(holdingCell.cellTransform, doorName);
                if (door != null) return door;
            }
        }
        
        return null;
    }

    void UpdateDoorAnimations()
    {
        float deltaTime = Time.deltaTime;

        // Update all cell doors
        foreach (var cell in cells)
        {
            if (cell.cellDoor.IsInstantiated())
            {
                cell.cellDoor.UpdateDoorAnimation(deltaTime);
            }
        }

        // Update all holding cell doors
        foreach (var holdingCell in holdingCells)
        {
            if (holdingCell.cellDoor.IsInstantiated())
            {
                holdingCell.cellDoor.UpdateDoorAnimation(deltaTime);
            }
        }

        // Update booking area doors
        if (booking.prisonEntryDoor != null && booking.prisonEntryDoor.IsInstantiated())
        {
            booking.prisonEntryDoor.UpdateDoorAnimation(deltaTime);
        }

        if (booking.guardDoor != null && booking.guardDoor.IsInstantiated())
        {
            booking.guardDoor.UpdateDoorAnimation(deltaTime);
        }

        foreach (var door in booking.doors)
        {
            if (door.IsInstantiated())
            {
                door.UpdateDoorAnimation(deltaTime);
            }
        }
    }

    void UpdateMonitorRotations()
    {
        float currentTime = Time.time;

        foreach (var assignment in monitorAssignments)
        {
            if (assignment.autoRotate && assignment.availableCameras.Count > 1)
            {
                if (currentTime - assignment.lastRotationTime >= assignment.rotationInterval)
                {
                    RotateMonitorCamera(assignment);
                    assignment.lastRotationTime = currentTime;
                }
            }
        }
    }

    void RotateMonitorCamera(MonitorAssignment assignment)
    {
        // Get list of cameras currently displayed on other monitors
        List<SecurityCamera> camerasInUse = GetCamerasCurrentlyInUse(assignment);

        // Get next available camera that's not in use
        SecurityCamera nextCamera = assignment.GetNextAvailableCamera(camerasInUse);

        if (nextCamera != null && assignment.monitor != null)
        {
            SetMonitorCamera(assignment.monitor, nextCamera);
            if (showDebugInfo)
            {
                Debug.Log($"Auto-rotated monitor {assignment.monitor.name} to camera {nextCamera.cameraName} (avoiding {camerasInUse.Count} cameras in use)");
            }
        }
    }

    List<SecurityCamera> GetCamerasCurrentlyInUse(MonitorAssignment excludeAssignment)
    {
        List<SecurityCamera> camerasInUse = new List<SecurityCamera>();

        foreach (var assignment in monitorAssignments)
        {
            // Don't include the assignment we're rotating
            if (assignment == excludeAssignment) continue;

            SecurityCamera currentCamera = assignment.GetCurrentCamera();
            if (currentCamera != null)
            {
                camerasInUse.Add(currentCamera);
            }
        }

        return camerasInUse;
    }

    public void SetMonitorCamera(MonitorController monitor, SecurityCamera camera)
    {
        if (monitor == null || camera == null)
        {
            Debug.LogWarning($"SetMonitorCamera: monitor={monitor != null}, camera={camera != null}");
            return;
        }

        // Ensure the camera has a render texture
        if (camera.renderTexture == null)
        {
            Debug.LogWarning($"Camera {camera.cameraName} has no render texture! Creating one...");
            // Force the SecurityCamera to setup its render texture
            camera.SetupRenderTexture();
        }

#if MONO
        // Mono-specific: Additional validation and setup
        if (camera.renderTexture != null && !camera.renderTexture.IsCreated())
        {
            camera.renderTexture.Create();
            Debug.Log($"Mono: Force-created render texture for {camera.cameraName}");
        }
        
        // Ensure the camera component is properly configured
        if (camera.cameraComponent != null)
        {
            camera.cameraComponent.enabled = false;
            camera.cameraComponent.targetTexture = camera.renderTexture;
            camera.cameraComponent.enabled = true;
        }
        
        // Wait a frame for Mono to process the texture assignment
        MelonCoroutines.Start(SetMonitorCameraDelayed(monitor, camera));
#else
        // Force the camera to render at least once
        if (camera.cameraComponent != null)
        {
            camera.cameraComponent.Render();
        }

        // Set the camera reference on the monitor
        monitor.SetCamera(camera);

        // Force the texture assignment to the RawImage
        if (camera.renderTexture != null && monitor.screenImage != null)
        {
            monitor.screenImage.texture = camera.renderTexture;
            Debug.Log($"✓ Monitor {monitor.name} → {camera.cameraName} (texture: {camera.renderTexture.width}x{camera.renderTexture.height})");
        }
#endif
    }

#if MONO
    private IEnumerator SetMonitorCameraDelayed(MonitorController monitor, SecurityCamera camera)
    {
        Debug.Log($"Mono: Starting delayed camera assignment for monitor {monitor.name}");
        
        // Wait a frame for Mono to process changes
        yield return null;
        
        Debug.Log($"Mono: Processing delayed assignment - Camera: {camera.cameraName}, RenderTexture: {camera.renderTexture != null}");
        
        // Force the camera to render at least once
        if (camera.cameraComponent != null)
        {
            camera.cameraComponent.Render();
            Debug.Log($"Mono: Forced camera render for {camera.cameraName}");
        }

        // Set the camera reference on the monitor
        monitor.SetCamera(camera);
        Debug.Log($"Mono: Set camera reference on monitor {monitor.name}");

        // Force the texture assignment to the RawImage
        if (camera.renderTexture != null && monitor.screenImage != null)
        {
            monitor.screenImage.texture = camera.renderTexture;
            Debug.Log($"✓ Mono Monitor {monitor.name} → {camera.cameraName} (texture: {camera.renderTexture.width}x{camera.renderTexture.height}) - ASSIGNMENT COMPLETE");
        }
        else
        {
            Debug.LogError($"Mono: Failed to assign texture: Camera.renderTexture={camera.renderTexture != null}, Monitor.screenImage={monitor.screenImage != null}");
        }
    }
#endif

    public void InitializeJail()
    {
        DiscoverJailStructure();
        SetupDoors();
        SetupCellBeds(); // Set up bed functionality
        SetupSecurityCameras();
        SetupMonitorAssignments(); // Add monitor setup
        
        // Initialize patrol points for NPCs
        InitializePatrolPoints();
        
        // Initialize static spawn points for holding cells
        InitializeHoldingCellSpawnPoints();
        
        // Initialize holding cell door references for keyboard shortcuts
        InitializeHoldingCellDoorReferences();
        
        // Find and cache emissive material for lighting control
        FindEmissiveMaterial();

        if (showDebugInfo)
        {
            LogJailStatus();
        }
    }

    void DiscoverJailStructure()
    {
        DiscoverCells();
        DiscoverHoldingCells();
        DiscoverSecurityCameraPositions();
        DiscoverAreaLighting();
        InitializeJailAreas();
    }

    void DiscoverCells()
    {
        cells.Clear();
        Transform cellsParent = transform.Find("Cells");
        if (cellsParent == null)
        {
            Debug.LogWarning("Cells parent folder not found!");
            return;
        }

        Debug.Log($"Found Cells parent with {cellsParent.childCount} children");

        // Scan all direct children of Cells folder
        for (int j = 0; j < cellsParent.childCount; j++)
        {
            Transform cellTransform = cellsParent.GetChild(j);

            // Check if this looks like a cell (contains "Cell" in name)
            if (!cellTransform.name.Contains("Cell"))
            {
                Debug.Log($"Skipping {cellTransform.name} - doesn't contain 'Cell'");
                continue;
            }

            Debug.Log($"Processing cell: {cellTransform.name}");

            // Create cell detail
            CellDetail cell = new CellDetail();
            cell.cellTransform = cellTransform;
            cell.cellIndex = j; // Use order in hierarchy
            cell.cellName = cellTransform.name.Replace("_", " "); // "Cell_00" -> "Cell 00"

            // Find door holder - look for any child with "DoorHolder" in name
            cell.cellDoor = new JailDoor();
            cell.cellDoor.doorHolder = FindDoorHolder(cellTransform, "DoorHolder");
            cell.cellDoor.doorName = $"{cell.cellName} Door";
            cell.cellDoor.doorType = JailDoor.DoorType.CellDoor;

            // Find cell bounds - look for any child with "Bounds" in name
            cell.cellBounds = FindChildContaining(cellTransform, "Bounds");
            
            // Find cell beds - look for CellBedBottom and CellBedTop
            cell.cellBedBottom = FindChildContaining(cellTransform, "CellBedBottom");
            cell.cellBedTop = FindChildContaining(cellTransform, "CellBedTop");
            
            // Find spawn points - look for children with "Spawn" in name
            cell.spawnPoints = FindAllChildrenContaining(cellTransform, "Spawn");

            Debug.Log($"Cell setup: DoorHolder={cell.cellDoor.doorHolder != null}, Bounds={cell.cellBounds != null}, Beds={cell.cellBedBottom != null}/{cell.cellBedTop != null}, SpawnPoints={cell.spawnPoints.Count}");

            if (cell.IsValid())
            {
                cells.Add(cell);
                Debug.Log($"✓ Successfully added {cell.cellName}");
            }
            else
            {
                Debug.LogWarning($"✗ Cell {cellTransform.name} is not valid - missing door holder");
            }
        }

        Debug.Log($"Discovered {cells.Count} prison cells total");
    }

    void DiscoverHoldingCells()
    {
        holdingCells.Clear();
        Transform holdingCellsParent = transform.Find("HoldingCells");
        if (holdingCellsParent == null)
        {
            Debug.LogWarning("HoldingCells parent not found!");
            return;
        }

        Debug.Log($"Found HoldingCells parent with {holdingCellsParent.childCount} children");

        // Log all child names for debugging
        for (int j = 0; j < holdingCellsParent.childCount; j++)
        {
            Transform child = holdingCellsParent.GetChild(j);
            Debug.Log($"HoldingCells child [{j}]: {child.name}");
        }

        // Scan all direct children of HoldingCells folder
        for (int j = 0; j < holdingCellsParent.childCount; j++)
        {
            Transform holdingCellTransform = holdingCellsParent.GetChild(j);

            // Check if this looks like a holding cell (contains "HoldingCell" in name)
            if (!holdingCellTransform.name.Contains("HoldingCell"))
            {
                Debug.Log($"Skipping {holdingCellTransform.name} - doesn't contain 'HoldingCell'");
                continue;
            }

            Debug.Log($"Processing potential holding cell: {holdingCellTransform.name}");

            // Log children for debugging
            Debug.Log($"  {holdingCellTransform.name} has {holdingCellTransform.childCount} children:");
            for (int k = 0; k < holdingCellTransform.childCount; k++)
            {
                Transform child = holdingCellTransform.GetChild(k);
                Debug.Log($"    Child [{k}]: {child.name}");
            }

            // Create holding cell detail
            CellDetail holdingCell = new CellDetail();
            holdingCell.cellTransform = holdingCellTransform;
            holdingCell.cellIndex = j; // Use order in hierarchy
            holdingCell.cellName = holdingCellTransform.name.Replace("_", " "); // "HoldingCell_00" -> "HoldingCell 00"

            // Find door holder - look for any child with "DoorHolder" in name
            holdingCell.cellDoor = new JailDoor();
            holdingCell.cellDoor.doorHolder = FindDoorHolder(holdingCellTransform, "DoorHolder");
            holdingCell.cellDoor.doorName = $"{holdingCell.cellName} Door";
            holdingCell.cellDoor.doorType = JailDoor.DoorType.HoldingCellDoor;

            // Find cell bounds - look for any child with "Bounds" in name
            holdingCell.cellBounds = FindChildContaining(holdingCellTransform, "Bounds");
            
            // Find exact spawn points - should be HoldingCellSpawn[0], [1], [2]
            holdingCell.spawnPoints.Clear();
            for (int spawnIndex = 0; spawnIndex < 3; spawnIndex++)
            {
                Transform spawnPoint = holdingCellTransform.Find($"HoldingCellSpawn[{spawnIndex}]");
                if (spawnPoint != null)
                {
                    holdingCell.spawnPoints.Add(spawnPoint);
                    Debug.Log($"  Found spawn point {spawnIndex}: {spawnPoint.name}");
                }
                else
                {
                    Debug.LogWarning($"  Missing spawn point {spawnIndex} for {holdingCellTransform.name}");
                }
            }
            
            // Set up holding cell properties
            holdingCell.maxOccupants = 3; // Holding cells can hold up to 3 people
            holdingCell.InitializeSpawnPointOccupancy();

            Debug.Log($"Holding cell setup: DoorHolder={holdingCell.cellDoor.doorHolder != null}, Bounds={holdingCell.cellBounds != null}, SpawnPoints={holdingCell.spawnPoints.Count}/3");

            if (holdingCell.IsValid())
            {
                holdingCells.Add(holdingCell);
                Debug.Log($"✓ Successfully added {holdingCell.cellName} with {holdingCell.spawnPoints.Count} spawn points");
            }
            else
            {
                Debug.LogWarning($"✗ Holding cell {holdingCellTransform.name} is not valid - missing door holder");
            }
        }

        Debug.Log($"Pattern-based discovery completed. Found {holdingCells.Count} holding cells.");

        // Fallback: search for any child containing "HoldingCell" if no cells found
        if (holdingCells.Count == 0)
        {
            Debug.Log("No holding cells found via patterns. Trying fallback search...");
            for (int j = 0; j < holdingCellsParent.childCount; j++)
            {
                Transform child = holdingCellsParent.GetChild(j);
                if (child.name.Contains("HoldingCell"))
                {
                    Debug.Log($"Fallback: examining {child.name}");

                    // Try to find the actual holding cell inside
                    Transform actualCell = null;

                    // Try common child names
                    string[] cellNames = { "HoldingCell", "Cell", "Holding" };
                    foreach (string cellName in cellNames)
                    {
                        // Try with brackets
                        for (int k = 0; k < 10; k++)
                        {
                            actualCell = child.Find($"{cellName}[{k}]");
                            if (actualCell != null) break;
                        }
                        if (actualCell != null) break;

                        // Try without brackets
                        actualCell = child.Find(cellName);
                        if (actualCell != null) break;
                    }

                    // If no specific child found, use the parent itself
                    if (actualCell == null)
                    {
                        actualCell = child;
                        Debug.Log($"Using {child.name} directly as holding cell");
                    }

                    if (actualCell != null)
                    {
                        CellDetail holdingCell = new CellDetail();
                        holdingCell.cellTransform = actualCell;
                        holdingCell.cellIndex = holdingCells.Count;
                        holdingCell.cellName = $"Holding Cell {holdingCells.Count}";

                        // Setup door
                        holdingCell.cellDoor = new JailDoor();
                        holdingCell.cellDoor.doorHolder = FindDoorHolder(actualCell, "DoorHolder");
                        if (holdingCell.cellDoor.doorHolder == null)
                        {
                            holdingCell.cellDoor.doorHolder = FindDoorHolder(child, "DoorHolder");
                        }
                        holdingCell.cellDoor.doorName = $"Holding Cell {holdingCells.Count} Door";
                        holdingCell.cellDoor.doorType = JailDoor.DoorType.HoldingCellDoor;

                        holdingCells.Add(holdingCell);
                        Debug.Log($"Fallback: Added holding cell from {child.name}");
                    }
                }
            }
        }

        Debug.Log($"DiscoverHoldingCells completed. Found {holdingCells.Count} holding cells.");
    }

    void DiscoverAreaLighting()
    {
        areaLights.Clear();
        Transform lightsParent = transform.Find("Lights");

        if (lightsParent == null)
        {
            Debug.LogWarning("Lights parent folder not found! Expected: JailRoot/Lights/");
            return;
        }

        Debug.Log($"Found Lights parent with {lightsParent.childCount} area lighting groups");

        // Scan all area lighting folders
        for (int i = 0; i < lightsParent.childCount; i++)
        {
            Transform areaLightingParent = lightsParent.GetChild(i);

            Debug.Log($"Processing area lighting: {areaLightingParent.name}");

            // Create area lighting
            AreaLighting areaLighting = new AreaLighting();
            areaLighting.areaName = areaLightingParent.name;
            areaLighting.lightsParent = areaLightingParent;

            // Find all light components in this area
            Light[] lightsInArea = areaLightingParent.GetComponentsInChildren<Light>();
            areaLighting.lights.AddRange(lightsInArea);

            // Store original light settings
            if (lightsInArea.Length > 0)
            {
                areaLighting.normalIntensity = lightsInArea[0].intensity;
                areaLighting.normalColor = lightsInArea[0].color;
            }

            areaLights.Add(areaLighting);

            // Limit real-time lights for performance (do this after adding to avoid divide by zero)
            int estimatedTotalAreas = lightsParent.childCount; // Use total child count instead of current areaLights.Count
            int maxRealTimePerArea = System.Math.Max(1, maxRealTimeLights / System.Math.Max(1, estimatedTotalAreas));

            if (areaLighting.realTimeLights.Count > maxRealTimePerArea)
            {
                var excessLights = areaLighting.realTimeLights.Skip(maxRealTimePerArea).ToList();

                foreach (var excessLight in excessLights)
                {
                    excessLight.enabled = false; // Disable excess lights
                    areaLighting.realTimeLights.Remove(excessLight);
                    areaLighting.bakedLights.Add(excessLight);
                }

                Debug.Log($"⚠️ Limited {areaLighting.areaName} to {maxRealTimePerArea} real-time lights for performance");
            }

            Debug.Log($"✓ Added area lighting: {areaLighting.areaName} with {areaLighting.lights.Count} lights ({areaLighting.realTimeLights.Count} RT, {areaLighting.bakedLights.Count} baked)");
        }

        Debug.Log($"Discovered {areaLights.Count} area lighting groups with {areaLights.Sum(a => a.lights.Count)} total lights");
    }

    void InitializeJailAreas()
    {
        Debug.Log("=== INITIALIZING JAIL AREAS ===");

        // Initialize each area with its corresponding transform
        Transform kitchenRoot = transform.Find("Kitchen");
        if (kitchenRoot != null)
        {
            kitchen.Initialize(kitchenRoot);
        }
        else
        {
            Debug.LogWarning("Kitchen area not found at Kitchen/");
        }

        Transform laundryRoot = transform.Find("Laundry");
        if (laundryRoot != null)
        {
            laundry.Initialize(laundryRoot);
        }
        else
        {
            Debug.LogWarning("Laundry area not found at Laundry/");
        }

        Transform phoneRoot = transform.Find("Phones");
        if (phoneRoot != null)
        {
            phoneArea.Initialize(phoneRoot);
        }
        else
        {
            Debug.LogWarning("Phone area not found at Phones/");
        }

        Transform bookingRoot = transform.Find("Booking");
        if (bookingRoot != null)
        {
            booking.Initialize(bookingRoot);
        }
        else
        {
            Debug.LogWarning("Booking area not found at Booking/");
        }

        Transform guardRoot = transform.Find("GuardRoom");
        if (guardRoot != null)
        {
            guardRoom.Initialize(guardRoot);
        }
        else
        {
            Debug.LogWarning("Guard room not found at GuardRoom/");
        }

        Transform mainRecRoot = transform.Find("MainRec");
        if (mainRecRoot != null)
        {
            mainRec.Initialize(mainRecRoot);
        }
        else
        {
            Debug.LogWarning("Main rec area not found at MainRec/");
        }

        Transform showerRoot = transform.Find("Showers");
        if (showerRoot != null)
        {
            showers.Initialize(showerRoot);
        }
        else
        {
            Debug.LogWarning("Shower area not found at Showers/");
        }

        // Count initialized areas
        int initializedCount = 0;
        JailAreaBase[] allAreas = { kitchen, laundry, phoneArea, booking, guardRoom, mainRec, showers };

        foreach (var area in allAreas)
        {
            if (area.isInitialized) initializedCount++;
        }

        Debug.Log($"✓ Initialized {initializedCount} out of {allAreas.Length} jail areas");
    }

    void UpdateLightingLOD()
    {
        // Find player position (update this less frequently for performance)
        if (Time.frameCount % 30 != 0) return; // Only update every 30 frames (~0.5 seconds)

        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return;

        Vector3 playerPosition = player.transform.position;

        foreach (var areaLighting in areaLights)
        {
            if (areaLighting.lightsParent == null) continue;

            float distanceToArea = Vector3.Distance(playerPosition, areaLighting.lightsParent.position);
            bool wasNearby = areaLighting.isPlayerNearby;
            areaLighting.isPlayerNearby = distanceToArea <= lightCullingDistance;

            // Only update if player proximity changed
            if (wasNearby != areaLighting.isPlayerNearby)
            {
                UpdateAreaLightingLOD(areaLighting, areaLighting.isPlayerNearby);
            }
        }
    }

    void UpdateAreaLightingLOD(AreaLighting areaLighting, bool playerNearby)
    {
        // Enable/disable real-time lights based on player proximity
        foreach (var light in areaLighting.realTimeLights)
        {
            if (light != null)
            {
                // Always keep essential lights on during emergency
                bool isEssential = currentLightingState == LightingState.Emergency;
                light.enabled = playerNearby || isEssential ? areaLighting.isOn : false;

                // Reduce quality when player is far away
                if (!playerNearby && light.enabled)
                {
                    light.shadows = LightShadows.None;
                    light.renderMode = LightRenderMode.ForceVertex; // Cheaper rendering
                }
                else if (playerNearby && light.enabled)
                {
                    light.renderMode = LightRenderMode.Auto; // Better quality when nearby
                }
            }
        }

        if (showDebugInfo)
        {
            Debug.Log($"💡 LOD: {areaLighting.areaName} lights {(playerNearby ? "enabled" : "culled")} (distance culling)");
        }
    }



    void DiscoverSecurityCameraPositions()
    {
        securityCameras.Clear();
        Transform securityCameraParent = transform.Find("SecurityCameras");
        if (securityCameraParent == null) return;

#if MONO
        foreach (Transform child in securityCameraParent)
        {
            if (child.name.Contains("Security Camera"))
            {
                if (showDebugInfo)
                    Debug.Log($"Found security camera position: {child.name}");

                SetupSecurityCameras(child);
            }
        }
#else
        // IL2CPP-safe iteration
        for (int i = 0; i < securityCameraParent.childCount; i++)
        {
            Transform child = securityCameraParent.GetChild(i);
            if (child.name.Contains("Security Camera"))
            {
                if (showDebugInfo)
                    Debug.Log($"Found security camera position: {child.name}");

                SetupSecurityCameras(child);
            }
        }
#endif
    }

    void SetupSecurityCameras(Transform cameraPosition)
    {
        // Check if there's already a SecurityCamera component
        SecurityCamera existingCamera = cameraPosition.GetComponent<SecurityCamera>();
        if (existingCamera != null)
        {
            Debug.Log($"SecurityCamera component already exists at {cameraPosition.name}");
            securityCameras.Add(existingCamera);
            return;
        }

        // Add SecurityCamera component directly to the transform
        SecurityCamera securityCamera = cameraPosition.gameObject.AddComponent<SecurityCamera>();

        // Configure the camera based on its name
        ConfigureSecurityCamera(securityCamera, cameraPosition.name);

        securityCameras.Add(securityCamera);
        Debug.Log($"✓ Added SecurityCamera component to: {cameraPosition.name}");
    }

    void ConfigureSecurityCamera(SecurityCamera camera, string cameraName)
    {
        // Set camera name for identification
        camera.cameraName = cameraName;

        // Set downward viewing angle
        camera.downwardAngle = cameraDownwardAngle;

        // Determine camera type based on name
        if (cameraName.Contains("Front") || cameraName.Contains("Back"))
        {
            camera.cameraType = SecurityCamera.CameraType.MainView;
            Debug.Log($"Configured {cameraName} as MainView camera");
        }
        else if (cameraName.Contains("Phone"))
        {
            camera.cameraType = SecurityCamera.CameraType.PhoneArea;
            Debug.Log($"Configured {cameraName} as PhoneArea camera");
        }
        else if (cameraName.Contains("Holding"))
        {
            camera.cameraType = SecurityCamera.CameraType.HoldingCell;
            Debug.Log($"Configured {cameraName} as HoldingCell camera");
        }
        else if (cameraName.Contains("Hall"))
        {
            camera.cameraType = SecurityCamera.CameraType.Hall;
            Debug.Log($"Configured {cameraName} as Hall camera");
        }
        else
        {
            camera.cameraType = SecurityCamera.CameraType.Other;
            Debug.Log($"Configured {cameraName} as Other camera");
        }

        // Performance settings
        camera.renderTextureSize = 128;  // Keep low for performance
        camera.targetFramerate = 5f;    // 5fps for security cameras

        // Fix camera head rendering in own view - exclude SecurityCamera layer or similar
        if (camera.cameraComponent != null)
        {
            // Exclude the camera's own transform from rendering
            // This prevents the camera head/mount from appearing in the view
            camera.cameraComponent.cullingMask = ~(1 << camera.gameObject.layer);
        }
    }


    Transform FindDoorHolder(Transform parent, string holderName)
    {
        // Try multiple search patterns
        Transform holder = parent.Find(holderName);
        if (holder != null) return holder;

        // Search in children recursively
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name.Contains("DoorHolder"))
                return child;
        }

        return null;
    }

    Transform FindChildContaining(Transform parent, string namePart)
    {
        if (parent == null) return null;

        // Search in direct children for name containing the specified part
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name.Contains(namePart))
                return child;
        }

        return null;
    }

    /// <summary>
    /// Finds all children containing the specified name part
    /// </summary>
    /// <param name="parent">Parent transform to search in</param>
    /// <param name="namePart">Part of name to search for</param>
    /// <returns>List of all matching child transforms</returns>
#if !MONO
    [HideFromIl2Cpp]
#endif
    List<Transform> FindAllChildrenContaining(Transform parent, string namePart)
    {
        List<Transform> results = new List<Transform>();
        if (parent == null) return results;

        // Search in direct children for names containing the specified part
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name.Contains(namePart))
            {
                results.Add(child);
            }
        }

        return results;
    }

#if !MONO
    [HideFromIl2Cpp]
#endif
    JailDoor CreateDoorFromHolder(Transform holder, string doorName, JailDoor.DoorType doorType)
    {
        JailDoor door = new JailDoor();
        door.doorHolder = holder;
        door.doorName = doorName;
        door.doorType = doorType;
        return door;
    }


    public void SetupDoors()
    {
        SetupCellDoors();
        SetupHoldingCellDoors();
        SetupAreaDoors();
    }

    void SetupCellDoors()
    {
        foreach (var cell in cells)
        {
            if (cell.cellDoor.IsValid() && jailDoorPrefab != null)
            {
                InstantiateDoor(cell.cellDoor, jailDoorPrefab);
            }
        }
        Debug.Log($"Setup {cells.Count(c => c.cellDoor.IsInstantiated())} cell doors");
    }

    void SetupHoldingCellDoors()
    {
        foreach (var holdingCell in holdingCells)
        {
            if (holdingCell.cellDoor.IsValid() && jailDoorPrefab != null)
            {
                InstantiateDoor(holdingCell.cellDoor, jailDoorPrefab);
            }
        }
        Debug.Log($"Setup {holdingCells.Count(c => c.cellDoor.IsInstantiated())} holding cell doors");
    }

    void SetupAreaDoors()
    {
        // Setup booking area doors using the area's own method
        if (booking.isInitialized)
        {
            booking.InstantiateDoors(steelDoorPrefab);
        }
        else
        {
            Debug.LogWarning("Booking area not initialized - cannot setup doors");
        }

        Debug.Log($"Setup area doors complete");
    }

    void SetupCellBeds()
    {
        int bedsSetupCount = 0;
        
        foreach (var cell in cells)
        {
            int cellBedsSetup = 0;
            
            // Setup bottom bed
            if (cell.cellBedBottom != null)
            {
                cell.bedBottomComponent = SetupJailBed(cell.cellBedBottom, $"{cell.cellName} Bottom Bunk", false);
                if (cell.bedBottomComponent != null)
                {
                    cellBedsSetup++;
                    bedsSetupCount++;
                }
            }
            
            // Setup top bed
            if (cell.cellBedTop != null)
            {
                cell.bedTopComponent = SetupJailBed(cell.cellBedTop, $"{cell.cellName} Top Bunk", true);
                if (cell.bedTopComponent != null)
                {
                    cellBedsSetup++;
                    bedsSetupCount++;
                }
            }
            
            if (cellBedsSetup > 0)
            {
                ModLogger.Info($"✓ Setup {cellBedsSetup} beds for {cell.cellName}");
            }
            else if (cell.cellBedBottom != null || cell.cellBedTop != null)
            {
                ModLogger.Warn($"⚠️ Found bed transforms but failed to setup beds for {cell.cellName}");
            }
        }
        
        ModLogger.Info($"Cell bed setup complete: {bedsSetupCount} beds across {cells.Count} cells");
    }
    
    JailBed SetupJailBed(Transform bedTransform, string bedName, bool isTopBunk)
    {
        if (bedTransform == null) return null;
        
        try
        {
            // Check if prison bed interactable already exists
            PrisonBedInteractable prisonBed = bedTransform.GetComponent<PrisonBedInteractable>();
            
            if (prisonBed == null)
            {
                // Add PrisonBedInteractable component for bed-making interaction
                prisonBed = bedTransform.gameObject.AddComponent<PrisonBedInteractable>();
                
                // Configure the prison bed setup
                prisonBed.isTopBunk = isTopBunk;
                prisonBed.cellName = ExtractCellNameFromBedName(bedName);
                
                // Find and assign bed component references
                SetupBedComponentReferences(prisonBed, bedTransform);
                
                ModLogger.Debug($"✓ Setup prison bed interactable: {bedName}");
            }
            
            // Check if there's already a completed JailBed (in case bed is already made)
            JailBed jailBed = bedTransform.GetComponent<JailBed>();
            if (jailBed != null)
            {
                // Bed is already complete - configure JailBed
                jailBed.bedName = bedName;
                jailBed.isTopBunk = isTopBunk;
                jailBed.sleepPosition = bedTransform;
                ModLogger.Debug($"✓ Found existing jail bed: {bedName}");
            }
            
            return jailBed; // May be null if bed isn't made yet
        }
        catch (System.Exception ex)
        {
            ModLogger.Error($"✗ Failed to setup jail bed '{bedName}': {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Extract cell name from bed name (e.g., "Cell 00 Bottom Bunk" → "Cell 00")
    /// </summary>
    private string ExtractCellNameFromBedName(string bedName)
    {
        if (string.IsNullOrEmpty(bedName)) return "Unknown Cell";
        
        // Remove " Bottom Bunk" or " Top Bunk" from the end
        string cellName = bedName;
        if (cellName.EndsWith(" Bottom Bunk"))
            cellName = cellName.Substring(0, cellName.Length - " Bottom Bunk".Length);
        else if (cellName.EndsWith(" Top Bunk"))
            cellName = cellName.Substring(0, cellName.Length - " Top Bunk".Length);
            
        return cellName;
    }
    
    /// <summary>
    /// Find and assign references to bed component child objects
    /// </summary>
    private void SetupBedComponentReferences(PrisonBedInteractable prisonBed, Transform bedTransform)
    {
        // Look for bed component children in the PrisonBed structure
        Transform prisonBedChild = bedTransform.Find("PrisonBed");
        if (prisonBedChild != null)
        {
            // Find bed components under PrisonBed
            prisonBed.bedMat = prisonBedChild.Find("BedMat");
            prisonBed.whiteSheet = prisonBedChild.Find("WhiteSheet");
            prisonBed.bedSheet = prisonBedChild.Find("BedSheet");
            prisonBed.pillow = prisonBedChild.Find("Pillow");
            
            ModLogger.Debug($"Found bed components for {prisonBed.cellName}: BedMat={prisonBed.bedMat != null}, WhiteSheet={prisonBed.whiteSheet != null}, BedSheet={prisonBed.bedSheet != null}, Pillow={prisonBed.pillow != null}");
        }
        else
        {
            // Try alternative structure - look directly under bed transform
            prisonBed.bedMat = bedTransform.Find("BedMat");
            prisonBed.whiteSheet = bedTransform.Find("WhiteSheet");
            prisonBed.bedSheet = bedTransform.Find("BedSheet");
            prisonBed.pillow = bedTransform.Find("Pillow");
            
            ModLogger.Debug($"Searched directly under bed transform for {prisonBed.cellName}");
        }
        
        // Log what we found
        int foundComponents = 0;
        if (prisonBed.bedMat != null) foundComponents++;
        if (prisonBed.whiteSheet != null) foundComponents++;
        if (prisonBed.bedSheet != null) foundComponents++;
        if (prisonBed.pillow != null) foundComponents++;
        
        if (foundComponents > 0)
        {
            ModLogger.Info($"✓ Found {foundComponents}/4 bed components for {prisonBed.cellName} {(prisonBed.isTopBunk ? "top bunk" : "bottom bunk")}");
        }
        else
        {
            ModLogger.Warn($"⚠️ No bed components found for {prisonBed.cellName} - bed making will not be available");
        }
    }

    void InstantiateDoor(JailDoor door, GameObject doorPrefab)
    {
        if (door.doorHolder == null) return;

        // Clear existing door
        if (door.doorInstance != null)
        {
            DestroyImmediate(door.doorInstance);
        }

        // Instantiate new door
        door.doorInstance = Instantiate(doorPrefab, door.doorHolder);
        door.doorInstance.transform.localPosition = Vector3.zero;
        door.doorInstance.transform.localRotation = Quaternion.identity;

        // Find the hinge (look for a child transform that could be the hinge)
        door.doorHinge = FindDoorHinge(door.doorInstance);

        // Initialize the door animation system
        door.InitializeDoor();

        if (showDebugInfo && door.doorHinge != null)
        {
            Debug.Log($"Found hinge for {door.doorName}: {door.doorHinge.name}");
        }
    }

    Transform FindDoorHinge(GameObject doorInstance)
    {
        // Look for common hinge names
        string[] hingeNames = { "Hinge", "Pivot", "Door", "DoorMesh", "Model" };

        foreach (string hingeName in hingeNames)
        {
            Transform hinge = doorInstance.transform.Find(hingeName);
            if (hinge != null) return hinge;
        }

        // Fallback: use the first child if any
        if (doorInstance.transform.childCount > 0)
        {
            return doorInstance.transform.GetChild(0);
        }

        return doorInstance.transform;
    }

    void SetupSecurityCameras()
    {
        // First, create/setup the actual security cameras
        CreateSecurityCameras();


        // Then setup monitor assignments
        SetupMonitorAssignments();
        ModLogger.Info($"Security camera setup completed with {securityCameras.Count} cameras and {monitorAssignments.Count} monitor assignments");
    }

    void CreateSecurityCameras()
    {
        // Clear existing cameras list but don't destroy them
        securityCameras.Clear();

        // Find all camera positions and create cameras
        Transform[] cameraPositions = FindSecurityCameraPositions();

        foreach (Transform cameraPosition in cameraPositions)
        {
            SetupSecurityCameras(cameraPosition);
        }
    }

#if !MONO
    [HideFromIl2Cpp]
#endif
    Transform[] FindSecurityCameraPositions()
    {
        List<Transform> positions = new List<Transform>();

        // Search in SecurityCameras parent (this should be the main location)
        Transform camerasParent = transform.Find("SecurityCameras");
        if (camerasParent != null)
        {
            Debug.Log($"Found SecurityCameras parent with {camerasParent.childCount} children");

            // Log all children for debugging
            for (int i = 0; i < camerasParent.childCount; i++)
            {
                Transform child = camerasParent.GetChild(i);
                Debug.Log($"SecurityCamera child [{i}]: {child.name}");

                // Add all direct children of SecurityCameras folder
                if (child.name.Contains("Security_Camera") || child.name.Contains("Camera"))
                {
                    positions.Add(child);
                    Debug.Log($"✓ Added camera position: {child.name}");
                }
            }
        }
        else
        {
            Debug.LogWarning("SecurityCameras parent folder not found! Expected: JailRoot/SecurityCameras/");
        }

        Debug.Log($"Total camera positions found: {positions.Count}");
        return positions.ToArray();
    }

    void SetupMonitorAssignments()
    {
        // Clear existing manual assignments - we'll auto-assign everything
        monitorAssignments.Clear();

        // Auto-discover monitors and assign cameras
        AutoDiscoverAndAssignMonitors();

        Debug.Log($"=== MONITOR SYSTEM SETUP COMPLETE ===");
        Debug.Log($"Total monitor assignments created: {monitorAssignments.Count}");
        Debug.Log($"Static monitors: {monitorAssignments.Count(m => !m.autoRotate)}");
        Debug.Log($"Rotating monitors: {monitorAssignments.Count(m => m.autoRotate)}");
        
        foreach (var assignment in monitorAssignments)
        {
            string rotationType = assignment.autoRotate ? "Rotating" : "Static";
            Debug.Log($"  {rotationType} monitor: {assignment.monitor.name} with {assignment.availableCameras.Count} cameras");
        }
    }

    void AutoDiscoverAndAssignMonitors()
    {
        // Find static monitors (static assignment)
        Transform staticMonitorsParent = transform.Find("Monitors/StaticMonitors");
        if (staticMonitorsParent != null)
        {
            SetupStaticMonitors(staticMonitorsParent);
        }
        else
        {
            Debug.LogWarning("StaticMonitors folder not found at Monitors/StaticMonitors/");
        }

        // Find rotating monitors (rotating assignment)
        Transform rotatingMonitorsParent = transform.Find("Monitors/RotatingMonitors");
        if (rotatingMonitorsParent != null)
        {
            SetupRotatingMonitors(rotatingMonitorsParent);
        }
        else
        {
            Debug.LogWarning("RotatingMonitors folder not found at Monitors/RotatingMonitors/");
        }
    }

    void SetupStaticMonitors(Transform staticMonitorsParent)
    {
        Debug.Log($"Setting up static monitors from {staticMonitorsParent.name}");

        // Get all available cameras (prioritize MainView, then use any available)
        List<SecurityCamera> availableCameras = new List<SecurityCamera>();
        
        // First add MainView cameras
        var mainViewCameras = securityCameras.Where(cam => 
            cam.cameraType == SecurityCamera.CameraType.MainView).ToList();
        availableCameras.AddRange(mainViewCameras);
        Debug.Log($"Found {mainViewCameras.Count} MainView cameras for static monitors");
        
        // Then add other camera types if we need more
        if (availableCameras.Count < staticMonitorsParent.childCount)
        {
            var otherCameras = securityCameras.Where(cam => 
                cam.cameraType != SecurityCamera.CameraType.MainView).ToList();
            availableCameras.AddRange(otherCameras);
            Debug.Log($"Added {otherCameras.Count} other cameras (total: {availableCameras.Count})");
        }

        Debug.Log($"Found {availableCameras.Count} cameras for {staticMonitorsParent.childCount} static monitors");

        int successfulAssignments = 0;
        // Assign each static monitor to a specific camera
        for (int i = 0; i < staticMonitorsParent.childCount; i++)
        {
            Transform monitorTransform = staticMonitorsParent.GetChild(i);
            
#if MONO
            ModLogger.Debug($"MONO: Processing static monitor {i}: {monitorTransform.name}");
#endif
            
            MonitorController monitor = FindMonitorController(monitorTransform);

            if (monitor == null)
            {
                Debug.LogWarning($"✗ No MonitorController found/created on {monitorTransform.name} or its children");
                continue;
            }
            
#if MONO
            ModLogger.Debug($"MONO: Successfully got MonitorController for {monitorTransform.name}");
#endif

            if (availableCameras.Count == 0)
            {
                Debug.LogWarning($"✗ No cameras available for monitor {monitorTransform.name}");
                continue;
            }

            // Use available camera or cycle through if we have fewer cameras than monitors
            SecurityCamera camera = availableCameras[i % availableCameras.Count];

            // Create assignment
            MonitorAssignment assignment = new MonitorAssignment();
            assignment.monitor = monitor;
            assignment.availableCameras.Add(camera);
            assignment.autoRotate = false; // Static monitors don't rotate
            assignment.currentCameraIndex = 0;

            monitorAssignments.Add(assignment);

            // Set the camera immediately
            SetMonitorCamera(monitor, camera);
            successfulAssignments++;

            Debug.Log($"✓ Static monitor {monitorTransform.name} → {camera.cameraName}");
        }
        
        Debug.Log($"Static monitor setup completed: {successfulAssignments}/{staticMonitorsParent.childCount} monitors assigned successfully");
    }

    // Helper method to find MonitorController on transform or its children
    // Auto-creates MonitorController if not found on monitor objects
    MonitorController FindMonitorController(Transform parent)
    {
        // Check the parent first
        MonitorController monitor = parent.GetComponent<MonitorController>();
        if (monitor != null) return monitor;
        
        // If not found, search children recursively
        for (int i = 0; i < parent.childCount; i++)
        {
            monitor = FindMonitorController(parent.GetChild(i));
            if (monitor != null) return monitor;
        }
        
        // If no MonitorController found anywhere, check if this is a monitor object and auto-create one
        if (IsMonitorObject(parent))
        {
            ModLogger.Info($"Auto-creating MonitorController component on {parent.name}");
            monitor = parent.gameObject.AddComponent<MonitorController>();
            return monitor;
        }
        
        return null;
    }
    
    // Helper method to identify if a GameObject should have a MonitorController
    bool IsMonitorObject(Transform obj)
    {
        string name = obj.name.ToLower();
        
        // Check if the object name indicates it's a monitor
        bool hasMonitorName = name.Contains("monitor") || 
                             name.Contains("screen") || 
                             name.Contains("display");
        
        // Check if it has UI components that suggest it's a monitor
        bool hasMonitorComponents = obj.GetComponent<RawImage>() != null || 
                                   obj.GetComponentInChildren<RawImage>() != null;
        
        // Check if it's in a monitor hierarchy (parent path contains "monitor")
        bool inMonitorHierarchy = obj.parent != null && 
                                 (obj.parent.name.ToLower().Contains("monitor") ||
                                  obj.parent.parent != null && obj.parent.parent.name.ToLower().Contains("monitor"));
        
#if MONO
        if (hasMonitorName || hasMonitorComponents || inMonitorHierarchy)
        {
            ModLogger.Debug($"MONO: {obj.name} identified as monitor object (name: {hasMonitorName}, components: {hasMonitorComponents}, hierarchy: {inMonitorHierarchy})");
        }
#endif
        
        return hasMonitorName || hasMonitorComponents || inMonitorHierarchy;
    }
    

    void SetupRotatingMonitors(Transform rotatingMonitorsParent)
    {
        Debug.Log($"Setting up rotating monitors from {rotatingMonitorsParent.name}");

        // Get all rotating cameras (non-MainView cameras)
        List<SecurityCamera> rotatingCameras = securityCameras.Where(cam =>
            cam.cameraType != SecurityCamera.CameraType.MainView).ToList();

        Debug.Log($"Found {rotatingCameras.Count} rotating cameras for {rotatingMonitorsParent.childCount} rotating monitors");
        
        foreach (var cam in rotatingCameras)
        {
            Debug.Log($"  Rotating camera: {cam.cameraName} (type: {cam.cameraType})");
        }

        int successfulAssignments = 0;
        // Assign each rotating monitor to cycle through cameras without overlap
        for (int i = 0; i < rotatingMonitorsParent.childCount; i++)
        {
            Transform monitorTransform = rotatingMonitorsParent.GetChild(i);
            
#if MONO
            Debug.Log($"MONO: Processing rotating monitor {i}: {monitorTransform.name}");
#endif
            
            MonitorController monitor = FindMonitorController(monitorTransform);

            if (monitor == null)
            {
                Debug.LogWarning($"✗ No MonitorController found/created on {monitorTransform.name} or its children");
                continue;
            }
            
#if MONO
            Debug.Log($"MONO: Successfully got MonitorController for rotating monitor {monitorTransform.name}");
#endif

            if (rotatingCameras.Count == 0)
            {
                Debug.LogWarning($"✗ No rotating cameras available for monitor {monitorTransform.name}");
                continue;
            }

            // Create assignment with all rotating cameras
            MonitorAssignment assignment = new MonitorAssignment();
            assignment.monitor = monitor;
            assignment.availableCameras.AddRange(rotatingCameras);
            assignment.autoRotate = true; // Rotating monitors rotate
            assignment.rotationInterval = 8f + (i * 2f); // Staggered timing: 8s, 10s, 12s, etc.
            assignment.currentCameraIndex = i % rotatingCameras.Count; // Start at different cameras
            assignment.lastRotationTime = Time.time + (i * 2f); // Stagger start times too

            monitorAssignments.Add(assignment);

            // Set initial camera
            if (rotatingCameras.Count > 0)
            {
                SecurityCamera initialCamera = assignment.GetCurrentCamera();
                SetMonitorCamera(monitor, initialCamera);
                successfulAssignments++;
                Debug.Log($"✓ Rotating monitor {monitorTransform.name} → {initialCamera.cameraName} (every {assignment.rotationInterval}s, starting after {i * 2f}s delay)");
            }
        }
        
        Debug.Log($"Rotating monitor setup completed: {successfulAssignments}/{rotatingMonitorsParent.childCount} monitors assigned successfully");
    }

    // Guard Control Methods
    public void EmergencyLockdown()
    {
        booking.LockAllDoors();

        foreach (var cell in cells)
        {
            cell.LockCell(true);
        }

        foreach (var holdingCell in holdingCells)
        {
            holdingCell.LockCell(true);
        }

        // Set emergency lighting
        SetJailLighting(LightingState.Emergency);

        Debug.Log("🔒 EMERGENCY LOCKDOWN ACTIVATED! Doors locked, emergency lighting enabled.");
    }

    public void UnlockAll()
    {
        // Unlock booking area doors
        booking.UnlockAllDoors();

        // Unlock all cell doors
        foreach (var cell in cells)
        {
            cell.LockCell(false); // This calls UnlockDoor()
        }

        // Unlock all holding cell doors  
        foreach (var holdingCell in holdingCells)
        {
            holdingCell.LockCell(false); // This calls UnlockDoor()
        }

        // Unlock holding cell doors in the booking area are handled by area management

        // Restore normal lighting
        SetJailLighting(LightingState.Normal);

        Debug.Log("🔓 All doors unlocked! Normal lighting restored.");
    }

    public void Blackout()
    {
        // Set blackout lighting (all lights off, emissive materials off)
        SetJailLighting(LightingState.Blackout);

        Debug.Log("🌑 BLACKOUT ACTIVATED! All lights are off.");
    }

    public void OpenAllCells()
    {
        foreach (var cell in cells)
        {
            if (!cell.cellDoor.isLocked)
                cell.OpenCell();
        }

        Debug.Log("Opened all unlocked cells");
    }

    public void CloseAllCells()
    {
        foreach (var cell in cells)
        {
            cell.CloseCell();
        }

        Debug.Log("Closed all cells");
    }

    // Lighting Control Methods
    public void SetJailLighting(LightingState state)
    {
        currentLightingState = state;

        foreach (var areaLighting in areaLights)
        {
            areaLighting.SetLightingState(state);
        }
        
        // Update emissive material to match lighting state
        SetEmissiveMaterial(state);

        string stateName = state switch
        {
            LightingState.Normal => "NORMAL",
            LightingState.Emergency => "EMERGENCY",
            LightingState.Blackout => "BLACKOUT",
            _ => "UNKNOWN"
        };

        Debug.Log($"💡 Jail lighting set to {stateName}");
    }

    public void ToggleAreaLighting(string areaName)
    {
        AreaLighting area = areaLights.FirstOrDefault(a => a.areaName.Equals(areaName, System.StringComparison.OrdinalIgnoreCase));
        if (area != null)
        {
            area.ToggleLights();
            Debug.Log($"💡 Toggled {areaName} lights: {(area.isOn ? "ON" : "OFF")}");
        }
        else
        {
            Debug.LogWarning($"Area lighting not found: {areaName}");
        }
    }

    public void SetAreaLighting(string areaName, bool enabled)
    {
        AreaLighting area = areaLights.FirstOrDefault(a => a.areaName.Equals(areaName, System.StringComparison.OrdinalIgnoreCase));
        if (area != null)
        {
            area.SetLights(enabled, area.normalIntensity, area.normalColor);
            Debug.Log($"💡 Set {areaName} lights: {(enabled ? "ON" : "OFF")}");
        }
        else
        {
            Debug.LogWarning($"Area lighting not found: {areaName}");
        }
    }

    // Emissive Material Control Methods
    void FindEmissiveMaterial()
    {
        if (!enableEmissiveControl)
        {
            ModLogger.Debug("Emissive control disabled, skipping material search");
            return;
        }
        
        if (emissiveMaterial != null)
        {
            ModLogger.Debug($"Emissive material already cached: {emissiveMaterial.name}");
            return;
        }
        
        ModLogger.Info($"Searching for emissive material containing name: '{emissiveMaterialName}'");
        
        // Find all renderers in the jail hierarchy
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        ModLogger.Info($"Found {renderers.Length} renderers in jail hierarchy");
        
        int totalMaterials = 0;
        List<string> allMaterialNames = new List<string>();
        
        foreach (var renderer in renderers)
        {
            if (renderer.materials != null)
            {
                totalMaterials += renderer.materials.Length;
                foreach (var material in renderer.materials)
                {
                    if (material != null)
                    {
                        allMaterialNames.Add(material.name);
                        
                        // Check for exact match or contains
                        if (material.name.Contains(emissiveMaterialName))
                        {
                            // Add to our collection of all emissive materials
                            if (!allEmissiveMaterials.Contains(material))
                            {
                                allEmissiveMaterials.Add(material);
                                ModLogger.Info($"✓ Found emissive material: '{material.name}' on renderer: {renderer.name}");
                                
                                // Test if material has emission properties
                                TestEmissiveMaterialProperties(material);
                                
                                // Set the first one as the primary reference
                                if (emissiveMaterial == null)
                                {
                                    emissiveMaterial = material;
                                }
                            }
                        }
                    }
                }
            }
        }
        
        // Log results
        if (allEmissiveMaterials.Count > 0)
        {
            ModLogger.Info($"✓ Found {allEmissiveMaterials.Count} emissive material instances total");
        }
        
        if (allEmissiveMaterials.Count == 0)
        {
            ModLogger.Warn($"⚠️ Emissive material containing '{emissiveMaterialName}' not found in jail hierarchy");
        }
        
        ModLogger.Info($"Searched {totalMaterials} materials across {renderers.Length} renderers");
        
        // Log first 10 material names for debugging
        if (allMaterialNames.Count > 0)
        {
            ModLogger.Info("First 10 materials found:");
            for (int i = 0; i < System.Math.Min(10, allMaterialNames.Count); i++)
            {
                ModLogger.Info($"  [{i}]: {allMaterialNames[i]}");
            }
        }
    }
    
    void TestEmissiveMaterialProperties(Material material)
    {
        ModLogger.Debug($"Testing emission properties on material: {material.name}");
        
        bool hasEmissionColor = material.HasProperty("_EmissionColor");
        bool hasEmission = material.HasProperty("_Emission");
        bool hasEmissiveKeyword = material.IsKeywordEnabled("_EMISSION");
        
        ModLogger.Info($"Material properties: _EmissionColor={hasEmissionColor}, _Emission={hasEmission}, _EMISSION keyword={hasEmissiveKeyword}");
        
        if (hasEmissionColor)
        {
            Color currentEmission = material.GetColor("_EmissionColor");
            ModLogger.Info($"Current _EmissionColor: {currentEmission}");
        }
        
        if (hasEmission)
        {
            Color currentEmission = material.GetColor("_Emission");
            ModLogger.Info($"Current _Emission: {currentEmission}");
        }
    }
    
    void SetEmissiveMaterial(LightingState state)
    {
        if (!enableEmissiveControl)
        {
            ModLogger.Debug($"Emissive control disabled, skipping material update for {state}");
            return;
        }
        
        if (emissiveMaterial == null)
        {
            ModLogger.Warn($"No emissive material cached, cannot update for {state}");
            return;
        }
        
        ModLogger.Info($"Updating emissive material '{emissiveMaterial.name}' for lighting state: {state}");
        
        Color targetColor;
        float targetIntensity;
        
        switch (state)
        {
            case LightingState.Normal:
                targetColor = emissiveNormalColor;
                targetIntensity = emissiveNormalIntensity;
                break;
            case LightingState.Emergency:
                targetColor = emissiveEmergencyColor;
                targetIntensity = emissiveEmergencyIntensity;
                break;
            case LightingState.Blackout:
                targetColor = emissiveBlackoutColor;
                targetIntensity = emissiveBlackoutIntensity;
                break;
            default:
                targetColor = emissiveNormalColor;
                targetIntensity = emissiveNormalIntensity;
                break;
        }
        
        // Apply the emission color and intensity
        Color finalEmissionColor = targetColor * targetIntensity;
        
        int updatedCount = 0;
        int failedCount = 0;
        
        // Update ALL emissive material instances
        foreach (var material in allEmissiveMaterials)
        {
            if (material == null) continue;
            
            bool materialUpdated = false;
            
            // Try different emission property names
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", finalEmissionColor);
                materialUpdated = true;
                ModLogger.Debug($"Set _EmissionColor on '{material.name}' to: {finalEmissionColor}");
            }
            else if (material.HasProperty("_Emission"))
            {
                material.SetColor("_Emission", finalEmissionColor);
                materialUpdated = true;
                ModLogger.Debug($"Set _Emission on '{material.name}' to: {finalEmissionColor}");
            }
            else if (material.HasProperty("_EmissiveColor"))
            {
                material.SetColor("_EmissiveColor", finalEmissionColor);
                materialUpdated = true;
                ModLogger.Debug($"Set _EmissiveColor on '{material.name}' to: {finalEmissionColor}");
            }
            
            if (materialUpdated)
            {
                // Enable/disable emission keyword for performance
                if (targetIntensity > 0)
                {
                    material.EnableKeyword("_EMISSION");
                }
                else
                {
                    material.DisableKeyword("_EMISSION");
                }
                updatedCount++;
            }
            else
            {
                ModLogger.Warn($"Material '{material.name}' has no supported emission property!");
                failedCount++;
            }
        }
        
        if (updatedCount > 0)
        {
            ModLogger.Info($"Successfully updated {updatedCount} emissive material instances to {state}: {finalEmissionColor} (intensity: {targetIntensity})");
        }
        
        if (failedCount > 0)
        {
            ModLogger.Error($"Failed to update {failedCount} emissive material instances - no compatible emission properties found");
        }
    }

    public void OpenBookingArea()
    {
        booking.UnlockAllDoors();
        Debug.Log("Opened booking area");
    }

    public void CloseBookingArea()
    {
        booking.LockAllDoors();
        Debug.Log("Closed booking area");
    }

    void LogJailStatus()
    {
        Debug.Log($"=== JAIL STATUS ===");
        Debug.Log($"Prison Cells: {cells.Count}");
        Debug.Log($"Holding Cells: {holdingCells.Count}");
        Debug.Log($"Jail Areas: 7 static areas initialized");
        Debug.Log($"Security Cameras: {securityCameras.Count}");

        int openCells = cells.Count(c => c.cellDoor.IsOpen());
        int lockedCells = cells.Count(c => c.cellDoor.isLocked);
        Debug.Log($"Open Cells: {openCells}, Locked Cells: {lockedCells}");
    }

    public void LogStatus()
    {
        LogJailStatus();
    }

    public void TestHoldingCellDiscovery()
    {
        Debug.Log("=== TESTING HOLDING CELL DISCOVERY ===");
        holdingCells.Clear();
        DiscoverHoldingCells();
        Debug.Log($"Discovery completed. Found {holdingCells.Count} holding cells.");
        
        // Test the new spawn point system
        TestHoldingCellSpawnSystem();
    }
    
    public void TestHoldingCellSpawnSystem()
    {
        Debug.Log("=== TESTING HOLDING CELL SPAWN SYSTEM ===");
        
        var (totalSpawns, available, occupied, totalCells) = GetHoldingCellStatus();
        Debug.Log($"Holding Cell Status: {totalCells} cells, {totalSpawns} total spawn points, {available} available, {occupied} occupied");
        
        // Test assigning players
        Debug.Log("Testing player assignments:");
        var spawn1 = AssignPlayerToHoldingCell("TestPlayer1");
        var spawn2 = AssignPlayerToHoldingCell("TestPlayer2");
        var spawn3 = AssignPlayerToHoldingCell("TestPlayer3");
        var spawn4 = AssignPlayerToHoldingCell("TestPlayer4"); // Should work if we have 2 holding cells (6 total spawns)
        
        // Check status after assignments
        var (totalAfter, availableAfter, occupiedAfter, totalCellsAfter) = GetHoldingCellStatus();
        Debug.Log($"Status after assignments: {totalCellsAfter} cells, {totalAfter} total spawn points, {availableAfter} available, {occupiedAfter} occupied");
        
        // Test releasing players
        Debug.Log("Testing player releases:");
        ReleasePlayerFromHoldingCell("TestPlayer2");
        ReleasePlayerFromHoldingCell("TestPlayer4");
        
        // Final status check
        var (totalFinal, availableFinal, occupiedFinal, totalCellsFinal) = GetHoldingCellStatus();
        Debug.Log($"Final status: {totalCellsFinal} cells, {totalFinal} total spawn points, {availableFinal} available, {occupiedFinal} occupied");
        
        // Detailed cell status
        foreach (var holdingCell in holdingCells)
        {
            var (current, max, availableCell) = holdingCell.GetOccupancyStatus();
            Debug.Log($"  {holdingCell.cellName}: {current}/{max} occupied, {availableCell} available");
            
            foreach (var spawn in holdingCell.spawnPointOccupancy)
            {
                string status = spawn.isOccupied ? $"occupied by {spawn.occupantName}" : "available";
                Debug.Log($"    Spawn {spawn.spawnIndex}: {status}");
            }
        }
    }

    public void SetupMonitorAssignmentsMenu()
    {
        SetupMonitorAssignments();
    }

    public void RotateAllMonitors()
    {
        foreach (var assignment in monitorAssignments)
        {
            if (assignment.availableCameras.Count > 1)
            {
                RotateMonitorCamera(assignment);
            }
        }
        Debug.Log("Rotated all monitors to next camera");
    }

    public void TestMonitorSystem()
    {
        Debug.Log("=== TESTING MONITOR SYSTEM ===");
        Debug.Log($"Total security cameras: {securityCameras.Count}");
        Debug.Log($"Total monitor assignments: {monitorAssignments.Count}");

        // Test camera components
        foreach (var camera in securityCameras)
        {
            if (camera != null)
            {
                bool hasRenderTexture = camera.renderTexture != null;
                bool hasCameraComponent = camera.cameraComponent != null;
                Debug.Log($"Camera {camera.cameraName}: RenderTexture={hasRenderTexture}, CameraComponent={hasCameraComponent}, Type={camera.cameraType}");
            }
        }

        // Test monitor assignments
        for (int i = 0; i < monitorAssignments.Count; i++)
        {
            var assignment = monitorAssignments[i];
            string monitorName = assignment.monitor != null ? assignment.monitor.name : "NULL";
            string currentCamera = assignment.GetCurrentCamera()?.cameraName ?? "NULL";
            bool autoRotate = assignment.autoRotate;
            Debug.Log($"Monitor {i}: {monitorName} -> Camera: {currentCamera} (AutoRotate: {autoRotate}, {assignment.availableCameras.Count} available)");
        }
    }

    public void ForceSetupAllMonitors()
    {
        ModLogger.Info("=== FORCE SETUP ALL MONITORS ===");
        CreateSecurityCameras();
        SetupMonitorAssignments();
        ModLogger.Info("Setup complete!");
    }

    public void TestLightingSystem()
    {
        Debug.Log("=== TESTING LIGHTING SYSTEM ===");
        Debug.Log($"Total area lighting groups: {areaLights.Count}");

        int totalLights = 0;
        int totalRealTime = 0;
        int totalBaked = 0;
        int enabledLights = 0;

        foreach (var areaLighting in areaLights)
        {
            totalLights += areaLighting.lights.Count;
            totalRealTime += areaLighting.realTimeLights.Count;
            totalBaked += areaLighting.bakedLights.Count;

            int areaEnabled = areaLighting.lights.Count(l => l != null && l.enabled);
            enabledLights += areaEnabled;

            string nearbyStatus = areaLighting.isPlayerNearby ? "NEAR" : "FAR";
            Debug.Log($"Area: {areaLighting.areaName} - {areaLighting.lights.Count} total ({areaLighting.realTimeLights.Count} RT, {areaLighting.bakedLights.Count} baked) - {areaEnabled} enabled - Player: {nearbyStatus}");
        }

        Debug.Log($"Performance Stats: {totalLights} total lights, {totalRealTime} real-time, {totalBaked} baked, {enabledLights} currently enabled");
        Debug.Log($"Current lighting state: {currentLightingState}");
        Debug.Log($"LOD enabled: {enableLightingLOD}, Max real-time: {maxRealTimeLights}, Cull distance: {lightCullingDistance}m");
    }

    public void EmergencyLightingTest()
    {
        SetJailLighting(LightingState.Emergency);
    }

    public void NormalLightingTest()
    {
        SetJailLighting(LightingState.Normal);
    }

    public void BlackoutTest()
    {
        SetJailLighting(LightingState.Blackout);
    }

    public void TestAreaSystem()
    {
        Debug.Log("=== TESTING AREA SYSTEM ===");

        JailAreaBase[] allAreas = { kitchen, laundry, phoneArea, booking, guardRoom, mainRec, showers };

        foreach (var area in allAreas)
        {
            if (area.isInitialized)
            {
                Bounds bounds = area.GetTotalBounds();
                Vector3 center = area.GetAreaCenter();
                Vector3 size = area.GetAreaSize();

                Debug.Log($"Area: {area.areaName}");
                Debug.Log($"  Initialized: {area.isInitialized}");
                Debug.Log($"  Bounds: {area.bounds.Count} objects");
                Debug.Log($"  Doors: {area.doors.Count} doors");
                Debug.Log($"  Lights: {area.lights.Count} lights");
                Debug.Log($"  Center: {center}");
                Debug.Log($"  Size: {size}");
                Debug.Log($"  Max Occupancy: {area.maxOccupancy}");
                Debug.Log($"  Requires Auth: {area.requiresAuthorization}");
                Debug.Log($"  Accessible: {area.isAccessible}");
            }
            else
            {
                Debug.LogWarning($"Area {area.areaName} not initialized");
            }
        }
    }

    public void LockDownAllAreas()
    {
        JailAreaBase[] allAreas = { kitchen, laundry, phoneArea, booking, guardRoom, mainRec, showers };

        foreach (var area in allAreas)
        {
            if (area.isInitialized)
            {
                area.SetAccessible(false);
            }
        }

        Debug.Log("🔒 All areas locked down");
    }

    public void OpenAllAreas()
    {
        JailAreaBase[] allAreas = { kitchen, laundry, phoneArea, booking, guardRoom, mainRec, showers };

        foreach (var area in allAreas)
        {
            if (area.isInitialized)
            {
                area.SetAccessible(true);
            }
        }

        Debug.Log("🔓 All areas opened");
    }

    // Bed Testing Methods
    public void TestBedSystem()
    {
        Debug.Log("=== TESTING BED SYSTEM ===");
        int totalBeds = 0;

        foreach (var cell in cells)
        {
            int cellBeds = 0;

            if (cell.bedBottomComponent != null)
            {
                cellBeds++;
                totalBeds++;
            }

            if (cell.bedTopComponent != null)
            {
                cellBeds++;
                totalBeds++;
            }

            if (cellBeds > 0)
            {
                Debug.Log($"  {cell.cellName}: {cellBeds} beds setup");
            }
        }

        Debug.Log($"Bed System Stats: {totalBeds} total beds across {cells.Count} cells");
        
        // Test Schedule I's sleep canvas using reflection
        try
        {
            var sleepCanvasType = System.Type.GetType("ScheduleOne.UI.SleepCanvas, Assembly-CSharp");
            if (sleepCanvasType != null)
            {
                var instanceProp = sleepCanvasType.GetProperty("Instance");
                if (instanceProp != null)
                {
                    var sleepCanvas = instanceProp.GetValue(null);
                    if (sleepCanvas != null)
                    {
                        Debug.Log("✓ Schedule I SleepCanvas found - beds will work with native sleep system");
                    }
                    else
                    {
                        Debug.LogWarning("✗ Schedule I SleepCanvas instance is null");
                    }
                }
                else
                {
                    Debug.LogWarning("✗ Schedule I SleepCanvas Instance property not found");
                }
            }
            else
            {
                Debug.LogWarning("✗ Schedule I SleepCanvas type not found");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"✗ Could not access SleepCanvas: {ex.Message}");
        }
        
        // Test TimeManager using reflection
        try
        {
            var timeManagerType = System.Type.GetType("ScheduleOne.GameTime.TimeManager, Assembly-CSharp");
            if (timeManagerType != null)
            {
                var instanceProp = timeManagerType.GetProperty("Instance");
                if (instanceProp != null)
                {
                    var timeManager = instanceProp.GetValue(null);
                    if (timeManager != null)
                    {
                        Debug.Log("✓ Schedule I TimeManager found - sleep time restrictions will work");
                    }
                    else
                    {
                        Debug.LogWarning("✗ Schedule I TimeManager instance is null");
                    }
                }
                else
                {
                    Debug.LogWarning("✗ Schedule I TimeManager Instance property not found");
                }
            }
            else
            {
                Debug.LogWarning("✗ Schedule I TimeManager type not found");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"✗ Could not access TimeManager: {ex.Message}");
        }
    }

    public void TestBedInteraction()
    {
        Debug.Log("=== TESTING BED INTERACTION ===");
        
        // Find first bed
        JailBed firstBed = null;
        foreach (var cell in cells)
        {
            firstBed = cell.GetFirstBed();
            if (firstBed != null)
            {
                Debug.Log($"Found bed: {firstBed.bedName} in {cell.cellName}");
                break;
            }
        }
        
        if (firstBed != null)
        {
            Debug.Log("Testing bed hover...");
            firstBed.Hovered();
            Debug.Log("Use this bed in-game to test Schedule I's sleep system");
        }
        else
        {
            Debug.Log("No beds found for testing");
        }
    }

    // Area Detection Methods
    public string GetPlayerCurrentArea(Vector3 playerPosition)
    {
        // Check if in booking area
        if (booking.IsPositionInArea(playerPosition))
            return booking.areaName;

        // Check all jail areas
        if (kitchen.IsPositionInArea(playerPosition)) return kitchen.areaName;
        if (laundry.IsPositionInArea(playerPosition)) return laundry.areaName;
        if (phoneArea.IsPositionInArea(playerPosition)) return phoneArea.areaName;
        if (guardRoom.IsPositionInArea(playerPosition)) return guardRoom.areaName;
        if (mainRec.IsPositionInArea(playerPosition)) return mainRec.areaName;
        if (showers.IsPositionInArea(playerPosition)) return showers.areaName;

        // Check if in a cell
        foreach (var cell in cells)
        {
            if (cell.cellBounds != null)
            {
                Collider cellCollider = cell.cellBounds.GetComponent<Collider>();
                if (cellCollider != null && cellCollider.bounds.Contains(playerPosition))
                    return cell.cellName;
            }
        }

        // Check if in holding cell
        foreach (var holdingCell in holdingCells)
        {
            if (holdingCell.cellBounds != null)
            {
                Collider cellCollider = holdingCell.cellBounds.GetComponent<Collider>();
                if (cellCollider != null && cellCollider.bounds.Contains(playerPosition))
                    return holdingCell.cellName;
            }
        }

        return "Unknown Area";
    }

    public void TestPlayerPosition()
    {
        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player != null)
        {
            string currentArea = GetPlayerCurrentArea(player.transform.position);
            Debug.Log($"Player is currently in: {currentArea}");
        }
        else
        {
            Debug.LogWarning("No player found! Make sure player has 'Player' tag.");
        }
    }

#if !MONO
    [HideFromIl2Cpp]
#endif
    public JailAreaBase GetAreaByName(string areaName)
    {
        switch (areaName.ToLowerInvariant())
        {
            case "kitchen": return kitchen;
            case "laundry": return laundry;
            case "phone area": case "phones": return phoneArea;
            case "booking": return booking;
            case "guard room": return guardRoom;
            case "main recreation": case "main rec": return mainRec;
            case "showers": return showers;
            default: return null;
        }
    }

#if !MONO
    [HideFromIl2Cpp]
#endif
    public CellDetail GetCellByIndex(int cellIndex)
    {
        return cells.FirstOrDefault(c => c.cellIndex == cellIndex);
    }

#if !MONO
    [HideFromIl2Cpp]
#endif
    public CellDetail GetHoldingCellByIndex(int cellIndex)
    {
        return holdingCells.FirstOrDefault(c => c.cellIndex == cellIndex);
    }

    /// <summary>
    /// Represents a specific spawn point within a holding cell
    /// </summary>
    [System.Serializable]
    public class HoldingCellSpawnPoint
    {
        public Transform spawnTransform;
        public int cellIndex;
        public int spawnIndex; // 0, 1, or 2
        public bool isOccupied = false;
        public string occupiedBy; // Player name or ID
        
        public string GetSpawnPointName()
        {
            return $"HoldingCell_{cellIndex}_Spawn_{spawnIndex}";
        }
    }
    
    // Static spawn point tracking for holding cells
    public List<HoldingCellSpawnPoint> holdingCellSpawnPoints = new List<HoldingCellSpawnPoint>();

    /// <summary>
    /// Initialize holding cell spawn points from the discovered holding cells
    /// </summary>
    void InitializeHoldingCellSpawnPoints()
    {
        holdingCellSpawnPoints.Clear();
        
        ModLogger.Info($"Starting holding cell spawn point initialization with {holdingCells.Count} holding cells");
        
        foreach (var holdingCell in holdingCells)
        {
            ModLogger.Debug($"Processing holding cell {holdingCell.cellIndex}: {holdingCell.cellName} at {holdingCell.cellTransform?.name}");
            
            if (holdingCell.cellTransform == null)
            {
                ModLogger.Warn($"Holding cell {holdingCell.cellIndex} has null cellTransform - skipping");
                continue;
            }
            
            // Find all spawn points for this holding cell (should be 3: [0], [1], [2])
            int foundSpawnPoints = 0;
            for (int spawnIndex = 0; spawnIndex < 3; spawnIndex++)
            {
                Transform spawnPoint = FindSpawnPoint(holdingCell.cellTransform, spawnIndex);
                if (spawnPoint != null)
                {
                    var holdingSpawn = new HoldingCellSpawnPoint();
                    holdingSpawn.spawnTransform = spawnPoint;
                    holdingSpawn.cellIndex = holdingCell.cellIndex;
                    holdingSpawn.spawnIndex = spawnIndex;
                    holdingSpawn.isOccupied = false;
                    holdingSpawn.occupiedBy = null;
                    
                    holdingCellSpawnPoints.Add(holdingSpawn);
                    foundSpawnPoints++;
                    ModLogger.Debug($"✓ Registered holding spawn point: {holdingSpawn.GetSpawnPointName()} at {spawnPoint.position}");
                }
                else
                {
                    ModLogger.Warn($"✗ Missing spawn point {spawnIndex} for holding cell {holdingCell.cellIndex} (searched in {holdingCell.cellTransform.name})");
                    
                    // Debug: List all children of this holding cell to help identify naming issues
                    ModLogger.Debug($"Holding cell {holdingCell.cellIndex} children:");
                    for (int i = 0; i < holdingCell.cellTransform.childCount; i++)
                    {
                        var child = holdingCell.cellTransform.GetChild(i);
                        ModLogger.Debug($"  Child {i}: {child.name}");
                    }
                }
            }
            
            ModLogger.Info($"Holding cell {holdingCell.cellIndex} registered {foundSpawnPoints}/3 spawn points");
        }
        
        ModLogger.Info($"Initialized {holdingCellSpawnPoints.Count} holding cell spawn points across {holdingCells.Count} holding cells");
    }
    
    Transform FindSpawnPoint(Transform cellTransform, int spawnIndex)
    {
        // Look for HoldingCellSpawn[spawnIndex] in the cell hierarchy
        string[] spawnNames = {
            $"HoldingCellSpawn[{spawnIndex}]",
            $"HoldingCellSpawn_{spawnIndex}",
            $"Spawn[{spawnIndex}]",
            $"Spawn_{spawnIndex}"
        };
        
        foreach (string spawnName in spawnNames)
        {
            Transform spawn = cellTransform.Find(spawnName);
            if (spawn != null) return spawn;
            
            // Search in children recursively
            spawn = FindChildRecursive(cellTransform, spawnName);
            if (spawn != null) return spawn;
        }
        
        return null;
    }
    
    Transform FindChildRecursive(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name) return child;
            
            Transform found = FindChildRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Gets the first available holding cell spawn point for short sentences
    /// </summary>
    /// <returns>Available spawn point, or null if all are occupied</returns>
#if !MONO
    [HideFromIl2Cpp]
#endif
    public HoldingCellSpawnPoint GetAvailableHoldingCellSpawnPoint()
    {
        return holdingCellSpawnPoints.FirstOrDefault(sp => !sp.isOccupied);
    }
    
    /// <summary>
    /// Assigns a player to a holding cell spawn point using the new system
    /// </summary>
    /// <param name="playerName">Player name/ID to assign</param>
    /// <returns>The assigned spawn point transform, or null if no space available</returns>
    public Transform AssignPlayerToHoldingCell(string playerName)
    {
        // Find first holding cell with available space
        foreach (var holdingCell in holdingCells)
        {
            var spawnPoint = holdingCell.AssignPlayerToSpawnPoint(playerName);
            if (spawnPoint != null)
            {
                ModLogger.Info($"Assigned {playerName} to {holdingCell.cellName} at spawn point {spawnPoint.name}");
                return spawnPoint;
            }
        }
        
        ModLogger.Warn($"No available holding cell spawn points - all {holdingCells.Count} holding cells are full!");
        return null;
    }
    
    /// <summary>
    /// Releases a player from their holding cell spawn point using the new system
    /// </summary>
    /// <param name="playerName">Player name/ID to release</param>
    public void ReleasePlayerFromHoldingCell(string playerName)
    {
        // Find the holding cell containing this player
        foreach (var holdingCell in holdingCells)
        {
            // Check if this player is in this holding cell
            if (holdingCell.spawnPointOccupancy.Any(sp => sp.occupantName == playerName))
            {
                holdingCell.ReleasePlayerFromSpawnPoint(playerName);
                ModLogger.Info($"Released {playerName} from {holdingCell.cellName}");
                return;
            }
        }
        
        ModLogger.Warn($"Player {playerName} not found in any holding cell");
    }
    
    /// <summary>
    /// Gets the first available (unoccupied) regular jail cell for longer sentences
    /// </summary>
    /// <returns>Available cell, or null if all are occupied</returns>
    public CellDetail GetAvailableJailCell()
    {
        return cells.FirstOrDefault(c => !c.isOccupied);
    }
    
    /// <summary>
    /// Gets holding cell availability status using the new system
    /// </summary>
    public (int totalSpawnPoints, int availableSpawnPoints, int occupiedSpawnPoints, int totalCells) GetHoldingCellStatus()
    {
        int totalSpawnPoints = 0;
        int occupiedSpawnPoints = 0;
        
        foreach (var holdingCell in holdingCells)
        {
            var (current, max, available) = holdingCell.GetOccupancyStatus();
            totalSpawnPoints += max;
            occupiedSpawnPoints += current;
        }
        
        int availableSpawnPoints = totalSpawnPoints - occupiedSpawnPoints;
        
        return (totalSpawnPoints, availableSpawnPoints, occupiedSpawnPoints, holdingCells.Count);
    }
    
    /// <summary>
    /// Gets the first available (unoccupied) holding cell - backwards compatibility
    /// </summary>
    /// <returns>Available holding cell, or null if all are occupied</returns>
    public CellDetail GetAvailableHoldingCell()
    {
        return holdingCells.FirstOrDefault(c => !c.isOccupied);
    }

    void OnValidate()
    {
        // Handle editor testing controls
        if (Application.isPlaying && cells != null && holdingCells != null)
        {
            if (testAllCellsOpen)
            {
                testAllCellsOpen = false; // Reset the toggle
                OpenAllCells();
            }

            if (testAllCellsLocked)
            {
                testAllCellsLocked = false; // Reset the toggle
                foreach (var cell in cells)
                {
                    cell.LockCell(true);
                }
                foreach (var holdingCell in holdingCells)
                {
                    holdingCell.LockCell(true);
                }
                Debug.Log("🔒 All cells locked via editor control");
            }

            if (testHoldingAreaLocked)
            {
                testHoldingAreaLocked = false; // Reset the toggle
                booking.LockAllDoors();
                Debug.Log("🔒 Booking area locked via editor control");
            }

            if (testEmergencyLockdown)
            {
                testEmergencyLockdown = false; // Reset the toggle
                EmergencyLockdown();
            }
        }
    }
}