#nullable enable
using System.Collections;
using MelonLoader;
using UnityEngine;
#if MONO
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.PlayerScripts;
using FishNet;
#else
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppFishNet;
using Object = Il2CppSystem.Object;

#endif

namespace Behind_Bars.Helpers
{
    /// <summary>
    /// Provides extension methods for converting between C# and Il2Cpp lists.
    /// </summary>
    public static class Il2CppListExtensions
    {
        /// <summary>
        /// Exposes a managed list as an enumerable.
        /// </summary>
        /// <typeparam name="T">The type of the objects in the collection.</typeparam>
        /// <param name="list">The managed list to expose.</param>
        /// <returns>The original list, or an empty enumerable when <paramref name="list"/> is null.</returns>
        /// <remarks>
        /// This is intentionally a no-op for managed lists. The matching IL2CPP
        /// overload below uses the same call shape while avoiding an enumeration
        /// over unused capacity in the native list backing array.
        /// </remarks>
        public static IEnumerable<T> AsEnumerable<T>(this List<T> list)
        {
            return list ?? [];
        }

#if !MONO
        /// <summary>
        /// Copies an enumerable into an IL2CPP list.
        /// </summary>
        /// <typeparam name="T">The type of the objects in the collection.</typeparam>
        /// <param name="source">The source enumerable to convert.</param>
        /// <returns>An IL2CPP list containing each element in source order.</returns>
        /// <remarks>
        /// The current implementation does not normalize a null source; a null
        /// enumerable reaches the <c>foreach</c> and throws. Callers should pass an
        /// empty enumerable when no values are available.
        /// </remarks>
        public static Il2CppSystem.Collections.Generic.List<T> ToIl2CppList<T>(
            this IEnumerable<T> source
        )
        {
            var il2CppList = new Il2CppSystem.Collections.Generic.List<T>();
            foreach (var item in source)
                il2CppList.Add(item);
            return il2CppList;
        }

        /// <summary>
        /// Copies an IL2CPP list into a managed list.
        /// </summary>
        /// <typeparam name="T">The type of the objects in the list.</typeparam>
        /// <param name="il2CppList">The Il2Cpp list to convert.</param>
        /// <returns>A managed list containing the elements in list order.</returns>
        /// <remarks>
        /// The current implementation calls <c>ToArray()</c> directly and does not
        /// normalize a null IL2CPP list; null therefore follows the underlying
        /// exception behavior.
        /// </remarks>
        public static List<T> ConvertToList<T>(Il2CppSystem.Collections.Generic.List<T> il2CppList)
        {
            List<T> csharpList = new List<T>();
            T[] array = il2CppList.ToArray();
            csharpList.AddRange(array);
            return csharpList;
        }

        /// <summary>
        /// Enumerates the live portion of an IL2CPP list as a managed enumerable.
        /// </summary>
        /// <typeparam name="T">The type of the objects in the list.</typeparam>
        /// <param name="list">The Il2Cpp list to convert.</param>
        /// <returns>The list's elements from index zero through its current size, or an empty enumerable for null.</returns>
        /// <remarks>
        /// This IL2CPP-specific path reads the generated <c>_items</c> and
        /// <c>_size</c> fields directly so unused backing-array capacity is not
        /// exposed. It relies on the runtime-generated list shape remaining stable.
        /// </remarks>
        public static IEnumerable<T> AsEnumerable<T>(this Il2CppSystem.Collections.Generic.List<T> list)
        {
            return list == null ? [] : list._items.Take(list._size);
        }
#endif
    }

    /// <summary>
    /// Common utility functions for the mod.
    /// </summary>
    public static class Helpers
    {
#if !MONO
        /// <summary>Type names for which IL2CPP resolution has already been logged.</summary>
        private static readonly HashSet<string> _typeResolutionFailuresLogged = new HashSet<string>();

        /// <summary>Serializes access to the one-time IL2CPP type-resolution log set.</summary>
        private static readonly object _typeResolutionLock = new object();

