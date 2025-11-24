using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BehindBars.Areas;
using MelonLoader;
using UnityEngine.UI;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems;
using Behind_Bars.Utils;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

#if MONO
public sealed class JailController : MonoBehaviour
#else
public sealed class JailController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
{
    public BookingProcess BookingProcessController { get; set; }

    public GameObject jailDoorPrefab;
    public GameObject steelDoorPrefab;
    public GameObject securityCameraPrefab;

    public JailLightingController lightingController;
    public JailMonitorController monitorController;
    public JailDoorController doorController;
    public JailCellManager cellManager;
    public JailPatrolManager patrolManager;
    public JailAreaManager areaManager;

#if MONO
    [Header("Guard Points - Direct References")]
#endif
    public Transform mugshotStationGuardPoint;
    public Transform scannerStationGuardPoint;
    public Transform exitScannerStationGuardPoint;
    public Transform storageGuardPoint;
    public Transform holdingCell00GuardPoint;
    public Transform holdingCell01GuardPoint;

    public List<SecurityCamera> securityCameras = new List<SecurityCamera>();

    public bool showDebugInfo = false;
    public float cameraDownwardAngle = 15f;

    public KeyCode modifierKey = KeyCode.LeftAlt;
    public KeyCode emergencyLockdownKey = KeyCode.L;
    public KeyCode unlockAllKey = KeyCode.U;
    public KeyCode openAllCellsKey = KeyCode.O;
    public KeyCode closeAllCellsKey = KeyCode.C;
    public KeyCode blackoutKey = KeyCode.H;
    public KeyCode normalLightingKey = KeyCode.N;

    // Properties for backward compatibility
    public List<CellDetail> cells => cellManager?.cells ?? new List<CellDetail>();
    public List<CellDetail> holdingCells => cellManager?.holdingCells ?? new List<CellDetail>();
    public List<Transform> patrolPoints => patrolManager?.GetPatrolPoints() ?? new List<Transform>();
    public BookingArea booking => areaManager?.GetBooking() ?? new BookingArea();
    public StorageArea storage => areaManager?.GetStorage() ?? new StorageArea();
    public ExitScannerArea exitScanner => areaManager?.GetExitScanner() ?? new ExitScannerArea();
    public KitchenArea kitchen => areaManager?.GetKitchen() ?? new KitchenArea();
    public LaundryArea laundry => areaManager?.GetLaundry() ?? new LaundryArea();
    public PhoneArea phoneArea => areaManager?.GetPhoneArea() ?? new PhoneArea();
    public GuardRoomArea guardRoom => areaManager?.GetGuardRoom() ?? new GuardRoomArea();
    public MainRecArea mainRec => areaManager?.GetMainRec() ?? new MainRecArea();
    public ShowerArea showers => areaManager?.GetShowers() ?? new ShowerArea();

    void Start()
    {
        InitializeJail();
    }

    void Update()
    {
        // Controllers handle their own updates
        HandleLightingKeyBindings();
    }

    void HandleLightingKeyBindings()
    {
        // Guard control inputs (restored from old version)
        if (Input.GetKey(modifierKey) && Input.GetKeyDown(emergencyLockdownKey))
        {
            EmergencyLockdown();
        }

        if (Input.GetKey(modifierKey) && Input.GetKeyDown(unlockAllKey))
        {
            UnlockAll();
        }

        if (Input.GetKey(modifierKey) && Input.GetKeyDown(openAllCellsKey))
        {
            OpenAllCells();
        }

        if (Input.GetKey(modifierKey) && Input.GetKeyDown(closeAllCellsKey))
        {
            CloseAllCells();
        }

        // Additional lighting controls
        if (Input.GetKey(modifierKey) && Input.GetKeyDown(blackoutKey))
        {
            ModLogger.Info("Blackout key pressed");
            if (lightingController == null)
            {
                ModLogger.Error("LightingController is null!");
                return;
            }
            Blackout();
            ModLogger.Info("Blackout command sent");
        }

        if (Input.GetKey(modifierKey) && Input.GetKeyDown(normalLightingKey))
        {
            ModLogger.Info("Normal lighting key pressed");
            if (lightingController == null)
            {
                ModLogger.Error("LightingController is null!");
                return;
            }
            SetJailLighting(JailLightingController.LightingState.Normal);
            ModLogger.Info("Normal lighting command sent");
        }
    }

