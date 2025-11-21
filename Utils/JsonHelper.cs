using System;
using System.Collections.Generic;
using System.Reflection;
using Behind_Bars.Helpers;
using Newtonsoft.Json;
#if !MONO
using Newtonsoft.Json;
#endif

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Helper class for safely creating JsonSerializerSettings in Mono domain
    /// Handles TypeLoadException issues with StreamingContext after game updates
    /// In Mono, completely avoids JsonSerializerSettings to prevent type loading
    /// </summary>
    public static class JsonHelper
    {
#if MONO
        // In Mono, use object to avoid type loading issues with JsonSerializerSettings
        private static object _cachedSettings = null;
        private static object _cachedSettingsFormatted = null;
#else
        private static JsonSerializerSettings _cachedSettings = null;
        private static JsonSerializerSettings _cachedSettingsFormatted = null;
#endif
        private static bool _initializationAttempted = false;
        private static bool _canUseSettings = false;

        /// <summary>
        /// Attempts to create JsonSerializerSettings using reflection to avoid type loading issues
        /// In Mono, we avoid using JsonSerializerSettings entirely due to StreamingContext type loading issues
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
        /// Gets default JSON serializer settings, handling TypeLoadException gracefully
        /// Returns null in Mono if settings can't be created (will use JsonConvert defaults)
        /// </summary>
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
        /// Gets formatted JSON serializer settings (with indentation), handling TypeLoadException gracefully
        /// </summary>
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
        /// Gets JSON serializer settings with custom converters, handling TypeLoadException gracefully
        /// </summary>
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
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 10,
                    Converters = converters
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
        /// Gets JSON serializer settings with custom converters and contract resolver, handling TypeLoadException gracefully
        /// </summary>
#if MONO
        public static object GetSettingsWithConvertersAndResolver(
            List<JsonConverter> converters, 
            Newtonsoft.Json.Serialization.IContractResolver contractResolver)
#else
        public static JsonSerializerSettings GetSettingsWithConvertersAndResolver(
            List<JsonConverter> converters, 
            Newtonsoft.Json.Serialization.IContractResolver contractResolver)
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
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 10,
                    Converters = converters,
                    ContractResolver = contractResolver
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
        /// Gets JSON serializer settings with reference loop handling, handling TypeLoadException gracefully
        /// </summary>
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
                    MaxDepth = maxDepth,
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
        /// Safely serializes an object to JSON string
        /// </summary>
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

                // If settings is still null, use JsonConvert without settings (uses defaults)
                if (settings == null)
                {
                    return JsonConvert.SerializeObject(value);
                }

#if MONO
                // In Mono, settings is always null, so this should never execute
                // But if it does, just use the no-settings overload
                return JsonConvert.SerializeObject(value);
#else
                return JsonConvert.SerializeObject(value, (JsonSerializerSettings)settings);
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error serializing object to JSON: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Safely deserializes JSON string to object
        /// </summary>
#if MONO
        public static T DeserializeObject<T>(string json, object settings = null)
#else
        public static T DeserializeObject<T>(string json, JsonSerializerSettings settings = null)
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
                    return JsonConvert.DeserializeObject<T>(json);
                }

#if MONO
                // In Mono, settings is always null, so this should never execute
                return JsonConvert.DeserializeObject<T>(json);
#else
                return JsonConvert.DeserializeObject<T>(json, (JsonSerializerSettings)settings);
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error deserializing JSON to {typeof(T).Name}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Safely populates an object from JSON string
        /// </summary>
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
                    JsonConvert.PopulateObject(json, target);
                    return;
                }

#if MONO
                // In Mono, settings is always null, so this should never execute
                JsonConvert.PopulateObject(json, target);
#else
                JsonConvert.PopulateObject(json, target, (JsonSerializerSettings)settings);
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error populating object from JSON: {ex.Message}");
                throw;
            }
        }
    }
}

