using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Behind_Bars.Helpers;
using UnityEngine;

#if !MONO
using Il2CppNewtonsoft.Json;
using STJ = System.Text.Json;
#else
using Newtonsoft.Json;
#endif

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Utility class for serializing and deserializing <see cref="Saveable"/>
    /// objects using the mod's attribute-based persistence contract.
    /// Only fields marked with <see cref="SaveableFieldAttribute"/> participate;
    /// this is a deliberately small bridge for save data, not a general-purpose
    /// replacement for either runtime's JSON serializer.
    /// </summary>
    public static class SaveableSerializer
    {
        /// <summary>
        /// Serializes a saveable to an indented JSON object containing its marked fields.
        /// </summary>
        /// <param name="saveable">Instance whose marked fields should be read.</param>
        /// <returns>
        /// JSON containing the fields that could be read, or <c>{}</c> when the
        /// instance is null or an outer serialization failure occurs.
        /// </returns>
        /// <remarks>
        /// Reflection walks the concrete type and intermediate base types, stopping
        /// before <see cref="Saveable"/>. A non-empty
        /// <see cref="SaveableFieldAttribute.SaveName"/> is the JSON key; otherwise
        /// the CLR field name is used. Non-serialized fields and fields that throw
        /// during access are skipped. Non-string <see cref="IEnumerable"/> values
        /// are emitted as arrays, which means dictionary keys are not preserved by
        /// this outer collection path. The final JSON call uses Newtonsoft.Json on
        /// Mono and System.Text.Json on IL2CPP.
        /// </remarks>
        public static string Serialize(Saveable saveable)
        {
            if (saveable == null)
            {
                ModLogger.Error("[SAVEABLE SERIALIZER] Cannot serialize null saveable");
                return "{}";
            }

            try
            {
                var saveData = new Dictionary<string, object>();
                var type = saveable.GetType();

                // Find all fields marked with [SaveableField]
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                
                // Also get fields from base classes
                var allFields = new List<FieldInfo>();
                var currentType = type;
                while (currentType != null && currentType != typeof(object) && currentType != typeof(Saveable))
                {
                    var typeFields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    allFields.AddRange(typeFields);
                    currentType = currentType.BaseType;
                }

                foreach (var field in allFields)
                {
                    // Check if field has [SaveableField] attribute
                    var attr = field.GetCustomAttribute<SaveableFieldAttribute>();
                    if (attr == null)
                        continue;

                    // Skip non-serializable fields
                    if (field.IsNotSerialized)
                        continue;

                    try
                    {
                        var value = field.GetValue(saveable);
                        var key = !string.IsNullOrEmpty(attr.SaveName) ? attr.SaveName : field.Name;

                        // Handle null values
                        if (value == null)
                        {
                            saveData[key] = null;
                            continue;
                        }

                        // Handle collections and dictionaries
                        if (value is IEnumerable && !(value is string))
                        {
                            var list = new List<object>();
                            foreach (var item in (IEnumerable)value)
                            {
                                list.Add(SerializeValue(item));
                            }
                            saveData[key] = list;
                        }
                        else
                        {
                            saveData[key] = SerializeValue(value);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Warn($"[SAVEABLE SERIALIZER] Error serializing field {field.Name}: {ex.Message}");
                    }
                }

                // Convert dictionary to JSON
#if !MONO
                return STJ.JsonSerializer.Serialize(saveData, new STJ.JsonSerializerOptions { WriteIndented = true });
#else
                return JsonConvert.SerializeObject(saveData, Formatting.Indented);
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE SERIALIZER] Error serializing {saveable.GetType().Name}: {ex.Message}");
                ModLogger.Error($"[SAVEABLE SERIALIZER] Stack trace: {ex.StackTrace}");
                return "{}";
            }
        }

        /// <summary>
        /// Deserializes a JSON object into the marked fields of a saveable.
        /// </summary>
        /// <param name="saveable">Instance whose marked fields should be assigned.</param>
        /// <param name="json">JSON object produced by this serializer or a compatible caller.</param>
        /// <remarks>
        /// The same concrete/intermediate-type reflection walk is used as in
        /// <see cref="Serialize"/>. Unknown keys are ignored, while duplicate
        /// attribute names resolve to the last field inserted into the field map.
        /// Empty JSON is treated as no data. Parse and assignment failures are
        /// logged without aborting the complete load; an assignment failure leaves
        /// that field unchanged, while <see cref="DeserializeValue"/> may return
        /// an explicit default for an invalid enum/vector/date. This method does
        /// not throw those errors to its caller. Newtonsoft.Json is used on Mono
        /// and System.Text.Json is flattened into plain CLR values on IL2CPP
        /// before conversion.
        /// </remarks>
        public static void Deserialize(Saveable saveable, string json)
        {
            if (saveable == null)
            {
                ModLogger.Error("[SAVEABLE SERIALIZER] Cannot deserialize to null saveable");
                return;
            }

            if (string.IsNullOrEmpty(json) || json == "{}")
            {
                ModLogger.Debug("[SAVEABLE SERIALIZER] JSON is empty - skipping deserialization");
                return;
            }

            try
            {
                // Parse JSON to dictionary
#if !MONO
                var rawData = STJ.JsonSerializer.Deserialize<Dictionary<string, STJ.JsonElement>>(json);
                Dictionary<string, object> saveData = null;
                if (rawData != null)
                {
                    saveData = new Dictionary<string, object>(rawData.Count);
                    foreach (var kvp in rawData)
                    {
                        saveData[kvp.Key] = ConvertJsonElementToPlainObject(kvp.Value);
                    }
                }
#else
                var saveData = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
#endif
                if (saveData == null)
                {
                    ModLogger.Warn("[SAVEABLE SERIALIZER] Failed to parse JSON to dictionary");
                    return;
                }

                var type = saveable.GetType();

                // Find all fields marked with [SaveableField]
                var allFields = new List<FieldInfo>();
                var currentType = type;
                while (currentType != null && currentType != typeof(object) && currentType != typeof(Saveable))
                {
                    var typeFields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    allFields.AddRange(typeFields);
                    currentType = currentType.BaseType;
                }

                // Create a map of field keys to FieldInfo
                var fieldMap = new Dictionary<string, FieldInfo>();
                foreach (var field in allFields)
                {
                    var attr = field.GetCustomAttribute<SaveableFieldAttribute>();
                    if (attr != null && !field.IsNotSerialized)
                    {
                        var key = !string.IsNullOrEmpty(attr.SaveName) ? attr.SaveName : field.Name;
                        fieldMap[key] = field;
                    }
                }

                // Set field values from dictionary
                foreach (var kvp in saveData)
                {
                    if (!fieldMap.TryGetValue(kvp.Key, out var field))
                    {
                        // Field not found - might be from old save format, skip it
                        continue;
                    }

                    try
                    {
                        var value = DeserializeValue(field.FieldType, kvp.Value);
                        field.SetValue(saveable, value);
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Warn($"[SAVEABLE SERIALIZER] Error deserializing field {field.Name} (key: {kvp.Key}): {ex.Message}");
                    }
                }

                ModLogger.Debug($"[SAVEABLE SERIALIZER] Successfully deserialized {saveable.GetType().Name}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE SERIALIZER] Error deserializing {saveable.GetType().Name}: {ex.Message}");
                ModLogger.Error($"[SAVEABLE SERIALIZER] Stack trace: {ex.StackTrace}");
            }
        }

#if !MONO
        /// <summary>
        /// Converts an IL2CPP <see cref="STJ.JsonElement"/> tree into the plain CLR
        /// dictionaries, lists, primitives, and dates consumed by the recursive
        /// save-value converter.
        /// </summary>
        /// <param name="element">JSON element to flatten.</param>
        /// <returns>A recursively converted CLR value, or null for JSON null/undefined.</returns>
        /// <remarks>
        /// Numeric values are narrowed in the order Int32, Int64, Single, then
        /// Double. JSON strings that System.Text.Json recognizes as dates become
        /// <see cref="DateTime"/> values; other strings remain strings.
        /// </remarks>
        private static object ConvertJsonElementToPlainObject(STJ.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case STJ.JsonValueKind.Object:
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (var property in element.EnumerateObject())
                        {
                            dict[property.Name] = ConvertJsonElementToPlainObject(property.Value);
                        }
                        return dict;
                    }
                case STJ.JsonValueKind.Array:
                    {
                        var list = new List<object>();
                        foreach (var item in element.EnumerateArray())
                        {
                            list.Add(ConvertJsonElementToPlainObject(item));
                        }
                        return list;
                    }
                case STJ.JsonValueKind.String:
                    if (element.TryGetDateTime(out var dateTime))
                        return dateTime;
                    return element.GetString();
                case STJ.JsonValueKind.Number:
                    if (element.TryGetInt32(out var int32Value))
                        return int32Value;
                    if (element.TryGetInt64(out var int64Value))
                        return int64Value;
                    if (element.TryGetSingle(out var floatValue))
                        return floatValue;
                    return element.GetDouble();
                case STJ.JsonValueKind.True:
                    return true;
                case STJ.JsonValueKind.False:
                    return false;
                case STJ.JsonValueKind.Null:
                case STJ.JsonValueKind.Undefined:
                default:
                    return null;
            }
        }
