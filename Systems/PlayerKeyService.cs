using Behind_Bars.Utils;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Default player-key resolver owned by <see cref="BehindBarsSystemManager"/>.
    /// Preference order is explicit player code, then FishNet object ID, then player name.
    /// </summary>
    public sealed class PlayerKeyService : IPlayerKeyService
    {
        /// <inheritdoc />
        public string GetPlayerKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(player.PlayerCode))
            {
                return $"playercode:{player.PlayerCode}";
            }

            int networkId = NetworkHelper.GetPlayerNetworkId(player);
            if (networkId >= 0)
            {
                return $"network:{networkId}";
            }

            return $"name:{player.name}";
        }
    }
}
