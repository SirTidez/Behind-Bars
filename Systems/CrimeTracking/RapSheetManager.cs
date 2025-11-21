using System.Collections.Generic;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Manages RapSheet instances. Caches instances to prevent repeated creation and registration.
    /// </summary>
    public class RapSheetManager
    {
        private static RapSheetManager _instance;
        public static RapSheetManager Instance => _instance ??= new RapSheetManager();

        /// <summary>
        /// Cache of RapSheet instances by player name.
        /// Prevents creating multiple instances for the same player.
        /// </summary>
        private readonly Dictionary<string, RapSheet> _rapSheetCache = new Dictionary<string, RapSheet>();

        private RapSheetManager()
        {
            ModLogger.Debug("RapSheetManager initialized");
        }

        /// <summary>
        /// Get or create a RapSheet for a player.
        /// Returns cached instance if available, otherwise creates and caches a new one.
        /// </summary>
        public RapSheet GetRapSheet(Player player)
        {
            if (player == null)
            {
                ModLogger.Warn("RapSheetManager: Cannot get rap sheet for null player");
                return null;
            }

            string playerName = player.name;
            
            // Check cache first to avoid creating duplicate instances
            if (_rapSheetCache.TryGetValue(playerName, out RapSheet cachedRapSheet))
            {
                // Update player reference in case it changed
                if (cachedRapSheet.Player != player)
                {
                    cachedRapSheet.Player = player;
                }
                
                // Return cached instance - no need to log every time
                return cachedRapSheet;
            }

            // Create new rap sheet only if not in cache
            // This will auto-register with SaveManager and load from save system
            // RapSheet constructor calls InitializeSaveable() which registers with SaveManager
            // The game's save system will call Loader.Load() when loading, which calls OnLoaded()
            var rapSheet = new RapSheet(player);
            int crimeCount = rapSheet.CrimesCommited?.Count ?? 0;
            ModLogger.Debug($"[RAP SHEET] RapSheet created for {playerName} - {crimeCount} crimes, LSI: {rapSheet.LSILevel}");

            // Cache the instance to prevent repeated creation
            _rapSheetCache[playerName] = rapSheet;

            return rapSheet;
        }

        /// <summary>
        /// Mark rap sheet data as changed - game's save system will handle saving automatically
        /// </summary>
        public void MarkRapSheetChanged(Player player)
        {
            if (player == null)
                return;

            // Get the rap sheet - uses cached instance if available
            var rapSheet = GetRapSheet(player);
            if (rapSheet != null)
            {
                // Mark as changed - game's save system will save it automatically
                rapSheet.MarkChanged();
                ModLogger.Debug($"[RAP SHEET] Marked RapSheet as changed for {player.name}");
            }
        }

        /// <summary>
        /// Clear the cache for a specific player (useful when player is removed or save changes).
        /// </summary>
        public void ClearCacheForPlayer(Player player)
        {
            if (player == null)
                return;

            string playerName = player.name;
            if (_rapSheetCache.Remove(playerName))
            {
                ModLogger.Debug($"[RAP SHEET] Cleared cache for {playerName}");
            }
        }

        /// <summary>
        /// Clear all cached RapSheet instances (useful when save changes).
        /// </summary>
        public void ClearCache()
        {
            int count = _rapSheetCache.Count;
            _rapSheetCache.Clear();
            ModLogger.Debug($"[RAP SHEET] Cleared all cached RapSheet instances ({count} removed)");
        }
    }
}

