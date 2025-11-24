using MelonLoader.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;



#if !MONO
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Persistence;
#else
using FishNet;
using ScheduleOne.DevUtilities;
using ScheduleOne.Persistence;
#endif

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Provides utility methods for file operations with caching capabilities.
    /// Manages file reading, writing, and caching for the Behind Bars mod data directory.
    /// Now supports save-specific directories for multiple save files.
    /// </summary>
    public class FileUtilities
    {
        private static string _baseDataDirectory;
        private static string _currentSaveDirectory;
        private static string _currentSaveName;
        public static FileUtilities Instance;

        private static Dictionary<string, string> _fileCache = new Dictionary<string, string>();

        /// <summary>
        /// Gets the current save name from GameManager or LoadManager
        /// </summary>
        /// <returns>The save name, or null if not available (to avoid creating default folder)</returns>
        private static string GetSaveName()
        {
            try
            {
#if !MONO
                // First try LoadManager.ActiveSaveInfo (more reliable, set when save is loaded)
                if (Singleton<Il2CppScheduleOne.Persistence.LoadManager>.InstanceExists)
                {
                    var loadManager = Singleton<Il2CppScheduleOne.Persistence.LoadManager>.Instance;
                    if (loadManager != null)
                    {
                        // Only try to get save name if a game is actually loaded
                        if (loadManager.IsGameLoaded && !string.IsNullOrEmpty(loadManager.LoadedGameFolderPath))
                        {
                            if (loadManager.ActiveSaveInfo != null)
                            {
                                string saveName = loadManager.ActiveSaveInfo.OrganisationName;
                                if (!string.IsNullOrEmpty(saveName) && saveName != "Game" && saveName != "Organisation")
                                {
                                    ModLogger.Debug($"Got organization name from LoadManager: {saveName}");
                                    return saveName;
                                }
                            }
                        }
                    }
                }

                // Fallback to GameManager (only if game is loaded)
                if (NetworkSingleton<GameManager>.InstanceExists && NetworkSingleton<GameManager>.Instance != null)
                {
                    string saveName = NetworkSingleton<GameManager>.Instance.OrganisationName;
                    if (!string.IsNullOrEmpty(saveName) && saveName != "Game" && saveName != "Organisation")
                    {
                        ModLogger.Debug($"Got organization name from GameManager: {saveName}");
                        return saveName;
                    }
                }
#else
                // First try LoadManager.ActiveSaveInfo (more reliable, set when save is loaded)
                if (Singleton<ScheduleOne.Persistence.LoadManager>.InstanceExists)
                {
                    var loadManager = Singleton<ScheduleOne.Persistence.LoadManager>.Instance;
                    if (loadManager != null)
                    {
                        // Only try to get save name if a game is actually loaded
                        if (loadManager.IsGameLoaded && !string.IsNullOrEmpty(loadManager.LoadedGameFolderPath))
                        {
                            if (loadManager.ActiveSaveInfo != null)
                            {
                                string saveName = loadManager.ActiveSaveInfo.OrganisationName;
                                if (!string.IsNullOrEmpty(saveName) && saveName != "Game" && saveName != "Organisation")
                                {
                                    ModLogger.Debug($"Got organization name from LoadManager: {saveName}");
                                    return saveName;
                                }
                            }
                        }
                    }
                }

                // Fallback to GameManager (only if game is loaded)
                if (NetworkSingleton<GameManager>.InstanceExists && NetworkSingleton<GameManager>.Instance != null)
                {
                    string saveName = NetworkSingleton<GameManager>.Instance.OrganisationName;
                    if (!string.IsNullOrEmpty(saveName) && saveName != "Game" && saveName != "Organisation")
                    {
                        ModLogger.Debug($"Got organization name from GameManager: {saveName}");
                        return saveName;
                    }
                }
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Could not get save name: {ex.Message}");
                ModLogger.Warn($"Stack trace: {ex.StackTrace}");
            }
            
            // Return null instead of "default" to avoid creating unnecessary folders
            // The directory will be created when a save is actually loaded
            // This is expected during mod initialization before a save is loaded, so don't log as an error
            return null;
        }

        /// <summary>
        /// Gets the save-specific directory path
        /// </summary>
        /// <returns>The full path to the save-specific directory, or base directory if save name not available</returns>
        private static string GetSaveDirectory()
        {
            string saveName = GetSaveName();
            
            // If no save name available yet, return base directory (don't create default folder)
            if (string.IsNullOrEmpty(saveName))
            {
                if (_baseDataDirectory == null)
                {
                    _baseDataDirectory = Path.Combine(MelonEnvironment.UserDataDirectory, "Behind-Bars");
                    if (!Directory.Exists(_baseDataDirectory))
                    {
                        Directory.CreateDirectory(_baseDataDirectory);
                    }
                }
                return _baseDataDirectory;
            }
            
            // If save name changed, update the directory and clear cache
            if (_currentSaveName != saveName || _currentSaveDirectory == null)
            {
                // Clear cache if save changed
                if (_currentSaveName != null && _currentSaveName != saveName)
                {
                    _fileCache.Clear();
                    ModLogger.Info($"Save changed from '{_currentSaveName}' to '{saveName}' - cleared file cache");
                }
                
                _currentSaveName = saveName;
                
                if (_baseDataDirectory == null)
                {
                    _baseDataDirectory = Path.Combine(MelonEnvironment.UserDataDirectory, "Behind-Bars");
                }
                
                // Sanitize save name for use as directory name
                string sanitizedSaveName = SanitizeFileName(saveName);
                // Prefix with "save-" to make it clear which folder is which
                string prefixedSaveName = $"save-{sanitizedSaveName}";
                _currentSaveDirectory = Path.Combine(_baseDataDirectory, prefixedSaveName);
                
                // Ensure directory exists
                if (!Directory.Exists(_currentSaveDirectory))
                {
                    Directory.CreateDirectory(_currentSaveDirectory);
                    ModLogger.Info($"Created save-specific directory: {_currentSaveDirectory}");
                }
            }
            
            return _currentSaveDirectory;
        }

        /// <summary>
        /// Refreshes the save directory (useful when save changes)
        /// </summary>
        public static void RefreshSaveDirectory()
        {
            _currentSaveName = null;
            _currentSaveDirectory = null;
            _fileCache.Clear();
            GetSaveDirectory();
            ModLogger.Info("Refreshed save directory and cleared cache");
        }

        /// <summary>
        /// Sanitizes a string to be safe for use as a file/directory name
        /// </summary>
        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "default";
            
            // Remove invalid characters
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
            {
                fileName = fileName.Replace(c, '_');
            }
            
            // Remove any remaining whitespace and replace with underscore
            fileName = fileName.Trim().Replace(" ", "_");
            
            // Ensure it's not empty after sanitization
            if (string.IsNullOrEmpty(fileName))
                return "default";
            
            return fileName;
        }

        /// <summary>
        /// Initializes the FileUtilities singleton instance and creates the data directory if it doesn't exist.
        /// Sets up the base directory path using MelonEnvironment.UserDataDirectory.
        /// </summary>
        public static void Initialize()
        {
            if (Instance == null)
            {
                Instance = new FileUtilities();
            }

            _baseDataDirectory = Path.Combine(MelonEnvironment.UserDataDirectory, "Behind-Bars");
            if (!Directory.Exists(_baseDataDirectory))
            {
                Directory.CreateDirectory(_baseDataDirectory);
            }
            
            // Initialize save-specific directory
            GetSaveDirectory();
        }

        /// <summary>
        /// Gets the mod's data directory path (save-specific).
        /// Initializes the directory if it hasn't been set up yet.
        /// </summary>
        /// <returns>The full path to the save-specific Behind-Bars data directory.</returns>
        public static string GetDataDirectory()
        {
            if (_baseDataDirectory == null)
            {
                Initialize();
            }
            return GetSaveDirectory();
        }

        /// <summary>
        /// Returns all files currently loaded in the cache.
        /// </summary>
        /// <returns>A dictionary containing all cached file names and their contents.</returns>
        public static Dictionary<string, string> AllLoadedFiles()
        {
            return _fileCache;
        }

        /// <summary>
        /// Adds a new file to the cache or updates an existing one if it already exists.
        /// </summary>
        /// <param name="fileName">The name of the file to add or update.</param>
        /// <param name="fileContents">The contents to store in the file.</param>
        /// <returns>True if the operation succeeded, false otherwise.</returns>
        public static bool AddOrUpdateFile(string fileName, string fileContents)
        {
            if (_fileCache.ContainsKey(fileName))
            {
                return UpdateFileContents(fileName, fileContents);
            }
            else
            {
                return AddNewFile(fileName, fileContents);
            }
        }

        /// <summary>
        /// Adds a new file to the cache and saves it to disk.
        /// </summary>
        /// <param name="fileName">The name of the file to add.</param>
        /// <param name="fileContents">The contents to store in the file.</param>
        /// <returns>True if the file was added successfully, false if it already exists in the cache.</returns>
        private static bool AddNewFile(string fileName, string fileContents)
        {
            if (_fileCache.ContainsKey(fileName))
            {
                return false;
            }
            _fileCache.Add(fileName, fileContents);
            SaveFileToDisk(fileName);
            return true;
        }

        /// <summary>
        /// Updates the contents of an existing file in the cache and saves it to disk.
        /// </summary>
        /// <param name="fileName">The name of the file to update.</param>
        /// <param name="fileContents">The new contents for the file.</param>
        /// <returns>True if the file was updated successfully, false if it doesn't exist in the cache.</returns>
        private static bool UpdateFileContents(string fileName, string fileContents)
        {
            if (!_fileCache.ContainsKey(fileName))
            {
                return false;
            }
            _fileCache[fileName] = fileContents;
            SaveFileToDisk(fileName);
            return true;
        }

        /// <summary>
        /// Retrieves the contents of a file from the cache.
        /// </summary>
        /// <param name="fileName">The name of the file to retrieve.</param>
        /// <returns>The file contents if found in cache, otherwise null.</returns>
        public static string GetFileContents(string fileName)
        {
            if (_fileCache.ContainsKey(fileName))
            {
                return _fileCache[fileName];
            }
            return null;
        }

        /// <summary>
        /// Checks if a file is currently loaded in the cache.
        /// </summary>
        /// <param name="fileName">The name of the file to check.</param>
        /// <returns>True if the file exists in the cache, false otherwise.</returns>
        public static bool IsFileLoaded(string fileName)
        {
            return _fileCache.ContainsKey(fileName);
        }

        /// <summary>
        /// Loads a file from disk into the cache.
        /// </summary>
        /// <param name="fileName">The name of the file to load.</param>
        /// <returns>True if the file was loaded successfully, false if the file doesn't exist or couldn't be read.</returns>
        public static bool LoadFileFromDisk(string fileName)
        {
            string path = Path.Combine(GetDataDirectory(), fileName);

            if (!File.Exists(path))
            {
                return false;
            }

            string fileContents = File.ReadAllText(path);
            if (fileContents == null)
            {
                return false;
            }

            if (_fileCache.ContainsKey(fileName))
            {
                _fileCache[fileName] = fileContents;
            }
            else
            {
                _fileCache.Add(fileName, fileContents);
            }
            return true;
        }

        /// <summary>
        /// Saves a file from the cache to disk.
        /// Creates the file if it doesn't exist in the cache but exists on disk, or writes the cached contents if available.
        /// </summary>
        /// <param name="fileName">The name of the file to save.</param>
        /// <returns>True if the file was saved successfully, false otherwise.</returns>
        public static bool SaveFileToDisk(string fileName)
        {
            if (!_fileCache.ContainsKey(fileName))
            {
                // File not in cache, check if it exists on disk
                string filePath = Path.Combine(GetDataDirectory(), fileName);
                if (File.Exists(filePath))
                {
                    // File exists on disk, load it into cache first
                    if (!LoadFileFromDisk(fileName))
                    {
                        return false;
                    }
                }
                else
                {
                    // File doesn't exist on disk and not in cache - cannot save
                    return false;
                }
            }

            // Save the cached contents to disk
            string path = Path.Combine(GetDataDirectory(), fileName);
            File.WriteAllText(path, _fileCache[fileName]);
            return true;
        }
    }
}
