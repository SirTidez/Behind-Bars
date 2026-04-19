namespace Behind_Bars.Systems
{
    /// <summary>
    /// Resolves a stable runtime key for a player across justice-system subsystems.
    /// This service exists so persistence, jail occupancy, bail tracking, and parole
    /// state can stop inventing their own player-name-based identifiers.
    /// </summary>
    public interface IPlayerKeyService
    {
#if !MONO
        string GetPlayerKey(Il2CppScheduleOne.PlayerScripts.Player player);
#else
        string GetPlayerKey(ScheduleOne.PlayerScripts.Player player);
#endif
    }
}