        /// <summary>
        /// Logs one type-resolution failure per full type name.
        /// </summary>
        /// <param name="typeName">Type name used as the de-duplication key.</param>
        /// <param name="reason">First observed failure reason.</param>
        /// <remarks>
        /// Later failures for the same name are intentionally suppressed, so the
        /// retained message is diagnostic context rather than a complete failure
        /// history.
        /// </remarks>
        private static void LogTypeResolutionFailureOnce(string typeName, string reason)
        {
            lock (_typeResolutionLock)
            {
                if (_typeResolutionFailuresLogged.Add(typeName))
                {
                    ModLogger.Error($"[IL2CPP] Failed to resolve type '{typeName}': {reason}");
                }
            }
        }

        /// <summary>
        /// Resolves the IL2CPP runtime type object required by non-generic Unity APIs.
        /// </summary>
        /// <typeparam name="T">Injected Unity object/component type to resolve.</typeparam>
        /// <param name="il2CppType">Resolved IL2CPP type when the method returns true; otherwise null.</param>
        /// <returns>True when <c>Il2CppType.Of&lt;T&gt;()</c> succeeds; false after a logged failure.</returns>
        /// <remarks>
        /// IL2CPP-injected component accessors use this bridge before calling
        /// type-based Unity APIs. It does not register a missing type or retry a
        /// failed resolution; callers receive a null/empty result instead.
        /// </remarks>
        private static bool TryResolveIl2CppType<T>(out Il2CppSystem.Type il2CppType)
            where T : UnityEngine.Object
        {
            il2CppType = null;
            var type = typeof(T);
            string typeName = type.FullName ?? type.Name;

            try
            {
                il2CppType = Il2CppType.Of<T>();
                return il2CppType != null;
            }
            catch (Exception firstEx)
            {
                LogTypeResolutionFailureOnce(typeName, firstEx.Message);
                return false;
            }
        }
#endif

        /// <summary>
        /// Gets a component from a GameObject through a runtime-compatible API.
        /// </summary>
        /// <typeparam name="T">Component type to find.</typeparam>
        /// <param name="gameObject">Object whose component should be queried.</param>
        /// <returns>The first matching component, or null when the object/type cannot be resolved.</returns>
        /// <remarks>
        /// Mono calls Unity's generic API directly. IL2CPP first resolves
        /// <typeparamref name="T"/> with <c>Il2CppType.Of&lt;T&gt;()</c>, then uses the
        /// type-based overload and a checked cast; this avoids exposing a generic
        /// injected-component call that IL2CPP cannot safely bind.
        /// </remarks>
        public static T GetComponentSafe<T>(GameObject gameObject)
            where T : Component
        {
            if (gameObject == null)
                return null;

#if MONO
            return gameObject.GetComponent<T>();
#else
            if (!TryResolveIl2CppType<T>(out var componentType))
                return null;

            var component = gameObject.GetComponent(componentType);
            return component?.TryCast<T>();
#endif
        }

        /// <summary>
        /// Adds a component through a runtime-compatible API.
        /// </summary>
        /// <typeparam name="T">Component type to add.</typeparam>
        /// <param name="gameObject">Object to receive the component.</param>
        /// <returns>The added component, or null when the object/type cannot be resolved.</returns>
        /// <remarks>
        /// Mono uses <c>AddComponent&lt;T&gt;()</c>. IL2CPP resolves the generated type
        /// first and calls the non-generic type overload, then casts the returned
        /// object. This helper does not check for an existing component; use
        /// <see cref="GetOrAddComponentSafe{T}(GameObject)"/> when that is required.
        /// </remarks>
        public static T AddComponentSafe<T>(GameObject gameObject)
            where T : Component
        {
            if (gameObject == null)
                return null;

#if MONO
            return gameObject.AddComponent<T>();
#else
            if (!TryResolveIl2CppType<T>(out var componentType))
                return null;

            var component = gameObject.AddComponent(componentType);
            return component?.TryCast<T>();
#endif
        }

        /// <summary>
        /// Gets an existing component or adds one when none is present.
        /// </summary>
        /// <typeparam name="T">Component type to find or add.</typeparam>
        /// <param name="gameObject">Object to inspect.</param>
        /// <returns>The existing/added component, or null when the object/type cannot be resolved.</returns>
        /// <remarks>
        /// The get and add operations are separate. It is not an atomic operation,
        /// so callers invoking it concurrently or during object destruction must
        /// still account for Unity lifetime races.
        /// </remarks>
        public static T GetOrAddComponentSafe<T>(GameObject gameObject)
            where T : Component
        {
            var existing = GetComponentSafe<T>(gameObject);
            return existing ?? AddComponentSafe<T>(gameObject);
        }

