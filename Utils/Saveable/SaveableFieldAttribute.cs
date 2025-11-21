using System;

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Attribute to mark fields that should be persisted in the save system.
    /// Fields marked with this attribute will be automatically serialized/deserialized.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class SaveableFieldAttribute : Attribute
    {
        /// <summary>
        /// The key name used in JSON serialization.
        /// If not provided, the field name will be used.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Constructor for SaveableField attribute.
        /// </summary>
        /// <param name="key">The JSON key name for this field. If null or empty, the field name is used.</param>
        public SaveableFieldAttribute(string key = null)
        {
            Key = key;
        }
    }
}

