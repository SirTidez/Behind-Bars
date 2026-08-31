using MelonLoader;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Behind_Bars.Helpers;

namespace Behind_Bars.Utils
{
#if MONO
    using RuntimeAssetBundle = UnityEngine.AssetBundle;
#else
    using RuntimeAssetBundle = UnityEngine.Il2CppAssetBundle;
#endif

    public static class AssetBundleUtils
    {
        /// <summary>
        /// The active mod instance used for embedded-resource access and fatal
        /// asset lookup diagnostics. It is resolved once when this type is
        /// initialized, so using the helper before <see cref="Core"/> exists
        /// can make the static initialization unavailable.
        /// </summary>
        private static readonly Core mod = MelonAssembly.FindMelonInstance<Core>();

        /// <summary>
        /// Assembly wrapper used to open embedded AssetBundle resources. This
        /// is also resolved once at type initialization and assumes that the
        /// owning <see cref="Core"/> instance has a valid assembly reference.
        /// </summary>
        private static readonly MelonAssembly melonAssembly = mod.MelonAssembly;

        /// <summary>
        /// Maps an asset-name flag to the loaded bundle that contains it. The
        /// key is an asset name, not an embedded bundle filename; a stale null
        /// entry is removed when it is encountered.
        /// </summary>
        private static Dictionary<string, RuntimeAssetBundle> _bundleCache = new Dictionary<string, RuntimeAssetBundle>();

        /// <summary>
        /// Keeps each embedded bundle alive for the gameplay session. The key
        /// is the exact embedded resource filename and the value is never
        /// explicitly unloaded by this helper; clearing the dictionary only
        /// releases this helper's reference.
        /// </summary>
        /// <remarks>
        /// Re-reading the same payload for every interaction creates competing
        /// IL2CPP load calls and can leave a later attempt with an invalid
        /// bundle, so this process-wide cache is intentionally retained.
        /// </remarks>
        private static readonly Dictionary<string, RuntimeAssetBundle> loadedResourceBundles = new();

        /// <summary>
        /// Loads an embedded AssetBundle using the requested resource filename.
        /// </summary>
        /// <param name="bundleFileName">The exact manifest-resource name of the embedded bundle.</param>
        /// <returns>The cached or newly loaded bundle, or <c>null</c> when the resource or load is unavailable.</returns>
        /// <remarks>
        /// Successful loads are cached by <paramref name="bundleFileName"/>.
        /// Mono loads from memory; IL2CPP first uses a unique temporary file and
        /// falls back to its memory wrapper when that load returns null. A
        /// temporary file may remain after an IL2CPP failure because cleanup is
        /// only attempted after a successful load.
        /// </remarks>
        public static RuntimeAssetBundle LoadAssetBundle(string bundleFileName)
        {
            Stream? bundleStream = null;
            string tempFilePath = null;

            try
            {
                if (loadedResourceBundles.TryGetValue(bundleFileName, out RuntimeAssetBundle cachedBundle) &&
                    cachedBundle != null)
                {
                    return cachedBundle;
                }

                RuntimeAssetBundle bundle = null;
                string streamPath = bundleFileName;
                bundleStream = melonAssembly.Assembly.GetManifestResourceStream($"{streamPath}");
                if (bundleStream == null)
                {
                    ModLogger.Error($"AssetBundle resource '{streamPath}' was not found in the mod assembly");
                    return null;
                }
                byte[] bundleData;
                using (MemoryStream ms = new())
                {
                    bundleStream.CopyTo(ms);
                    bundleData = ms.ToArray();
                }

                // Dispose manifest stream after reading - frees ~1-2 MB
                bundleStream.Dispose();
                bundleStream = null;

#if !MONO
                // IL2CPP: use Il2CppAssetBundleManager wrapper to avoid AssetBundle wrapper init issues.
                // A unique filename prevents two quick interactions from
                // racing over one shared file while IL2CPP still owns it.
                tempFilePath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"bb_{System.Guid.NewGuid():N}_{System.IO.Path.GetFileName(bundleFileName)}.bundle");
                System.IO.File.WriteAllBytes(tempFilePath, bundleData);
                bundle = Il2CppAssetBundleManager.LoadFromFile(tempFilePath);

                if (bundle == null)
                {
                    ModLogger.Warn($"IL2CPP LoadFromFile returned null for '{bundleFileName}', retrying with LoadFromMemory");
                    var il2CppData = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(bundleData.Length);
                    for (int i = 0; i < bundleData.Length; i++)
                    {
                        il2CppData[i] = bundleData[i];
                    }

                    bundle = Il2CppAssetBundleManager.LoadFromMemory(il2CppData);
                }
#else
                bundle = RuntimeAssetBundle.LoadFromMemory(bundleData);
#endif

                if (bundle == null)
                {
                    ModLogger.Error($"AssetBundle load returned null for '{bundleFileName}'. The requesting feature will remain unavailable.");
                    return null;
                }

                loadedResourceBundles[bundleFileName] = bundle;

                // Clean up temp file after successful load
                if (tempFilePath != null && System.IO.File.Exists(tempFilePath)) {
                    try {
                        System.IO.File.Delete(tempFilePath);
                    } catch {}
                }

                return bundle;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Failed to load AssetBundle '{bundleFileName}': {e}");
                return null;
            }
            finally
            {
                // Safety cleanup - dispose streams even on error
                bundleStream?.Dispose();
            }
        }

