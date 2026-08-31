using System;
using System.Collections.Generic;
using System.Reflection;
using Behind_Bars.Helpers;
#if !MONO
using Il2CppNewtonsoft.Json;
using JsonSerialization = Il2CppNewtonsoft.Json.Serialization;
using STJ = System.Text.Json;
#else
using Newtonsoft.Json;
using JsonSerialization = Newtonsoft.Json.Serialization;
#endif

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Cross-runtime JSON compatibility helpers.
    ///
    /// Mono avoids constructing <see cref="JsonSerializerSettings"/> because the
    /// game's Newtonsoft dependency can fail to load <c>StreamingContext</c>
    /// after updates. IL2CPP can construct the settings type, but the public
    /// serialize, deserialize, and populate methods currently use their runtime
    /// defaults instead of applying the optional settings argument. The factory
    /// methods remain as compatibility surfaces for callers that share code
    /// between the two runtimes.
    /// </summary>
    public static class JsonHelper
    {
#if MONO
        // In Mono, use object to avoid type loading issues with JsonSerializerSettings.
        // The value remains null; callers fall back to JsonConvert defaults.
        private static object _cachedSettings = null;
        private static object _cachedSettingsFormatted = null;
#else
        private static JsonSerializerSettings _cachedSettings = null;
        private static JsonSerializerSettings _cachedSettingsFormatted = null;
#endif
        // Prevent repeated settings-construction attempts after a runtime load failure.
        private static bool _initializationAttempted = false;

        // Records whether IL2CPP settings construction succeeded. The current
        // serializer methods do not consult this flag, but it preserves the
        // result for the compatibility factory surface.
        private static bool _canUseSettings = false;

        /// <summary>
        /// Attempts to create the runtime's optional serializer settings object.
        ///
        /// The method name is retained for compatibility with the original
        /// reflection-based implementation. The current implementation creates
        /// settings directly on IL2CPP and returns null on Mono; it is not used by
        /// the public serialization operations.
        /// </summary>
#if MONO
        private static object TryCreateSettingsReflection()
#else
        private static JsonSerializerSettings TryCreateSettingsReflection()
#endif
        {
#if MONO
            // In Mono, avoid JsonSerializerSettings entirely due to type loading issues
            // JsonConvert will use its default settings which work fine
            ModLogger.Debug("Mono build: Skipping JsonSerializerSettings creation, will use JsonConvert defaults");
            return null;
#else
            // In IL2CPP, use normal instantiation
            return new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore
            };
#endif
        }

        /// <summary>
        /// Gets the default optional JSON serializer settings.
        /// </summary>
        /// <returns>
        /// An IL2CPP <see cref="JsonSerializerSettings"/> instance when it can be
        /// constructed; <c>null</c> on Mono or after construction fails. A null
        /// result means callers should use the serializer's defaults.
        /// </returns>
        /// <remarks>
        /// The result is cached as a one-time construction attempt. The public
        /// serialization methods currently do not apply this object to their
        /// underlying serializer calls.
        /// </remarks>
#if MONO
        public static object GetDefaultSettings()
#else
        public static JsonSerializerSettings GetDefaultSettings()
#endif
        {
            if (_cachedSettings != null)
            {
#if MONO
                return null; // Always null in Mono
#else
                return (JsonSerializerSettings)_cachedSettings;
#endif
            }

            if (!_initializationAttempted)
            {
                _initializationAttempted = true;
                
#if MONO
                // In Mono, avoid JsonSerializerSettings entirely due to StreamingContext type loading issues
                // JsonConvert works fine with null settings (uses defaults)
                _cachedSettings = null;
                _canUseSettings = false;
                ModLogger.Debug("Mono build: JsonSerializerSettings disabled, using JsonConvert defaults");
#else
                // In IL2CPP, use normal instantiation
                try
                {
                    _cachedSettings = new JsonSerializerSettings
                    {
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                        NullValueHandling = NullValueHandling.Ignore
                    };
                    _canUseSettings = true;
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"Error creating JsonSerializerSettings: {ex.Message}");
                    _cachedSettings = null;
                    _canUseSettings = false;
                }
#endif
            }

