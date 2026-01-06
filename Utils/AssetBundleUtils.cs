using MelonLoader;
using UnityEngine;
using System.Collections.Generic;
using Behind_Bars.Helpers;

namespace Behind_Bars.Utils
{
    public static class AssetBundleUtils
    {
        private static readonly Core mod = MelonAssembly.FindMelonInstance<Core>();
        private static readonly MelonAssembly melonAssembly = mod.MelonAssembly;
        
        // Asset bundle cache for O(1) lookups instead of O(n) searches
#if !MONO
        private static Dictionary<string, Il2CppAssetBundle> _bundleCache = new Dictionary<string, Il2CppAssetBundle>();
#else
        private static Dictionary<string, AssetBundle> _bundleCache = new Dictionary<string, AssetBundle>();
#endif

        public static
#if !MONO
            Il2CppAssetBundle
#elif MONO
            AssetBundle
#endif
            LoadAssetBundle(string bundleFileName)
        {
            Stream? bundleStream = null;
#if !MONO
            Il2CppSystem.IO.MemoryStream? il2cppStream = null;
#endif

            try
            {
                AssetBundle bundle = null;
                string streamPath = bundleFileName;
                bundleStream = melonAssembly.Assembly.GetManifestResourceStream($"{streamPath}");
                if (bundleStream == null)
                {
                    mod.Unregister($"AssetBundle: '{streamPath}' not found. \nOpen .csproj file and search for '{bundleFileName}'.\nIf it doesn't exist,\nCopy your asset to Assets/ folder then look for 'your.assetbundle' in .csproj file.");
                    return null;
                }
#if !MONO
                byte[] bundleData;
                using (MemoryStream ms = new())
                {
                    bundleStream.CopyTo(ms);
                    bundleData = ms.ToArray();
                }

                // Dispose manifest stream after reading - frees ~1-2 MB
                bundleStream.Dispose();
                bundleStream = null;

                il2cppStream = new Il2CppSystem.IO.MemoryStream(bundleData);
                bundle = Il2CppAssetBundle.LoadFromStream(il2cppStream);

                // Dispose IL2CPP stream after bundle loads - frees ~15-25 MB
                il2cppStream.Dispose();
                il2cppStream = null;

                return bundle;
#elif MONO
                bundle = AssetBundle.LoadFromStream(bundleStream);
                bundleStream.Close();
                bundleStream = null;
                return bundle;
#endif
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
#if !MONO
                il2cppStream?.Dispose();
#endif
            }
        }

        public static
#if !MONO
            Il2CppAssetBundle
#elif MONO
            AssetBundle
#endif
            GetLoadedAssetBundle(string asset_name_flag)
        {
            // Check cache first - O(1) lookup
#if !MONO
            if (_bundleCache.TryGetValue(asset_name_flag, out Il2CppAssetBundle cachedBundle))
#else
            if (_bundleCache.TryGetValue(asset_name_flag, out AssetBundle cachedBundle))
#endif
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

            Il2CppAssetBundle[] loadedBundles = Il2CppAssetBundleManager.GetAllLoadedAssetBundles();
#elif MONO
            AssetBundle[] loadedBundles = AssetBundle.GetAllLoadedAssetBundles().ToArray();
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
                foreach (var bundle in loadedBundles)
                {
                    string[] bundleAssetNames = bundle.GetAllAssetNames();
                    string bundleAssetNamesString = string.Join("\n\r -", bundleAssetNames);
                    assetNames +=
#if !MONO
                        bundle
#elif MONO
                        bundle.name
#endif
                        +$"({bundleAssetNames.Length} assets):" + bundleAssetNamesString;
                }
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
            return bundle.LoadAsset<GameObject>(asset_name);
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