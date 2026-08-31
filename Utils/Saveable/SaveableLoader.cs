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
    /// Loader implementation for <see cref="Saveable"/> objects.
    /// Resolves the path supplied by the game's S1API loader and delegates the
    /// actual per-field work to <see cref="Saveable.LoadInternal"/>.
    ///
    /// The loader is intentionally tolerant of missing paths: a new save starts
    /// with the instance's constructor defaults and still receives its
    /// <see cref="ISaveable.OnLoaded"/> callback.
    /// </summary>
    public class SaveableLoader : Loader
    {
        /// <summary>The saveable whose fields and lifecycle hooks this loader serves.</summary>
        private readonly Saveable _saveable;

        /// <summary>
        /// Creates a loader bound to one saveable instance.
        /// </summary>
        /// <param name="saveable">Instance that will receive loaded field values and callbacks.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="saveable"/> is null.</exception>
        public SaveableLoader(Saveable saveable)
        {
            _saveable = saveable ?? throw new ArgumentNullException(nameof(saveable));
        }

        /// <summary>
        /// Loads saveable data from the path supplied by the game's save system.
        /// </summary>
        /// <param name="mainPath">
        /// A save file path or a parent save directory. When the saveable uses a
        /// folder layout and the path is not an existing file, its configured
        /// <see cref="Saveable.SaveFolderNameInternal"/> is appended.
        /// </param>
        /// <remarks>
        /// Existing file paths are reduced to their containing directory; this
        /// loader does not deserialize that file as one aggregate object. Missing
        /// or empty paths initialize through <see cref="ISaveable.OnLoaded"/>
        /// instead. A present directory delegates to <see cref="Saveable.LoadInternal"/>
        /// (which invokes the hook after its field loop). Exceptions are logged,
        /// then the hook is attempted as a best-effort fallback; a callback that
        /// itself throws is logged separately. If the delegated load already ran
        /// the hook and then threw (for example, because the hook itself threw),
        /// this fallback can invoke it a second time.
        /// </remarks>
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
                
                // If the path is a file, get its directory
                if (File.Exists(mainPath))
                {
                    folderPath = Path.GetDirectoryName(mainPath);
                }
                // If ShouldSaveUnderFolder, the folder should be the SaveFolderName subdirectory
                else if (_saveable.ShouldSaveUnderFolderInternal)
                {
                    // mainPath might be the parent folder, so we need to add SaveFolderName
                    string parentFolder = Path.GetDirectoryName(mainPath);
                    if (string.IsNullOrEmpty(parentFolder))
                        parentFolder = mainPath;
                    
                    folderPath = Path.Combine(parentFolder, _saveable.SaveFolderNameInternal);
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

