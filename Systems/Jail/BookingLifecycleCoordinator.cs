using System;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Managed-only notification bridge for the point at which booking has actually
    /// finished: the prisoner was secured and the sentence countdown was started.
    /// Keeping this outside an injected behaviour avoids exposing delegate fields to IL2CPP.
    /// </summary>
    internal static class BookingLifecycleCoordinator
    {
        internal static event Action<Player> BookingFinalized;

        internal static void PublishFinalized(Player player)
        {
            if (player == null)
            {
                return;
            }

            BookingFinalized?.Invoke(player);
        }
    }
}
