using System;
using System.Collections.Generic;
using System.IO;
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

            // Check if we should load from save data first
            bool shouldLoadFromSave = false;
            string savePath = null;
            try
            {
                var loadManager = ScheduleOne.Persistence.LoadManager.Instance;
                if (loadManager != null && !string.IsNullOrEmpty(loadManager.LoadedGameFolderPath))
                {
                    // RapSheet saves to Modded/Saveables/BehindBars/{PlayerName}/
                    savePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables", "BehindBars", playerName);
                    shouldLoadFromSave = Directory.Exists(savePath);
                    ModLogger.Debug($"[RAP SHEET] Checking save path for {playerName}: {savePath} (exists: {shouldLoadFromSave})");
                }
                else
                {
                    ModLogger.Debug($"[RAP SHEET] LoadManager not available or no loaded game folder path for {playerName}");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[RAP SHEET] Error checking save path for {playerName}: {ex.Message}");
            }
            
            // Create new rap sheet only if not in cache
            // Skip OnLoaded() if we're going to load data - LoadInternal() will call it
            // This will auto-register with SaveManager
            // RapSheet constructor calls InitializeSaveable() which registers with SaveManager
            var rapSheet = new RapSheet(player, skipOnLoaded: shouldLoadFromSave);
            
            // Load from save data if available
            // The Loader.Load() will be called by the game's save system, but we need to trigger it manually
            // since RapSheet is not auto-discovered (it's per-player, not singleton)
            if (shouldLoadFromSave && !string.IsNullOrEmpty(savePath))
            {
                try
                {
                    ModLogger.Debug($"[RAP SHEET] Loading RapSheet data for {playerName} from {savePath}");
                    rapSheet.LoadInternal(savePath);
                    int loadedCrimeCount = rapSheet.CrimesCommited?.Count ?? 0;
                    bool hasParoleRecord = rapSheet.CurrentParoleRecord != null;
                    ModLogger.Debug($"[RAP SHEET] Successfully loaded RapSheet data for {playerName} - Crimes: {loadedCrimeCount}, HasParoleRecord: {hasParoleRecord}, LSI: {rapSheet.LSILevel}");
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[RAP SHEET] Error loading RapSheet data for {playerName}: {ex.Message}");
                    ModLogger.Warn($"[RAP SHEET] Stack trace: {ex.StackTrace}");
                    // If loading failed, call OnLoaded() via ISaveable interface to ensure initialization
                    try
                    {
                        Behind_Bars.Utils.Saveable.ISaveable saveableInterface = rapSheet;
                        saveableInterface.OnLoaded();
                    }
                    catch (Exception onLoadedEx)
                    {
                        ModLogger.Error($"[RAP SHEET] Error calling OnLoaded() after load failure: {onLoadedEx.Message}");
                    }
                }
            }
            else if (!shouldLoadFromSave)
            {
                // No save data - this is a new RapSheet, OnLoaded() was already called in constructor
                ModLogger.Debug($"[RAP SHEET] No save data found for {playerName} - creating new RapSheet");
            }
            
            int crimeCount = rapSheet.CrimesCommited?.Count ?? 0;
            bool hasCurrentParole = rapSheet.CurrentParoleRecord != null;
            ModLogger.Debug($"[RAP SHEET] RapSheet final state for {playerName} - Crimes: {crimeCount}, HasCurrentParole: {hasCurrentParole}, LSI: {rapSheet.LSILevel}");

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

        /// <summary>
        /// Gets all cached RapSheet instances. Used by the save system to save all RapSheets.
        /// </summary>
        /// <returns>Collection of all cached RapSheet instances.</returns>
        public IEnumerable<RapSheet> GetAllRapSheets()
        {
            return _rapSheetCache.Values;
        }
    }
}

