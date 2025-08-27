using System.Collections;
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
#else
using Il2CppFishNet;
using Il2CppInterop.Runtime.Injection;
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
                if (sceneName == "Main") { 
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
            yield return new WaitForSeconds(5f);
            
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
    }
}
