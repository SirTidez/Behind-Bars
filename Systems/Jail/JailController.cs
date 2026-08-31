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
    // JailController is the scene-level composition root. Managers own their own
    // runtime state; these references only establish initialization order and provide
    // compatibility delegation for older callers.
    public BookingProcess BookingProcessController { get; set; }

    public GameObject jailDoorPrefab;
    public GameObject steelDoorPrefab;
    public GameObject securityCameraPrefab;

    public JailLightingController lightingController;
    public JailMonitorController monitorController;
    public SecurityCameraCullingManager cameraCullingManager;
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

    // Camera records are rebuilt from the authored SecurityCameras hierarchy before
    // monitor assignment; they are not persisted scene data.
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

    // Backward-compatible views delegate to the owning managers. Missing managers return
    // fresh empty records so diagnostics can continue without mutating a fake singleton.
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
        if (!Behind_Bars.Core.EnableDeveloperShortcuts)
        {
            return;
        }

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

    /// <summary>
    /// Initializes jail managers in dependency order, discovers security cameras and
    /// triggers, and optionally emits a status snapshot. Repeated calls rebuild the
    /// manager-owned scene lists and should be treated as a scene setup operation.
    /// </summary>
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

    // Components are created through the project-safe helper before their authored
    // lists are initialized. A missing required manager aborts the remainder of setup.
    void InitializeControllers()
    {
        ModLogger.Debug("InitializeControllers: ensuring jail controller components");

        // Create controllers if they don't exist
        ModLogger.Debug("InitializeControllers: JailLightingController");
        if (lightingController == null)
            lightingController = Helpers.AddComponentSafe<JailLightingController>(gameObject);

        ModLogger.Debug("InitializeControllers: JailMonitorController");
        if (monitorController == null)
            monitorController = Helpers.AddComponentSafe<JailMonitorController>(gameObject);

        ModLogger.Debug("InitializeControllers: SecurityCameraCullingManager");
        if (cameraCullingManager == null)
            cameraCullingManager = Helpers.AddComponentSafe<SecurityCameraCullingManager>(gameObject);

        ModLogger.Debug("InitializeControllers: JailDoorController");
        if (doorController == null)
            doorController = Helpers.AddComponentSafe<JailDoorController>(gameObject);

        ModLogger.Debug("InitializeControllers: JailCellManager");
        if (cellManager == null)
            cellManager = Helpers.AddComponentSafe<JailCellManager>(gameObject);

        ModLogger.Debug("InitializeControllers: JailPatrolManager");
        if (patrolManager == null)
            patrolManager = Helpers.AddComponentSafe<JailPatrolManager>(gameObject);

        ModLogger.Debug("InitializeControllers: JailAreaManager");
        if (areaManager == null)
            areaManager = Helpers.AddComponentSafe<JailAreaManager>(gameObject);

        if (lightingController == null || monitorController == null || doorController == null || cellManager == null || patrolManager == null || areaManager == null)
        {
            ModLogger.Error("Failed to initialize one or more jail controllers - aborting jail controller initialization");
            return;
        }

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
        doorController.Initialize(cellManager.cells, cellManager.holdingCells, areaManager.GetBooking(), this, false);

        if (jailDoorPrefab != null || steelDoorPrefab != null)
        {
            doorController.SetupDoors();
        }

        ConfigureIntakeCorridorDoorOpening();

        lightingController.Initialize(transform);

        // Performance: Initialize NPCUpdateManager for event-driven NPC updates
        InitializeNPCUpdateSystem();

        ModLogger.Debug("✓ All controllers initialized");
    }

    /// <summary>
    /// Narrows only the Booking_InnerDoor swing. This is the intake-side door to the shared
    /// hallway; a full 135 degree opening lets the player outrun the escort trigger.
    /// </summary>
    void ConfigureIntakeCorridorDoorOpening()
    {
        const float intakeCorridorOpenAngle = 105f;
        JailDoor bookingInnerDoor = areaManager?.GetBooking()?.bookingInnerDoor;
        if (bookingInnerDoor == null)
        {
            ModLogger.Warn("Booking_InnerDoor was not available to configure its intake corridor opening");
            return;
        }

        float direction = bookingInnerDoor.openAngle < 0f ? -1f : 1f;
        bookingInnerDoor.openAngle = direction * intakeCorridorOpenAngle;
        ModLogger.Info($"Configured Booking_InnerDoor to open {intakeCorridorOpenAngle:0} degrees toward the intake corridor");
    }

    /// <summary>
    /// Initialize direct references to all guard points from the documented jail structure
    /// </summary>
    void InitializeGuardPointReferences()
    {
        // Get direct references to guard points based on JAIL_STRUCTURE_DOCUMENTATION.md
        mugshotStationGuardPoint = transform.Find("Booking/MugshotStation/GuardPoint");
        scannerStationGuardPoint = transform.Find("Booking/ScannerStation/GuardPoint");
        exitScannerStationGuardPoint = transform.Find("Hallway/ExitScannerStation/GuardPoint");
        if (exitScannerStationGuardPoint == null)
        {
            exitScannerStationGuardPoint = transform.Find("ExitScannerStation/GuardPoint");
        }
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
    /// Initialize the centralized NPC update manager for event-driven updates.
    /// Performance: Reduces per-NPC Update() overhead by consolidating into throttled intervals.
    /// </summary>
    void InitializeNPCUpdateSystem()
    {
        // Create NPCUpdateManager if it doesn't exist
        var updateManager = Helpers.FindObjectOfTypeSafe<Behind_Bars.Systems.NPCs.NPCUpdateManager>();
        if (updateManager == null)
        {
            var managerObj = new GameObject("NPCUpdateManager");
            Helpers.AddComponentSafe<Behind_Bars.Systems.NPCs.NPCUpdateManager>(managerObj);
            ModLogger.Info("✓ NPCUpdateManager initialized - Event-driven NPC updates enabled");
        }
        else
        {
            ModLogger.Debug("NPCUpdateManager already exists");
        }
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

    // Camera components must exist before monitors and culling are initialized; this
    // method therefore owns the camera -> monitor -> culling ordering.
    void SetupSecurityCameras()
    {
        // First, create/setup the actual security cameras
        CreateSecurityCameras();

        // Then setup monitor assignments using the monitor controller
        monitorController.Initialize(transform, securityCameras);
        
        // Initialize camera culling manager after monitors are set up
        if (cameraCullingManager != null)
        {
            cameraCullingManager.Initialize(securityCameras, monitorController.monitorAssignments, transform);
            ModLogger.Debug("Security camera culling manager initialized");
        }
        
        ModLogger.Debug($"Security camera setup completed with {securityCameras.Count} cameras");
    }

    // Rebuild the runtime camera list from authored positions. Existing camera
    // components are reused rather than duplicated.
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

    // Security camera positions are direct children of SecurityCameras; child order is
    // preserved only for deterministic discovery/logging, not as a gameplay index.
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

    // Resolve or add one safe camera component at the authored position, then classify
    // it from its name before monitor assignment consumes the list.
    void SetupSecurityCameras(Transform cameraPosition)
    {
        SecurityCamera existingCamera = Helpers.GetComponentSafe<SecurityCamera>(cameraPosition.gameObject);
        if (existingCamera != null)
        {
            if (!securityCameras.Contains(existingCamera))
            {
                securityCameras.Add(existingCamera);
                ConfigureSecurityCamera(existingCamera, cameraPosition.name);
            }
            return;
        }

        SecurityCamera camera = Helpers.AddComponentSafe<SecurityCamera>(cameraPosition.gameObject);
        securityCameras.Add(camera);
        ConfigureSecurityCamera(camera, cameraPosition.name);
    }

    // Called once for each discovered camera. It also applies cameraDownwardAngle, so a
    // repeated setup call would accumulate the rotation adjustment.
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

    // Public API methods delegate to the owning controllers. These wrappers intentionally
    // do not duplicate manager state or provide fallback behavior of their own.
    /// <summary>
    /// Locks jail doors through JailDoorController and switches lighting to Emergency.
    /// </summary>
    public void EmergencyLockdown()
    {
        // Lock all doors
        doorController?.EmergencyLockdown();

        // Set emergency lighting (like the old version)
        lightingController?.SetJailLighting(JailLightingController.LightingState.Emergency);

        ModLogger.Info("🔒 EMERGENCY LOCKDOWN ACTIVATED! Doors locked, emergency lighting enabled.");
    }
    /// <summary>Unlocks all jail doors through the door controller and restores normal lighting.</summary>
    public void UnlockAll()
    {
        // Unlock all doors
        doorController?.UnlockAll();

        // Restore normal lighting (like the old version)
        lightingController?.SetJailLighting(JailLightingController.LightingState.Normal);

        ModLogger.Info("🔓 All doors unlocked! Normal lighting restored.");
    }
    /// <summary>Opens every regular jail-cell door through the door controller.</summary>
    public void OpenAllCells() => doorController?.OpenAllCells();

    /// <summary>Closes every regular jail-cell door through the door controller.</summary>
    public void CloseAllCells() => doorController?.CloseAllCells();

    /// <summary>Switches the jail lighting controller to its Blackout state.</summary>
    public void Blackout() => lightingController?.SetJailLighting(JailLightingController.LightingState.Blackout);

    /// <summary>Applies a complete jail lighting state through the lighting controller.</summary>
    /// <param name="state">Lighting state to apply.</param>
    public void SetJailLighting(JailLightingController.LightingState state) => lightingController?.SetJailLighting(state);

    /// <summary>Toggles one named area through the lighting controller.</summary>
    /// <param name="areaName">Configured area name.</param>
    public void ToggleAreaLighting(string areaName) => lightingController?.ToggleAreaLighting(areaName);

    /// <summary>Sets one named area enabled or disabled through the lighting controller.</summary>
    /// <param name="areaName">Configured area name.</param>
    /// <param name="enabled">Whether the area lights should be enabled.</param>
    public void SetAreaLighting(string areaName, bool enabled) => lightingController?.SetAreaLighting(areaName, enabled);

    /// <summary>Advances every monitor assignment to its next available camera.</summary>
    public void RotateAllMonitors() => monitorController?.RotateAllMonitors();

    /// <summary>Assigns a camera to a monitor through the monitor controller.</summary>
    /// <param name="monitor">Monitor surface to update.</param>
    /// <param name="camera">Camera source to display.</param>
    public void SetMonitorCamera(MonitorController monitor, SecurityCamera camera) => monitorController?.SetMonitorCamera(monitor, camera);

    /// <summary>Assigns a player to the first free holding-cell spawn point.</summary>
    /// <param name="player">Player entering custody.</param>
    /// <returns>Reserved spawn transform, or null when no manager/space exists.</returns>
    public Transform AssignPlayerToHoldingCell(Player player) => cellManager?.AssignPlayerToHoldingCell(player);

    /// <summary>Assigns a player to a named holding-cell spawn point.</summary>
    /// <param name="player">Player entering custody.</param>
    /// <param name="holdingCellName">Authored holding-cell transform name.</param>
    /// <returns>Reserved spawn transform, or null when assignment fails.</returns>
    public Transform AssignPlayerToHoldingCellByName(Player player, string holdingCellName) => cellManager?.AssignPlayerToHoldingCellByName(player, holdingCellName);

    /// <summary>Releases the player's holding-cell occupancy reservation.</summary>
    /// <param name="player">Player whose reservation should be cleared.</param>
    public void ReleasePlayerFromHoldingCell(Player player) => cellManager?.ReleasePlayerFromHoldingCell(player);

    /// <summary>Returns the first available regular jail cell, if the cell manager exists.</summary>
    public CellDetail GetAvailableJailCell() => cellManager?.GetAvailableJailCell();

    /// <summary>Returns the first holding cell with free spawn capacity.</summary>
    public CellDetail GetAvailableHoldingCell() => cellManager?.GetAvailableHoldingCell();

    /// <summary>Finds a regular cell by its authored cell index.</summary>
    /// <param name="cellIndex">Authored CellDetail index.</param>
    public CellDetail GetCellByIndex(int cellIndex) => cellManager?.GetCellByIndex(cellIndex);

    /// <summary>Finds a holding cell by its stored cell index.</summary>
    /// <param name="cellIndex">Stored holding-cell index.</param>
    public CellDetail GetHoldingCellByIndex(int cellIndex) => cellManager?.GetHoldingCellByIndex(cellIndex);

    /// <summary>Finds a holding cell by its authored transform name.</summary>
    /// <param name="holdingCellName">Exact holding-cell name.</param>
    public CellDetail GetHoldingCellByName(string holdingCellName) => cellManager?.GetHoldingCellByName(holdingCellName);

    /// <summary>Gets the compact holding-cell list index used by bounds and door checks.</summary>
    /// <param name="holdingCellName">Exact holding-cell name.</param>
    public int GetHoldingCellRuntimeIndexByName(string holdingCellName) => cellManager?.GetHoldingCellRuntimeIndexByName(holdingCellName) ?? -1;

    /// <summary>Finds the compact holding-cell list index containing the player.</summary>
    /// <param name="player">Player to locate.</param>
    public int FindPlayerHoldingCell(Player player) => cellManager?.FindPlayerHoldingCell(player) ?? -1;

    /// <summary>Tests player position against a compact holding-cell list index.</summary>
    /// <param name="player">Player to test.</param>
    /// <param name="holdingCellIndex">Compact holding-cell list index.</param>
    public bool IsPlayerInHoldingCellBounds(Player player, int holdingCellIndex) => cellManager?.IsPlayerInHoldingCellBounds(player, holdingCellIndex) ?? false;

    /// <summary>Returns whether the player has cleared the specified holding-cell boundary.</summary>
    /// <param name="player">Player to test.</param>
    /// <param name="holdingCellIndex">Compact holding-cell list index.</param>
    public bool HasPlayerExitedHoldingCell(Player player, int holdingCellIndex) => cellManager?.HasPlayerExitedHoldingCell(player, holdingCellIndex) ?? true;

    /// <summary>Tests player position against an authored regular jail-cell index.</summary>
    /// <param name="player">Player to test.</param>
    /// <param name="cellIndex">Authored cell index.</param>
    public bool IsPlayerInJailCellBounds(Player player, int cellIndex) => cellManager?.IsPlayerInJailCellBounds(player, cellIndex) ?? false;

    /// <summary>Returns the first matching area name for a world position.</summary>
    /// <param name="playerPosition">World position to classify.</param>
    public string GetPlayerCurrentArea(Vector3 playerPosition) => areaManager?.GetPlayerCurrentArea(playerPosition) ?? "Unknown";

    /// <summary>Returns a copy of the currently discovered patrol-point list.</summary>
    public List<Transform> GetPatrolPoints() => patrolManager?.GetPatrolPoints() ?? new List<Transform>();

    /// <summary>Rebuilds patrol points from this jail root through JailPatrolManager.</summary>
    public void InitializePatrolPoints() => patrolManager?.Initialize(transform);
    /// <summary>
    /// Rebuilds authored jail, holding, booking, and exit doors through the door controller.
    /// </summary>
    public void SetupDoors()
    {
        if (doorController == null)
            return;

        doorController.jailDoorPrefab = jailDoorPrefab;
        doorController.steelDoorPrefab = steelDoorPrefab;
        doorController.SetupDoors();
    }
    /// <summary>Returns the lighting controller's area records, or an empty list if unavailable.</summary>
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
    /// Discovers and logs the authored door-trigger objects used by escort diagnostics.
    /// The active component wiring remains disabled in this branch, so this method does
    /// not currently install DoorTriggerHandler instances or configure colliders.
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