#if MONO
            return null; // Always null in Mono - JsonConvert will use defaults
#else
            return (JsonSerializerSettings)_cachedSettings; // May be null, which is fine - JsonConvert will use defaults
#endif
        }

        /// <summary>
        /// Gets optional indented JSON serializer settings.
        /// </summary>
        /// <returns>
        /// An IL2CPP settings object with indentation when construction succeeds;
        /// <c>null</c> on Mono or when the default settings cannot be created.
        /// </returns>
        /// <remarks>
        /// This factory describes a compatibility surface only. The current
        /// public serialization methods do not consume the returned settings.
        /// </remarks>
#if MONO
        public static object GetFormattedSettings()
#else
        public static JsonSerializerSettings GetFormattedSettings()
#endif
        {
            if (_cachedSettingsFormatted != null)
            {
#if MONO
                return null; // Always null in Mono
#else
                return (JsonSerializerSettings)_cachedSettingsFormatted;
#endif
            }

            var defaultSettings = GetDefaultSettings();
            if (defaultSettings == null)
            {
                // Can't create settings, return null (will use JsonConvert defaults)
                return null;
            }

#if MONO
            // In Mono, avoid JsonSerializerSettings entirely
            _cachedSettingsFormatted = null;
            return null;
#else
            // In IL2CPP, use normal instantiation
            try
            {
                _cachedSettingsFormatted = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented
                };
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Error creating formatted JsonSerializerSettings: {ex.Message}. Using default settings.");
                _cachedSettingsFormatted = defaultSettings;
            }
            return _cachedSettingsFormatted;
#endif
        }

        /// <summary>
        /// Gets optional indented settings for callers that request custom converters.
        /// </summary>
        /// <param name="converters">Converters requested by the caller.</param>
        /// <returns>
        /// An IL2CPP settings object, or <c>null</c> on Mono or when settings cannot
        /// be created.
        /// </returns>
        /// <remarks>
        /// The current implementation does not attach <paramref name="converters"
        /// /> to the returned IL2CPP settings, and Mono intentionally returns null.
        /// This method must not be documented as applying converters to a JSON
        /// operation until the implementation changes.
        /// </remarks>
#if MONO
        public static object GetSettingsWithConverters(List<JsonConverter> converters)
#else
        public static JsonSerializerSettings GetSettingsWithConverters(List<JsonConverter> converters)
#endif
        {
            var defaultSettings = GetDefaultSettings();
            if (defaultSettings == null)
            {
                // Can't create settings, return null (will use JsonConvert defaults)
                ModLogger.Debug("Cannot create JsonSerializerSettings with converters, using null (JsonConvert defaults)");
                return null;
            }

#if MONO
            // In Mono, avoid JsonSerializerSettings entirely
            // Note: Converters won't work without settings, but this is better than crashing
            ModLogger.Debug("Mono build: JsonSerializerSettings with converters not available, using null (JsonConvert defaults)");
            return null;
#else
            // In IL2CPP, use normal instantiation
            try
            {
                return new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Error creating JsonSerializerSettings with converters: {ex.Message}. Using default settings.");
                return defaultSettings;
            }
#endif
        }

        /// <summary>
        /// Gets optional settings for callers that request converters and a contract resolver.
        /// </summary>
        /// <param name="converters">Converters requested by the caller.</param>
        /// <param name="contractResolver">Contract resolver requested by the caller.</param>
        /// <returns>
        /// An IL2CPP settings object, or <c>null</c> on Mono or when settings cannot
        /// be created.
        /// </returns>
        /// <remarks>
        /// The current implementation does not attach either supplied argument to
        /// the returned IL2CPP settings, and Mono intentionally returns null. The
        /// parameters are retained for source compatibility.
        /// </remarks>
#if MONO
        public static object GetSettingsWithConvertersAndResolver(
            List<JsonConverter> converters, 
            JsonSerialization.IContractResolver contractResolver)
