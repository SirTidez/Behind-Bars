using UnityEngine;
using Behind_Bars.Helpers;
using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine.UI;

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
        private static bool _loggedMissingBaseFont;

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
            CacheFont(key, asset, ResolveMaterialForFont(asset));
        }

        /// <summary>
        /// Cache a TMP font with an explicit material, preserving the exact pair that is known-good in the scene.
        /// </summary>
        public static void CacheFont(string key, TMP_FontAsset asset, Material material)
        {
            _fonts[key] = asset ?? throw new ArgumentNullException(nameof(asset));
            _mats[key] = material ?? ResolveMaterialForFont(asset);

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
            if (root == null)
            {
                return;
            }

            if (!TryResolveFontAndMaterial(key, out var font, out var mat))
            {
                ModLogger.Debug($"No font cached under key '{key}'.");
                return;
            }

            var texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var t in texts)
            {
                ApplySafeFont(t, key, font, mat);
            }
            ModLogger.Debug($"Applied font '{font.name}' to {texts.Length} texts under '{root.name}'");
        }

        /// <summary>
        /// Apply a safe font/material pair to one TMP text component, lazily resolving the base font if needed.
        /// </summary>
        public static void ApplySafeFont(TextMeshProUGUI text, string key = "base")
        {
            if (text == null)
            {
                return;
            }

            if (TryResolveFontAndMaterial(key, out var font, out var mat))
            {
                ApplySafeFont(text, key, font, mat);
                return;
            }

            ModLogger.Debug($"TMPFontFix: Skipping font apply for '{text.name}' because no valid font/material pair was resolved for key '{key}'");
        }

        /// <summary>
        /// Ensure a usable font/material pair is cached, preferring an existing text under the provided canvas.
        /// </summary>
        public static bool EnsureFontCached(Canvas preferredCanvas = null, string key = "base")
        {
            if (TryResolveFontAndMaterial(key, out _, out _))
            {
                return true;
            }

            if (preferredCanvas != null)
            {
                var sampleText = preferredCanvas.GetComponentInChildren<TextMeshProUGUI>(true);
                if (sampleText != null && sampleText.font != null)
                {
                    var material = sampleText.fontSharedMaterial ?? sampleText.fontMaterial ?? sampleText.font.material;
                    if (material != null)
                    {
                        CacheFont(key, sampleText.font, material);
                        return true;
                    }
                }
            }

            return TryCacheFallbackFont(key) && TryResolveFontAndMaterial(key, out _, out _);
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

        private static void ApplySafeFont(TextMeshProUGUI text, string key, TMP_FontAsset font, Material mat)
        {
            if (text == null || font == null || mat == null)
            {
                return;
            }

            text.font = font;
            text.fontSharedMaterial = mat;
            text.fontMaterial = mat;

            text.havePropertiesChanged = true;
            text.SetAllDirty();
        }

        private static bool TryResolveFontAndMaterial(string key, out TMP_FontAsset font, out Material mat)
        {
            if (_fonts.TryGetValue(key, out font))
            {
                if (!_mats.TryGetValue(key, out mat) || mat == null)
                {
                    mat = ResolveMaterialForFont(font);
                    if (mat != null)
                    {
                        _mats[key] = mat;
                    }
                }

                if (font != null && mat != null)
                {
                    return true;
                }
            }

            if (TryCacheFallbackFont(key))
            {
                font = _fonts[key];
                mat = _mats.TryGetValue(key, out var cachedMat) ? cachedMat : ResolveMaterialForFont(font);
                if (mat != null)
                {
                    _mats[key] = mat;
                }

                return font != null && mat != null;
            }

            font = null;
            mat = null;
            return false;
        }

        private static bool TryCacheFallbackFont(string key)
        {
            if (_fonts.TryGetValue(key, out var cachedFont) && cachedFont != null)
            {
                return true;
            }

            if (TMP_Settings.defaultFontAsset != null)
            {
                var defaultMat = ResolveMaterialForFont(TMP_Settings.defaultFontAsset);
                if (defaultMat != null)
                {
                    CacheFont(key, TMP_Settings.defaultFontAsset, defaultMat);
                    return true;
                }
            }

            if (CacheFont(key, "OpenSans-Regular") ||
                CacheFont(key, "ComicNeue") ||
                CacheFont(key, "LiberationSans"))
            {
                return true;
            }

            var sceneText = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                .FirstOrDefault(t => t != null &&
                                     t.font != null &&
                                     (t.fontSharedMaterial != null || t.fontMaterial != null || t.font.material != null));
            if (sceneText != null && sceneText.font != null)
            {
                CacheFont(key, sceneText.font, sceneText.fontSharedMaterial ?? sceneText.fontMaterial ?? sceneText.font.material);
                return true;
            }

            return false;
        }

        private static Material ResolveMaterialForFont(TMP_FontAsset font)
        {
            if (font == null)
            {
                return null;
            }

            if (font.material != null)
            {
                return font.material;
            }

            var sceneText = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                .FirstOrDefault(t => t != null &&
                                     t.font == font &&
                                     (t.fontSharedMaterial != null || t.fontMaterial != null));

            if (sceneText != null)
            {
                return sceneText.fontSharedMaterial ?? sceneText.fontMaterial;
            }

            return null;
        }
    }
}
