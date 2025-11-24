using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Behind_Bars.Helpers;
using Newtonsoft.Json;
using UnityEngine;

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Utility class for serializing/deserializing Saveable objects using reflection.
    /// Finds fields marked with [SaveableField] and converts them to/from JSON.
    /// </summary>
    public static class SaveableSerializer
    {
        /// <summary>
        /// Serializes a Saveable object to JSON by finding all [SaveableField] marked fields.
        /// </summary>
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
                return JsonConvert.SerializeObject(saveData, Formatting.Indented);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SAVEABLE SERIALIZER] Error serializing {saveable.GetType().Name}: {ex.Message}");
                ModLogger.Error($"[SAVEABLE SERIALIZER] Stack trace: {ex.StackTrace}");
                return "{}";
            }
        }

        /// <summary>
        /// Deserializes JSON to a Saveable object by setting fields marked with [SaveableField].
        /// </summary>
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
                var saveData = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
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

        /// <summary>
        /// Serializes a single value to a format suitable for JSON.
        /// Handles objects with SaveableField attributes by serializing them recursively.
        /// </summary>
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
                var jsonString = JsonConvert.SerializeObject(value);
                return JsonConvert.DeserializeObject<object>(jsonString);
            }
            catch
            {
                // Fallback: try to convert to string
                return value.ToString();
            }
        }

        /// <summary>
        /// Deserializes a value from JSON to the target type.
        /// Handles objects with SaveableField attributes by deserializing them recursively.
        /// </summary>
        public static object DeserializeValue(Type targetType, object value)
        {
            if (value == null)
            {
                // Return default value for the type
                if (targetType.IsValueType)
                    return Activator.CreateInstance(targetType);
                return null;
            }

            // Handle Unity types
            if (targetType == typeof(Vector3))
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, float>>(value.ToString());
                if (dict != null && dict.ContainsKey("x") && dict.ContainsKey("y") && dict.ContainsKey("z"))
                    return new Vector3(dict["x"], dict["y"], dict["z"]);
                return Vector3.zero;
            }
            if (targetType == typeof(Vector2))
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, float>>(value.ToString());
                if (dict != null && dict.ContainsKey("x") && dict.ContainsKey("y"))
                    return new Vector2(dict["x"], dict["y"]);
                return Vector2.zero;
            }
            if (targetType == typeof(Color))
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, float>>(value.ToString());
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
                var jsonString = JsonConvert.SerializeObject(value);
                return JsonConvert.DeserializeObject(jsonString, targetType);
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