    public void InitializeJail()
    {
        // Initialize all controllers
        InitializeControllers();

        // Setup security cameras
        SetupSecurityCameras();

        // Setup door triggers for automatic door handling
        SetupDoorTriggers();

        if (showDebugInfo)
        {
            LogJailStatus();
        }
    }

    void InitializeControllers()
    {
        // Create controllers if they don't exist
        if (lightingController == null)
            lightingController = gameObject.AddComponent<JailLightingController>();
        if (monitorController == null)
            monitorController = gameObject.AddComponent<JailMonitorController>();
        if (doorController == null)
            doorController = gameObject.AddComponent<JailDoorController>();
        if (cellManager == null)
            cellManager = gameObject.AddComponent<JailCellManager>();
        if (patrolManager == null)
            patrolManager = gameObject.AddComponent<JailPatrolManager>();
        if (areaManager == null)
            areaManager = gameObject.AddComponent<JailAreaManager>();

        // Initialize each controller
        cellManager.Initialize(transform);
        areaManager.Initialize(transform);
        patrolManager.Initialize(transform);

        // Initialize direct guard point references
        InitializeGuardPointReferences();

        // Set prefab references before initializing door controller
        doorController.jailDoorPrefab = jailDoorPrefab;
        doorController.steelDoorPrefab = steelDoorPrefab;
        doorController.modifierKey = modifierKey;
        doorController.Initialize(cellManager.cells, cellManager.holdingCells, areaManager.GetBooking(), this);

        lightingController.Initialize(transform);

        ModLogger.Debug("✓ All controllers initialized");
    }

    /// <summary>
    /// Initialize direct references to all guard points from the documented jail structure
    /// </summary>
    void InitializeGuardPointReferences()
    {
        // Get direct references to guard points based on JAIL_STRUCTURE_DOCUMENTATION.md
        mugshotStationGuardPoint = transform.Find("Booking/MugshotStation/GuardPoint");
        scannerStationGuardPoint = transform.Find("Booking/ScannerStation/GuardPoint");
        exitScannerStationGuardPoint = transform.Find("ExitScannerStation/GuardPoint");
        storageGuardPoint = transform.Find("Storage/GuardPoint");
        holdingCell00GuardPoint = transform.Find("Cells/HoldingCells/HoldingCell_00/HoldingDoorHolder[0]/DoorPoint");
        holdingCell01GuardPoint = transform.Find("Cells/HoldingCells/HoldingCell_01/HoldingDoorHolder[1]/DoorPoint");

        // Log what we found
        ModLogger.Debug($"✓ Guard point references initialized:");
        ModLogger.Debug($"  MugshotStation GuardPoint: {(mugshotStationGuardPoint != null ? "FOUND" : "MISSING")}");
        ModLogger.Debug($"  ScannerStation GuardPoint: {(scannerStationGuardPoint != null ? "FOUND" : "MISSING")}");
        ModLogger.Debug($"  ExitScannerStation GuardPoint: {(exitScannerStationGuardPoint != null ? "FOUND" : "MISSING")}");
        ModLogger.Debug($"  Storage GuardPoint: {(storageGuardPoint != null ? "FOUND" : "MISSING")}");
        ModLogger.Debug($"  HoldingCell_00 GuardPoint: {(holdingCell00GuardPoint != null ? "FOUND" : "MISSING")}");
        ModLogger.Debug($"  HoldingCell_01 GuardPoint: {(holdingCell01GuardPoint != null ? "FOUND" : "MISSING")}");
    }

    /// <summary>
    /// Get guard point for a specific station - NO FINDS, direct references only
    /// </summary>
    public Transform GetGuardPoint(string stationName)
    {
        switch (stationName)
        {
            case "MugshotStation":
                return mugshotStationGuardPoint;
            case "ScannerStation":
                return scannerStationGuardPoint;
            case "ExitScannerStation":
                return exitScannerStationGuardPoint;
            case "Storage":
                return storageGuardPoint;
            case "HoldingCell_00":
                return holdingCell00GuardPoint;
            case "HoldingCell_01":
                return holdingCell01GuardPoint;
            default:
                ModLogger.Warn($"Unknown guard point station: {stationName}");
                return null;
        }
    }