#endif

        /// <summary>
        /// Converts one field value into a JSON-compatible CLR representation.
        /// </summary>
        /// <param name="value">Value to convert; null is preserved as null.</param>
        /// <returns>
        /// A primitive, string, anonymous Unity-value object, list, nested saveable
        /// dictionary, or runtime-serializer-compatible object.
        /// </returns>
        /// <remarks>
        /// Unity vectors and colors are reduced to component objects, enums become
        /// names, and <see cref="DateTime"/> uses round-trip (ISO 8601) text.
        /// Objects with marked fields recurse through their fields; other custom
        /// objects round-trip through the active JSON runtime. If that fallback
        /// fails, the value's <c>ToString()</c> result is used. Non-string
        /// <see cref="IEnumerable"/> values are treated as arrays, including
        /// dictionaries.
        /// </remarks>
        public static object SerializeValue(object value)
        {
            if (value == null)
                return null;

            var valueType = value.GetType();

            // Handle Unity types
            if (valueType == typeof(Vector3))
            {
                var v = (Vector3)value;
                return new { x = v.x, y = v.y, z = v.z };
            }
            if (valueType == typeof(Vector2))
            {
                var v = (Vector2)value;
                return new { x = v.x, y = v.y };
            }
            if (valueType == typeof(Color))
            {
                var c = (Color)value;
                return new { r = c.r, g = c.g, b = c.b, a = c.a };
            }

            // Handle enums
            if (valueType.IsEnum)
            {
                return value.ToString();
            }

            // Handle DateTime
            if (valueType == typeof(DateTime))
            {
                return ((DateTime)value).ToString("O"); // ISO 8601 format
            }

            // Handle primitives and simple types
            if (valueType.IsPrimitive || valueType == typeof(string) || valueType == typeof(decimal))
            {
                return value;
            }

            // For custom types, check if they have [SaveableField] attributes
            // If so, serialize recursively using SaveableSerializer logic
            if (valueType.IsClass && !valueType.IsPrimitive && valueType != typeof(string))
            {
                // Check if this type has [SaveableField] attributes
                var fields = valueType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                bool hasSaveableFields = false;
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<SaveableFieldAttribute>() != null && !field.IsNotSerialized)
                    {
                        hasSaveableFields = true;
                        break;
                    }
                }

                // If it has SaveableField attributes, serialize recursively
                if (hasSaveableFields)
                {
                    try
                    {
                        var nestedSaveData = new Dictionary<string, object>();
                        foreach (var field in fields)
                        {
                            var attr = field.GetCustomAttribute<SaveableFieldAttribute>();
                            if (attr != null && !field.IsNotSerialized)
                            {
                                var fieldValue = field.GetValue(value);
                                var key = !string.IsNullOrEmpty(attr.SaveName) ? attr.SaveName : field.Name;
                                
                                if (fieldValue == null)
                                {
                                    nestedSaveData[key] = null;
                                }
                                else if (fieldValue is IEnumerable && !(fieldValue is string))
                                {
                                    var list = new List<object>();
                                    foreach (var item in (IEnumerable)fieldValue)
                                    {
                                        list.Add(SerializeValue(item));
                                    }
                                    nestedSaveData[key] = list;
                                }
                                else
                                {
                                    nestedSaveData[key] = SerializeValue(fieldValue);
                                }
                            }
                        }
                        return nestedSaveData;
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Warn($"[SAVEABLE SERIALIZER] Error recursively serializing {valueType.Name}: {ex.Message}");
                    }
                }
            }

            // For custom types without SaveableField attributes, serialize as JSON string then parse back
            // This ensures proper serialization of complex objects
            try
            {
#if !MONO
                var jsonString = STJ.JsonSerializer.Serialize(value);
                return STJ.JsonSerializer.Deserialize<object>(jsonString);
#else
                var jsonString = JsonConvert.SerializeObject(value);
                return JsonConvert.DeserializeObject<object>(jsonString);
#endif
            }
            catch
            {
                // Fallback: try to convert to string
                return value.ToString();
            }
        }

        /// <summary>
        /// Converts a plain JSON value into the requested target type.
        /// </summary>
        /// <param name="targetType">CLR type expected by the destination field.</param>
        /// <param name="value">Plain JSON value, or an IL2CPP JsonElement before flattening.</param>
        /// <returns>
        /// A converted value, a type default, the original value for a failed
        /// primitive conversion, or null when no compatible value can be created.
        /// </returns>
        /// <remarks>
        /// The supported special cases are Unity vectors/colors, enums, dates,
        /// <see cref="List{T}"/>, <see cref="Dictionary{TKey,TValue}"/>, arrays,
        /// primitives, and classes whose fields carry
        /// <see cref="SaveableFieldAttribute"/>. Recursive class creation requires
        /// a usable parameterless constructor. Other types fall back to the active
        /// runtime JSON serializer. Conversion failures are intentionally reduced
        /// to defaults/null or a logged warning so one bad field does not abort the
        /// complete save load.
        /// </remarks>
        public static object DeserializeValue(Type targetType, object value)
        {
            if (value == null)
            {
                // Return default value for the type
                if (targetType.IsValueType)
                    return Activator.CreateInstance(targetType);
                return null;
            }

#if !MONO
            if (value is STJ.JsonElement element)
            {
                value = ConvertJsonElementToPlainObject(element);
                if (value == null)
                {
                    if (targetType.IsValueType)
                        return Activator.CreateInstance(targetType);
                    return null;
                }
            }
#endif

            // Handle Unity types
            if (targetType == typeof(Vector3))
            {
#if !MONO
                var dict = STJ.JsonSerializer.Deserialize<Dictionary<string, float>>(value.ToString());
#else
                var dict = JsonConvert.DeserializeObject<Dictionary<string, float>>(value.ToString());
#endif
                if (dict != null && dict.ContainsKey("x") && dict.ContainsKey("y") && dict.ContainsKey("z"))
                    return new Vector3(dict["x"], dict["y"], dict["z"]);
                return Vector3.zero;
            }
            if (targetType == typeof(Vector2))
            {
#if !MONO
                var dict = STJ.JsonSerializer.Deserialize<Dictionary<string, float>>(value.ToString());
#else
                var dict = JsonConvert.DeserializeObject<Dictionary<string, float>>(value.ToString());
#endif
                if (dict != null && dict.ContainsKey("x") && dict.ContainsKey("y"))
                    return new Vector2(dict["x"], dict["y"]);
                return Vector2.zero;
            }
            if (targetType == typeof(Color))
            {
#if !MONO
                var dict = STJ.JsonSerializer.Deserialize<Dictionary<string, float>>(value.ToString());
#else
                var dict = JsonConvert.DeserializeObject<Dictionary<string, float>>(value.ToString());
#endif
                if (dict != null && dict.ContainsKey("r") && dict.ContainsKey("g") && dict.ContainsKey("b"))
                    return new Color(dict["r"], dict["g"], dict["b"], dict.ContainsKey("a") ? dict["a"] : 1f);
                return Color.white;
            }

            // Handle enums
            if (targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, value.ToString(), true, out var enumValue))
                    return enumValue;
                return Enum.GetValues(targetType).GetValue(0); // Default to first enum value
            }

            // Handle DateTime
            if (targetType == typeof(DateTime))
            {
                if (DateTime.TryParse(value.ToString(), out var dateTime))
                    return dateTime;
                return DateTime.MinValue;
            }

            // Handle collections
            if (targetType.IsGenericType)
            {
                var genericTypeDef = targetType.GetGenericTypeDefinition();
                var elementType = targetType.GetGenericArguments()[0];

                if (genericTypeDef == typeof(List<>))
                {
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    var list = (IList)Activator.CreateInstance(listType);
                    
                    if (value is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            var deserializedItem = DeserializeValue(elementType, item);
                            list.Add(deserializedItem);
                        }
                    }
                    return list;
                }

                if (genericTypeDef == typeof(Dictionary<,>))
                {
                    var keyType = targetType.GetGenericArguments()[0];
                    var valueType = targetType.GetGenericArguments()[1];
                    var dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
                    var dict = (IDictionary)Activator.CreateInstance(dictType);

                    if (value is IDictionary dictValue)
                    {
                        foreach (DictionaryEntry entry in dictValue)
                        {
                            var key = DeserializeValue(keyType, entry.Key);
                            var val = DeserializeValue(valueType, entry.Value);
                            dict.Add(key, val);
                        }
                    }
                    return dict;
                }
            }

            // Handle arrays
            if (targetType.IsArray)
            {
                var elementType = targetType.GetElementType();
                if (value is IEnumerable enumerable)
                {
                    var list = new List<object>();
                    foreach (var item in enumerable)
                    {
                        list.Add(DeserializeValue(elementType, item));
                    }
                    var array = Array.CreateInstance(elementType, list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        array.SetValue(list[i], i);
                    }
                    return array;
                }
            }

            // Handle primitives and simple types
            if (targetType.IsPrimitive || targetType == typeof(string) || targetType == typeof(decimal))
            {
                try
                {
                    return Convert.ChangeType(value, targetType);
                }
                catch
                {
                    return value;
                }
            }

            // For custom types, check if they have [SaveableField] attributes
            // If so, deserialize recursively using SaveableSerializer logic
            if (targetType.IsClass && !targetType.IsPrimitive && targetType != typeof(string))
            {
                // Check if this type has [SaveableField] attributes
                var fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                bool hasSaveableFields = false;
                foreach (var field in fields)
                {
                    if (field.GetCustomAttribute<SaveableFieldAttribute>() != null && !field.IsNotSerialized)
                    {
                        hasSaveableFields = true;
                        break;
                    }
                }

                // If it has SaveableField attributes, deserialize recursively
                if (hasSaveableFields)
                {
                    try
                    {
                        // Create instance of the type
                        var instance = Activator.CreateInstance(targetType);
                        
                        // If value is a dictionary (from recursive serialization), deserialize from it
                        if (value is Dictionary<string, object> nestedData)
                        {
                            foreach (var field in fields)
                            {
                                var attr = field.GetCustomAttribute<SaveableFieldAttribute>();
                                if (attr != null && !field.IsNotSerialized)
                                {
                                    var key = !string.IsNullOrEmpty(attr.SaveName) ? attr.SaveName : field.Name;
                                    if (nestedData.TryGetValue(key, out var fieldValue))
                                    {
                                        var deserializedValue = DeserializeValue(field.FieldType, fieldValue);
                                        field.SetValue(instance, deserializedValue);
                                    }
                                }
                            }
                            return instance;
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Warn($"[SAVEABLE SERIALIZER] Error recursively deserializing {targetType.Name}: {ex.Message}");
                    }
                }
            }

            // For custom types without SaveableField attributes, try JSON deserialization
            try
            {
#if !MONO
                var jsonString = STJ.JsonSerializer.Serialize(value);
                return STJ.JsonSerializer.Deserialize(jsonString, targetType);
#else
                var jsonString = JsonConvert.SerializeObject(value);
                return JsonConvert.DeserializeObject(jsonString, targetType);
#endif
            }
            catch
            {
                ModLogger.Warn($"[SAVEABLE SERIALIZER] Failed to deserialize value to {targetType.Name}, using default");
                if (targetType.IsValueType)
                    return Activator.CreateInstance(targetType);
                return null;
            }
        }
    }
}