#else
        public static JsonSerializerSettings GetSettingsWithConvertersAndResolver(
            List<JsonConverter> converters, 
            JsonSerialization.IContractResolver contractResolver)
#endif
        {
            var defaultSettings = GetDefaultSettings();
            if (defaultSettings == null)
            {
                // Can't create settings, return null (will use JsonConvert defaults)
                ModLogger.Debug("Cannot create JsonSerializerSettings with converters and resolver, using null (JsonConvert defaults)");
                return null;
            }

#if MONO
            // In Mono, avoid JsonSerializerSettings entirely
            // Note: Converters and ContractResolver won't work without settings, but this is better than crashing
            ModLogger.Debug("Mono build: JsonSerializerSettings with converters and resolver not available, using null (JsonConvert defaults)");
            return null;
#else
            // In IL2CPP, use normal instantiation
            try
            {
                return new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                };
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Error creating JsonSerializerSettings with converters and resolver: {ex.Message}. Using default settings.");
                return defaultSettings;
            }
#endif
        }

        /// <summary>
        /// Gets optional settings that request ignored reference loops.
        /// </summary>
        /// <param name="maxDepth">
        /// Compatibility parameter retained from the original API. It is not
        /// applied by the current implementation.
        /// </param>
        /// <returns>
        /// An IL2CPP settings object with reference-loop handling, or <c>null</c> on
        /// Mono or when settings cannot be created.
        /// </returns>
        /// <remarks>
        /// The current implementation does not set a maximum depth and the public
        /// serialization methods do not consume the returned settings.
        /// </remarks>
#if MONO
        public static object GetSettingsWithReferenceLoopHandling(int maxDepth = 5)
#else
        public static JsonSerializerSettings GetSettingsWithReferenceLoopHandling(int maxDepth = 5)
#endif
        {
            var defaultSettings = GetDefaultSettings();
            if (defaultSettings == null)
            {
                // Can't create settings, return null (will use JsonConvert defaults)
                ModLogger.Debug("Cannot create JsonSerializerSettings with reference loop handling, using null (JsonConvert defaults)");
                return null;
            }

#if MONO
            // In Mono, avoid JsonSerializerSettings entirely
            ModLogger.Debug("Mono build: JsonSerializerSettings with reference loop handling not available, using null (JsonConvert defaults)");
            return null;
#else
            // In IL2CPP, use normal instantiation
            try
            {
                return new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                };
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Error creating JsonSerializerSettings with reference loop handling: {ex.Message}. Using default settings.");
                return defaultSettings;
            }
#endif
        }

        /// <summary>
        /// Serializes an object to a JSON string using the current runtime's default serializer.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="settings">
        /// Compatibility settings argument. It is currently ignored by both
        /// runtime implementations.
        /// </param>
        /// <returns>The serialized JSON string.</returns>
        /// <remarks>
        /// Mono uses Newtonsoft.Json and IL2CPP uses System.Text.Json. Exceptions
        /// are logged and rethrown; this method does not convert failures to an
        /// empty JSON document.
        /// </remarks>
#if MONO
        public static string SerializeObject(object value, object settings = null)
#else
        public static string SerializeObject(object value, JsonSerializerSettings settings = null)
