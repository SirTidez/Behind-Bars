using UnityEngine;
using Behind_Bars.Helpers;
using System.Collections.Generic;
using System.Linq;
using System;

#if MONO
using TMPro;
#else
using Il2CppTMPro;
#endif

namespace Behind_Bars.Utils
{
    public static class TMPFontFix
    {
        // Internal caches
        private static readonly Dictionary<string, TMP_FontAsset> _fonts = new();
        private static readonly Dictionary<string, Material> _mats = new();

        /// <summary>
        /// Returns all the keys of fonts you've cached so far.
        /// </summary>
        public static IEnumerable<string> GetCachedFontKeys() => _fonts.Keys;

        /// <summary>
        /// Explicitly cache a TMP_FontAsset under a custom key.
        /// If the key already exists, it will be overwritten.
        /// </summary>
        public static void CacheFont(string key, TMP_FontAsset asset)
        {
            _fonts[key] = asset ?? throw new ArgumentNullException(nameof(asset));
            _mats[key] = asset.material;

            ModLogger.Debug($"Cached font '{asset.name}' as key '{key}'");
        }

        /// <summary>
        /// Convenience: cache under the asset's own name.
        /// </summary>
        public static void CacheFont(TMP_FontAsset asset)
            => CacheFont(asset.name, asset);

        /// <summary>
        /// Auto-finds a font in the scene whose name contains <paramref name="namePart"/> 
        /// (case-insensitive), and caches it under that same namePart key.
        /// </summary>
        public static bool CacheFont(string key, string namePart)
        {
            var found = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                                 .FirstOrDefault(f => f.name
                                    .IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0);
            if (found != null)
            {
                CacheFont(key, found);
                return true;
            }
            ModLogger.Debug($"No TMP_FontAsset with '{namePart}' in its name found.");
            return false;
        }

        /// <summary>
        /// Applies the cached font/material identified by <paramref name="key"/>
        /// to every TextMeshProUGUI under <paramref name="root"/>. 
        /// </summary>
        public static void FixAllTMPFonts(GameObject root, string key = "base")
        {
            if (!_fonts.TryGetValue(key, out var font) || !_mats.TryGetValue(key, out var mat))
            {
                ModLogger.Debug($"No font cached under key '{key}'.");
                return;
            }

            var texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var t in texts)
            {
                t.font = font;
                t.fontMaterial = mat;
                t.havePropertiesChanged = true;
                t.SetAllDirty();
                t.ForceMeshUpdate();
            }
            ModLogger.Debug($"Applied font '{font.name}' to {texts.Length} texts under '{root.name}'");
        }

        /// <summary>
        /// Finds and logs all TextMeshPro FontAssets currently loaded in the game.
        /// </summary>
        public static TMP_FontAsset[] ListAllGameFonts()
        {
            // This will return every TMP_FontAsset in memory (even those not in a scene)
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

            ModLogger.Debug($"Found {fonts.Length} TMP_FontAsset(s):");
            foreach (var f in fonts.OrderBy(f => f.name))
                ModLogger.Debug($"  • {f.name}");

            return fonts;
        }

        /// <summary>
        /// Find a font by name (case-insensitive substring) or null if not found.
        /// </summary>
        public static TMP_FontAsset FindFont(string nameContains)
        {
            return ListAllGameFonts()
                .FirstOrDefault(
                    f => f.name.ToLower().Contains(nameContains.ToLower())
                );
        }
    }
}