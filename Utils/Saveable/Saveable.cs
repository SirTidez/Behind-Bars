using System;
using System.Collections.Generic;
using Behind_Bars.Helpers;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Persistence.Loaders;
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.Persistence;
using ScheduleOne.Persistence.Loaders;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Base class for saveable objects that automatically handles serialization/deserialization.
    /// Similar to S1API's Saveable but implemented independently.
    /// 
    /// Usage:
    /// public class MySaveData : Saveable
    /// {
    ///     [SaveableField("myValue")]
    ///     private int _myValue = 0;
    ///     
    ///     protected override string SaveFolderName => "MyMod";
    ///     protected override string SaveFileName => "MyData";
    ///     
    ///     protected override void OnLoaded()
    ///     {
    ///         // Initialize data after loading
    ///     }
    /// }
    /// </summary>
    public abstract class Saveable : ISaveable
    {
        /// <summary>
        /// The folder name where this saveable is stored (if ShouldSaveUnderFolder is true).
        /// </summary>
        protected abstract string SaveFolderName { get; }

        /// <summary>
        /// The file name for this saveable (without extension).
        /// </summary>
        protected abstract string SaveFileName { get; }

        /// <summary>
        /// Whether this saveable should be saved under a folder or directly in the save directory.
        /// </summary>
        protected virtual bool ShouldSaveUnderFolder => true;

        /// <summary>
        /// Additional files to save alongside this saveable.
        /// </summary>
        public List<string> LocalExtraFiles { get; set; } = new List<string>();

        /// <summary>
        /// Additional folders to save alongside this saveable.
        /// </summary>
        public List<string> LocalExtraFolders { get; set; } = new List<string>();

        /// <summary>
        /// Flag indicating whether this saveable has changed and needs saving.
        /// </summary>
        public bool HasChanged { get; set; }

        /// <summary>
        /// The loader used to deserialize this saveable from JSON.
        /// </summary>
        public Loader Loader { get; private set; }

        /// <summary>
        /// ISaveable implementation - returns the folder name.
        /// </summary>
        string ISaveable.SaveFolderName => SaveFolderName;

        /// <summary>
        /// ISaveable implementation - returns the file name.
        /// </summary>
        string ISaveable.SaveFileName => SaveFileName;

        /// <summary>
        /// ISaveable implementation - returns whether to save under folder.
        /// </summary>
        bool ISaveable.ShouldSaveUnderFolder => ShouldSaveUnderFolder;

        /// <summary>
        /// Constructor - automatically registers this saveable with SaveManager.
        /// </summary>
        protected Saveable()
        {
            // Create loader for this saveable
            Loader = new SaveableLoader(this);
            
            // Auto-register with SaveManager when available
            // Note: SaveManager might not be available immediately, so we'll register in InitializeSaveable
        }

        /// <summary>
        /// Initializes the saveable and registers it with SaveManager.
        /// Called by the game's save system.
        /// </summary>
        public void InitializeSaveable()
        {
            try
            {
                // Register with SaveManager
                if (Singleton<SaveManager>.Instance != null)
                {
                    Singleton<SaveManager>.Instance.RegisterSaveable(this);
                    ModLogger.Debug($"[SAVEABLE] Registered {GetType().Name} with SaveManager");
                }
                else
                {
                    ModLogger.Warn($"[SAVEABLE] SaveManager not available yet - {GetType().Name} will register later");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE] Error initializing {GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the JSON string representation of this saveable.
        /// Uses reflection to find fields marked with [SaveableField] and serializes them.
        /// </summary>
        public string GetSaveString()
        {
            try
            {
                OnSaved(); // Call OnSaved before serialization
                return SaveableSerializer.Serialize(this);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE] Error getting save string for {GetType().Name}: {ex.Message}");
                ModLogger.Error($"[SAVEABLE] Stack trace: {ex.StackTrace}");
                return "{}";
            }
        }

        /// <summary>
        /// Internal method to deserialize JSON data into this saveable.
        /// Called by SaveableLoader.
        /// </summary>
        internal void DeserializeFromJson(string json)
        {
            try
            {
                SaveableSerializer.Deserialize(this, json);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE] Error deserializing {GetType().Name}: {ex.Message}");
                ModLogger.Error($"[SAVEABLE] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Called after data is loaded from JSON.
        /// Override this method to initialize transient data or validate loaded data.
        /// </summary>
        protected virtual void OnLoaded()
        {
            // Override in derived classes
        }

        /// <summary>
        /// Internal method to call OnLoaded from SaveableLoader.
        /// </summary>
        internal void InternalOnLoaded()
        {
            OnLoaded();
        }

        /// <summary>
        /// Called before data is saved to JSON.
        /// Override this method to clean up data or prepare for serialization.
        /// </summary>
        protected virtual void OnSaved()
        {
            // Override in derived classes
        }

        /// <summary>
        /// Marks this saveable as changed, triggering a save on the next save cycle.
        /// </summary>
        public void MarkChanged()
        {
            HasChanged = true;
        }

        /// <summary>
        /// Marks this saveable as unchanged, preventing unnecessary saves.
        /// </summary>
        public void MarkUnchanged()
        {
            HasChanged = false;
        }
    }
}

