#if !MONO
using ListString = Il2CppSystem.Collections.Generic.List<string>;
#else
using System.Collections.Generic;
using ListString = System.Collections.Generic.List<string>;
#endif

using System;
using System.IO;
using System.Reflection;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;

#if !MONO
using Il2CppNewtonsoft.Json;
using STJ = System.Text.Json;
#else
using Newtonsoft.Json;
#endif

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
    /// Generic wrapper for mod-owned saveable classes.
    ///
    /// Derived types opt fields into persistence with <see cref="SaveableFieldAttribute"/>
    /// and provide stable folder/file names through the protected name properties.
    /// The wrapper supports the game's file-based save contract and the IL2CPP
    /// dynamic-save patch path. Runtime-only caches and scene references are not
    /// automatically persisted by this class.
    /// </summary>
#if MONO
    public abstract class Saveable : Registerable, ISaveable, S1Persistence.ISaveable
#else
    public abstract class Saveable : Registerable, ISaveable
#endif
    {
        /// <summary>
        /// The stable folder name where this saveable is stored when
        /// <see cref="ShouldSaveUnderFolder"/> is true.
        /// </summary>
        protected abstract string SaveFolderName { get; }

        /// <summary>
        /// The stable base file name for this saveable when the game's save
        /// contract requests a single file. Individual <see cref="SaveableFieldAttribute"/>
        /// files still use their own attribute names.
        /// </summary>
        protected abstract string SaveFileName { get; }

        /// <summary>
        /// Whether this saveable should be saved under <see cref="SaveFolderName"/>
        /// or directly in the parent save directory.
        /// </summary>
        protected virtual bool ShouldSaveUnderFolder => true;

        /// <summary>Internal view of the derived save folder name used by the loader.</summary>
        internal string SaveFolderNameInternal => SaveFolderName;

        /// <summary>Internal view of the derived save file name used by the loader.</summary>
        internal string SaveFileNameInternal => SaveFileName;

        /// <summary>Internal view of the folder-layout choice used by the loader.</summary>
        internal bool ShouldSaveUnderFolderInternal => ShouldSaveUnderFolder;

        /// <summary>
        /// Reserved compatibility list for additional files associated with this
        /// saveable. The current <see cref="Saveable"/> implementation does not
        /// automatically copy or write entries in this list.
        /// </summary>
        public List<string> LocalExtraFiles { get; set; } = new List<string>();

        /// <summary>
        /// Reserved compatibility list for additional folders associated with this
        /// saveable. The current <see cref="Saveable"/> implementation does not
        /// automatically create or write entries in this list.
        /// </summary>
        public List<string> LocalExtraFolders { get; set; } = new List<string>();

        /// <summary>
        /// Flag indicating whether this saveable has changed and should be
        /// considered during the next save cycle.
        ///
        /// Mono requests a save through the game's manager when marked; IL2CPP
        /// relies on the mod's save patches to inspect this flag.
        /// </summary>
        public bool HasChanged { get; set; }

        /// <summary>
        /// The loader used by the game's persistence pipeline to deserialize this
        /// saveable from its save path.
        /// </summary>
        public S1Loaders.Loader Loader { get; private set; }

        /// <summary>
        /// Game's ISaveable implementation - returns the folder name.
        /// </summary>
#if MONO
        string S1Persistence.ISaveable.SaveFolderName => SaveFolderName;

        /// <summary>
        /// Game's ISaveable implementation - returns the file name.
        /// </summary>
        string S1Persistence.ISaveable.SaveFileName => SaveFileName;

        /// <summary>
        /// Game's ISaveable implementation - returns whether to save under folder.
        /// </summary>
        bool S1Persistence.ISaveable.ShouldSaveUnderFolder => ShouldSaveUnderFolder;

        /// <summary>
        /// Game's ISaveable implementation - returns the loader.
        /// </summary>
        S1Loaders.Loader S1Persistence.ISaveable.Loader => Loader;
#endif

        /// <summary>
        /// Creates the loader associated with this saveable.
        /// </summary>
        /// <remarks>
        /// The loader is created before registration. Derived constructors should
        /// initialize their field defaults without assuming that a game save has
        /// already been loaded.
        /// </remarks>
        protected Saveable()
        {
            // Create loader for this saveable
            Loader = new SaveableLoader(this);
        }

        /// <summary>
        /// Initializes this saveable for the game's persistence pipeline.
        /// </summary>
        /// <remarks>
        /// Mono registers directly with <c>SaveManager</c>. IL2CPP does not add
        /// the instance to the game's manager here; the mod's Harmony save path
        /// discovers saveables separately. If the manager is unavailable,
        /// initialization logs a warning and does not throw; callers may need to
        /// retry when the manager is ready.
        /// </remarks>
        public void InitializeSaveable()
        {
            try
            {
                // Register with SaveManager
                if (Singleton<S1Persistence.SaveManager>.Instance != null)
                {
#if MONO
                    Singleton<S1Persistence.SaveManager>.Instance.RegisterSaveable(this);
                    ModLogger.Debug($"[SAVEABLE] Registered {GetType().Name} with SaveManager");
#else
                    ModLogger.Debug($"[SAVEABLE] SaveManager available for {GetType().Name} (registration handled by IL2CPP save patches)");
#endif
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
        /// Gets the JSON representation of fields marked with
        /// <see cref="SaveableFieldAttribute"/> on this saveable.
        /// </summary>
        /// <returns>
        /// An indented JSON object, or <c>{}</c> when serialization fails after
        /// logging the error.
        /// </returns>
        /// <remarks>
        /// <see cref="OnSaved"/> is invoked before serialization on this API.
        /// This representation is an in-memory compatibility helper; the normal
        /// game save path writes individual field files or dynamic-save entries.
        /// </remarks>
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
        /// Marks this saveable as changed for the next save cycle.
        /// </summary>
        /// <remarks>
        /// Mono also requests the game's save manager to save immediately through
        /// its normal API. IL2CPP only changes the flag; the Harmony save patches
        /// decide when and how to write it. A successful return does not prove
        /// that data reached disk.
        /// </remarks>
        public void MarkChanged()
        {
            HasChanged = true;

#if MONO
            // Request a delayed save from SaveManager
            try
            {
                if (Singleton<S1Persistence.SaveManager>.Instance != null)
                {
                    Singleton<S1Persistence.SaveManager>.Instance.Save();
                    ModLogger.Debug($"[SAVEABLE] Requested delayed save for {GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[SAVEABLE] Error requesting delayed save for {GetType().Name}: {ex.Message}");
            }
#else
            // Save is handled by Harmony patches on SaveManager.Save;
            // MarkChanged sets HasChanged flag which patches check during save cycle
            ModLogger.Debug($"[SAVEABLE] MarkChanged for {GetType().Name}");
#endif
        }

        /// <summary>
        /// Marks this saveable as unchanged so the save patches can skip it when
        /// no other save path requires a write.
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
        /// INTERNAL: Loads all fields marked with the <see cref="SaveableFieldAttribute"/>
        /// attribute from JSON files in the specified folder.
        /// </summary>
        /// <remarks>
        /// Reflection traverses the concrete type and every base type through
        /// <see cref="Saveable"/> until <see cref="object"/>; only fields carrying
        /// the attribute participate. Missing files and fields that fail to
        /// deserialize or assign are isolated per field; an assignment exception
        /// leaves the existing value in place, while converter fallbacks may
        /// explicitly assign a type default. After a normal field pass,
        /// <see cref="OnLoaded"/> is invoked even when no marked files were
        /// found; an exception before the method reaches that point can prevent
        /// the hook.
        /// </remarks>
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
#if !MONO
                        var jsonObject = STJ.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
#else
                        var jsonObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
#endif
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
#if !MONO
                        value = STJ.JsonSerializer.Deserialize(json, type);
#else
                        value = JsonConvert.DeserializeObject(json, type, ISaveable.SerializerSettings);
#endif
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
            System.Collections.Generic.List<string> systemList = new System.Collections.Generic.List<string>();
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
        /// INTERNAL: Saves all fields marked with the <see cref="SaveableFieldAttribute"/>
        /// attribute to JSON files in the specified folder.
        /// </summary>
        /// <remarks>
        /// Attribute names become file names and receive a <c>.json</c> suffix
        /// when one is not already present. Null fields delete an existing field
        /// file. Non-null file names are appended to <paramref name="extraSaveables"/>
        /// so the base game's cleanup does not remove them. <see cref="OnSaved"/>
        /// runs after the field loop, including after individual field failures.
        /// A null <paramref name="extraSaveables"/> list or an exception outside
        /// the per-field handling can prevent the loop from reaching that hook.
        /// </remarks>
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
#if !MONO
                            data = STJ.JsonSerializer.Serialize(serializedValue, new STJ.JsonSerializerOptions { WriteIndented = true });
#else
                            data = JsonConvert.SerializeObject(serializedValue, Formatting.Indented, ISaveable.SerializerSettings);
#endif
                            ModLogger.Debug($"[SAVEABLE] Saved field {saveableField.Name} (with SaveableField attributes) to {saveFileName}");
                        }
                        else
                        {
                            // Use standard JSON serialization for types without SaveableField attributes
#if !MONO
                            data = STJ.JsonSerializer.Serialize(value, new STJ.JsonSerializerOptions { WriteIndented = true });
#else
                            data = JsonConvert.SerializeObject(value, Formatting.Indented, ISaveable.SerializerSettings);
#endif
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
        /// Called after the field-based or dynamic-save load pass. Derived classes
        /// can rebuild runtime-only state after serialized values are restored.
        /// The loader also invokes this hook for missing or failed save paths.
        /// </summary>
        protected virtual void OnLoaded() { }

        /// <summary>
        /// INTERNAL: Explicit interface implementation that delegates to the virtual OnSaved method.
        /// Called after all saveable fields have been saved to their respective JSON files.
        /// </summary>
        void ISaveable.OnSaved() => OnSaved();

        /// <summary>
        /// Called by the save paths to finalize derived data around serialization.
        /// <see cref="GetSaveString"/> invokes it before producing JSON, while
        /// the field and dynamic-save writers invoke it after their write loops.
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
        /// <remarks>
        /// This Mono-facing adapter appends <see cref="SaveFolderName"/> when
        /// <see cref="ShouldSaveUnderFolder"/> is true, creates that directory,
        /// and then delegates to <see cref="SaveInternal"/>. A directory-creation
        /// failure is logged and returns an empty list. The returned file names are
        /// the non-null marked fields reported to the base game's cleanup pass;
        /// per-field write failures are logged by the delegated method.
        /// </remarks>
#if MONO
        System.Collections.Generic.List<string> S1Persistence.ISaveable.WriteData(string parentFolderPath)
        {
            System.Collections.Generic.List<string> extraSaveables = new System.Collections.Generic.List<string>();
            
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
#endif

        #endregion

        #region Dynamic Save Data Support

        /// <summary>
        /// INTERNAL: Writes fields marked with <see cref="SaveableFieldAttribute"/>
        /// into a <c>DynamicSaveData</c> record for the base game's consolidated
        /// JSON save format.
        /// </summary>
        /// <remarks>
        /// Null fields are omitted. The attribute's save name is used as the
        /// dynamic key, and <see cref="OnSaved"/> is invoked after the field loop
        /// when a non-null data record is supplied. A null record returns without
        /// invoking the hook.
        /// </remarks>
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
#if !MONO
                    string data;
                    var valueType = value.GetType();
                    if (HasSaveableFields(valueType))
                    {
                        object serializedValue = SaveableSerializer.SerializeValue(value);
                        data = STJ.JsonSerializer.Serialize(serializedValue);
                    }
                    else
                    {
                        data = STJ.JsonSerializer.Serialize(value);
                    }
#else
                    string data = JsonConvert.SerializeObject(value, Formatting.None, ISaveable.SerializerSettings);
#endif
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
        /// INTERNAL: Reads fields marked with <see cref="SaveableFieldAttribute"/>
        /// from a <c>DynamicSaveData</c> record for the base game's consolidated
        /// JSON save format.
        /// </summary>
        /// <remarks>
        /// Missing or empty dynamic keys are skipped. Assignment exceptions leave
        /// the existing value in place, while converter fallbacks may explicitly
        /// assign a type default. <see cref="OnLoaded"/> is invoked after the
        /// field loop when a non-null data record is supplied. A null record
        /// returns without invoking the hook.
        /// </remarks>
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
#if !MONO
                    object? value = STJ.JsonSerializer.Deserialize(json, type);
                    if (HasSaveableFields(type))
                    {
                        var jsonObject = STJ.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                        if (jsonObject != null)
                        {
                            value = SaveableSerializer.DeserializeValue(type, jsonObject);
                        }
                        else
                        {
                            continue;
                        }
                    }
                    else
                    {
                        value = STJ.JsonSerializer.Deserialize(json, type);
                    }
#else
                    object? value = JsonConvert.DeserializeObject(json, type, ISaveable.SerializerSettings);
#endif
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
