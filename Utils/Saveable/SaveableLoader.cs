using System;
using System.IO;
using Behind_Bars.Helpers;
using ScheduleOne.Persistence;
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
    /// Handles deserialization of Saveable data from JSON files using the S1API pattern.
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
        /// Uses LoadInternal to load individual field files (S1API pattern).
        /// </summary>
        public override void Load(string mainPath)
        {
            try
            {
                if (string.IsNullOrEmpty(mainPath))
                {
                    ModLogger.Warn("[SAVEABLE] Load path is null or empty");
                    // Still call OnLoaded for initialization via ISaveable interface
                    Behind_Bars.Utils.Saveable.ISaveable internalInterface = _saveable;
                    internalInterface.OnLoaded();
                    return;
                }

                // Determine the folder path for loading
                string folderPath = mainPath;
                ScheduleOne.Persistence.ISaveable saveableInterface = _saveable;
                
                // If the path is a file, get its directory
                if (File.Exists(mainPath))
                {
                    folderPath = Path.GetDirectoryName(mainPath);
                }
                // If ShouldSaveUnderFolder, the folder should be the SaveFolderName subdirectory
                else if (saveableInterface.ShouldSaveUnderFolder)
                {
                    // mainPath might be the parent folder, so we need to add SaveFolderName
                    string parentFolder = Path.GetDirectoryName(mainPath);
                    if (string.IsNullOrEmpty(parentFolder))
                        parentFolder = mainPath;
                    
                    folderPath = Path.Combine(parentFolder, saveableInterface.SaveFolderName);
                }

                // Ensure folder exists (might not for new saves)
                if (!Directory.Exists(folderPath))
                {
                    ModLogger.Debug($"[SAVEABLE] Save folder not found at {folderPath} - initializing new save data");
                    // Still call OnLoaded for initialization via ISaveable interface
                    Behind_Bars.Utils.Saveable.ISaveable internalSaveable = _saveable;
                    internalSaveable.OnLoaded();
                    return;
                }

                // Use LoadInternal to load all fields from individual JSON files
                _saveable.LoadInternal(folderPath);

                ModLogger.Debug($"[SAVEABLE] Successfully loaded {_saveable.GetType().Name} from {folderPath}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE] Error loading {_saveable.GetType().Name} from {mainPath}: {ex.Message}");
                ModLogger.Error($"[SAVEABLE] Stack trace: {ex.StackTrace}");
                // Still call OnLoaded even if loading failed
                try
                {
                    ISaveable saveableInterface = _saveable;
                    saveableInterface.OnLoaded();
                }
                catch (Exception onLoadedEx)
                {
                    ModLogger.Error($"[SAVEABLE] Error in OnLoaded after load failure: {onLoadedEx.Message}");
                }
            }
        }
    }
}