    void SetupSecurityCameras()
    {
        // First, create/setup the actual security cameras
        CreateSecurityCameras();

        // Then setup monitor assignments using the monitor controller
        monitorController.Initialize(transform, securityCameras);
        ModLogger.Debug($"Security camera setup completed with {securityCameras.Count} cameras");
    }

    void CreateSecurityCameras()
    {
        securityCameras.Clear();

        DiscoverSecurityCameraPositions();

        foreach (var camera in securityCameras)
        {
            SetupSecurityCameras(camera.transform);
        }

        ModLogger.Debug($"✓ Created {securityCameras.Count} security cameras");
    }

    void DiscoverSecurityCameraPositions()
    {
        Transform camerasParent = transform.Find("SecurityCameras");
        if (camerasParent == null)
        {
            ModLogger.Warn("SecurityCameras parent not found!");
            return;
        }

        for (int i = 0; i < camerasParent.childCount; i++)
        {
            Transform cameraPosition = camerasParent.GetChild(i);
            SetupSecurityCameras(cameraPosition);
        }
    }

    void SetupSecurityCameras(Transform cameraPosition)
    {
        SecurityCamera existingCamera = cameraPosition.GetComponent<SecurityCamera>();
        if (existingCamera != null)
        {
            if (!securityCameras.Contains(existingCamera))
            {
                securityCameras.Add(existingCamera);
                ConfigureSecurityCamera(existingCamera, cameraPosition.name);
            }
            return;
        }

        SecurityCamera camera = cameraPosition.gameObject.AddComponent<SecurityCamera>();
        securityCameras.Add(camera);
        ConfigureSecurityCamera(camera, cameraPosition.name);
    }

    void ConfigureSecurityCamera(SecurityCamera camera, string cameraName)
    {
        camera.cameraName = cameraName;
        camera.SetupRenderTexture();

        if (cameraName.ToLower().Contains("main") || cameraName.ToLower().Contains("front") || cameraName.ToLower().Contains("back"))
        {
            camera.cameraType = SecurityCamera.CameraType.MainView;
        }
        else if (cameraName.ToLower().Contains("phone"))
        {
            camera.cameraType = SecurityCamera.CameraType.PhoneArea;
        }
        else if (cameraName.ToLower().Contains("holding"))
        {
            camera.cameraType = SecurityCamera.CameraType.HoldingCell;
        }
        else if (cameraName.ToLower().Contains("hall"))
        {
            camera.cameraType = SecurityCamera.CameraType.Hall;
        }
        else
        {
            camera.cameraType = SecurityCamera.CameraType.Other;
        }

        if (camera.cameraComponent != null)
        {
            Vector3 currentRotation = camera.cameraComponent.transform.eulerAngles;
            camera.cameraComponent.transform.eulerAngles = new Vector3(
                currentRotation.x - cameraDownwardAngle,
                currentRotation.y,
                currentRotation.z
            );
        }

        ModLogger.Debug($"✓ Configured camera: {cameraName} (Type: {camera.cameraType})");
    }

