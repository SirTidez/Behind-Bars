using System;
using System.Collections.Generic;
using System.IO;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using S1Persistence = Il2CppScheduleOne.Persistence;
#else
using ScheduleOne.PlayerScripts;
using S1Persistence = ScheduleOne.Persistence;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Manages RapSheet instances. Caches instances to prevent repeated creation and registration.
    /// </summary>
    public class RapSheetManager
    {
        private static RapSheetManager _instance;
        private static bool _isManagedBySystemManager;

        /// <summary>
        /// Compatibility singleton accessor. Prefers the manager-registered instance when available.
        /// </summary>
        public static RapSheetManager Instance
        {
            get
            {
                if (TryGetRegisteredInstance(out var existing))
                {
                    return existing;
                }

                return RegisterInstance(new RapSheetManager(), false);
            }
        }

        /// <summary>
        /// Returns true when a rap-sheet manager is already registered.
        /// </summary>
        public static bool HasRegisteredInstance => _instance != null;

        /// <summary>
        /// Registers the active rap-sheet manager instance.
        /// </summary>
        public static RapSheetManager RegisterInstance(RapSheetManager instance, bool managedBySystemManager = false)
        {
            if (instance == null)
            {
                return null;
            }

            _instance = instance;
            _isManagedBySystemManager = managedBySystemManager;
            return _instance;
        }

        /// <summary>
        /// Creates the manager-owned instance when none is registered yet.
        /// </summary>
        public static RapSheetManager BootstrapManagedInstance()
        {
            if (TryGetRegisteredInstance(out var existing))
            {
                return existing;
            }

            return RegisterInstance(new RapSheetManager(), true);
        }

        /// <summary>
        /// Returns the currently registered instance when present.
        /// </summary>
        public static bool TryGetRegisteredInstance(out RapSheetManager instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// Tears down the manager-owned instance while leaving compatibility-created instances alone.
        /// </summary>
        public static bool ShutdownManagedInstance()
        {
            if (_instance == null || !_isManagedBySystemManager)
            {
                return false;
            }

            _instance.ClearCache();
            _instance = null;
            _isManagedBySystemManager = false;
            return true;
        }

        /// <summary>
        /// Cache of RapSheet instances by stable player key.
        /// Save-path lookup is stable-key-first with a legacy name-folder fallback.
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
            string playerCacheKey = GetPlayerCacheKey(player);
            string stableSaveIdentity = RapSheet.GetPersistenceIdentityForPlayer(player);
            
            // Check cache first to avoid creating duplicate instances
            if (_rapSheetCache.TryGetValue(playerCacheKey, out RapSheet cachedRapSheet))
            {
                // Update player reference in case it changed
                if (cachedRapSheet.Player != player)
                {
                    cachedRapSheet.SetPlayer(player);
                }
                
                // Return cached instance - no need to log every time
                return cachedRapSheet;
            }

            if (!string.Equals(playerCacheKey, playerName, StringComparison.Ordinal) &&
                _rapSheetCache.TryGetValue(playerName, out RapSheet legacyCachedRapSheet))
            {
                if (legacyCachedRapSheet.Player != player)
                {
                    legacyCachedRapSheet.SetPlayer(player);
                }

                _rapSheetCache.Remove(playerName);
                _rapSheetCache[playerCacheKey] = legacyCachedRapSheet;
                return legacyCachedRapSheet;
            }

            // Check if we should load from save data first
            bool shouldLoadFromSave = false;
            string savePath = null;
            bool loadedFromLegacyNamePath = false;
            try
            {
                var loadManager = S1Persistence.LoadManager.Instance;
                if (loadManager != null && !string.IsNullOrEmpty(loadManager.LoadedGameFolderPath))
                {
                    string stableSavePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables", "BehindBars", stableSaveIdentity);
                    string legacySavePath = Path.Combine(loadManager.LoadedGameFolderPath, "Modded", "Saveables", "BehindBars", playerName);

                    if (Directory.Exists(stableSavePath))
                    {
                        savePath = stableSavePath;
                        shouldLoadFromSave = true;
                    }
                    else if (Directory.Exists(legacySavePath))
                    {
                        savePath = legacySavePath;
                        shouldLoadFromSave = true;
                        loadedFromLegacyNamePath = true;
                    }

                    ModLogger.Debug($"[RAP SHEET] Checking save paths for {playerName} - stable: {stableSavePath} (exists: {Directory.Exists(stableSavePath)}), legacy: {legacySavePath} (exists: {Directory.Exists(legacySavePath)})");
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

                    if (loadedFromLegacyNamePath)
                    {
                        rapSheet.MarkChanged();
                        ModLogger.Info($"[RAP SHEET] Migrated RapSheet save identity for {playerName} to {stableSaveIdentity}");
                    }
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
            _rapSheetCache[playerCacheKey] = rapSheet;

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

            string playerCacheKey = GetPlayerCacheKey(player);
            string playerName = player.name;
            bool removed = _rapSheetCache.Remove(playerCacheKey);

            if (!string.Equals(playerCacheKey, playerName, StringComparison.Ordinal))
            {
                removed = _rapSheetCache.Remove(playerName) || removed;
            }

            if (removed)
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

        private static string GetPlayerCacheKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Behind_Bars.Core.ResolvePlayerKey(player);
        }
    }
}

