using System.Collections;
using MelonLoader;
using HarmonyLib;
using System;
using Behind_Bars.Helpers;
using UnityEngine;
using Il2CppScheduleOne.PlayerScripts;
using Behind_Bars.Players;
using Behind_Bars.Systems;

#if MONO
using FishNet;
#else
using Il2CppFishNet;
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
        
        // Core systems
        private JailSystem? _jailSystem;
        private BailSystem? _bailSystem;
        private CourtSystem? _courtSystem;
        private ProbationSystem? _probationSystem;
        
        // Player management
        private Dictionary<Player, PlayerHandler> _playerHandlers = new();

        public override void OnInitializeMelon()
        {
            Instance = this;
            
            // Initialize core systems
            _jailSystem = new JailSystem();
            _bailSystem = new BailSystem();
            _courtSystem = new CourtSystem();
            _probationSystem = new ProbationSystem();
            
            ModLogger.Info("Behind Bars initialized with all systems");
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
                Player.Local.onArrested.AddListener(new Action(OnPlayerArrested));
                
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
                MelonCoroutines.Start(_jailSystem!.HandlePlayerArrest(Player.Local));
            }
        }

        public JailSystem GetJailSystem() => _jailSystem!;
        public BailSystem GetBailSystem() => _bailSystem!;
        public CourtSystem GetCourtSystem() => _courtSystem!;
        public ProbationSystem GetProbationSystem() => _probationSystem!;
    }
}