    // Public API methods - delegate to appropriate controllers
    public void EmergencyLockdown()
    {
        // Lock all doors
        doorController?.EmergencyLockdown();

        // Set emergency lighting (like the old version)
        lightingController?.SetJailLighting(JailLightingController.LightingState.Emergency);

        ModLogger.Info("🔒 EMERGENCY LOCKDOWN ACTIVATED! Doors locked, emergency lighting enabled.");
    }
    public void UnlockAll()
    {
        // Unlock all doors
        doorController?.UnlockAll();

        // Restore normal lighting (like the old version)
        lightingController?.SetJailLighting(JailLightingController.LightingState.Normal);

        ModLogger.Info("🔓 All doors unlocked! Normal lighting restored.");
    }
    public void OpenAllCells() => doorController?.OpenAllCells();
    public void CloseAllCells() => doorController?.CloseAllCells();
    public void Blackout() => lightingController?.SetJailLighting(JailLightingController.LightingState.Blackout);
    public void SetJailLighting(JailLightingController.LightingState state) => lightingController?.SetJailLighting(state);
    public void ToggleAreaLighting(string areaName) => lightingController?.ToggleAreaLighting(areaName);
    public void SetAreaLighting(string areaName, bool enabled) => lightingController?.SetAreaLighting(areaName, enabled);
    public void RotateAllMonitors() => monitorController?.RotateAllMonitors();
    public void SetMonitorCamera(MonitorController monitor, SecurityCamera camera) => monitorController?.SetMonitorCamera(monitor, camera);
    public Transform AssignPlayerToHoldingCell(string playerName) => cellManager?.AssignPlayerToHoldingCell(playerName);
    public void ReleasePlayerFromHoldingCell(string playerName) => cellManager?.ReleasePlayerFromHoldingCell(playerName);
    public CellDetail GetAvailableJailCell() => cellManager?.GetAvailableJailCell();
    public CellDetail GetAvailableHoldingCell() => cellManager?.GetAvailableHoldingCell();
    public CellDetail GetCellByIndex(int cellIndex) => cellManager?.GetCellByIndex(cellIndex);
    public CellDetail GetHoldingCellByIndex(int cellIndex) => cellManager?.GetHoldingCellByIndex(cellIndex);
    public int FindPlayerHoldingCell(Player player) => cellManager?.FindPlayerHoldingCell(player) ?? -1;
    public bool IsPlayerInHoldingCellBounds(Player player, int holdingCellIndex) => cellManager?.IsPlayerInHoldingCellBounds(player, holdingCellIndex) ?? false;
    public bool HasPlayerExitedHoldingCell(Player player, int holdingCellIndex) => cellManager?.HasPlayerExitedHoldingCell(player, holdingCellIndex) ?? true;
    public bool IsPlayerInJailCellBounds(Player player, int cellIndex) => cellManager?.IsPlayerInJailCellBounds(player, cellIndex) ?? false;
    public string GetPlayerCurrentArea(Vector3 playerPosition) => areaManager?.GetPlayerCurrentArea(playerPosition) ?? "Unknown";
    public List<Transform> GetPatrolPoints() => patrolManager?.GetPatrolPoints() ?? new List<Transform>();
    public void InitializePatrolPoints() => patrolManager?.Initialize(transform);
    public void SetupDoors() => doorController?.SetupDoors();
    public List<JailLightingController.AreaLighting> areaLights => lightingController?.areaLights ?? new List<JailLightingController.AreaLighting>();

    // Test methods - delegate to appropriate controllers
    public void TestHoldingCellDiscovery() => cellManager?.TestHoldingCellDiscovery();
    public void TestHoldingCellSpawnSystem() => cellManager?.TestHoldingCellSpawnSystem();
    public void TestMonitorSystem() => monitorController?.TestMonitorSystem();
    public void ForceSetupAllMonitors() => monitorController?.ForceSetupAllMonitors();
    public void EmergencyLightingTest() => lightingController?.EmergencyLightingTest();
    public void NormalLightingTest() => lightingController?.NormalLightingTest();
    public void BlackoutTest() => lightingController?.BlackoutTest();
    public void TestAreaSystem() => areaManager?.TestAreaSystem();
    public void LockDownAllAreas() => areaManager?.LockDownAllAreas();
    public void OpenAllAreas() => areaManager?.OpenAllAreas();
    public void TestPlayerPosition() => areaManager?.TestPlayerPosition();

