using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using HarmonyLib;
using System;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
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
        public static
#if !MONO
            Il2CppAssetBundle
#else
            AssetBundle
#endif
            ? CachedJailBundle { get; private set; }

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

            // Register NPC Behavior Components
            ClassInjector.RegisterTypeInIl2Cpp<JailGuardBehavior>();
            ClassInjector.RegisterTypeInIl2Cpp<JailInmateBehavior>();
            
            // Register State Machine Components (skip abstract base class)
            ClassInjector.RegisterTypeInIl2Cpp<GuardStateMachine>();
            ClassInjector.RegisterTypeInIl2Cpp<InmateStateMachine>();
            
            // Register Prison NPC System Components
            ClassInjector.RegisterTypeInIl2Cpp<PrisonNPCManager>();
            ClassInjector.RegisterTypeInIl2Cpp<PrisonGuard>();
            ClassInjector.RegisterTypeInIl2Cpp<PrisonInmate>();
            
            // Register Test Components
            ClassInjector.RegisterTypeInIl2Cpp<TestNPCController>();
            ClassInjector.RegisterTypeInIl2Cpp<MoveableTargetController>();
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
                    //// Spawn toilet sink using generic method
                    //var toiletSink = AssetManager.SpawnAsset<ToiletSink>(FurnitureType.ToiletSink);
                    //if (toiletSink != null)
                    //{
                    //    ModLogger.Info($"Successfully spawned toilet sink on scene initialization: {toiletSink.GetDebugInfo()}");
                    //    ModLogger.Debug($"Total toilet sinks in scene: {ToiletSinkManager.GetToiletSinkCount()}");
                    //}
                    //else
                    //{
                    //    ModLogger.Warn("Failed to spawn toilet sink on scene initialization");
                    //}

                    //// Spawn bunk bed using generic method
                    //var bunkBed = AssetManager.SpawnAsset<BunkBed>(FurnitureType.BunkBed);
                    //if (bunkBed != null)
                    //{
                    //    ModLogger.Info($"Successfully spawned bunk bed on scene initialization: {bunkBed.GetDebugInfo()}");
                    //    ModLogger.Debug($"Total bunk beds in scene: {BunkBedManager.GetBunkBedCount()}");
                    //}
                    //else
                    //{
                    //    ModLogger.Warn("Failed to spawn bunk bed on scene initialization");
                    //}

                    //// Spawn common room table using generic method
                    //var commonRoomTable = AssetManager.SpawnAsset<CommonRoomTable>(FurnitureType.CommonRoomTable);
                    //if (commonRoomTable != null)
                    //{
                    //    ModLogger.Info($"Successfully spawned common room table on scene initialization: {commonRoomTable.GetDebugInfo()}");
                    //    ModLogger.Debug($"Total common room tables in scene: {CommonRoomTableManager.GetCommonRoomTableCount()}");
                    //}
                    //else
                    //{
                    //    ModLogger.Warn("Failed to spawn common room table on scene initialization");
                    //}

                    //// Spawn cell table using generic method
                    //var cellTable = AssetManager.SpawnAsset<CellTable>(FurnitureType.CellTable);
                    //if (cellTable != null)
                    //{
                    //    ModLogger.Info($"Successfully spawned cell table on scene initialization: {cellTable.GetDebugInfo()}");
                    //    ModLogger.Debug($"Total cell tables in scene: {CellTableManager.GetCellTableCount()}");
                    //}
                    //else
                    //{
                    //    ModLogger.Warn("Failed to spawn cell table on scene initialization");
                    //}

                    //// Test the systems after successful spawning
                    //TestToiletSinkSystem();
                    //TestBunkBedSystem();

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

            // Load the behind-bars bundle specifically and cache it
            if (CachedJailBundle == null)
            {
                CachedJailBundle = Utils.AssetBundleUtils.LoadAssetBundle("Behind_Bars.behind_bars");
            }

            var jailBundle = CachedJailBundle;
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

            // Attach NavMesh data from asset bundle (asset bundles don't preserve NavMesh data)
            yield return new WaitForSeconds(0.5f); // Let components settle first
            JailNavMeshSetup.AttachJailNavMesh(jail.transform);

            // Initialize JailController system
            yield return new WaitForSeconds(1f); // Give the NavMesh time to build
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

                // Load and assign prefabs from bundle, then trigger door setup
                if (ActiveJailController != null)
                {
                    LoadAndAssignJailPrefabs(ActiveJailController);
                    ModLogger.Info("✓ JailController prefabs loaded");

                    // Manually call SetupDoors after prefabs are loaded
                    ActiveJailController.SetupDoors();
                    ModLogger.Info("✓ Door setup completed after prefab loading");

                    // Log status after a frame to let everything complete
                    MelonCoroutines.Start(LogStatusAfterFrame());

                    // Create jail NPCs after JailController is fully initialized
                    MelonCoroutines.Start(CreateJailNPCs());
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
        
        private static IEnumerator LogStatusAfterFrame()
        {
            // Wait a frame to let Unity's Start() method complete
            yield return null;
            yield return new WaitForSeconds(0.5f); // Extra time for initialization
            
            ModLogger.Info("Logging jail status after initialization...");
            LogJailControllerStatus();
        }

        private static IEnumerator CreateJailNPCs()
        {
            // Wait for everything to be fully initialized before creating NPCs
            yield return new WaitForSeconds(2f);

            ModLogger.Info("Creating jail NPCs with custom appearances...");

            // Create PrisonNPCManager to handle all NPC spawning and management
            if (ActiveJailController != null)
            {
                var npcManager = ActiveJailController.gameObject.AddComponent<PrisonNPCManager>();
                ModLogger.Info("✓ PrisonNPCManager added to JailController");
            }
            else
            {
                ModLogger.Error("ActiveJailController is null - cannot add PrisonNPCManager");
            }

            ModLogger.Info("✓ Jail NPCs created successfully with custom appearances");
            
            // Door interaction system temporarily disabled to reduce log spam
            // NPCDoorInteraction.InitializeDoorDatabase();
            // ModLogger.Info("✓ Door interaction system initialized");
            
            // Validate NavMesh before finishing
            yield return new WaitForSeconds(1f);
            var jail = Core.ActiveJailController;
            if (jail != null)
            {
                if (JailNavMeshSetup.HasValidNavMesh(jail.transform))
                {
                    ModLogger.Info("✓ NavMesh validation passed");
                }
                else
                {
                    ModLogger.Warn("NavMesh validation failed - NavMesh may not be properly attached");
                }
            }
            
            yield return new WaitForSeconds(1f);
            ModLogger.Info("✓ NPC initialization completed");
        }

        private static void CreateTestNPC()
        {
            try
            {
                var jailController = UnityEngine.Object.FindObjectOfType<JailController>();
                if (jailController == null)
                {
                    ModLogger.Error("JailController not found for test NPC positioning");
                    return;
                }

                // Position at jail center (relative to jail transform) - but find a valid NavMesh position first
                Vector3 jailCenter = jailController.transform.position;
                ModLogger.Info($"Jail center is at: {jailCenter}");
                
                // FORCE spawn at known ground-level patrol points - no searching!
                Vector3[] knownGroundPositions = {
                    new Vector3(54.72f, 9.31f, -232.63f), // Patrol_Upper_Right (from logs)
                    new Vector3(52.53f, 9.31f, -204.89f), // Patrol_Upper_Left 
                    new Vector3(52.53f, 9.31f, -205.00f), // Patrol_Lower_Left
                    new Vector3(81.92f, 9.31f, -203.99f), // Patrol_Kitchen
                    new Vector3(78.62f, 9.31f, -235.63f), // Patrol_Laundry
                };
                
                Vector3 testNPCPosition = knownGroundPositions[0]; // Default to first patrol point
                
                // Try each known ground position until we find NavMesh
                foreach (var groundPos in knownGroundPositions)
                {
                    if (UnityEngine.AI.NavMesh.SamplePosition(groundPos, out UnityEngine.AI.NavMeshHit hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        testNPCPosition = hit.position;
                        ModLogger.Info($"Using GROUND LEVEL NavMesh at: {testNPCPosition} (Y={testNPCPosition.y:F2})");
                        
                        // CRITICAL: Reject if Y position is too high (roof level)
                        if (testNPCPosition.y > 11f)
                        {
                            ModLogger.Warn($"Rejected roof position Y={testNPCPosition.y:F2}, trying next...");
                            continue;
                        }
                        break;
                    }
                }
                
                ModLogger.Info($"Final TestNPC position: {testNPCPosition} (Y={testNPCPosition.y:F2})");
                
                ModLogger.Info($"Creating test NPC at jail center: {testNPCPosition}");
                
                var testNPC = DirectNPCBuilder.CreateTestNPC("TestNPC", testNPCPosition);
                if (testNPC == null)
                {
                    ModLogger.Error("DirectNPCBuilder.CreateTestNPC returned null!");
                    return;
                }
                
                ModLogger.Info($"✓ DirectNPCBuilder created GameObject: {testNPC.name} at {testNPC.transform.position}");
                ModLogger.Info($"TestNPC components: {string.Join(", ", testNPC.GetComponents<Component>().Select(c => c.GetType().Name))}");
                
                // Verify the NPC is active and positioned correctly
                if (!testNPC.activeSelf)
                {
                    testNPC.SetActive(true);
                    ModLogger.Info("✓ Activated TestNPC GameObject");
                }
                
                // Initialize patrol points in JailController first
                jailController.InitializePatrolPoints();
                
                // Initialize patrol and debug systems
                PatrolSystem.Initialize();
                
                // Add the test controller
                try
                {
                    var testController = testNPC.AddComponent<TestNPCController>();
                    ModLogger.Info($"✓ Added TestNPCController to test NPC");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"Failed to add TestNPCController: {ex.Message}");
                }
                
                // Create debug targets for TestNPC and planned participants
                //CreateDebugTargetsForParticipants(jailCenter);
                
                ModLogger.Info($"✓ Created test NPC at {testNPCPosition} for pathfinding debugging");
                ModLogger.Info("Use Arrow Keys + Numpad 9/3 to move the red target cube, NPC should follow it");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating test NPC: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }


        private static void LoadAndAssignJailPrefabs(JailController controller)
        {
            try
            {
                ModLogger.Info("Loading jail prefabs from asset bundle...");

                // Use the cached behind-bars bundle
                var jailBundle = CachedJailBundle;
                if (jailBundle == null)
                {
                    ModLogger.Error("Failed to load behind-bars bundle for prefabs - bundle not cached");
                    return;
                }

                // Load JailDoor prefab - try multiple naming variations
#if MONO
                var jailDoorPrefab = jailBundle.LoadAsset<GameObject>("JailDoor") ??
                                   jailBundle.LoadAsset<GameObject>("jaildoor") ??
                                   jailBundle.LoadAsset<GameObject>("assets/behindbars/jaildoor.prefab");
#else
                var jailDoorPrefab = jailBundle.LoadAsset("JailDoor", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                                   jailBundle.LoadAsset("jaildoor", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                                   jailBundle.LoadAsset("assets/behindbars/jaildoor.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                if (jailDoorPrefab != null)
                {
                    controller.jailDoorPrefab = jailDoorPrefab;
                    ModLogger.Info($"✓ Loaded JailDoor prefab: {jailDoorPrefab.name}");
                }
                else
                {
                    ModLogger.Warn("JailDoor prefab not found in bundle - no cell doors will be instantiated!");
                }

                // Load GuardDoors prefab - try multiple naming variations
#if MONO
                var steelDoorPrefab = jailBundle.LoadAsset<GameObject>("GuardDoors") ??
                                    jailBundle.LoadAsset<GameObject>("guarddoors") ??
                                    jailBundle.LoadAsset<GameObject>("assets/behindbars/guarddoors.prefab");
#else
                var steelDoorPrefab = jailBundle.LoadAsset("GuardDoors", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                                    jailBundle.LoadAsset("guarddoors", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                                    jailBundle.LoadAsset("assets/behindbars/guarddoors.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                if (steelDoorPrefab != null)
                {
                    controller.steelDoorPrefab = steelDoorPrefab;
                    ModLogger.Info($"✓ Loaded SteelDoor prefab: {steelDoorPrefab.name}");
                }
                else
                {
                    ModLogger.Warn("SteelDoor prefab not found in bundle - no steel doors will be instantiated!");
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
                ModLogger.Info($"Door prefabs loaded: JailDoor={ActiveJailController.jailDoorPrefab != null}, SteelDoor={ActiveJailController.steelDoorPrefab != null}");

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

        /// <summary>
        /// Handle hotkeys for testing and debugging
        /// </summary>
        public override void OnUpdate()
        {
            try
            {
                // Home key - Teleport to jail for testing
                if (Input.GetKeyDown(KeyCode.Home))
                {
                    TeleportToJail();
                }
            }
            catch (Exception e)
            {
                // Silently ignore input errors to avoid spam
            }
        }

        /// <summary>
        /// Teleport player to inside the jail for testing
        /// </summary>
        private void TeleportToJail()
        {
            try
            {
#if !MONO
                var player = Object.FindObjectOfType<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var player = Object.FindObjectOfType<ScheduleOne.PlayerScripts.Player>();
#endif
                if (player != null)
                {
                    Vector3 jailPosition = new Vector3(66.5362f, 9.5001f, -220.6056f);
                    player.transform.position = jailPosition;
                    ModLogger.Info($"✓ Teleported player to jail at {jailPosition}");
                }
                else
                {
                    ModLogger.Warn("Player not found for teleportation");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error teleporting to jail: {e.Message}");
            }
        }
    }
}