        /// <summary>
        /// Finds the first loaded Unity object of the requested type.
        /// </summary>
        /// <typeparam name="T">Unity object type to find.</typeparam>
        /// <returns>The first matching object, or null when none/type resolution fails.</returns>
        /// <remarks>
        /// Mono calls Unity's generic search. IL2CPP resolves the type and uses the
        /// type-based overload, returning a checked cast. Search scope and active
        /// object behavior therefore remain those of Unity's FindObjectOfType API.
        /// </remarks>
        public static T FindObjectOfTypeSafe<T>()
            where T : UnityEngine.Object
        {
#if MONO
            return UnityEngine.Object.FindObjectOfType<T>();
#else
            if (!TryResolveIl2CppType<T>(out var targetType))
                return null;

            var found = UnityEngine.Object.FindObjectOfType(targetType);
            return found?.TryCast<T>();
#endif
        }

        /// <summary>
        /// Finds all loaded Unity objects of the requested type.
        /// </summary>
        /// <typeparam name="T">Unity object type to find.</typeparam>
        /// <returns>A managed array of castable matches, or an empty array when none/type resolution fails.</returns>
        /// <remarks>
        /// Mono returns Unity's generic result directly. IL2CPP resolves the type,
        /// filters any objects that cannot be cast to <typeparamref name="T"/>, and
        /// always normalizes a null native result to an empty managed array.
        /// </remarks>
        public static T[] FindObjectsOfTypeSafe<T>()
            where T : UnityEngine.Object
        {
#if MONO
            return UnityEngine.Object.FindObjectsOfType<T>();
#else
            if (!TryResolveIl2CppType<T>(out var targetType))
                return Array.Empty<T>();

            var objects = UnityEngine.Object.FindObjectsOfType(targetType);
            if (objects == null)
                return Array.Empty<T>();

            var typed = new List<T>(objects.Length);
            for (int i = 0; i < objects.Length; i++)
            {
                var cast = objects[i]?.TryCast<T>();
                if (cast != null)
                    typed.Add(cast);
            }

            return typed.ToArray();
#endif
        }

        /// <summary>
        /// Searches all loaded objects of type <typeparamref name="T"/> and returns the first one matching the given name.
        /// </summary>
        /// <typeparam name="T">The type of UnityEngine.Object to search for (e.g., Sprite, AudioClip).</typeparam>
        /// <param name="objectName">The name of the object to find.</param>
        /// <returns>The first matching object of type <typeparamref name="T"/> if found; otherwise, null.</returns>
        /// <remarks>
        /// The search uses <see cref="Resources.FindObjectsOfTypeAll{T}"/>, so it
        /// includes loaded assets and inactive objects as well as active scene
        /// objects. It does not load assets that are not already resident.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Example usage for finding a Sprite by name
        /// var sprite = FindObjectByName&lt;‌Sprite‌&gt;("Dan_Mugshot");
        /// </code>
        /// </example>
        public static T FindObjectByName<T>(string objectName)
            where T : UnityEngine.Object
        {
            try
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll<T>())
                {
                    if (obj.name != objectName)
                        continue;
                    ModLogger.Debug($"Found {typeof(T).Name} '{objectName}' directly in loaded objects");
                    return obj;
                }

                return null;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error finding {typeof(T).Name} '{objectName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets all components of type <typeparamref name="T"/> in the given GameObject and its children recursively.
        /// </summary>
        /// <param name="obj">The GameObject to search in.</param>
        /// <typeparam name="T">The type of component to search for.</typeparam>
        /// <returns>A list of all components of type <typeparamref name="T"/> found in the GameObject and its children.</returns>
        /// <remarks>
        /// Results are returned in the traversal order produced by Unity: the
        /// supplied object first, followed by each child subtree. A null root
        /// produces an empty list.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Example usage for getting all colliders in a GameObject
        /// List&lt;‌Collider‌&gt; colliders = GetAllComponentsInChildrenRecursive&lt;‌Collider‌&gt;(someGameObject);
        /// </code>
        /// </example>
        public static List<T> GetAllComponentsInChildrenRecursive<T>(GameObject obj)
            where T : Component
        {
            var results = new List<T>();
            if (obj == null)
                return results;

            T[] components = obj.GetComponents<T>();
            if (components.Length > 0)
            {
                results.AddRange(components);
            }

            for (var i = 0; i < obj.transform.childCount; i++)
            {
                var child = obj.transform.GetChild(i);
                results.AddRange(GetAllComponentsInChildrenRecursive<T>(child.gameObject));
            }

            return results;
        }