        /// <summary>
        /// Finds the loaded bundle containing an asset and caches that mapping.
        /// </summary>
        /// <param name="asset_name_flag">The exact asset path/name to search for.</param>
        /// <returns>The containing loaded bundle, or <c>null</c> when no bundle contains the asset or lookup fails.</returns>
        /// <remarks>
        /// A cache hit is O(1); a miss scans all currently loaded bundles. The
        /// current missing-asset path logs through <see cref="Core.Unregister"/>
        /// and returns null, so a lookup failure has a mod-lifecycle side effect.
        /// An exception while obtaining the loaded-bundle list occurs before
        /// the guarded search and can therefore escape this method.
        /// </remarks>
        public static RuntimeAssetBundle GetLoadedAssetBundle(string asset_name_flag)
        {
            // Check cache first - O(1) lookup
            if (_bundleCache.TryGetValue(asset_name_flag, out RuntimeAssetBundle cachedBundle))
            {
                // Verify bundle is still valid (not unloaded)
                if (cachedBundle != null)
                {
                    return cachedBundle;
                }
                else
                {
                    // Bundle was unloaded, remove from cache
                    _bundleCache.Remove(asset_name_flag);
                }
            }

            // Cache miss - search through loaded bundles (existing logic)
#if !MONO
            RuntimeAssetBundle[] loadedBundles = Il2CppAssetBundleManager.GetAllLoadedAssetBundles();
#else
            RuntimeAssetBundle[] loadedBundles = RuntimeAssetBundle.GetAllLoadedAssetBundles().ToArray();
#endif
            try
            {
                foreach (var bundle in loadedBundles)
                {
                    if (bundle.Contains(asset_name_flag))
                    {
                        // Add to cache for future lookups
                        _bundleCache[asset_name_flag] = bundle;
                        return bundle;
                    }
                }
                string assetNames = "";
#if MONO
                foreach (var bundle in loadedBundles)
                {
                    string[] bundleAssetNames = bundle.GetAllAssetNames();
                    string bundleAssetNamesString = string.Join("\n\r -", bundleAssetNames);
                    assetNames +=
                        bundle.name
                        +$"({bundleAssetNames.Length} assets):" + bundleAssetNamesString;
                }
#else
                for (int i = 0; i < loadedBundles.Length; i++)
                {
                    var bundle = loadedBundles[i];
                    var bundleAssetNames = bundle.GetAllAssetNames();
                    var readableNames = new List<string>();
                    if (bundleAssetNames != null)
                    {
                        for (int j = 0; j < bundleAssetNames.Length; j++)
                        {
                            readableNames.Add(bundleAssetNames[j]?.ToString() ?? "<null>");
                        }
                    }

                    string bundleAssetNamesString = string.Join("\n\r -", readableNames);
                    assetNames += $"bundle[{i}] ({readableNames.Count} assets):" + bundleAssetNamesString;
                }
#endif
                throw new Exception($"Asset '{asset_name_flag}' not found in {loadedBundles.Length} bundle(s).\n{assetNames}");
            }
            catch (Exception e)
            {
                mod.Unregister($"Failed to get loaded AssetBundle. Please report to dev: \n{e}");
                return null;
            }

        }

