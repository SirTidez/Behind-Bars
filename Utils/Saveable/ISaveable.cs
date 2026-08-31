#if !MONO
using ListString = Il2CppSystem.Collections.Generic.List<string>;
using Il2CppNewtonsoft.Json;
#else
using System.Collections.Generic;
using ListString = System.Collections.Generic.List<string>;
using Newtonsoft.Json;
#endif

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// INTERNAL: Contract shared by the mod's saveable wrappers and the game's
    /// persistence pipeline.
    ///
    /// Saveables may be persisted as individual JSON files or, through the
    /// IL2CPP save patches, as entries in the game's consolidated dynamic-save
    /// data. Implementations must keep the attribute names and lifecycle hook
    /// order stable across both formats.
    /// </summary>
    internal interface ISaveable : IRegisterable
    {
        /// <summary>
        /// INTERNAL: Writes the instance's marked fields into the supplied save
        /// folder and reports files that the base game must preserve during its
        /// cleanup pass.
        /// </summary>
        /// <param name="path">Folder path in which the instance's field files are written.</param>
        /// <param name="extraSaveables">
        /// Mutable base-game file list. Implementations append their non-null
        /// field file names so the game's cleanup does not delete them.
        /// </param>
        void SaveInternal(string path, ref ListString extraSaveables);
        
        /// <summary>
        /// INTERNAL: Restores marked fields from JSON files in the supplied
        /// folder. Missing field files are treated as absent data rather than
        /// clearing the current field value. Conversion/assignment failures are
        /// isolated per field, subject to the converter's documented defaults.
        /// </summary>
        /// <param name="folderPath">Folder containing the instance's field files.</param>
        void LoadInternal(string folderPath);
        
        /// <summary>
        /// Called by a save path to let the instance finalize derived data around
        /// serialization. The in-memory <see cref="Saveable.GetSaveString"/>
        /// path invokes it before serialization; file and dynamic-save writers
        /// invoke it after their field-write loops.
        /// </summary>
        void OnSaved();
        
        /// <summary>
        /// Called after loading has completed, including the no-file and load
        /// failure paths used by <see cref="SaveableLoader"/>.
        /// </summary>
        void OnLoaded();

        /// <summary>
        /// INTERNAL: Creates the standard Newtonsoft settings used by the Mono
        /// file-based save path.
        /// </summary>
        /// <remarks>
        /// A new settings object is created for each access. Mono attaches the
        /// GUID reference converter; the IL2CPP branch leaves converters null
        /// because IL2CPP save serialization uses the separate
        /// <c>System.Text.Json</c>-based path.
        /// </remarks>
        internal static JsonSerializerSettings SerializerSettings =>
            new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
#if MONO
                Converters = new System.Collections.Generic.List<JsonConverter>() { new GUIDReferenceConverter() }
#else
                Converters = null
#endif
            };
    }
}
