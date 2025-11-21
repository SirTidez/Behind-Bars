using System;
using System.IO;
using Behind_Bars.Helpers;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.Persistence.Loaders;
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Loader implementation for Saveable objects.
    /// Handles deserialization of Saveable data from JSON files.
    /// </summary>
    public class SaveableLoader : Loader
    {
        private readonly Saveable _saveable;

        public SaveableLoader(Saveable saveable)
        {
            _saveable = saveable ?? throw new ArgumentNullException(nameof(saveable));
        }

        /// <summary>
        /// Loads saveable data from a file path.
        /// This is called by the game's save system when loading.
        /// </summary>
        public override void Load(string mainPath)
        {
            try
            {
                if (string.IsNullOrEmpty(mainPath))
                {
                    ModLogger.Warn("[SAVEABLE] Load path is null or empty");
                    return;
                }

                // Try to load the file
                string jsonContent;
                if (!TryLoadFile(mainPath, out jsonContent))
                {
                    // File doesn't exist - this is normal for new saves
                    ModLogger.Debug($"[SAVEABLE] Save file not found at {mainPath} - initializing new save data");
                    _saveable.InternalOnLoaded(); // Still call OnLoaded for initialization
                    return;
                }

                if (string.IsNullOrEmpty(jsonContent))
                {
                    ModLogger.Warn("[SAVEABLE] Loaded JSON content is empty");
                    _saveable.InternalOnLoaded(); // Still call OnLoaded for initialization
                    return;
                }

                // Deserialize and apply to the saveable
                _saveable.DeserializeFromJson(jsonContent);
                _saveable.InternalOnLoaded();

                ModLogger.Debug($"[SAVEABLE] Successfully loaded {_saveable.GetType().Name} from {mainPath}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE] Error loading {_saveable.GetType().Name} from {mainPath}: {ex.Message}");
                ModLogger.Error($"[SAVEABLE] Stack trace: {ex.StackTrace}");
                // Still call OnLoaded even if deserialization failed
                try
                {
                    _saveable.InternalOnLoaded();
                }
                catch (Exception onLoadedEx)
                {
                    ModLogger.Error($"[SAVEABLE] Error in OnLoaded after load failure: {onLoadedEx.Message}");
                }
            }
        }
    }
}