        /// <summary>
        /// Checks if the given object is of type <typeparamref name="T"/> and casts it to that type.
        /// </summary>
        /// <param name="obj">The object to check.</param>
        /// <param name="result">The cast object if the check is successful; otherwise, null.</param>
        /// <typeparam name="T">The type to check against.</typeparam>
        /// <returns>True if the object is of type <typeparamref name="T"/>; otherwise, false.</returns>
        /// <remarks>
        /// Method adapted from S1API (https://github.com/KaBooMa/S1API/blob/stable/S1API/Internal/Utils/CrossType.cs)
        /// </remarks>
        /// <example>
        /// <code>
        /// // Example usage for checking if an object is of type GameObject
        /// if (Is&lt;‌GameObject‌&gt;(someObject, out GameObject result))
        /// {
        ///     // Do something with result
        /// }
        /// </code>
        /// </example>
        public static bool Is<T>(object obj, out T result)
#if !MONO
            where T : Object
#else
            where T : class
#endif
        {
#if !MONO
            if (obj is Object il2CppObj)
            {
                var targetType = Il2CppType.Of<T>();
                var objType = il2CppObj.GetIl2CppType();

                if (targetType.IsAssignableFrom(objType))
                {
                    result = il2CppObj.TryCast<T>()!;
                    return result != null;
                }
            }
#else
            if (obj is T t)
            {
                result = t;
                return true;
            }
#endif

            result = null!;
            return false;
        }

        /// <summary>
        /// Gets all storable item definitions from the item registry.
        /// </summary>
        /// <returns>A list of all storable item definitions.</returns>
        /// <remarks>
        /// Each registry entry is inspected for a public <c>Definition</c>
        /// property through reflection. Entries without a readable definition are
        /// omitted and logged; the returned values are intentionally typed as
        /// <see cref="object"/> because the registry contains multiple definition
        /// subtypes.
        /// </remarks>
        public static List<object> GetAllStorableItemDefinitions()
        {
#if !MONO
            var itemRegistry = Il2CppListExtensions.ConvertToList(Registry.Instance.ItemRegistry);
#else
            var itemRegistry = Registry.Instance.ItemRegistry.ToList();
#endif
            var itemDefinitions = new List<object>();

            foreach (var item in itemRegistry)
            {
                var definition = GetItemDefinitionObject(item);
                if (definition != null)
                {
                    itemDefinitions.Add(definition);
                }
                else
                {
                    ModLogger.Warn(
                        $"Definition {GetDefinitionTypeName(item)} is not a storable item definition"
                    );
                }
            }

            return itemDefinitions.ToList();
        }

        /// <summary>
        /// Reads an item's public <c>Definition</c> property when available.
        /// </summary>
        /// <param name="item">Registry entry to inspect.</param>
        /// <returns>The property value, or null when the entry/property cannot be read.</returns>
        /// <remarks>
        /// This reflection bridge is intentionally best-effort because registry
        /// entries differ across game/runtime versions; access failures are logged
        /// at debug level and do not abort the registry scan.
        /// </remarks>
        private static object GetItemDefinitionObject(object item)
        {
            if (item == null)
            {
                return null;
            }

