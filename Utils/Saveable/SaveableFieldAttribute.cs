using System;

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Marks a field to be saved alongside the class instance.
    /// This attribute is intended to work across all custom game elements.
    /// Compatible with S1API's SaveableField pattern.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class SaveableFieldAttribute : Attribute
    {
        /// <summary>
        /// What the save data should be named.
        /// </summary>
        internal string SaveName { get; }

        /// <summary>
        /// Base constructor for initializing a SaveableField.
        /// </summary>
        /// <param name="saveName">The name to use for the save file.</param>
        public SaveableFieldAttribute(string saveName)
        {
            SaveName = saveName;
        }
    }
}


