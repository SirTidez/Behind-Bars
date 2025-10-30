using MelonLoader.Utils;
using System;
using System.Collections.Generic;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Provides utility methods for file operations with caching capabilities.
    /// Manages file reading, writing, and caching for the Behind Bars mod data directory.
    /// </summary>
    public class FileUtilities
    {
        private static string _dataDirectory;
        public static FileUtilities Instance;

        private static Dictionary<string, string> _fileCache = new Dictionary<string, string>();

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

            _dataDirectory = Path.Combine(MelonEnvironment.UserDataDirectory, "Behind-Bars");
            if (!Directory.Exists(_dataDirectory))
            {
                Directory.CreateDirectory(_dataDirectory);
            }
        }

        /// <summary>
        /// Gets the mod's data directory path.
        /// Initializes the directory if it hasn't been set up yet.
        /// </summary>
        /// <returns>The full path to the Behind-Bars data directory.</returns>
        public static string GetDataDirectory()
        {
            if (_dataDirectory == null)
            {
                Initialize();
            }
            return _dataDirectory;
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
