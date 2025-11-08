using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using HarmonyLib;
using System;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.UI;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.Jail;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Behind_Bars.Systems.NPCs.PresetParoleOfficerRoutes;

using Object = UnityEngine.Object;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif
using Behind_Bars.Players;
using Behind_Bars.Systems;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Harmony;
using Behind_Bars.Utils;

#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

#if MONO
using FishNet;
using ScheduleOne.UI.Phone;
using ScheduleOne.DevUtilities;
using FishNet.Managing;
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
        private ParoleSystem? _paroleSystem;
        private FileUtilities _fileUtilities;

        // Player management
        private Dictionary<Player, PlayerHandler> _playerHandlers = new();

        // Jail management
        public static JailController? JailController { get; private set; }
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
        public ParoleSystem? ParoleSystem => _paroleSystem;
        public FileUtilities FileUtilities => _fileUtilities;

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
            ClassInjector.RegisterTypeInIl2Cpp<JailAsset>();
            ClassInjector.RegisterTypeInIl2Cpp<JailManager>();

            // Register Jail System Components
            ClassInjector.RegisterTypeInIl2Cpp<JailController>();
            ClassInjector.RegisterTypeInIl2Cpp<SecurityCamera>();
            ClassInjector.RegisterTypeInIl2Cpp<MonitorController>();
            ClassInjector.RegisterTypeInIl2Cpp<JailMonitorController>();
            ClassInjector.RegisterTypeInIl2Cpp<JailLightingController>();
            ClassInjector.RegisterTypeInIl2Cpp<JailCellManager>();
            ClassInjector.RegisterTypeInIl2Cpp<JailAreaManager>();
            ClassInjector.RegisterTypeInIl2Cpp<JailDoorController>();
            ClassInjector.RegisterTypeInIl2Cpp<JailPatrolManager>();

            // Register Prison NPC System Components
            // NOTE: BaseJailNPC is abstract and shouldn't be registered directly
            ClassInjector.RegisterTypeInIl2Cpp<PrisonNPCManager>();
            ClassInjector.RegisterTypeInIl2Cpp<PrisonGuard>();
            ClassInjector.RegisterTypeInIl2Cpp<PrisonInmate>();

            // Testing BaseJailNPC-derived types one at a time
            // ClassInjector.RegisterTypeInIl2Cpp<ParoleOfficerBehavior>();
            ClassInjector.RegisterTypeInIl2Cpp<IntakeOfficerStateMachine>();
            // ClassInjector.RegisterTypeInIl2Cpp<ReleaseOfficerBehavior>(); // Moved here for testing

            // Re-enabling registrations after fixing trampoline error
            ClassInjector.RegisterTypeInIl2Cpp<SecurityDoorBehavior>();
            ClassInjector.RegisterTypeInIl2Cpp<JailNPCDialogueController>();
            ClassInjector.RegisterTypeInIl2Cpp<JailNPCAudioController>();
            ClassInjector.RegisterTypeInIl2Cpp<OfficerCoordinator>();
            ClassInjector.RegisterTypeInIl2Cpp<DoorTriggerHandler>();

            // Register Test Components
            ClassInjector.RegisterTypeInIl2Cpp<TestNPCController>();
            ClassInjector.RegisterTypeInIl2Cpp<MoveableTargetController>();

            // Register UI Components
            ClassInjector.RegisterTypeInIl2Cpp<BehindBarsUIWrapper>();
            ClassInjector.RegisterTypeInIl2Cpp<WantedLevelUI>();
            ClassInjector.RegisterTypeInIl2Cpp<OfficerCommandUI>();

            // Register Booking System Components
            ClassInjector.RegisterTypeInIl2Cpp<BookingProcess>();
            ClassInjector.RegisterTypeInIl2Cpp<MugshotStation>();
            ClassInjector.RegisterTypeInIl2Cpp<ScannerStation>();
            ClassInjector.RegisterTypeInIl2Cpp<InventoryDropOff>();
            ClassInjector.RegisterTypeInIl2Cpp<JailBed>();
            ClassInjector.RegisterTypeInIl2Cpp<PrisonBedInteractable>();
            ClassInjector.RegisterTypeInIl2Cpp<PrisonItemEquippable>();

            // Register Cell Management Components
            ClassInjector.RegisterTypeInIl2Cpp<CellAssignmentManager>();

            // Register Jail Inventory System
            ClassInjector.RegisterTypeInIl2Cpp<JailInventoryPickupStation>();
            ClassInjector.RegisterTypeInIl2Cpp<InventoryPickupStation>();
            // ClassInjector.RegisterTypeInIl2Cpp<PrisonStorageEntity>(); // REMOVED - inherits from StorageEntity which has NetworkConnection methods
            ClassInjector.RegisterTypeInIl2Cpp<ExitScannerStation>();
            ClassInjector.RegisterTypeInIl2Cpp<SimpleExitDoor>();

            // Register Release System
            ClassInjector.RegisterTypeInIl2Cpp<ReleaseManager>();
            ClassInjector.RegisterTypeInIl2Cpp<ReleaseOfficerBehavior>(); // Re-enabled after fixing IEnumerator issue
            ClassInjector.RegisterTypeInIl2Cpp<ParoleOfficer>();
            // NOTE: ParoleOfficerBehavior and PrisonNPCManager already registered above - removed duplicates