        /// <summary>
        /// Loads a <see cref="GameObject"/> using an exact asset path.
        /// </summary>
        /// <param name="asset_name">The exact asset path/name used by the bundle.</param>
        /// <returns>The loaded object, or <c>null</c> when no containing bundle is found.</returns>
        /// <remarks>
        /// This method does not perform the type-scanning fallback used by the
        /// animation and audio helpers. Errors raised by the underlying bundle
        /// load are not caught here.
        /// </remarks>
        public static GameObject LoadAssetFromBundle(string asset_name)
        {
            var bundle = GetLoadedAssetBundle(asset_name);
            if (bundle == null) {
                ModLogger.Error($"AssetBundle not found for asset '{asset_name}'");
                return null;
            }
#if MONO
            return bundle.LoadAsset<GameObject>(asset_name);
#else
            return bundle.LoadAsset(asset_name, Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
        }

        /// <summary>
        /// Loads the scanner's animation-only bundle without relying on a generic
        /// AssetBundle wrapper surface on IL2CPP.
        /// </summary>
        /// <param name="bundleFileName">The exact embedded bundle resource name.</param>
        /// <param name="assetName">The preferred exact animation asset path.</param>
        /// <returns>The exact or first type-matching animation clip, or <c>null</c> if none can be loaded.</returns>
        /// <remarks>
        /// If the exact path is unavailable, every asset name is tried as an
        /// <see cref="AnimationClip"/> and the first successful match is used.
        /// The embedded bundle remains held in the process-wide bundle cache.
        /// </remarks>
        public static AnimationClip LoadAnimationClipFromBundle(string bundleFileName, string assetName)
        {
            RuntimeAssetBundle bundle = LoadAssetBundle(bundleFileName);
            if (bundle == null)
            {
                return null;
            }

#if MONO
            AnimationClip clip = bundle.LoadAsset<AnimationClip>(assetName);
#else
            AnimationClip clip = bundle.LoadAsset(assetName, Il2CppInterop.Runtime.Il2CppType.Of<AnimationClip>())?.TryCast<AnimationClip>();
#endif
            if (clip != null)
            {
                return clip;
            }

            // AssetBundle paths are generated by Unity and can differ only in
            // case or directory layout between authoring projects. Resolve the
            // only AnimationClip in this dedicated pose bundle by type before
            // declaring the scanner pose unavailable.
            var assetNames = bundle.GetAllAssetNames();
            var readableNames = new List<string>();
            if (assetNames != null)
            {
                for (int index = 0; index < assetNames.Length; index++)
                {
                    string candidateName = assetNames[index]?.ToString() ?? string.Empty;
                    readableNames.Add(candidateName);
                    if (string.IsNullOrWhiteSpace(candidateName))
                    {
                        continue;
                    }

#if MONO
                    clip = bundle.LoadAsset<AnimationClip>(candidateName);
#else
                    clip = bundle.LoadAsset(candidateName, Il2CppInterop.Runtime.Il2CppType.Of<AnimationClip>())?.TryCast<AnimationClip>();
#endif
                    if (clip != null)
                    {
                        ModLogger.Warn($"Resolved animation clip '{candidateName}' from '{bundleFileName}' after exact path '{assetName}' was unavailable");
                        return clip;
                    }
                }
            }

            ModLogger.Error($"AssetBundle '{bundleFileName}' did not provide AnimationClip '{assetName}'. Available assets: {string.Join(", ", readableNames)}");
            return null;
        }

        /// <summary>
        /// Loads a single audio clip from a dedicated embedded bundle.  This
        /// follows the same IL2CPP-safe non-generic asset surface as the
        /// scanner animation loader while keeping audio assets independent of
        /// the jail geometry bundle.
        /// </summary>
        /// <param name="bundleFileName">The exact embedded bundle resource name.</param>
        /// <param name="assetName">The preferred exact audio asset path.</param>
        /// <returns>The exact or first type-matching audio clip, or <c>null</c> if none can be loaded.</returns>
        /// <remarks>
        /// If the exact path is unavailable, every asset name is tried as an
        /// <see cref="AudioClip"/> and the first successful match is used. The
        /// embedded bundle remains held in the process-wide bundle cache.
        /// </remarks>
        public static AudioClip LoadAudioClipFromBundle(string bundleFileName, string assetName)
        {
            RuntimeAssetBundle bundle = LoadAssetBundle(bundleFileName);
            if (bundle == null)
            {
                return null;
            }

#if MONO
            AudioClip clip = bundle.LoadAsset<AudioClip>(assetName);
#else
            AudioClip clip = bundle.LoadAsset(assetName, Il2CppInterop.Runtime.Il2CppType.Of<AudioClip>())?.TryCast<AudioClip>();
#endif
            if (clip != null)
            {
                return clip;
            }

            var assetNames = bundle.GetAllAssetNames();
            var readableNames = new List<string>();
            if (assetNames != null)
            {
                for (int index = 0; index < assetNames.Length; index++)
                {
                    string candidateName = assetNames[index]?.ToString() ?? string.Empty;
                    readableNames.Add(candidateName);
                    if (string.IsNullOrWhiteSpace(candidateName))
                    {
                        continue;
                    }

#if MONO
                    clip = bundle.LoadAsset<AudioClip>(candidateName);
#else
                    clip = bundle.LoadAsset(candidateName, Il2CppInterop.Runtime.Il2CppType.Of<AudioClip>())?.TryCast<AudioClip>();
#endif
                    if (clip != null)
                    {
                        ModLogger.Warn($"Resolved audio clip '{candidateName}' from '{bundleFileName}' after exact path '{assetName}' was unavailable");
                        return clip;
                    }
                }
            }

            ModLogger.Error($"AssetBundle '{bundleFileName}' did not provide AudioClip '{assetName}'. Available assets: {string.Join(", ", readableNames)}");
            return null;
        }

        /// <summary>
        /// Drops cached bundle references maintained by this helper.
        /// </summary>
        /// <param name="bundleFileName">Optional exact embedded bundle filename; <c>null</c> clears both caches.</param>
        /// <remarks>
        /// This method does not call the Unity/IL2CPP unload API, so loaded
        /// assets are not destroyed here. The all-cache path clears the asset
        /// and embedded-bundle dictionaries. The specific path removes the
        /// embedded-bundle entry by filename but also attempts to remove an
        /// asset-cache entry using that same filename, even though asset-cache
        /// keys are normally asset names; callers should not assume that all
        /// mappings for the bundle are removed.
        /// </remarks>
        public static void ClearBundleCache(string bundleFileName = null)
        {
            if (bundleFileName == null)
            {
                _bundleCache.Clear();
                loadedResourceBundles.Clear();
                ModLogger.Debug("Cleared entire asset bundle cache");
            }
            else
            {
                loadedResourceBundles.Remove(bundleFileName);
                if (_bundleCache.Remove(bundleFileName))
                {
                    ModLogger.Debug($"Removed {bundleFileName} from asset bundle cache");
                }
            }
        }
    }
}
