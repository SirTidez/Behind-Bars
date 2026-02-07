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

        public static RuntimeAssetBundle LoadAssetBundle(string bundleFileName)
        {
            Stream? bundleStream = null;
            string tempFilePath = null;

            try
            {
                RuntimeAssetBundle bundle = null;
                string streamPath = bundleFileName;
                bundleStream = melonAssembly.Assembly.GetManifestResourceStream($"{streamPath}");
                if (bundleStream == null)
                {
                    mod.Unregister($"AssetBundle: '{streamPath}' not found. \nOpen .csproj file and search for '{bundleFileName}'.\nIf it doesn't exist,\nCopy your asset to Assets/ folder then look for 'your.assetbundle' in .csproj file.");
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
                tempFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bb_{bundleFileName}");
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
                    mod.Unregister($"AssetBundle load returned null for '{bundleFileName}'.");
                    return null;
                }

                return bundle;
            }
            catch (Exception e)
            {
                mod.Unregister($"Failed to load AssetBundle. Please report to dev: {e}");
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
#if MONO
            return bundle.LoadAsset<GameObject>(asset_name);
#else
            return bundle.LoadAsset(asset_name, Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
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
                ModLogger.Debug("Cleared entire asset bundle cache");
            }
            else
            {
                if (_bundleCache.Remove(bundleFileName))
                {
                    ModLogger.Debug($"Removed {bundleFileName} from asset bundle cache");
                }
            }
        }
    }
}
