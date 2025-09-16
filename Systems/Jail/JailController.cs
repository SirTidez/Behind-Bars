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
#endif

#if MONO
public sealed class JailController : MonoBehaviour
#else
public sealed class JailController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
{
    [Header("Core Components")]
    public GameObject jailDoorPrefab;
    public GameObject steelDoorPrefab;
    public GameObject securityCameraPrefab;

    [Header("Controllers")]
    public JailLightingController lightingController;
    public JailMonitorController monitorController;
    public JailDoorController doorController;
    public JailCellManager cellManager;
    public JailPatrolManager patrolManager;
    public JailAreaManager areaManager;

    [Header("System Data")]
    public List<SecurityCamera> securityCameras = new List<SecurityCamera>();

    [Header("Debug")]
    public bool showDebugInfo = false;
    public float cameraDownwardAngle = 15f;

    [Header("Key Bindings")]
    public KeyCode emergencyLockdownKey = KeyCode.L;
    public KeyCode unlockAllKey = KeyCode.U;
    public KeyCode openAllCellsKey = KeyCode.O;
    public KeyCode closeAllCellsKey = KeyCode.C;
    public KeyCode blackoutKey = KeyCode.B;
    public KeyCode normalLightingKey = KeyCode.N;

    // Properties for backward compatibility
    public List<CellDetail> cells => cellManager?.cells ?? new List<CellDetail>();
    public List<CellDetail> holdingCells => cellManager?.holdingCells ?? new List<CellDetail>();
    public List<Transform> patrolPoints => patrolManager?.GetPatrolPoints() ?? new List<Transform>();
    public BookingArea booking => areaManager?.GetBooking() ?? new BookingArea();
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

        // Additional lighting controls
        if (Input.GetKeyDown(blackoutKey))
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

        if (Input.GetKeyDown(normalLightingKey))
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

        // Set prefab references before initializing door controller
        doorController.jailDoorPrefab = jailDoorPrefab;
        doorController.steelDoorPrefab = steelDoorPrefab;
        doorController.Initialize(cellManager.cells, cellManager.holdingCells, areaManager.GetBooking(), this);

        lightingController.Initialize(transform);

        ModLogger.Info("✓ All controllers initialized");
    }

    void SetupSecurityCameras()
    {
        // First, create/setup the actual security cameras
        CreateSecurityCameras();

        // Then setup monitor assignments using the monitor controller
        monitorController.Initialize(transform, securityCameras);
        ModLogger.Info($"Security camera setup completed with {securityCameras.Count} cameras");
    }

    void CreateSecurityCameras()
    {
        securityCameras.Clear();

        DiscoverSecurityCameraPositions();

        foreach (var camera in securityCameras)
        {
            SetupSecurityCameras(camera.transform);
        }

        ModLogger.Info($"✓ Created {securityCameras.Count} security cameras");
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

        ModLogger.Info($"✓ Configured camera: {cameraName} (Type: {camera.cameraType})");
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
}