#endif
            // Initialize core systems
            HarmonyPatches.Initialize(this);
            
            // Initialize GameTimeManager first (needed by other systems)
            GameTimeManager.Instance.Initialize();
            ModLogger.Info("GameTimeManager initialized");
            
            _jailSystem = new JailSystem();
            _jailSystem.Initialize(); // Initialize JailSystem components

            // Initialize ReleaseManager for coordinated prisoner releases
            MelonLogger.Msg("[Core] Initializing ReleaseManager from Core.cs");
            var releaseManager = ReleaseManager.Instance; // This will create the singleton
            ModLogger.Info("ReleaseManager initialized and ready for coordinated releases");

            _bailSystem = new BailSystem();
            
            // Initialize crime detection UI
            CrimeUIManager.Instance.Initialize();
            _courtSystem = new CourtSystem();
            _paroleSystem = new ParoleSystem();
            FileUtilities.Initialize();
            _fileUtilities = FileUtilities.Instance;

            // Initialize preset parole officer routes
            PresetParoleOfficerRoutes.InitializePatrolPoints();
            ModLogger.Info("Preset parole officer routes initialized");

            AssetManager = new AssetManager();
            AssetManager.Init();

            // Add scene change detection for cleanup
#if !MONO
            SceneManager.activeSceneChanged += new System.Action<Scene, Scene>(OnSceneChanged);
#else
            SceneManager.activeSceneChanged += OnSceneChanged;