    // Additional lighting test methods
    public void TestLightingSystem()
    {
        if (lightingController == null)
        {
            ModLogger.Error("Lighting controller is null!");
            return;
        }

        ModLogger.Info("=== TESTING LIGHTING SYSTEM ===");
        ModLogger.Info($"Current lighting state: {lightingController.currentLightingState}");
        ModLogger.Info($"Area lights discovered: {lightingController.areaLights.Count}");
        ModLogger.Info($"Emissive control enabled: {lightingController.enableEmissiveControl}");
        ModLogger.Info($"Emissive material: {(lightingController.emissiveMaterial != null ? lightingController.emissiveMaterial.name : "NULL")}");
        ModLogger.Info($"All emissive materials: {lightingController.allEmissiveMaterials.Count}");

        // Test lighting state changes
        ModLogger.Info("Testing emergency lighting...");
        lightingController.SetJailLighting(JailLightingController.LightingState.Emergency);

        // Wait a moment then test normal lighting
        ModLogger.Info("Testing normal lighting...");
        lightingController.SetJailLighting(JailLightingController.LightingState.Normal);

        ModLogger.Info("=== LIGHTING TEST COMPLETE ===");
    }

    public void LogJailStatus()
    {
        ModLogger.Info($"=== JAIL STATUS ===");
        ModLogger.Info($"Prison Cells: {cells.Count}");
        ModLogger.Info($"Holding Cells: {holdingCells.Count}");
        ModLogger.Info($"Security Cameras: {securityCameras.Count}");
        ModLogger.Info($"Controllers Initialized: {(cellManager != null && doorController != null && lightingController != null && monitorController != null && patrolManager != null && areaManager != null)}");
    }

    public void LogStatus() => LogJailStatus();

    /// <summary>
    /// Programmatically setup door triggers for automatic door handling during escort
    ///
    /// DOOR STRUCTURE (from Unity Hierarchy):
    ///
    /// Booking/
    /// ├── Booking_GuardDoor/
    /// │   ├── GuardRoomDoorTrigger_FromGuardRoom (→ BookingToHall direction)
    /// │   ├── GuardRoomDoorTrigger_FromBooking (→ HallToBooking direction)
    /// │   ├── DoorPoint_GuardRoom
    /// │   └── DoorPoint_Booking
    /// │
    /// ├── Booking_InnerDoor/
    /// │   ├── BookingDoorTrigger_FromBooking (→ BookingToHall direction)
    /// │   ├── BookingDoorTrigger_FromHall (→ HallToBooking direction)
    /// │   ├── DoorPoint_Booking
    /// │   └── DoorPoint_Hall
    /// │
    /// Prison_EnterDoor/
    /// ├── PrisonDoorTrigger_FromHall (→ HallToPrison direction)
    /// ├── PrisonDoorTrigger_FromPrison (→ return direction)
    /// ├── DoorPoint_Hall
    /// └── DoorPoint_Prison
    ///
    /// ESCORT FLOW:
    /// 1. Booking → Hall (BookingDoorTrigger_FromBooking)
    /// 2. Hall → Prison (PrisonDoorTrigger_FromHall)
    /// 3. Return: Prison → Hall → Booking
    /// </summary>
    private void SetupDoorTriggers()
    {
        try
        {
            ModLogger.Debug("Setting up door triggers for escort system...");

            // Debug: Find all objects with "Trigger" in their name
            var allTriggers = FindObjectsOfType<GameObject>().Where(obj => obj.name.Contains("Trigger")).ToList();
            ModLogger.Debug($"Found {allTriggers.Count} objects with 'Trigger' in name:");
            foreach (var trigger in allTriggers)
            {
                ModLogger.Debug($"  - {trigger.name}");
            }

            // Find all door trigger GameObjects using EXACT names from Unity hierarchy
            var doorTriggers = new Dictionary<string, GameObject>
            {
                // Booking_GuardDoor triggers
                { "GuardRoomDoorTrigger_FromGuardRoom", GameObject.Find("GuardRoomDoorTrigger_FromGuardRoom") },
                { "GuardRoomDoorTrigger_FromBooking", GameObject.Find("GuardRoomDoorTrigger_FromBooking") },

                // Booking_InnerDoor triggers
                { "BookingDoorTrigger_FromBooking", GameObject.Find("BookingDoorTrigger_FromBooking") },
                { "BookingDoorTrigger_FromHall", GameObject.Find("BookingDoorTrigger_FromHall") },

                // Prison_EnterDoor triggers
                { "PrisonDoorTrigger_FromHall", GameObject.Find("PrisonDoorTrigger_FromHall") },
                { "PrisonDoorTrigger_FromPrison", GameObject.Find("PrisonDoorTrigger_FromPrison") }
            };

            int triggersConfigured = 0;

            foreach (var kvp in doorTriggers)
            {
                string triggerName = kvp.Key;
                GameObject triggerObject = kvp.Value;

                if (triggerObject == null)
                {
                    ModLogger.Debug($"Door trigger not found: {triggerName}");
                    continue;
                }

                // Add DoorTriggerHandler component if not present - DISABLED FOR NOW
                /*
                var triggerHandler = triggerObject.GetComponent<Behind_Bars.Systems.NPCs.DoorTriggerHandler>();
                if (triggerHandler == null)
                {
                    triggerHandler = triggerObject.AddComponent<Behind_Bars.Systems.NPCs.DoorTriggerHandler>();
                    ModLogger.Debug($"Added DoorTriggerHandler to {triggerName}");
                }

                // Auto-detect and configure the associated door
                JailDoor associatedDoor = FindAssociatedDoorForTrigger(triggerName);
                if (associatedDoor != null)
                {
                    triggerHandler.associatedDoor = associatedDoor;
                    triggerHandler.autoDetectDoor = false; // We've manually assigned it
                    ModLogger.Info($"✓ Configured door trigger: {triggerName} → {associatedDoor.doorName}");
                    triggersConfigured++;
                }
                else
                {
                    triggerHandler.autoDetectDoor = true; // Let it try auto-detection
                    ModLogger.Warn($"Could not find associated door for trigger: {triggerName} - using auto-detection");
                }

                // Ensure the trigger has a collider set as trigger
                var collider = triggerObject.GetComponent<Collider>();
                if (collider != null && !collider.isTrigger)
                {
                    collider.isTrigger = true;
                    ModLogger.Debug($"Set collider as trigger for {triggerName}");
                }
                */
            }

            ModLogger.Debug($"Door trigger setup complete: {triggersConfigured}/6 triggers configured");
        }
        catch (System.Exception e)
        {
            ModLogger.Error($"Error setting up door triggers: {e.Message}");
        }
    }

