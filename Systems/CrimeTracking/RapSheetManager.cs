using System.Collections.Generic;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Manages cached RapSheet instances to avoid repeated file loads
    /// </summary>
    public class RapSheetManager
    {
        private static RapSheetManager _instance;
        public static RapSheetManager Instance => _instance ??= new RapSheetManager();

        private Dictionary<Player, RapSheet> _rapSheetCache = new Dictionary<Player, RapSheet>();

        private RapSheetManager()
        {
            ModLogger.Info("RapSheetManager initialized");
        }

        /// <summary>
        /// Get or create a RapSheet for a player. Caches the instance after first load.
        /// </summary>
        public RapSheet GetRapSheet(Player player)
        {
            if (player == null)
            {
                ModLogger.Warn("RapSheetManager: Cannot get rap sheet for null player");
                return null;
            }

            // Check cache first
            if (_rapSheetCache.TryGetValue(player, out RapSheet cachedSheet))
            {
                // Verify the cached sheet is still valid (player reference matches)
                if (cachedSheet != null && cachedSheet.Player == player)
                {
                    return cachedSheet;
                }
                else
                {
                    // Cache entry is stale, remove it
                    _rapSheetCache.Remove(player);
                }
            }

            // Create new rap sheet and load from file
            var rapSheet = new RapSheet(player);
            
            // Try to load existing rap sheet
            if (!rapSheet.LoadRapSheet())
            {
                // No existing rap sheet - initialize new one
                ModLogger.Debug($"No existing rap sheet found for {player.name}, creating new one");
                rapSheet.InmateID = rapSheet.GenerateInmateID();
                if (rapSheet.CrimesCommited == null)
                    rapSheet.CrimesCommited = new List<CrimeInstance>();
                if (rapSheet.PastParoleRecords == null)
                    rapSheet.PastParoleRecords = new List<ParoleRecord>();
            }

            // Cache the rap sheet
            _rapSheetCache[player] = rapSheet;
            ModLogger.Debug($"RapSheet cached for {player.name}");

            return rapSheet;
        }

        /// <summary>
        /// Invalidate the cache for a specific player (e.g., after saving changes)
        /// </summary>
        public void InvalidateCache(Player player)
        {
            if (player != null && _rapSheetCache.ContainsKey(player))
            {
                _rapSheetCache.Remove(player);
                ModLogger.Debug($"RapSheet cache invalidated for {player.name}");
            }
        }

        /// <summary>
        /// Clear all cached rap sheets
        /// </summary>
        public void ClearCache()
        {
            _rapSheetCache.Clear();
            ModLogger.Info("RapSheet cache cleared");
        }

        /// <summary>
        /// Save a rap sheet and optionally invalidate cache
        /// </summary>
        public bool SaveRapSheet(Player player, bool invalidateCache = false)
        {
            var rapSheet = GetRapSheet(player);
            if (rapSheet == null)
            {
                return false;
            }

            bool success = rapSheet.SaveRapSheet();
            
            if (success && invalidateCache)
            {
                InvalidateCache(player);
            }

            return success;
        }
    }
}

