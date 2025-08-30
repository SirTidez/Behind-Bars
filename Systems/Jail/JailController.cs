using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BehindBars.Areas;


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

    public KitchenArea kitchen = new KitchenArea();
    public LaundryArea laundry = new LaundryArea();
    public PhoneArea phoneArea = new PhoneArea();
    public BookingArea booking = new BookingArea();
    public GuardRoomArea guardRoom = new GuardRoomArea();
    public MainRecArea mainRec = new MainRecArea();
    public ShowerArea showers = new ShowerArea();

    public List<AreaLighting> areaLights = new List<AreaLighting>();
    public LightingState currentLightingState = LightingState.Normal;

    public bool enableLightingLOD = true;
    public float lightCullingDistance = 50f;
    public int maxRealTimeLights = 20;
    public bool preferBakedLighting = true;

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
        else
        {
            Debug.LogError($"Failed to assign texture: Camera.renderTexture={camera.renderTexture != null}, Monitor.screenImage={monitor.screenImage != null}");
        }
    }

    public void InitializeJail()
    {
        DiscoverJailStructure();
        SetupDoors();
        SetupSecurityCameras();

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

            Debug.Log($"Cell setup: DoorHolder={cell.cellDoor.doorHolder != null}, Bounds={cell.cellBounds != null}");

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

            Debug.Log($"Holding cell setup: DoorHolder={holdingCell.cellDoor.doorHolder != null}, Bounds={holdingCell.cellBounds != null}");

            if (holdingCell.IsValid())
            {
                holdingCells.Add(holdingCell);
                Debug.Log($"✓ Successfully added {holdingCell.cellName}");
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

    JailDoor CreateDoorFromHolder(Transform holder, string doorName, JailDoor.DoorType doorType)
    {
        JailDoor door = new JailDoor();
        door.doorHolder = holder;
        door.doorName = doorName;
        door.doorType = doorType;
        return door;
    }


    void SetupDoors()
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
        // Setup booking area doors
        if (booking.prisonEntryDoor != null && booking.prisonEntryDoor.IsValid() && steelDoorPrefab != null)
        {
            InstantiateDoor(booking.prisonEntryDoor, steelDoorPrefab);
        }

        if (booking.guardDoor != null && booking.guardDoor.IsValid() && steelDoorPrefab != null)
        {
            InstantiateDoor(booking.guardDoor, steelDoorPrefab);
        }

        Debug.Log($"Setup area doors");
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
        Debug.Log($"Security camera setup completed with {securityCameras.Count} cameras and {monitorAssignments.Count} monitor assignments");
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

        Debug.Log($"Auto-assigned {monitorAssignments.Count} monitor assignments");
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
        
        // Then add other camera types if we need more
        if (availableCameras.Count < staticMonitorsParent.childCount)
        {
            var otherCameras = securityCameras.Where(cam => 
                cam.cameraType != SecurityCamera.CameraType.MainView).ToList();
            availableCameras.AddRange(otherCameras);
        }

        Debug.Log($"Found {availableCameras.Count} cameras for {staticMonitorsParent.childCount} static monitors");

        // Assign each static monitor to a specific camera
        for (int i = 0; i < staticMonitorsParent.childCount; i++)
        {
            Transform monitorTransform = staticMonitorsParent.GetChild(i);
            MonitorController monitor = FindMonitorController(monitorTransform);

            if (monitor == null)
            {
                Debug.LogWarning($"No MonitorController found on {monitorTransform.name} or its children");
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

            Debug.Log($"✓ Static monitor {monitorTransform.name} → {camera.cameraName}");
        }
    }

    // Helper method to find MonitorController on transform or its children
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
        
        return null;
    }

    void SetupRotatingMonitors(Transform rotatingMonitorsParent)
    {
        Debug.Log($"Setting up rotating monitors from {rotatingMonitorsParent.name}");

        // Get all rotating cameras (non-MainView cameras)
        List<SecurityCamera> rotatingCameras = securityCameras.Where(cam =>
            cam.cameraType != SecurityCamera.CameraType.MainView).ToList();

        Debug.Log($"Found {rotatingCameras.Count} rotating cameras for rotating monitors");

        // Assign each rotating monitor to cycle through cameras without overlap
        for (int i = 0; i < rotatingMonitorsParent.childCount; i++)
        {
            Transform monitorTransform = rotatingMonitorsParent.GetChild(i);
            MonitorController monitor = FindMonitorController(monitorTransform);

            if (monitor == null)
            {
                Debug.LogWarning($"No MonitorController found on {monitorTransform.name} or its children");
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
                Debug.Log($"✓ Rotating monitor {monitorTransform.name} → {initialCamera.cameraName} (every {assignment.rotationInterval}s, starting after {i * 2f}s delay)");
            }
        }
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
        Debug.Log("=== FORCE SETUP ALL MONITORS ===");
        CreateSecurityCameras();
        SetupMonitorAssignments();
        Debug.Log("Setup complete!");
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

    public CellDetail GetCellByIndex(int cellIndex)
    {
        return cells.FirstOrDefault(c => c.cellIndex == cellIndex);
    }

    public CellDetail GetHoldingCellByIndex(int cellIndex)
    {
        return holdingCells.FirstOrDefault(c => c.cellIndex == cellIndex);
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