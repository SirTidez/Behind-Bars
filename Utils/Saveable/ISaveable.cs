#if !MONO
using Il2CppSystem.Collections.Generic;
using ListString = Il2CppSystem.Collections.Generic.List<string>;
#else
using System.Collections.Generic;
using ListString = System.Collections.Generic.List<string>;
#endif

using Newtonsoft.Json;

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// INTERNAL: Provides rigidity for saveable instance wrappers.
    /// </summary>
    internal interface ISaveable : IRegisterable
    {
        /// <summary>
        /// INTERNAL: Called when saving the instance.
        /// </summary>
        /// <param name="path">Path to save to.</param>
        /// <param name="extraSaveables">Manipulation of the base game saveable lists.</param>
        void SaveInternal(string path, ref ListString extraSaveables);
        
        /// <summary>
        /// INTERNAL: Called when loading the instance.
        /// </summary>
        /// <param name="folderPath"></param>
        void LoadInternal(string folderPath);
        
        /// <summary>
        /// Called when saving the instance.
        /// </summary>
        void OnSaved();
        
        /// <summary>
        /// Called when loading the instance.
        /// </summary>
        void OnLoaded();

        /// <summary>
        /// INTERNAL: Standard serialization settings to apply for all saveables.
        /// </summary>
        internal static JsonSerializerSettings SerializerSettings =>
            new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Converters = new System.Collections.Generic.List<JsonConverter>() { new GUIDReferenceConverter() }
            };
    }
}
