#if !MONO
using Il2CppSystem.Collections.Generic;
using ListString = Il2CppSystem.Collections.Generic.List<string>;
#else
using System.Collections.Generic;
using ListString = System.Collections.Generic.List<string>;
#endif

using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
#if !MONO
using S1Datas = Il2CppScheduleOne.Persistence.Datas;
using S1Persistence = Il2CppScheduleOne.Persistence;
using S1Loaders = Il2CppScheduleOne.Persistence.Loaders;
using Il2CppScheduleOne.DevUtilities;
#else
using S1Datas = ScheduleOne.Persistence.Datas;
using S1Persistence = ScheduleOne.Persistence;
using S1Loaders = ScheduleOne.Persistence.Loaders;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Generic wrapper for saveable classes.
    /// Inherits from Registerable and implements both the internal ISaveable interface
    /// and the game's ScheduleOne.Persistence.ISaveable interface.
    /// </summary>
    public abstract class Saveable : Registerable, ISaveable, ScheduleOne.Persistence.ISaveable
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
        public S1Loaders.Loader Loader { get; private set; }

        /// <summary>
        /// Game's ISaveable implementation - returns the folder name.
        /// </summary>
        string ScheduleOne.Persistence.ISaveable.SaveFolderName => SaveFolderName;

        /// <summary>
        /// Game's ISaveable implementation - returns the file name.
        /// </summary>
        string ScheduleOne.Persistence.ISaveable.SaveFileName => SaveFileName;

        /// <summary>
        /// Game's ISaveable implementation - returns whether to save under folder.
        /// </summary>
        bool ScheduleOne.Persistence.ISaveable.ShouldSaveUnderFolder => ShouldSaveUnderFolder;

        /// <summary>
        /// Game's ISaveable implementation - returns the loader.
        /// </summary>
        S1Loaders.Loader ScheduleOne.Persistence.ISaveable.Loader => Loader;

        /// <summary>
        /// Constructor - automatically creates loader for this saveable.
        /// </summary>
        protected Saveable()
        {
            // Create loader for this saveable
            Loader = new SaveableLoader(this);
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
                if (Singleton<S1Persistence.SaveManager>.Instance != null)
                {
                    Singleton<S1Persistence.SaveManager>.Instance.RegisterSaveable(this);
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
        /// Marks this saveable as changed, triggering a save on the next save cycle.
        /// Also requests a delayed save from SaveManager.
        /// </summary>
        public void MarkChanged()
        {
            HasChanged = true;
            
            // Request a delayed save from SaveManager
            try
            {
                if (Singleton<S1Persistence.SaveManager>.Instance != null)
                {
                    Singleton<S1Persistence.SaveManager>.Instance.DelayedSave();
                    ModLogger.Debug($"[SAVEABLE] Requested delayed save for {GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[SAVEABLE] Error requesting delayed save for {GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Marks this saveable as unchanged, preventing unnecessary saves.
        /// </summary>
        public void MarkUnchanged()
        {
            HasChanged = false;
        }

        #region Internal ISaveable Implementation

        /// <summary>
        /// INTERNAL: Explicit interface implementation that delegates to the internal LoadInternal method.
        /// Loads all fields marked with the <see cref="SaveableFieldAttribute"/> attribute from JSON files in the specified folder.
        /// </summary>
        /// <param name="folderPath">The folder path containing the save files to load.</param>
        void ISaveable.LoadInternal(string folderPath) =>
            LoadInternal(folderPath);

        /// <summary>
        /// INTERNAL: Loads all fields marked with the <see cref="SaveableFieldAttribute"/> attribute from JSON files in the specified folder.
        /// This method uses reflection to find fields with the SaveableField attribute and deserializes their values from JSON files.
        /// After loading all fields, it calls the <see cref="OnLoaded"/> method to allow derived classes to perform additional initialization.
        /// </summary>
        /// <param name="folderPath">The folder path containing the save files to load from.</param>
        internal virtual void LoadInternal(string folderPath)
        {
            FieldInfo[] saveableFields = ReflectionUtils.GetAllFields(GetType(), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo saveableField in saveableFields)
            {
                SaveableFieldAttribute? saveableFieldAttribute = saveableField.GetCustomAttribute<SaveableFieldAttribute>();
                if (saveableFieldAttribute == null)
                    continue;

                string filename = saveableFieldAttribute.SaveName.EndsWith(".json")
                    ? saveableFieldAttribute.SaveName
                    : $"{saveableFieldAttribute.SaveName}.json";

                string saveDataPath = Path.Combine(folderPath, filename);
                if (!File.Exists(saveDataPath))
                    continue;

                try
                {
                    string json = File.ReadAllText(saveDataPath);
                    Type type = saveableField.FieldType;
                    object? value;
                    
                    // Check if this type has SaveableField attributes (like ParoleRecord)
                    // If so, use SaveableSerializer.DeserializeValue which handles SaveableField attributes
                    if (HasSaveableFields(type))
                    {
                        // Parse JSON to dictionary first, then deserialize using SaveableSerializer
                        var jsonObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                        if (jsonObject != null)
                        {
                            value = SaveableSerializer.DeserializeValue(type, jsonObject);
                            ModLogger.Debug($"[SAVEABLE] Loaded field {saveableField.Name} (with SaveableField attributes) from {filename}");
                        }
                        else
                        {
                            ModLogger.Warn($"[SAVEABLE] Failed to parse JSON for field {saveableField.Name} from {filename}");
                            continue;
                        }
                    }
                    else
                    {
                        // Use standard JSON deserialization for types without SaveableField attributes
                        value = JsonConvert.DeserializeObject(json, type, ISaveable.SerializerSettings);
                        ModLogger.Debug($"[SAVEABLE] Loaded field {saveableField.Name} from {filename}");
                    }
                    
                    saveableField.SetValue(this, value);
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[SAVEABLE] Error loading field {saveableField.Name} from {filename}: {ex.Message}");
                    ModLogger.Warn($"[SAVEABLE] Stack trace: {ex.StackTrace}");
                }
            }

            OnLoaded();
        }

        /// <summary>
        /// INTERNAL: Explicit interface implementation that delegates to the internal SaveInternal method.
        /// Saves all fields marked with the <see cref="SaveableFieldAttribute"/> attribute to JSON files in the specified folder.
        /// </summary>
        /// <param name="folderPath">The folder path where save files should be written.</param>
        /// <param name="extraSaveables">Reference to a list of extra saveable files that should not be deleted during cleanup.</param>
        void ISaveable.SaveInternal(string folderPath, ref ListString extraSaveables)
        {
            // Convert to System.Collections.Generic.List<string> for internal processing
            List<string> systemList = new List<string>();
            SaveInternal(folderPath, ref systemList);
            
            // Convert back to ListString
            extraSaveables.Clear();
            foreach (string item in systemList)
            {
                extraSaveables.Add(item);
            }
        }

        /// <summary>
        /// Checks if a type has fields marked with SaveableField attributes.
        /// </summary>
        private static bool HasSaveableFields(Type type)
        {
            if (type == null)
                return false;
                
            var fields = ReflectionUtils.GetAllFields(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                if (field.GetCustomAttribute<SaveableFieldAttribute>() != null && !field.IsNotSerialized)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// INTERNAL: Saves all fields marked with the <see cref="SaveableFieldAttribute"/> attribute to JSON files in the specified folder.
        /// This method uses reflection to find fields with the SaveableField attribute and serializes their values to JSON files.
        /// Null fields result in their corresponding save files being deleted. Non-null fields are added to the extraSaveables list
        /// to prevent the base game from deleting them during cleanup. After saving all fields, it calls the <see cref="OnSaved"/> method
        /// to allow derived classes to perform additional finalization.
        /// </summary>
        /// <param name="folderPath">The folder path where save files should be written.</param>
        /// <param name="extraSaveables">Reference to a list of extra saveable files that should not be deleted during cleanup.</param>
        internal virtual void SaveInternal(string folderPath, ref List<string> extraSaveables)
        {
            FieldInfo[] saveableFields = ReflectionUtils.GetAllFields(GetType(), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo saveableField in saveableFields)
            {
                SaveableFieldAttribute? saveableFieldAttribute = saveableField.GetCustomAttribute<SaveableFieldAttribute>();
                if (saveableFieldAttribute == null)
                    continue;

                string saveFileName = saveableFieldAttribute.SaveName.EndsWith(".json")
                    ? saveableFieldAttribute.SaveName
                    : $"{saveableFieldAttribute.SaveName}.json";

                string saveDataPath = Path.Combine(folderPath, saveFileName);

                object? value = saveableField.GetValue(this);
                if (value == null)
                {
                    // Remove the save if the field is null
                    if (File.Exists(saveDataPath))
                    {
                        try
                        {
                            File.Delete(saveDataPath);
                            ModLogger.Debug($"[SAVEABLE] Deleted null field save file: {saveFileName}");
                        }
                        catch (Exception ex)
                        {
                            ModLogger.Warn($"[SAVEABLE] Error deleting null field save file {saveFileName}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    // We add this to the extra saveables to prevent the game from deleting it
                    // Otherwise, it'll delete it after it finishes saving and does clean up
                    extraSaveables.Add(saveFileName);

                    // Write our data
                    try
                    {
                        string data;
                        
                        // Check if this value has SaveableField attributes (like ParoleRecord)
                        // If so, use SaveableSerializer logic to properly serialize private fields
                        Type valueType = value.GetType();
                        if (HasSaveableFields(valueType))
                        {
                            // Use SaveableSerializer.SerializeValue which handles SaveableField attributes
                            object serializedValue = SaveableSerializer.SerializeValue(value);
                            data = JsonConvert.SerializeObject(serializedValue, Formatting.Indented, ISaveable.SerializerSettings);
                            ModLogger.Debug($"[SAVEABLE] Saved field {saveableField.Name} (with SaveableField attributes) to {saveFileName}");
                        }
                        else
                        {
                            // Use standard JSON serialization for types without SaveableField attributes
                            data = JsonConvert.SerializeObject(value, Formatting.Indented, ISaveable.SerializerSettings);
                            ModLogger.Debug($"[SAVEABLE] Saved field {saveableField.Name} to {saveFileName}");
                        }
                        
                        File.WriteAllText(saveDataPath, data);
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"[SAVEABLE] Error saving field {saveableField.Name} to {saveFileName}: {ex.Message}");
                        ModLogger.Error($"[SAVEABLE] Stack trace: {ex.StackTrace}");
                    }
                }
            }

            OnSaved();
        }

        /// <summary>
        /// INTERNAL: Explicit interface implementation that delegates to the virtual OnLoaded method.
        /// Called after all saveable fields have been loaded from their respective JSON files.
        /// </summary>
        void ISaveable.OnLoaded() => OnLoaded();

        /// <summary>
        /// Called after all saveable fields have been loaded from their respective JSON files.
        /// This method can be overridden in derived classes to perform additional initialization
        /// or processing after the save data has been restored.
        /// </summary>
        protected virtual void OnLoaded() { }

        /// <summary>
        /// INTERNAL: Explicit interface implementation that delegates to the virtual OnSaved method.
        /// Called after all saveable fields have been saved to their respective JSON files.
        /// </summary>
        void ISaveable.OnSaved() => OnSaved();

        /// <summary>
        /// Called after all saveable fields have been saved to their respective JSON files.
        /// This method can be overridden in derived classes to perform additional finalization
        /// or processing after the save data has been written to disk.
        /// </summary>
        protected virtual void OnSaved() { }

        #endregion

        #region Game's ISaveable WriteData Implementation

        /// <summary>
        /// INTERNAL: Explicit interface implementation that delegates to the internal SaveInternal method.
        /// Saves all fields marked with the <see cref="SaveableFieldAttribute"/> attribute to JSON files in the specified folder.
        /// </summary>
        /// <param name="parentFolderPath">The folder path where save files should be written.</param>
        /// <returns>List of extra saveable files that should not be deleted during cleanup.</returns>
        List<string> ScheduleOne.Persistence.ISaveable.WriteData(string parentFolderPath)
        {
            List<string> extraSaveables = new List<string>();
            
            // Get the folder path for this saveable
            string folderPath = parentFolderPath;
            if (ShouldSaveUnderFolder)
            {
                folderPath = Path.Combine(parentFolderPath, SaveFolderName);
                if (!Directory.Exists(folderPath))
                {
                    try
                    {
                        Directory.CreateDirectory(folderPath);
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"[SAVEABLE] Error creating save folder {folderPath}: {ex.Message}");
                        return extraSaveables;
                    }
                }
            }

            // Call SaveInternal to save all fields
            SaveInternal(folderPath, ref extraSaveables);

            return extraSaveables;
        }

        #endregion

        #region Dynamic Save Data Support

        /// <summary>
        /// INTERNAL: Writes fields marked with <see cref="SaveableFieldAttribute"/> into a DynamicSaveData blob
        /// to support the base game's consolidated JSON save format.
        /// </summary>
        /// <param name="dynamicSaveData">The dynamic save data record to write into.</param>
        internal void SaveToDynamic(S1Datas.DynamicSaveData dynamicSaveData)
        {
            if (dynamicSaveData == null)
                return;

            FieldInfo[] saveableFields = ReflectionUtils.GetAllFields(GetType(), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo saveableField in saveableFields)
            {
                SaveableFieldAttribute? saveableFieldAttribute = saveableField.GetCustomAttribute<SaveableFieldAttribute>();
                if (saveableFieldAttribute == null)
                    continue;

                object? value = saveableField.GetValue(this);
                if (value == null)
                    continue; // Do not write nulls

                try
                {
                    string data = JsonConvert.SerializeObject(value, Formatting.None, ISaveable.SerializerSettings);
                    // Use the declared save name as the dynamic key
                    dynamicSaveData.AddData(saveableFieldAttribute.SaveName, data);
                    ModLogger.Debug($"[SAVEABLE] Saved field {saveableField.Name} to DynamicSaveData with key {saveableFieldAttribute.SaveName}");
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[SAVEABLE] Error saving field {saveableField.Name} to DynamicSaveData: {ex.Message}");
                }
            }

            OnSaved();
        }

        /// <summary>
        /// INTERNAL: Reads fields marked with <see cref="SaveableFieldAttribute"/> from a DynamicSaveData blob
        /// to support the base game's consolidated JSON save format.
        /// </summary>
        /// <param name="dynamicSaveData">The dynamic save data record to read from.</param>
        internal void LoadFromDynamic(S1Datas.DynamicSaveData dynamicSaveData)
        {
            if (dynamicSaveData == null)
                return;

            FieldInfo[] saveableFields = ReflectionUtils.GetAllFields(GetType(), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo saveableField in saveableFields)
            {
                SaveableFieldAttribute? saveableFieldAttribute = saveableField.GetCustomAttribute<SaveableFieldAttribute>();
                if (saveableFieldAttribute == null)
                    continue;

                // Read the raw json for this save name and deserialize to the field type
                if (!dynamicSaveData.TryGetData(saveableFieldAttribute.SaveName, out string json) || string.IsNullOrEmpty(json))
                    continue;

                try
                {
                    Type type = saveableField.FieldType;
                    object? value = JsonConvert.DeserializeObject(json, type, ISaveable.SerializerSettings);
                    saveableField.SetValue(this, value);
                    ModLogger.Debug($"[SAVEABLE] Loaded field {saveableField.Name} from DynamicSaveData with key {saveableFieldAttribute.SaveName}");
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[SAVEABLE] Error loading field {saveableField.Name} from DynamicSaveData: {ex.Message}");
                }
            }

            OnLoaded();
        }

        #endregion
    }
}
