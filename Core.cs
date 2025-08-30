using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using HarmonyLib;
using System;
using Behind_Bars.Helpers;
using UnityEngine;

using Object = UnityEngine.Object;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif
using Behind_Bars.Players;
using Behind_Bars.Systems;
using Behind_Bars.Harmony;


#if MONO
using FishNet;
using ScheduleOne.UI.Phone;
using ScheduleOne.DevUtilities;
#else
using Il2CppFishNet;
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.UI.Phone;
using Il2CppScheduleOne.DevUtilities;
#endif

[assembly: MelonInfo(
    typeof(Behind_Bars.Core),
    Constants.MOD_NAME,
    Constants.MOD_VERSION,
    Constants.MOD_AUTHOR
)]
[assembly: MelonColor(1, 255, 0, 0)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Behind_Bars
{
    public class Core : MelonMod
    {
        public static Core? Instance { get; private set; }
        public static AssetManager AssetManager { get; private set; }

        // Core systems
        private JailSystem? _jailSystem;
        private BailSystem? _bailSystem;
        private CourtSystem? _courtSystem;
        private ProbationSystem? _probationSystem;

        // Player management
        private Dictionary<Player, PlayerHandler> _playerHandlers = new();
        
        // Jail management
        public static JailController? ActiveJailController { get; private set; }

        public JailSystem? JailSystem => _jailSystem;
        public BailSystem? BailSystem => _bailSystem;
        public CourtSystem? CourtSystem => _courtSystem;
        public ProbationSystem? ProbationSystem => _probationSystem;

        public override void OnInitializeMelon()
        {
            Instance = this;
#if !MONO
            ClassInjector.RegisterTypeInIl2Cpp<ToiletSink>();
            ClassInjector.RegisterTypeInIl2Cpp<ToiletSinkManager>();
            ClassInjector.RegisterTypeInIl2Cpp<BunkBed>();
            ClassInjector.RegisterTypeInIl2Cpp<BunkBedManager>();
            ClassInjector.RegisterTypeInIl2Cpp<CommonRoomTable>();
            ClassInjector.RegisterTypeInIl2Cpp<CommonRoomTableManager>();
            ClassInjector.RegisterTypeInIl2Cpp<CellTable>();
            ClassInjector.RegisterTypeInIl2Cpp<CellTableManager>();
            ClassInjector.RegisterTypeInIl2Cpp<Jail>();
            ClassInjector.RegisterTypeInIl2Cpp<JailManager>();
            
            // Register Jail System Components
            ClassInjector.RegisterTypeInIl2Cpp<JailController>();
            ClassInjector.RegisterTypeInIl2Cpp<SecurityCamera>();
            ClassInjector.RegisterTypeInIl2Cpp<MonitorController>();
#endif
            // Initialize core systems
            HarmonyPatches.Initialize(this);
            _jailSystem = new JailSystem();
            _bailSystem = new BailSystem();
            _courtSystem = new CourtSystem();
            _probationSystem = new ProbationSystem();

            AssetManager = new AssetManager();
            AssetManager.Init();

            ModLogger.Info("Behind Bars initialized with all systems");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            ModLogger.Debug($"Scene initialized: {sceneName} (Build Index: {buildIndex})");

            // Ensure ToiletSinkManager exists
            if (ToiletSinkManager.Instance == null)
            {
                ModLogger.Debug("ToiletSinkManager instance is null, creating...");
            }

            // Spawn furniture when the scene is initialized
            try
            {
                if (sceneName == "Main")
                {
                    // Spawn toilet sink using generic method
                    var toiletSink = AssetManager.SpawnAsset<ToiletSink>(FurnitureType.ToiletSink);
                    if (toiletSink != null)
                    {
                        ModLogger.Info($"Successfully spawned toilet sink on scene initialization: {toiletSink.GetDebugInfo()}");
                        ModLogger.Debug($"Total toilet sinks in scene: {ToiletSinkManager.GetToiletSinkCount()}");
                    }
                    else
                    {
                        ModLogger.Warn("Failed to spawn toilet sink on scene initialization");
                    }

                    // Spawn bunk bed using generic method
                    var bunkBed = AssetManager.SpawnAsset<BunkBed>(FurnitureType.BunkBed);
                    if (bunkBed != null)
                    {
                        ModLogger.Info($"Successfully spawned bunk bed on scene initialization: {bunkBed.GetDebugInfo()}");
                        ModLogger.Debug($"Total bunk beds in scene: {BunkBedManager.GetBunkBedCount()}");
                    }
                    else
                    {
                        ModLogger.Warn("Failed to spawn bunk bed on scene initialization");
                    }

                    // Spawn common room table using generic method
                    var commonRoomTable = AssetManager.SpawnAsset<CommonRoomTable>(FurnitureType.CommonRoomTable);
                    if (commonRoomTable != null)
                    {
                        ModLogger.Info($"Successfully spawned common room table on scene initialization: {commonRoomTable.GetDebugInfo()}");
                        ModLogger.Debug($"Total common room tables in scene: {CommonRoomTableManager.GetCommonRoomTableCount()}");
                    }
                    else
                    {
                        ModLogger.Warn("Failed to spawn common room table on scene initialization");
                    }

                    // Spawn cell table using generic method
                    var cellTable = AssetManager.SpawnAsset<CellTable>(FurnitureType.CellTable);
                    if (cellTable != null)
                    {
                        ModLogger.Info($"Successfully spawned cell table on scene initialization: {cellTable.GetDebugInfo()}");
                        ModLogger.Debug($"Total cell tables in scene: {CellTableManager.GetCellTableCount()}");
                    }
                    else
                    {
                        ModLogger.Warn("Failed to spawn cell table on scene initialization");
                    }

                    // Test the systems after successful spawning
                    TestToiletSinkSystem();
                    TestBunkBedSystem();

                    // Load and setup jail from asset bundle (simplified approach)
                    MelonCoroutines.Start(SetupJail());
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning furniture on scene initialization: {e.Message}");
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ModLogger.Debug($"Scene loaded: {sceneName}");
            if (sceneName == "Main")
            {
                ModLogger.Debug("Main scene loaded, initializing player systems");
                MelonCoroutines.Start(InitializePlayerSystems());
            }
        }

        private IEnumerator InitializePlayerSystems()
        {
            ModLogger.Info("Waiting for player to be ready...");
            #if !MONO
            // IL2CPP - More robust null checking
            while (true)
            {
                try
                {
                    var instance = PlayerSingleton<AppsCanvas>.Instance;
                    if (instance != null && instance.Pointer != System.IntPtr.Zero)
                        break;
                }
                catch
                {
                    // Instance is null or not ready
                }
                yield return null;
            }
            #else
            // Mono - Standard Unity null check
            while (PlayerSingleton<AppsCanvas>.Instance == null)
                yield return null;
            #endif

            // Initialize player handler for local player
            if (Player.Local != null)
            {
                var playerHandler = new PlayerHandler(Player.Local);
                _playerHandlers[Player.Local] = playerHandler;
                // Subscribe to arrest events
#if !MONO
                Player.Local.onArrested.AddListener(new Action(OnPlayerArrested));
#else
                Player.Local.onArrested.AddListener(OnPlayerArrested);
#endif

                ModLogger.Info("Player systems initialized successfully");
            }
            else
            {
                ModLogger.Warn("Player.Local is null, retrying in 2 seconds...");
                yield return new WaitForSeconds(2f);
                MelonCoroutines.Start(InitializePlayerSystems());
            }
        }

        private static IEnumerator SetupJail()
        {
            ModLogger.Info("Setting up jail from asset bundle...");
            
            // Load the behind-bars bundle specifically
            var jailBundle = Utils.AssetBundleUtils.LoadAssetBundle("Behind_Bars.behind_bars");
            if (jailBundle == null)
            {
                ModLogger.Error("Failed to load behind-bars bundle");
                yield break;
            }

            // Debug: List all assets in the bundle
            var allAssets = jailBundle.GetAllAssetNames();
            ModLogger.Info($"Assets in bundle ({allAssets.Length} total):");
            foreach (var asset in allAssets)
            {
                ModLogger.Info($"  - {asset}");
            }

            // Also list all GameObjects
#if MONO
            var gameObjects = jailBundle.LoadAllAssets<GameObject>();
#else
            var gameObjects = jailBundle.LoadAllAssets(Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
#endif
            ModLogger.Info($"GameObjects in bundle ({gameObjects.Length} total):");
            for (int i = 0; i < gameObjects.Length; i++)
            {
#if MONO
                var obj = gameObjects[i];
                ModLogger.Info($"  - {obj?.name ?? "<null>"}");
#else
                var obj = gameObjects[i].TryCast<GameObject>();
                ModLogger.Info($"  - {obj?.name ?? "<null>"}");
#endif
            }

            // Wait for player to be ready (using our IL2CPP-safe check)
            #if !MONO
            while (true)
            {
                try
                {
                    var instance = PlayerSingleton<AppsCanvas>.Instance;
                    if (instance != null && instance.Pointer != System.IntPtr.Zero)
                        break;
                }
                catch
                {
                    // Instance is null or not ready
                }
                yield return null;
            }
            #else
            while (PlayerSingleton<AppsCanvas>.Instance == null)
                yield return null;
            #endif

            var jailPrefab = jailBundle.LoadAsset<GameObject>("Jail");
            if (jailPrefab == null)
            {
                ModLogger.Error("Jail_2 prefab not found in asset bundle!");
                yield break;
            }

            // Spawn the jail
            var jail = Object.Instantiate(jailPrefab, new Vector3(66.5362f, 8.5001f, -220.6056f), Quaternion.identity);
            jail.name = "[Prefab] JailHouseBlues";
            
            ModLogger.Info($"Jail spawned successfully at {jail.transform.position}");
            
            // Initialize JailController system
            yield return new WaitForSeconds(1f); // Give the jail a moment to settle
            InitializeJailController(jail);
        }

        private static void InitializeJailController(GameObject jail)
        {
            try
            {
                ModLogger.Info("Initializing JailController system...");
                
                // Check if the jail already has a JailController
                var existingController = jail.GetComponent<JailController>();
                if (existingController != null)
                {
                    ModLogger.Info("Found existing JailController on jail prefab");
                    ActiveJailController = existingController;
                }
                else
                {
                    ModLogger.Info("Adding JailController component to jail");
                    ActiveJailController = jail.AddComponent<JailController>();
                }

                // Load and assign prefabs from bundle before initialization
                if (ActiveJailController != null)
                {
                    LoadAndAssignJailPrefabs(ActiveJailController);
                    
                    ModLogger.Info("Calling JailController.InitializeJail()...");
                    ActiveJailController.InitializeJail();
                    ModLogger.Info("✓ JailController initialized successfully!");
                    
                    // Log status for debugging
                    LogJailControllerStatus();
                }
                else
                {
                    ModLogger.Error("Failed to get JailController component");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing JailController: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        private static void LoadAndAssignJailPrefabs(JailController controller)
        {
            try
            {
                ModLogger.Info("Loading jail prefabs from asset bundle...");
                
                // Load the behind-bars bundle
                var jailBundle = Utils.AssetBundleUtils.LoadAssetBundle("Behind_Bars.behind_bars");
                if (jailBundle == null)
                {
                    ModLogger.Error("Failed to load behind-bars bundle for prefabs");
                    return;
                }

                // Load JailDoor prefab
#if MONO
                var jailDoorPrefab = jailBundle.LoadAsset<GameObject>("JailDoor");
#else
                var jailDoorPrefab = jailBundle.LoadAsset("JailDoor", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                if (jailDoorPrefab != null)
                {
                    controller.jailDoorPrefab = jailDoorPrefab;
                    ModLogger.Info("✓ Loaded JailDoor prefab");
                }
                else
                {
                    ModLogger.Warn("JailDoor prefab not found in bundle");
                }

                // Load SteelDoor prefab  
#if MONO
                var steelDoorPrefab = jailBundle.LoadAsset<GameObject>("GuardDoors");
#else
                var steelDoorPrefab = jailBundle.LoadAsset("GuardDoors", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                if (steelDoorPrefab != null)
                {
                    controller.steelDoorPrefab = steelDoorPrefab;
                    ModLogger.Info("✓ Loaded SteelDoor prefab");
                }
                else
                {
                    ModLogger.Warn("SteelDoor prefab not found in bundle");
                }

                // Load SecurityCamera prefab (if available)
#if MONO
                var cameraPrefab = jailBundle.LoadAsset<GameObject>("SecurityCameraPlaceHolder");
#else
                var cameraPrefab = jailBundle.LoadAsset("SecurityCameraPlaceHolder", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                if (cameraPrefab != null)
                {
                    controller.securityCameraPrefab = cameraPrefab;
                    ModLogger.Info("✓ Loaded SecurityCamera prefab");
                }
                else
                {
                    ModLogger.Warn("SecurityCamera prefab not found in bundle (optional)");
                }

                ModLogger.Info("Jail prefab loading completed");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error loading jail prefabs: {e.Message}");
            }
        }
        
        private static void LogJailControllerStatus()
        {
            if (ActiveJailController == null)
            {
                ModLogger.Warn("ActiveJailController is null");
                return;
            }

            try
            {
                ModLogger.Info("=== JAIL CONTROLLER STATUS ===");
                ModLogger.Info($"Cells discovered: {ActiveJailController.cells?.Count ?? 0}");
                ModLogger.Info($"Holding cells discovered: {ActiveJailController.holdingCells?.Count ?? 0}");
                ModLogger.Info($"Security cameras: {ActiveJailController.securityCameras?.Count ?? 0}");
                ModLogger.Info($"Area lights: {ActiveJailController.areaLights?.Count ?? 0}");
                
                // Check area initialization
                var areas = new[]
                {
                    ("Kitchen", ActiveJailController.kitchen?.isInitialized ?? false),
                    ("Laundry", ActiveJailController.laundry?.isInitialized ?? false),
                    ("Phone Area", ActiveJailController.phoneArea?.isInitialized ?? false),
                    ("Booking", ActiveJailController.booking?.isInitialized ?? false),
                    ("Guard Room", ActiveJailController.guardRoom?.isInitialized ?? false),
                    ("Main Rec", ActiveJailController.mainRec?.isInitialized ?? false),
                    ("Showers", ActiveJailController.showers?.isInitialized ?? false)
                };

                ModLogger.Info("Area status:");
                foreach (var (name, initialized) in areas)
                {
                    ModLogger.Info($"  {name}: {(initialized ? "✓ Initialized" : "✗ Not initialized")}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error logging jail controller status: {e.Message}");
            }
        }

        private void OnPlayerArrested()
        {
            ModLogger.Info("Player arrested - initiating arrest sequence");

            if (Player.Local != null)
            {
                // Start the arrest sequence
                // MelonCoroutines.Start(_jailSystem!.HandlePlayerArrest(Player.Local));
            }
        }

        public JailSystem GetJailSystem() => _jailSystem!;
        public BailSystem GetBailSystem() => _bailSystem!;
        public CourtSystem GetCourtSystem() => _courtSystem!;
        public ProbationSystem GetProbationSystem() => _probationSystem!;

        public void TestToiletSinkSystem()
        {
            ModLogger.Info("Testing ToiletSink system from Core...");

            try
            {
                var sinkCount = ToiletSinkManager.GetToiletSinkCount();
                ModLogger.Info($"Current toilet sink count: {sinkCount}");

                var allSinks = ToiletSinkManager.GetAllToiletSinks();
                for (int i = 0; i < allSinks.Count; i++)
                {
                    var sink = allSinks[i];
                    ModLogger.Info($"ToiletSink {i}: {sink.GetDebugInfo()}");
                }

                // Test spawning another sink
                var newSink = AssetManager.SpawnAsset<ToiletSink>(FurnitureType.ToiletSink);
                if (newSink != null)
                {
                    ModLogger.Info($"Successfully spawned additional toilet sink: {newSink.GetDebugInfo()}");
                    ModLogger.Info($"Total toilet sinks now: {ToiletSinkManager.GetToiletSinkCount()}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error testing toilet sink system: {e.Message}");
            }
        }

        public void TestBunkBedSystem()
        {
            ModLogger.Info("Testing BunkBed system from Core...");

            try
            {
                var bedCount = BunkBedManager.GetBunkBedCount();
                ModLogger.Info($"Current bunk bed count: {bedCount}");

                var allBeds = BunkBedManager.GetAllBunkBeds();
                for (int i = 0; i < allBeds.Count; i++)
                {
                    var bed = allBeds[i];
                    ModLogger.Info($"BunkBed {i}: {bed.GetDebugInfo()}");
                }

                // Test spawning another bed
                var newBed = AssetManager.SpawnAsset<BunkBed>(FurnitureType.BunkBed);
                if (newBed != null)
                {
                    ModLogger.Info($"Successfully spawned additional bunk bed: {newBed.GetDebugInfo()}");
                    ModLogger.Info($"Total bunk beds now: {BunkBedManager.GetBunkBedCount()}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error testing bunk bed system: {e.Message}");
            }
        }

        // Jail Controller convenience methods
        public static bool IsJailControllerReady() => ActiveJailController != null;
        
        public static void TriggerEmergencyLockdown()
        {
            if (ActiveJailController != null)
            {
                ActiveJailController.EmergencyLockdown();
                ModLogger.Info("Emergency lockdown triggered via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot trigger emergency lockdown - JailController not available");
            }
        }
        
        public static void UnlockAllDoors()
        {
            if (ActiveJailController != null)
            {
                ActiveJailController.UnlockAll();
                ModLogger.Info("All doors unlocked via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot unlock doors - JailController not available");
            }
        }
        
        public static void SetJailLighting(JailController.LightingState state)
        {
            if (ActiveJailController != null)
            {
                ActiveJailController.SetJailLighting(state);
                ModLogger.Info($"Jail lighting set to {state} via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot set lighting - JailController not available");
            }
        }
        
        public static string GetPlayerCurrentArea()
        {
            if (ActiveJailController != null && Player.Local != null)
            {
                return ActiveJailController.GetPlayerCurrentArea(Player.Local.transform.position);
            }
            return "Unknown - JailController not available";
        }
    }
}
