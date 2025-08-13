using System.Collections;
using MelonLoader;
using Behind_Bars.Helpers;
using UnityEngine;
#if MONO
using FishNet;
#else
using Il2CppFishNet;
#endif

[assembly: MelonInfo(
    typeof(Behind_Bars.Behind_Bars),
    Behind_Bars.BuildInfo.Name,
    Behind_Bars.BuildInfo.Version,
    Behind_Bars.BuildInfo.Author
)]
[assembly: MelonColor(1, 255, 0, 0)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Behind_Bars
{
    public static class BuildInfo
    {
        public const string Name = "Behind Bars";
        public const string Description = "Behind Bars is a gameplay expansion mod designed to enhance the law enforcement mechanics in Schedule 1.";
        public const string Author = "SirTidez";
        public const string Version = "1.0.0";
    }

    public class Behind_Bars : MelonMod
    {
        private static MelonLogger.Instance Logger;

        public override void OnInitializeMelon()
        {
            Logger = LoggerInstance;
            Logger.Msg("Behind_Bars initialized");
            Logger.Debug("This will only show in debug mode");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Logger.Debug($"Scene loaded: {sceneName}");
            if (sceneName == "Main")
            {
                Logger.Debug("Main scene loaded, waiting for player");
                MelonCoroutines.Start(Utils.WaitForPlayer(DoStuff()));

                Logger.Debug("Main scene loaded, waiting for network");
                MelonCoroutines.Start(Utils.WaitForNetwork(DoNetworkStuff()));
            }
        }

        private IEnumerator DoStuff()
        {
            Logger.Msg("Player ready, doing stuff...");
            yield return new WaitForSeconds(2f);
            Logger.Msg("Did some stuff!");
        }

        private IEnumerator DoNetworkStuff()
        {
            Logger.Msg("Network ready, doing network stuff...");
            var nm = InstanceFinder.NetworkManager;
            if (nm.IsServer && nm.IsClient)
                Logger.Debug("Host");
            else if (!nm.IsServer && !nm.IsClient)
                Logger.Debug("Singleplayer");
            else if (nm.IsClient && !nm.IsServer)
                Logger.Debug("Client-only");
            else if (nm.IsServer && !nm.IsClient)
                Logger.Debug("Server-only");
            yield return new WaitForSeconds(2f);
            Logger.Msg("Did some network stuff!");
        }
    }
}