    /// <summary>
    /// Find the JailDoor associated with a specific door trigger
    /// </summary>
    private JailDoor FindAssociatedDoorForTrigger(string triggerName)
    {
        try
        {
            // Get booking area doors
            var booking = areaManager?.GetBooking();

            switch (triggerName)
            {
                case "GuardRoomDoorTrigger_FromGuardRoom":
                case "GuardRoomDoorTrigger_FromBooking":
                    return booking?.guardDoor;

                case "BookingDoorTrigger_FromBooking":
                case "BookingDoorTrigger_FromHall":
                    return booking?.bookingInnerDoor;

                case "PrisonDoorTrigger_FromHall":
                case "PrisonDoorTrigger_FromPrison":
                    // Prison entry door - find by checking the Prison_EnterDoor GameObject
                    var prisonEnterDoor = GameObject.Find("Prison_EnterDoor");
                    if (prisonEnterDoor != null)
                    {
                        var jailDoorComp = prisonEnterDoor.GetComponent<JailDoor>();
                        if (jailDoorComp != null)
                        {
                            return jailDoorComp;
                        }

                        // Also check children for JailDoor component
                        jailDoorComp = prisonEnterDoor.GetComponentInChildren<JailDoor>();
                        if (jailDoorComp != null)
                        {
                            return jailDoorComp;
                        }
                    }

                    // Fallback: try to find any door with EntryDoor type
                    var allCells = cells?.Concat(holdingCells ?? new List<CellDetail>()) ?? new List<CellDetail>();
                    return allCells.FirstOrDefault(c => c.cellDoor?.doorType == JailDoor.DoorType.EntryDoor)?.cellDoor;

                default:
                    ModLogger.Warn($"Unknown trigger name: {triggerName}");
                    return null;
            }
        }
        catch (System.Exception e)
        {
            ModLogger.Error($"Error finding door for trigger {triggerName}: {e.Message}");
            return null;
        }
    }
}