#endif

            ModLogger.Info("Behind Bars initialized with all systems");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            ModLogger.Debug($"Scene initialized: {sceneName} (Build Index: {buildIndex})");

            // Spawn furniture when the scene is initialized
            try
            {
                if (sceneName == "Main")
                {
                    // Initialize UI system
                    MelonCoroutines.Start(InitializeUISystem());
                    
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
                // Arrest handling is centralized in HarmonyPatches; no direct listener needed here

                ModLogger.Info("Player systems initialized successfully");
            }
            else
            {
                ModLogger.Warn("Player.Local is null, retrying in 2 seconds...");
                yield return new WaitForSeconds(2f);
                MelonCoroutines.Start(InitializePlayerSystems());
            }
        }

        private static IEnumerator InitializeUISystem()
        {
            ModLogger.Info("Initializing Behind Bars UI system...");
            
            // Wait for essential systems to be ready
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
            
            // Wait for asset manager to be ready
            yield return new WaitForSeconds(1f);
            
            // Initialize the UI manager
            try
            {
                BehindBarsUIManager.Instance.Initialize();
                ModLogger.Info("✓ Behind Bars UI system initialized successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing UI system: {e.Message}");
                yield break;
            }
            
            // Test the UI system (optional - can be removed in production)
            if (Constants.DEBUG_LOGGING)
            {
                yield return new WaitForSeconds(2f);
                TestUISystem();
            }
        }
        
        private static void TestUISystem()
        {
            ModLogger.Info("Testing Behind Bars UI system...");
            
            try
            {
                // Show test jail info UI
                BehindBarsUIManager.Instance.ShowJailInfoUI(
                    crime: "Major Possession, Assaulting Officer, Resisting Arrest", 
                    timeInfo: "2 days", 
                    bailInfo: "$500"
                );
                
                ModLogger.Info("✓ Test UI displayed successfully - check your screen!");
                
                // Auto-hide after 10 seconds for testing
                MelonCoroutines.Start(AutoHideTestUI());
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error testing UI system: {e.Message}");
            }
        }
        
        private static IEnumerator AutoHideTestUI()
        {
            yield return new WaitForSeconds(10f);
            BehindBarsUIManager.Instance.HideJailInfoUI();
            ModLogger.Info("Test UI auto-hidden after 10 seconds");
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
                    JailController = existingController;
                }
                else
                {
                    ModLogger.Info("Adding JailController component to jail");
                    JailController = jail.AddComponent<JailController>();
                }

                // Load and assign prefabs from bundle, then trigger door setup
                if (JailController != null)
                {
                    LoadAndAssignJailPrefabs(JailController);
                    ModLogger.Info("✓ JailController prefabs loaded");

                    // Manually call SetupDoors after prefabs are loaded
                    JailController.SetupDoors();
                    ModLogger.Info("✓ Door setup completed after prefab loading");

                    // Setup exit door specifically
                    SetupExitDoor(JailController);
                    ModLogger.Info("✓ Exit door setup completed");

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
            if (JailController != null)
            {
                var npcManager = JailController.gameObject.AddComponent<PrisonNPCManager>();
                ModLogger.Info("✓ PrisonNPCManager added to JailController");
                
                // Add CellAssignmentManager for cell tracking
                var cellManager = JailController.gameObject.AddComponent<CellAssignmentManager>();
                ModLogger.Info("✓ CellAssignmentManager added to JailController");
            }
            else
            {
                ModLogger.Error("ActiveJailController is null - cannot add managers");
            }

            ModLogger.Info("✓ Jail NPCs created successfully with custom appearances");
            
            // Door interaction system temporarily disabled to reduce log spam
            // NPCDoorInteraction.InitializeDoorDatabase();
            // ModLogger.Info("✓ Door interaction system initialized");
            
            // Validate NavMesh before finishing
            yield return new WaitForSeconds(1f);
            var jail = Core.JailController;
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
            
            // Initialize booking system
            InitializeBookingSystem();
            
            ModLogger.Info("✓ NPC initialization completed");
        }
        
        /// <summary>
        /// Initialize the booking process system
        /// </summary>
        private static void InitializeBookingSystem()
        {
            try
            {
                ModLogger.Info("Initializing booking system...");
                
                if (JailController == null)
                {
                    ModLogger.Error("Cannot initialize booking system - no active jail controller");
                    return;
                }
                
                GameObject jailGameObject = JailController.gameObject;

                // Add BookingProcess component if it doesn't exist
                JailController.BookingProcessController = jailGameObject.GetComponent<Behind_Bars.Systems.Jail.BookingProcess>();
                if (JailController.BookingProcessController == null)
                {
                    JailController.BookingProcessController = jailGameObject.AddComponent<Behind_Bars.Systems.Jail.BookingProcess>();
                    ModLogger.Info("✓ BookingProcess component added to jail");
                }
                else
                {
                    ModLogger.Info("✓ BookingProcess component already exists");
                }
                
                // Find and set up booking stations
                SetupBookingStations(jailGameObject.transform);
                
                ModLogger.Info("✓ Booking system initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initializing booking system: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set up booking stations (mugshot and scanner)
        /// </summary>
        private static void SetupBookingStations(Transform jailTransform)
        {
            try
            {
                // Find booking area
                Transform bookingArea = jailTransform.Find("Booking");
                if (bookingArea == null)
                {
                    ModLogger.Error("Booking area not found in jail hierarchy");
                    return;
                }
                
                // Set up Mugshot Station - SINGLE COMPONENT ONLY (like ScannerStation)
                Transform mugshotStation = bookingArea.Find("MugshotStation");
                if (mugshotStation != null)
                {
                    var mugshotComponent = mugshotStation.GetComponent<Behind_Bars.Systems.Jail.MugshotStation>();
                    if (mugshotComponent == null)
                    {
                        mugshotComponent = mugshotStation.gameObject.AddComponent<Behind_Bars.Systems.Jail.MugshotStation>();
                        ModLogger.Info("✓ MugshotStation component added to main GameObject");
                    }
                    
                    // DO NOT add manual collider - let InteractableObject handle collision detection
                    ModLogger.Info("MugshotStation setup complete - single component approach");
                }
                else
                {
                    ModLogger.Warn("MugshotStation not found in booking area");
                }
                
                // Set up Scanner Station - SINGLE COMPONENT ONLY
                Transform scannerStation = bookingArea.Find("ScannerStation");
                if (scannerStation != null)
                {
                    var scannerComponent = scannerStation.GetComponent<Behind_Bars.Systems.Jail.ScannerStation>();
                    if (scannerComponent == null)
                    {
                        scannerComponent = scannerStation.gameObject.AddComponent<Behind_Bars.Systems.Jail.ScannerStation>();
                        ModLogger.Info("✓ ScannerStation component added to main GameObject");
                    }
                    
                    // DO NOT add ScannerStation to Interaction child - this causes duplicates!
                    ModLogger.Info("ScannerStation setup complete - single component approach");
                }
                else
                {
                    ModLogger.Warn("ScannerStation not found in booking area");
                }

                // Set up Exit Scanner Station - SINGLE COMPONENT ONLY
                ModLogger.Info("Searching for ExitScannerStation...");
                Transform hallway = jailTransform.Find("Hallway");
                Transform exitScannerStation = null;

                if (hallway != null)
                {
                    ModLogger.Info($"Found Hallway at {hallway.name}");
                    exitScannerStation = hallway.Find("ExitScannerStation");
                    if (exitScannerStation != null)
                    {
                        ModLogger.Info($"Found ExitScannerStation in Hallway: {exitScannerStation.name}");
                    }
                    else
                    {
                        ModLogger.Warn("ExitScannerStation not found in Hallway");
                    }
                }
                else
                {
                    ModLogger.Warn("Hallway not found in jail");
                }

                if (exitScannerStation == null)
                {
                    exitScannerStation = jailTransform.Find("ExitScannerStation");
                    if (exitScannerStation != null)
                    {
                        ModLogger.Info($"Found ExitScannerStation directly in jail: {exitScannerStation.name}");
                    }
                }

                if (exitScannerStation != null)
                {
                    var exitScannerComponent = exitScannerStation.GetComponent<Behind_Bars.Systems.Jail.ExitScannerStation>();
                    if (exitScannerComponent == null)
                    {
                        exitScannerComponent = exitScannerStation.gameObject.AddComponent<Behind_Bars.Systems.Jail.ExitScannerStation>();
                        ModLogger.Info("✓ ExitScannerStation component added to GameObject at " + exitScannerStation.name);
                    }
                    else
                    {
                        ModLogger.Info("ExitScannerStation component already exists");
                    }

                    ModLogger.Info("ExitScannerStation setup complete - found at " + exitScannerStation.name);
                }
                else
                {
                    ModLogger.Warn("ExitScannerStation not found in jail area or Hallway - searching all children");

                    // Debug: List all children of jailTransform
                    for (int i = 0; i < jailTransform.childCount; i++)
                    {
                        var child = jailTransform.GetChild(i);
                        ModLogger.Info($"Jail child {i}: {child.name}");

                        if (child.name == "Hallway")
                        {
                            ModLogger.Info($"Found Hallway, checking its children:");
                            for (int j = 0; j < child.childCount; j++)
                            {
                                var grandchild = child.GetChild(j);
                                ModLogger.Info($"  Hallway child {j}: {grandchild.name}");
                            }
                        }
                    }
                }

                // Set up Inventory Drop-off Station
                // Based on Unity hierarchy, look for Storage/InventoryDropOff
                Transform storageArea = jailTransform.Find("Storage");
                Transform inventoryDropOff = null;
                
                if (storageArea != null)
                {
                    inventoryDropOff = storageArea.Find("InventoryDropOff");
                }
                
                if (inventoryDropOff != null)
                {
                    var inventoryComponent = inventoryDropOff.GetComponent<Behind_Bars.Systems.Jail.InventoryDropOffStation>();
                    if (inventoryComponent == null)
                    {
                        inventoryComponent = inventoryDropOff.gameObject.AddComponent<Behind_Bars.Systems.Jail.InventoryDropOffStation>();
                        ModLogger.Info("✓ InventoryDropOffStation component added to InventoryDropOff GameObject");
                    }
                    
                    ModLogger.Info("InventoryDropOffStation setup complete");
                }
                else
                {
                    ModLogger.Warn("Storage/InventoryDropOff not found in jail hierarchy");
                }
                
                // Set up Jail Inventory Pickup Station (for prison items)
                Transform jailInventoryPickup = null;
                if (storageArea != null)
                {
                    jailInventoryPickup = storageArea.Find("JailInventoryPickup");
                }

                if (jailInventoryPickup != null)
                {
                    var jailPickupComponent = jailInventoryPickup.GetComponent<Behind_Bars.Systems.Jail.JailInventoryPickupStation>();
                    if (jailPickupComponent == null)
                    {
                        jailPickupComponent = jailInventoryPickup.gameObject.AddComponent<Behind_Bars.Systems.Jail.JailInventoryPickupStation>();
                        ModLogger.Info("✓ JailInventoryPickupStation component added to JailInventoryPickup GameObject");
                    }

                    ModLogger.Info("JailInventoryPickupStation setup complete");
                }
                else
                {
                    ModLogger.Warn("Storage/JailInventoryPickup not found in jail hierarchy");
                }

                // Set up Inventory Pickup Station (for personal belongings return)
                Transform inventoryPickup = null;
                if (storageArea != null)
                {
                    inventoryPickup = storageArea.Find("InventoryPickup");
                }

                if (inventoryPickup != null)
                {
                    var pickupComponent = inventoryPickup.GetComponent<Behind_Bars.Systems.Jail.InventoryPickupStation>();
                    if (pickupComponent == null)
                    {
                        pickupComponent = inventoryPickup.gameObject.AddComponent<Behind_Bars.Systems.Jail.InventoryPickupStation>();
                        ModLogger.Info("✓ InventoryPickupStation component added to InventoryPickup GameObject");
                    }

                    ModLogger.Info("InventoryPickupStation setup complete");
                }
                else
                {
                    ModLogger.Warn("Storage/InventoryPickup not found in jail hierarchy");
                }
                
                ModLogger.Info("✓ Booking stations setup completed");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error setting up booking stations: {ex.Message}");
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
            if (JailController == null)
            {
                ModLogger.Warn("ActiveJailController is null");
                return;
            }

            try
            {
                ModLogger.Info("=== JAIL CONTROLLER STATUS ===");
                ModLogger.Info($"Cells discovered: {JailController.cells?.Count ?? 0}");
                ModLogger.Info($"Holding cells discovered: {JailController.holdingCells?.Count ?? 0}");
                ModLogger.Info($"Security cameras: {JailController.securityCameras?.Count ?? 0}");
                ModLogger.Info($"Area lights: {JailController.areaLights?.Count ?? 0}");
                ModLogger.Info($"Door prefabs loaded: JailDoor={JailController.jailDoorPrefab != null}, SteelDoor={JailController.steelDoorPrefab != null}");

                // Check area initialization
                var areas = new[]
                {
                    ("Kitchen", JailController.kitchen?.isInitialized ?? false),
                    ("Laundry", JailController.laundry?.isInitialized ?? false),
                    ("Phone Area", JailController.phoneArea?.isInitialized ?? false),
                    ("Booking", JailController.booking?.isInitialized ?? false),
                    ("Guard Room", JailController.guardRoom?.isInitialized ?? false),
                    ("Main Rec", JailController.mainRec?.isInitialized ?? false),
                    ("Showers", JailController.showers?.isInitialized ?? false)
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


        public JailSystem GetJailSystem() => _jailSystem!;
        public BailSystem GetBailSystem() => _bailSystem!;
        public CourtSystem GetCourtSystem() => _courtSystem!;
        public ParoleSystem GetParoleSystem() => _paroleSystem!;

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
        public static bool IsJailControllerReady() => JailController != null;

        public static void TriggerEmergencyLockdown()
        {
            if (JailController != null)
            {
                JailController.EmergencyLockdown();
                ModLogger.Info("Emergency lockdown triggered via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot trigger emergency lockdown - JailController not available");
            }
        }

        public static void UnlockAllDoors()
        {
            if (JailController != null)
            {
                JailController.UnlockAll();
                ModLogger.Info("All doors unlocked via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot unlock doors - JailController not available");
            }
        }

        public static void SetJailLighting(JailLightingController.LightingState state)
        {
            if (JailController != null)
            {
                JailController.SetJailLighting(state);
                ModLogger.Info($"Jail lighting set to {state} via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot set lighting - JailController not available");
            }
        }

        public static string GetPlayerCurrentArea()
        {
            if (JailController != null && Player.Local != null)
            {
                return JailController.GetPlayerCurrentArea(Player.Local.transform.position);
            }
            return "Unknown - JailController not available";
        }
        
        /// <summary>
        /// Get the PlayerHandler for a given player
        /// </summary>
        public static PlayerHandler? GetPlayerHandler(Player player)
        {
            if (Instance != null && player != null && Instance._playerHandlers.ContainsKey(player))
            {
                return Instance._playerHandlers[player];
            }
            return null;
        }

        /// <summary>
        /// Public API: Show jail information UI
        /// </summary>
        public static void ShowJailInfoUI(string crime, string timeInfo, string bailInfo)
        {
            try
            {
                BehindBarsUIManager.Instance.ShowJailInfoUI(crime, timeInfo, bailInfo);
                ModLogger.Info($"Jail info UI shown: Crime={crime}, Time={timeInfo}, Bail={bailInfo}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error showing jail info UI: {e.Message}");
            }
        }

        /// <summary>
        /// Public API: Hide jail information UI
        /// </summary>
        public static void HideJailInfoUI()
        {
            try
            {
                BehindBarsUIManager.Instance.HideJailInfoUI();
                ModLogger.Info("Jail info UI hidden");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error hiding jail info UI: {e.Message}");
            }
        }

        /// <summary>
        /// Setup door triggers for the specific jail door triggers
        /// Call this in-game to add DoorTriggerHandler components to your jail triggers
        /// </summary>
        public static void SetupDoorTriggers()
        {
            try
            {
                ModLogger.Info("Starting jail door trigger setup...");
                
                // Setup only the specific jail door triggers under PatrolPoints
                Behind_Bars.Utils.ManualDoorTriggerSetup.SetupJailDoorTriggers();
                
                ModLogger.Info("Jail door trigger setup completed!");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting up door triggers: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Manual setup for a specific door trigger by name
        /// Example: Core.SetupSpecificDoorTrigger("BookingDoorTrigger", "Booking Inner Door")
        /// </summary>
        public static void SetupSpecificDoorTrigger(string triggerName, string doorName = null)
        {
            try
            {
                bool success = Behind_Bars.Utils.ManualDoorTriggerSetup.SetupDoorTriggerByName(triggerName, doorName);
                if (success)
                {
                    ModLogger.Info($"Successfully setup door trigger: {triggerName}");
                }
                else
                {
                    ModLogger.Error($"Failed to setup door trigger: {triggerName}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting up specific door trigger {triggerName}: {e.Message}");
            }
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

                // End key - Teleport to Taco Ticklers
                if (Input.GetKeyDown(KeyCode.End))
                {
                    TeleportToTacoTicklers();
                }

                // F9 key - Show crime details (debug)
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    CrimeUIManager.Instance.ShowCrimeDetails();
                }

                // F6 key - Quick 10-second jail sentence for release testing
                if (Input.GetKeyDown(KeyCode.F6))
                {
                    QuickJailForReleaseTesting();
                }

                // F8 key - Trigger instant arrest for testing
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    TriggerTestArrest();
                }

                // F12 key - Test spawn NPC with new avatar system
                if (Input.GetKeyDown(KeyCode.F12))
                {
                    TestSpawnNPCWithAvatar();
                }
            }
            catch (Exception e)
            {
                // Silently ignore input errors to avoid spam
            }
        }

        /// <summary>
        /// Test spawn an NPC with the new avatar system
        /// </summary>
        private void TestSpawnNPCWithAvatar()
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
                    ModLogger.Info("F12 pressed - Testing NPC spawn with working avatar system");

                    // Spawn in front of player
                    var spawnPos = player.transform.position + (player.transform.forward * 3f);

                    // Randomly choose between guard and inmate
                    GameObject testNPC = null;
                    bool spawnGuard = UnityEngine.Random.Range(0f, 1f) > 0.5f;

                    if (spawnGuard)
                    {
                        ModLogger.Info("Spawning test GUARD with proper uniform...");
                        testNPC = BaseNPCSpawner.SpawnGuard(spawnPos, "Officer", "Test", $"G{UnityEngine.Random.Range(1000, 9999)}");
                    }
                    else
                    {
                        ModLogger.Info("Spawning test INMATE with orange jumpsuit...");
                        testNPC = BaseNPCSpawner.SpawnInmate(spawnPos, "Inmate", $"Test{UnityEngine.Random.Range(100, 999)}");
                    }

                    if (testNPC != null)
                    {
                        ModLogger.Info($"✅ Test {(spawnGuard ? "GUARD" : "INMATE")} spawned successfully: {testNPC.name}");
                        ModLogger.Info($"Appearance: {(spawnGuard ? "Blue uniform with police cap and combat boots" : "Orange jumpsuit with sandals")}");
                    }
                    else
                    {
                        ModLogger.Error("❌ Failed to spawn test NPC");
                    }
                }
                else
                {
                    ModLogger.Warn("Player not found for NPC spawn location");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error testing NPC spawn: {e.Message}");
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
                    Vector3 jailPosition = new Vector3(44.324f, 10.2846f, -218.7174f);
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
        
        /// <summary>
        /// Teleport player to Taco Ticklers for testing
        /// </summary>
        private void TeleportToTacoTicklers()
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
                    Vector3 tacoTicklersPosition = new Vector3(-30.4759f, 2.0734f, 61.9903f);
                    player.transform.position = tacoTicklersPosition;
                    ModLogger.Info($"✓ Teleported player to Taco Ticklers at {tacoTicklersPosition}");
                }
                else
                {
                    ModLogger.Warn("Player not found for teleportation");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error teleporting to Taco Ticklers: {e.Message}");
            }
        }

        /// <summary>
        /// Quick 10-second jail sentence for release testing - skips full booking process
        /// </summary>
        private void QuickJailForReleaseTesting()
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
                    ModLogger.Info("F6 pressed - Quick 10-second jail for release testing!");

                    if (JailSystem != null && JailController != null)
                    {
                        // Create a minimal 10-second sentence
                        var testSentence = new JailSystem.JailSentence
                        {
                            JailTime = 10f, // 10 seconds
                            FineAmount = 100f,
                            Severity = JailSystem.JailSeverity.Minor,
                            Description = "Quick Test Sentence",
                            CanPayFine = true
                        };

                        // Skip booking stations - go straight to cell assignment
                        ModLogger.Info("Skipping booking process for quick test...");

                        // Assign player to a cell
                        var cellManager = Behind_Bars.Systems.Jail.CellAssignmentManager.Instance;
                        if (cellManager != null)
                        {
                            int cellNumber = cellManager.AssignPlayerToCell(player);
                            if (cellNumber >= 0)
                            {
                                ModLogger.Info($"✓ Player assigned to cell {cellNumber}");

                                // Teleport player to the cell
                                var cell = JailController.GetCellByIndex(cellNumber);
                                if (cell?.cellTransform != null)
                                {
                                    player.transform.position = cell.cellTransform.position + Vector3.up * 1f;
                                    ModLogger.Info($"✓ Player teleported to cell {cellNumber}");

                                    // Close and lock the cell door
                                    if (JailController.doorController != null)
                                    {
                                        JailController.doorController.CloseJailCellDoor(cellNumber);
                                        ModLogger.Info($"✓ Cell {cellNumber} door closed and locked");
                                    }

                                    // Start UI timer
                                    if (BehindBarsUIManager.Instance?.GetUIWrapper() != null)
                                    {
                                        float bailAmount = JailSystem.CalculateBailAmount(testSentence.FineAmount, testSentence.Severity);
                                        BehindBarsUIManager.Instance.GetUIWrapper().StartDynamicUpdates(testSentence.JailTime, bailAmount);
                                        ModLogger.Info($"✓ UI timer started: 10s jail time, ${bailAmount} bail");
                                    }

                                    ModLogger.Info("✓ Quick jail test complete - player in cell with 10-second sentence!");
                                    ModLogger.Info("   Timer will trigger automatic release when complete");
                                }
                                else
                                {
                                    ModLogger.Error("Could not find cell transform for teleport");
                                }
                            }
                            else
                            {
                                ModLogger.Error("Failed to assign cell");
                            }
                        }
                        else
                        {
                            ModLogger.Error("CellAssignmentManager not available");
                        }
                    }
                    else
                    {
                        ModLogger.Error("JailSystem or JailController not available");
                    }
                }
                else
                {
                    ModLogger.Warn("No player found for quick jail test");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error in quick jail test: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Trigger an instant arrest for testing purposes
        /// </summary>
        private void TriggerTestArrest()
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
                    ModLogger.Info("F8 pressed - Triggering test arrest!");

                    // Start the arrest process through JailSystem
                    if (JailSystem != null)
                    {
                        // Trigger immediate arrest using the existing system
                        MelonCoroutines.Start(JailSystem.HandleImmediateArrest(player));
                        ModLogger.Info("✓ Test arrest triggered - player will be processed through booking");
                    }
                    else
                    {
                        ModLogger.Error("JailSystem not available for arrest trigger");
                    }
                }
                else
                {
                    ModLogger.Warn("No player found to arrest");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error triggering test arrest: {e.Message}");
            }
        }

        /// <summary>
        /// Handle scene changes for cleanup (especially when game is quit)
        /// </summary>
        private void OnSceneChanged(Scene oldScene, Scene newScene)
        {
            try
            {
                ModLogger.Info($"Scene changed from '{oldScene.name}' to '{newScene.name}'");

                // Clean up UI when leaving the main game scene
                if (oldScene.name == "Main" || newScene.name == "Menu" || newScene.name == "Loading")
                {
                    ModLogger.Info("Game scene exiting - cleaning up Behind Bars UI");
                    HideJailInfoUI();
                    
                    // Stop any dynamic updates that might be running
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.DestroyJailInfoUI();
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error handling scene change: {e.Message}");
            }
        }

        /// <summary>
        /// Cleanup when mod is being destroyed
        /// </summary>
        public override void OnDeinitializeMelon()
        {
            try
            {
                ModLogger.Info("Behind Bars shutting down - cleaning up...");

                // Unsubscribe from scene events
#if !MONO
                SceneManager.activeSceneChanged -= new System.Action<Scene, Scene>(OnSceneChanged);
#else
                SceneManager.activeSceneChanged -= OnSceneChanged;
#endif

                // Clean up UI
                HideJailInfoUI();
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.DestroyJailInfoUI();
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error during Behind Bars cleanup: {e.Message}");
            }
        }

        /// <summary>
        /// Setup exit door using GuardDoor prefab like other doors
        /// </summary>
        private static void SetupExitDoor(JailController jailController)
        {
            try
            {
                ModLogger.Info("Setting up exit door...");

                // Get the ExitScannerArea
                if (jailController.exitScanner?.exitDoor != null)
                {
                    var exitDoor = jailController.exitScanner.exitDoor;
                    ModLogger.Info($"Found exitDoor in ExitScannerArea: {exitDoor.doorName}");

                    // Instantiate using steelDoorPrefab (GuardDoor)
                    if (jailController.doorController?.steelDoorPrefab != null && exitDoor.doorHolder != null)
                    {
                        if (!exitDoor.IsInstantiated())
                        {
                            exitDoor.doorInstance = UnityEngine.Object.Instantiate(jailController.doorController.steelDoorPrefab, exitDoor.doorHolder);
                            ModLogger.Info("✓ Exit door instantiated using steelDoorPrefab");

                            // Enable SecuritySlots for visual difference
                            var hingePoint = exitDoor.doorInstance.transform.Find("HingePoint");
                            if (hingePoint != null)
                            {
                                var securitySlots = hingePoint.Find("SecuritySlots");
                                if (securitySlots != null)
                                {
                                    securitySlots.gameObject.SetActive(true);
                                    ModLogger.Info("✓ SecuritySlots enabled on exit door");
                                }
                            }

                            // Lock the door initially
                            exitDoor.LockDoor();
                            ModLogger.Info("✓ Exit door locked initially");
                        }
                        else
                        {
                            ModLogger.Info("Exit door already instantiated");
                        }
                    }
                    else
                    {
                        ModLogger.Warn($"Cannot instantiate exit door - steelDoorPrefab: {jailController.doorController?.steelDoorPrefab != null}, doorHolder: {exitDoor.doorHolder != null}");
                    }
                }
                else
                {
                    ModLogger.Warn("No exitScanner or exitDoor found in JailController for setup");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error setting up exit door: {ex.Message}");
            }
        }
    }
}
