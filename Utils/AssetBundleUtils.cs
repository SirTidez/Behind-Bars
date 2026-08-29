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
        private static readonly Core mod = MelonAssembly.FindMelonInstance<Core>();
        private static readonly MelonAssembly melonAssembly = mod.MelonAssembly;

        // Asset bundle cache for O(1) lookups instead of O(n) searches
        private static Dictionary<string, RuntimeAssetBundle> _bundleCache = new Dictionary<string, RuntimeAssetBundle>();

        // Keep each embedded bundle alive for the lifetime of the gameplay
        // session. Re-reading the same payload for every interaction creates
        // competing IL2CPP LoadFromFile calls against one fixed temporary
        // filename, which can leave a later attempt with an invalid bundle.
        private static readonly Dictionary<string, RuntimeAssetBundle> loadedResourceBundles = new();

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
        /// Clear the asset bundle cache (call when unloading bundles)
        /// </summary>
        /// <param name="bundleFileName">Optional: Clear specific bundle, or null to clear all</param>
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