            try
            {
                var property = item.GetType().GetProperty("Definition");
                if (property != null)
                {
                    return property.GetValue(item);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Failed to read item definition from {item.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Gets a useful type name for an item or its resolved definition.
        /// </summary>
        /// <param name="item">Registry entry to describe.</param>
        /// <returns>Definition type, item type, or <c>Unknown</c> when neither exists.</returns>
        private static string GetDefinitionTypeName(object item)
        {
            var definition = GetItemDefinitionObject(item);
            return definition?.GetType().FullName ?? item?.GetType().FullName ?? "Unknown";
        }

        /// <summary>
        /// Waits for the local player to exist, then starts the supplied coroutine.
        /// </summary>
        /// <param name="routine">Valid coroutine to start when the player is ready.</param>
        /// <returns>An enumerator that completes after starting the routine.</returns>
        /// <remarks>
        /// The player readiness check is unbounded. Once ready, the routine is
        /// handed to <see cref="MelonCoroutines.Start(IEnumerator)"/> as a separate
        /// coroutine; this enumerator does not wait for the routine to finish and
        /// does not validate a null routine before starting it.
        /// </remarks>
        public static IEnumerator WaitForPlayer(IEnumerator routine)
        {
            ModLogger.Debug("Waiting for player to continue");
            while (Player.Local == null || Player.Local.gameObject == null)
                yield return null;
            ModLogger.Debug("Player found, running routine");
            // player is ready, start the coroutine
            MelonCoroutines.Start(routine);
        }

        /// <summary>
        /// Waits for either the server or client network state to be ready, then
        /// starts the supplied coroutine.
        /// </summary>
        /// <param name="routine">Valid coroutine to start when network is ready.</param>
        /// <returns>An enumerator that completes after starting the routine.</returns>
        /// <remarks>
        /// The readiness check is unbounded and accepts either
        /// <c>InstanceFinder.IsServer</c> or <c>InstanceFinder.IsClient</c>. The
        /// routine runs independently through <see cref="MelonCoroutines.Start(IEnumerator)"/>
        /// and is not awaited by this wrapper.
        /// </remarks>
        public static IEnumerator WaitForNetwork(IEnumerator routine)
        {
            while (InstanceFinder.IsServer == false && InstanceFinder.IsClient == false)
                yield return null;
            // network is ready, start the coroutine
            MelonCoroutines.Start(routine);
        }

        /// <summary>
        /// Waits while the supplied object value is null, up to an optional timeout.
        /// </summary>
        /// <param name="obj">
        /// Object value sampled when the enumerator is created. It is passed by
        /// value and is not re-read if another part of the program later assigns
        /// a reference to the original variable.
        /// </param>
        /// <param name="timeout">Scaled Unity-time seconds to wait; NaN disables the timeout.</param>
        /// <param name="onTimeout">Action to execute when the scaled timeout is exceeded.</param>
        /// <param name="onFinish">Action to execute when the sampled value is non-null.</param>
        /// <returns>An enumerator that waits for the sampled value to be non-null.</returns>
        /// <remarks>
        /// Because <paramref name="obj"/> is by value, passing null cannot observe
        /// a later assignment and will remain null until the timeout (or forever
        /// when timeout is NaN). Elapsed time uses scaled <see cref="Time.time"/>,
        /// not real-time/unscaled time. The callbacks run on the coroutine's
        /// completion path and exceptions from them are not swallowed here.
        /// </remarks>
        public static IEnumerator WaitForNotNull(
            object? obj,
            float timeout = Single.NaN,
            Action onTimeout = null,
            Action onFinish = null
        )
        {
            float startTime = Time.time;

            while (obj == null)
            {
                if (!float.IsNaN(timeout) && Time.time - startTime > timeout)
                {
                    onTimeout?.Invoke();
                    yield break;
                }

                yield return null;
            }
            onFinish?.Invoke();
        }

        /// <summary>
        /// Waits for a <see cref="NetworkSingleton{T}"/> to exist, then yields the
        /// supplied coroutine.
        /// </summary>
        /// <typeparam name="T">The type of the NetworkSingleton.</typeparam>
        /// <param name="coroutine">Coroutine to run after the singleton is ready.</param>
        /// <returns>An enumerator that waits for and then yields the nested coroutine.</returns>
        /// <remarks>
        /// This helper has no timeout and does not call
        /// <see cref="MelonCoroutines.Start(IEnumerator)"/>. The nested coroutine
        /// is yielded directly, so this enumerator remains active until that
        /// nested work completes; null input follows Unity coroutine behavior.
        /// </remarks>
        public static IEnumerator WaitForNetworkSingleton<T>(IEnumerator coroutine)
            where T : NetworkSingleton<T>
        {
            while (!NetworkSingleton<T>.InstanceExists)
                yield return null;

            yield return coroutine;
        }
    }
}
