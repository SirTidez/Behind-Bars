using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// INTERNAL: Provides generic reflection based methods for easier API development
    /// </summary>
    internal static class ReflectionUtils
    {
        /// <summary>
        /// Identifies all classes derived from another class.
        /// </summary>
        /// <typeparam name="TBaseClass">The base class derived from.</typeparam>
        /// <returns>A list of all types derived from the base class.</returns>
        /// <remarks>Scans every currently loaded non-skipped assembly and
        /// returns non-abstract assignable types. Assembly/type load failures
        /// are silently ignored, and enumeration order follows the runtime's
        /// assembly/type order; duplicates are not explicitly removed.</remarks>
        internal static List<Type> GetDerivedClasses<TBaseClass>()
        {
            List<Type> derivedClasses = new List<Type>();
            Assembly[] applicableAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !ShouldSkipAssembly(assembly))
                .ToArray();
            foreach (Assembly assembly in applicableAssemblies)
                foreach (Type type in SafeGetTypes(assembly))
                {
                    try
                    {
                        if (type == null)
                            continue;
                        if (typeof(TBaseClass).IsAssignableFrom(type)
                            && type != typeof(TBaseClass)
                            && !type.IsAbstract)
                        {
                            derivedClasses.Add(type);
                        }
                    }
                    catch (TypeLoadException)
                    {
                        // Ideally, we'd log this, but can be noisy and we've got no logger elsewhere
                        continue;
                    }
                    catch (Exception)
                    {
                        // Catch-all for anything else (e.g., MissingMethodException)
                        continue;
                    }
                }
            return derivedClasses;
        }

        /// <summary>
        /// INTERNAL: Gets all types by their name.
        /// </summary>
        /// <param name="typeName">The name of the type.</param>
        /// <returns>The first matching type, or <c>null</c> when no loaded
        /// assembly contains one.</returns>
        /// <remarks>Attempts an exact <see cref="Type.GetType(string, bool, bool)"/>
        /// lookup, then searches non-skipped assemblies and finally all
        /// assemblies. Matching is case-sensitive; failed reflection loads and
        /// the direct lookup exception path are silent and produce no log.</remarks>
        internal static Type? GetTypeByName(string typeName)
        {
            // Fast path: allow fully-qualified type names to resolve quickly
            try
            {
                var direct = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (direct != null)
                    return direct;
            }
            catch { /* ignore */ }

            // First search through likely candidate assemblies (skip core/system ones)
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies.Where(a => !ShouldSkipAssembly(a)))
            {
                foreach (Type type in SafeGetTypes(assembly))
                {
                    if (type == null)
                        continue;

                    if (type.Name == typeName || type.FullName == typeName)
                        return type;
                }
            }

            // Fallback: search all assemblies but still use SafeGetTypes
            foreach (Assembly assembly in assemblies)
            {
                foreach (Type type in SafeGetTypes(assembly))
                {
                    if (type == null)
                        continue;

                    if (type.Name == typeName || type.FullName == typeName || (type.FullName != null && type.FullName.EndsWith("." + typeName)))
                        return type;
                }
            }

            return null;
        }
        
        /// <summary>
        /// INTERNAL: Determines whether to skip an assembly during reflection searches. Will skip assemblies that are unlikely to contain mod/game types.
        /// </summary>
        /// <param name="assembly">The assembly to check.</param>
        /// <returns>Whether to skip the assembly or not.</returns>
        /// <remarks>Uses case-sensitive prefix checks against
        /// <see cref="Assembly.FullName"/>. A null assembly is not guarded and
        /// therefore throws when its full name is accessed.</remarks>
        internal static bool ShouldSkipAssembly(Assembly assembly)
        {
            string? fullName = assembly.FullName;
            if (string.IsNullOrEmpty(fullName))
                return false;

            return fullName.StartsWith("System")
                   || fullName.StartsWith("Unity")
                   || fullName.StartsWith("Il2Cpp")
                   || fullName.StartsWith("mscorlib")
                   || fullName.StartsWith("Mono.")
                   || fullName.StartsWith("netstandard")
                   || fullName.StartsWith("com.rlabrecque")
                   || fullName.StartsWith("__Generated");
        }
        
        /// <summary>
        /// INTERNAL: Safely gets types from an assembly, even if some types fail to load.
        /// </summary>
        /// <param name="asm">The assembly to get types from.</param>
        /// <returns>The types that were successfully loaded from the assembly.</returns>
        /// <remarks>For a <see cref="ReflectionTypeLoadException"/>, only the
        /// non-null successfully loaded types are returned. Any other exception
        /// (including a null assembly) produces an empty sequence without
        /// logging.</remarks>
        internal static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!.Cast<Type>();
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        /// <summary>
        /// INTERNAL: Recursively gets fields from a class down to the object type.
        /// </summary>
        /// <param name="type">The type you want to recursively search.</param>
        /// <param name="bindingFlags">The binding flags to apply during the search.</param>
        /// <returns>Fields declared on the type and each base type up to, but
        /// excluding, <see cref="object"/>.</returns>
        /// <remarks><see cref="BindingFlags.DeclaredOnly"/> is added for each
        /// level. A null type returns an empty array; the result can include
        /// inherited framework or saveable fields when the supplied flags allow
        /// them.</remarks>
        internal static FieldInfo[] GetAllFields(Type? type, BindingFlags bindingFlags)
        {
            List<FieldInfo> fieldInfos = new List<FieldInfo>();
            while (type != null && type != typeof(object))
            {
                fieldInfos.AddRange(type.GetFields(bindingFlags | BindingFlags.DeclaredOnly));
                type = type.BaseType;
            }
            return fieldInfos.ToArray();
        }

        /// <summary>
        /// Recursively searches for a method by name from a class down to the object type.
        /// </summary>
        /// <param name="type">The type you want to recursively search.</param>
        /// <param name="methodName">The name of the method you're searching for.</param>
        /// <param name="bindingFlags">The binding flags to apply during the search.</param>
        /// <returns>The method info if found, otherwise null.</returns>
        /// <remarks>Searches the type and each base type up to, but excluding,
        /// <see cref="object"/> and returns the first runtime match. Reflection
        /// exceptions are not caught here.</remarks>
        internal static MethodInfo? GetMethod(Type? type, string methodName, BindingFlags bindingFlags)
        {
            while (type != null && type != typeof(object))
            {
                MethodInfo? method = type.GetMethod(methodName, bindingFlags);
                if (method != null)
                    return method;

                type = type.BaseType;
            }

            return null;
        }

        /// <summary>
        /// INTERNAL: The different ValueTuple types.
        /// </summary>
        /// <remarks>Stores the open generic definitions for one through eight
        /// tuple elements; larger/rest-field tuple shapes are not represented.</remarks>
        private static readonly HashSet<Type> _valueTupleTypes = new HashSet<Type>()
        {
            typeof(ValueTuple<>),
            typeof(ValueTuple<,>),
            typeof(ValueTuple<,,>),
            typeof(ValueTuple<,,,>),
            typeof(ValueTuple<,,,,>),
            typeof(ValueTuple<,,,,,>),
            typeof(ValueTuple<,,,,,,>),
            typeof(ValueTuple<,,,,,,,>)
        };

        /// <summary>
        /// Checks whether the object is a ValueTuple
        /// </summary>
        /// <param name="obj">The object type to check</param>
        /// <returns>Whether the type is a ValueTuple or not</returns>
        /// <remarks>Only generic value tuples with one through eight generic
        /// definitions are recognized. Reference types and null return false.</remarks>
        internal static bool IsValueTuple(object obj)
        {
            if (obj == null)
                return false;

            var type = obj.GetType();
            if (!type.IsValueType || !type.IsGenericType)
                return false;

            var genericType = type.GetGenericTypeDefinition();
            return _valueTupleTypes.Contains(genericType);
        }

        /// <summary>
        /// Retrieves the Items from the ValueTuple instance.
        /// </summary>
        /// <param name="obj">The ValueTuple instance</param>
        /// <returns>The public instance field values in reflection field order,
        /// or <c>null</c> when <paramref name="obj"/> is not a recognized tuple.</returns>
        /// <remarks>Field access exceptions are allowed to propagate. The
        /// helper relies on the same one-through-eight tuple recognition as
        /// <see cref="IsValueTuple(object)"/>.</remarks>
        internal static object[]? GetValueTupleItems(object obj)
        {
            if (!IsValueTuple(obj))
                return null;

            var fields = GetAllFields(obj.GetType(), BindingFlags.Instance | BindingFlags.Public);
            if (fields == null || fields.Length == 0)
                return null;

            return fields.Select(f => f.GetValue(obj))
                .ToArray();
        }

        /// <summary>
        /// INTERNAL: Shared cache for const string field retrieval across appearance classes.
        /// </summary>
        /// <remarks>This process-wide cache is keyed by type, has no locking or
        /// invalidation, and stores mutable lists returned directly to callers.</remarks>
        private static readonly Dictionary<Type, List<string>> _constStringFieldsCache = new Dictionary<Type, List<string>>();

        /// <summary>
        /// INTERNAL: Retrieves and caches all public <c>const string</c> fields defined in the specified type.
        /// Shared implementation used by appearance classes to avoid code duplication.
        /// </summary>
        /// <param name="type">The type from which to retrieve constant string fields.</param>
        /// <returns>
        /// A list of constant string values defined in the type.
        /// </returns>
        /// <remarks>
        /// Uses reflection to gather public static literal string fields and
        /// caches them for future calls to improve performance. A cached list is
        /// returned directly, so caller mutations affect later callers.
        /// </remarks>
        internal static List<string> GetConstStringFields(Type type)
        {
            if (type == null)
                return new List<string>();

            if (_constStringFieldsCache.TryGetValue(type, out var cached))
                return cached;

            var consts = new List<string>();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var field in fields)
            {
                if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                    consts.Add((string)field.GetRawConstantValue());
            }

            _constStringFieldsCache[type] = consts;
            return consts;
        }

        /// <summary>
        /// INTERNAL: Attempts to set a field or property on an object using reflection.
        /// Tries field first, then property. Handles both public and non-public members.
        /// </summary>
        /// <param name="target">The target object to set the member on.</param>
        /// <param name="memberName">The name of the field or property.</param>
        /// <param name="value">The value to set.</param>
        /// <returns><c>true</c> if the member was successfully set; otherwise, <c>false</c>.</returns>
        /// <remarks>Searches fields before properties using public/non-public
        /// instance flags and requires the supplied value to already be an
        /// instance of the declared type (or null). Member lookup/set failures
        /// are swallowed without logging; this direct type lookup does not
        /// explicitly walk private base members.</remarks>
        internal static bool TrySetFieldOrProperty(object target, string memberName, object value)
        {
            if (target == null) return false;
            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            
            // Try field first
            var fi = type.GetField(memberName, flags);
            if (fi != null)
            {
                try
                {
                    if (value == null || fi.FieldType.IsInstanceOfType(value))
                    {
                        fi.SetValue(target, value);
                        return true;
                    }
                }
                catch { }
            }
            
            // Try property
            var pi = type.GetProperty(memberName, flags);
            if (pi != null && pi.CanWrite)
            {
                try
                {
                    if (value == null || pi.PropertyType.IsInstanceOfType(value))
                    {
                        pi.SetValue(target, value);
                        return true;
                    }
                }
                catch { }
            }
            
            return false;
        }

        /// <summary>
        /// INTERNAL: Attempts to get a field or property value from an object using reflection.
        /// Tries field first, then property. Handles both public and non-public members.
        /// </summary>
        /// <param name="target">The target object to get the member from.</param>
        /// <param name="memberName">The name of the field or property.</param>
        /// <returns>The value of the member, or <c>null</c> if not found or inaccessible.</returns>
        /// <remarks>Searches fields before readable properties using
        /// public/non-public instance flags. Lookup and getter failures are
        /// swallowed without logging, and a legitimate null value is
        /// indistinguishable from a missing/inaccessible member.</remarks>
        internal static object TryGetFieldOrProperty(object target, string memberName)
        {
            if (target == null) return null;
            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            
            // Try field first
            var fi = type.GetField(memberName, flags);
            if (fi != null)
            {
                try
                {
                    return fi.GetValue(target);
                }
                catch { }
            }
            
            // Try property
            var pi = type.GetProperty(memberName, flags);
            if (pi != null && pi.CanRead)
            {
                try
                {
                    return pi.GetValue(target);
                }
                catch { }
            }
            
            return null;
        }

        /// <summary>
        /// INTERNAL: Attempts to get a static field or property value from a type using reflection.
        /// Tries field first, then property. Handles both public and non-public members.
        /// Fields on Mono are typically properties on IL2CPP.
        /// </summary>
        /// <param name="type">The type to get the static member from.</param>
        /// <param name="memberName">The name of the field or property.</param>
        /// <returns>The value of the member, or <c>null</c> if not found or inaccessible.</returns>
        /// <remarks>Searches fields before readable properties using
        /// public/non-public static flags. Reflection failures are swallowed
        /// without logging, and a null result is ambiguous. This lookup is made
        /// on the supplied type rather than an explicit base-type walk.</remarks>
        internal static object TryGetStaticFieldOrProperty(Type type, string memberName)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            
            // Try field first
            var fi = type.GetField(memberName, flags);
            if (fi != null)
            {
                try
                {
                    return fi.GetValue(null);
                }
                catch { }
            }
            
            // Try property
            var pi = type.GetProperty(memberName, flags);
            if (pi != null && pi.CanRead)
            {
                try
                {
                    return pi.GetValue(null);
                }
                catch { }
            }
            
            return null;
        }

        /// <summary>
        /// INTERNAL: Attempts to set a static field or property value on a type using reflection.
        /// Tries field first, then property. Handles both public and non-public members.
        /// Fields on Mono are typically properties on IL2CPP.
        /// </summary>
        /// <param name="type">The type to set the static member on.</param>
        /// <param name="memberName">The name of the field or property.</param>
        /// <param name="value">The value to set.</param>
        /// <returns><c>true</c> if the member was successfully set; otherwise, <c>false</c>.</returns>
        /// <remarks>Searches fields before writable properties using
        /// public/non-public static flags and performs no type conversion.
        /// Lookup and setter failures are swallowed without logging; a null
        /// value is accepted before the runtime setter decides whether the
        /// target type permits it.</remarks>
        internal static bool TrySetStaticFieldOrProperty(Type type, string memberName, object value)
        {
            if (type == null) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            
            // Try field first
            var fi = type.GetField(memberName, flags);
            if (fi != null)
            {
                try
                {
                    if (value == null || fi.FieldType.IsInstanceOfType(value))
                    {
                        fi.SetValue(null, value);
                        return true;
                    }
                }
                catch { }
            }
            
            // Try property
            var pi = type.GetProperty(memberName, flags);
            if (pi != null && pi.CanWrite)
            {
                try
                {
                    if (value == null || pi.PropertyType.IsInstanceOfType(value))
                    {
                        pi.SetValue(null, value);
                        return true;
                    }
                }
                catch { }
            }
            
            return false;
        }
    }
}