#endif
        {
            try
            {
                if (settings == null)
                {
                    settings = GetDefaultSettings();
                }

                // Settings are resolved for API compatibility, but the current
                // runtime calls below intentionally use serializer defaults.
                // Do not claim that caller-provided settings affect this method
                // until both branches pass them through.
                if (settings == null)
                {
#if !MONO
                    return STJ.JsonSerializer.Serialize(value);
#else
                    return JsonConvert.SerializeObject(value);
#endif
                }

#if MONO
                // In Mono, settings is always null, so this should never execute
                // But if it does, just use the no-settings overload
                return JsonConvert.SerializeObject(value);
#else
                return STJ.JsonSerializer.Serialize(value);
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error serializing object to JSON: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Deserializes a JSON string to the requested type using the current runtime's default serializer.
        /// </summary>
        /// <typeparam name="T">The target type.</typeparam>
        /// <param name="json">The JSON text to deserialize.</param>
        /// <param name="settings">
        /// Compatibility settings argument. It is currently ignored by both
        /// runtime implementations.
        /// </param>
        /// <returns>The deserialized value, or the serializer's default null value.</returns>
        /// <remarks>
        /// IL2CPP uses System.Text.Json because the Il2CppNewtonsoft generic
        /// surface is not safe for managed types; Mono uses Newtonsoft.Json.
        /// Exceptions are logged and rethrown.
        /// </remarks>
#if MONO
        public static T DeserializeObject<T>(string json, object settings = null)
#else
        public static T DeserializeObject<T>(string json, JsonSerializerSettings settings = null)
#endif
        {
            try
            {
#if !MONO
                // IL2CPP: Il2CppNewtonsoft generic methods fail with managed types; use System.Text.Json
                return STJ.JsonSerializer.Deserialize<T>(json);
#else
                if (settings == null)
                {
                    settings = GetDefaultSettings();
                }

                // If settings is still null, use JsonConvert without settings (uses defaults)
                if (settings == null)
                {
                    return JsonConvert.DeserializeObject<T>(json);
                }

                // In Mono, settings is always null, so this should never execute
                return JsonConvert.DeserializeObject<T>(json);
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error deserializing JSON to {typeof(T).Name}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Populates an existing object from JSON using the current runtime's default serializer behavior.
        /// </summary>
        /// <param name="json">The JSON text to apply.</param>
        /// <param name="target">The object to populate.</param>
        /// <param name="settings">
        /// Compatibility settings argument. It is currently ignored by both
        /// runtime implementations.
        /// </param>
        /// <remarks>
        /// Mono delegates to Newtonsoft.Json. IL2CPP uses a fallback that
        /// deserializes a temporary object and copies only public writable
        /// properties and public fields whose values are non-null; it cannot
        /// clear an existing member with a JSON null. Exceptions are logged and
        /// rethrown.
        /// </remarks>
#if MONO
        public static void PopulateObject(string json, object target, object settings = null)
#else
        public static void PopulateObject(string json, object target, JsonSerializerSettings settings = null)
#endif
        {
            try
            {
                if (settings == null)
                {
                    settings = GetDefaultSettings();
                }

                // If settings is still null, use JsonConvert without settings (uses defaults)
                if (settings == null)
                {
#if !MONO
                    // System.Text.Json doesn't have a direct PopulateObject equivalent
                    // Deserialize to a dictionary and merge properties via reflection
                    PopulateObjectFallback(json, target);
#else
                    JsonConvert.PopulateObject(json, target);
#endif
                    return;
                }

#if MONO
                // In Mono, settings is always null, so this should never execute
                JsonConvert.PopulateObject(json, target);
#else
                PopulateObjectFallback(json, target);
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error populating object from JSON: {ex.Message}");
                throw;
            }
        }

#if !MONO
        /// <summary>
        /// Fallback for PopulateObject under IL2CPP using System.Text.Json
        /// Deserializes JSON and copies matching properties to the target object
        /// </summary>
        /// <remarks>
        /// This is intentionally a reduced PopulateObject implementation. Private
        /// members and read-only properties are skipped, null values are not
        /// copied, and individual assignment failures are swallowed so one
        /// incompatible member does not abort the rest of the merge.
        /// </remarks>
        /// <param name="json">JSON object used to create the temporary source instance.</param>
        /// <param name="target">Existing object whose public writable members receive non-null values.</param>
        private static void PopulateObjectFallback(string json, object target)
        {
            if (target == null) return;
            var targetType = target.GetType();
            var deserialized = STJ.JsonSerializer.Deserialize(json, targetType);
            if (deserialized == null) return;

            foreach (var prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                try
                {
                    var val = prop.GetValue(deserialized);
                    if (val != null)
                        prop.SetValue(target, val);
                }
                catch { /* skip properties that can't be set */ }
            }

            foreach (var field in targetType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    var val = field.GetValue(deserialized);
                    if (val != null)
                        field.SetValue(target, val);
                }
                catch { /* skip fields that can't be set */ }
            }
        }
#endif
    }
}

