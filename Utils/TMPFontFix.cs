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
    /// <summary>
    /// Resolves and applies TextMeshPro font/material pairs across runtime UI.
    /// </summary>
    /// <remarks>
    /// The caches are process-wide and keyed by caller-provided strings. There
    /// is no invalidation API, so callers should choose stable keys and should
    /// expect a cached Unity object to remain referenced until the process ends.
    /// </remarks>
    public static class TMPFontFix
    {
        /// <summary>
        /// Cached fonts keyed by the caller's logical font key.
        /// </summary>
        private static readonly Dictionary<string, TMP_FontAsset> _fonts = new();

        /// <summary>
        /// Cached materials paired with <see cref="_fonts"/> entries.
        /// </summary>
        private static readonly Dictionary<string, Material> _mats = new();

        /// <summary>
        /// Retained diagnostic flag for a missing base font; it is currently
        /// not read by the implementation.
        /// </summary>
        private static bool _loggedMissingBaseFont;

        /// <summary>
        /// Returns all the keys of fonts you've cached so far.
        /// </summary>
        /// <returns>A live dictionary key view; it is not a snapshot and changes
        /// as the cache is modified.</returns>
        public static IEnumerable<string> GetCachedFontKeys() => _fonts.Keys;

        /// <summary>
        /// Explicitly cache a TMP_FontAsset under a custom key.
        /// If the key already exists, it will be overwritten.
        /// </summary>
        /// <param name="key">The logical key used by later resolve/apply calls.</param>
        /// <param name="asset">The font asset to cache.</param>
        /// <remarks>
        /// The material is resolved before the explicit-pair overload runs. A
        /// null key or asset causes the current dictionary/argument behavior to
        /// throw. If no material resolves, the font is still cached with a null
        /// material and can be repaired by a later resolve/fallback call.
        /// </remarks>
        public static void CacheFont(string key, TMP_FontAsset asset)
        {
            CacheFont(key, asset, ResolveMaterialForFont(asset));
        }

        /// <summary>
        /// Cache a TMP font with an explicit material, preserving the exact pair that is known-good in the scene.
        /// </summary>
        /// <param name="key">The logical key used by later resolve/apply calls.</param>
        /// <param name="asset">The font asset to cache.</param>
        /// <param name="material">The material to pair with the font, or <c>null</c> to resolve one.</param>
        /// <remarks>
        /// Both dictionaries are overwritten independently. The font is
        /// validated first; a null material falls back to the font/scene lookup
        /// and remains null if no material is available.
        /// </remarks>
        public static void CacheFont(string key, TMP_FontAsset asset, Material material)
        {
            _fonts[key] = asset ?? throw new ArgumentNullException(nameof(asset));
            _mats[key] = material ?? ResolveMaterialForFont(asset);

            ModLogger.Debug($"Cached font '{asset.name}' as key '{key}'");
        }

        /// <summary>
        /// Convenience: cache under the asset's own name.
        /// </summary>
        /// <param name="asset">The font asset whose name becomes the cache key.</param>
        /// <remarks>A null asset is dereferenced to obtain its name before the
        /// delegated validation can run.</remarks>
        public static void CacheFont(TMP_FontAsset asset)
            => CacheFont(asset.name, asset);

        /// <summary>
        /// Auto-finds a font in the scene whose name contains <paramref name="namePart"/> 
        /// (case-insensitive), and caches it under that same namePart key.
        /// </summary>
        /// <param name="key">The key under which the first matching asset is cached.</param>
        /// <param name="namePart">Case-insensitive substring to search for.</param>
        /// <returns><c>true</c> when a matching asset was found and cached;
        /// otherwise <c>false</c>.</returns>
        /// <remarks>The search includes all loaded/in-memory assets, not just
        /// active scene objects. A null search string currently propagates from
        /// <see cref="string.IndexOf(string, StringComparison)"/>.</remarks>
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
        /// <param name="root">The hierarchy whose active and inactive TMP UI children are updated.</param>
        /// <param name="key">The cached/fallback font key.</param>
        /// <remarks>A null root is a no-op. Resolution may populate the cache
        /// through the fallback order documented by <see cref="EnsureFontCached"/>;
        /// each resolved text is marked dirty after its font and materials are
        /// changed.</remarks>
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
        /// <param name="text">The TMP UI component to update.</param>
        /// <param name="key">The cached/fallback font key.</param>
        /// <remarks>A null text is a no-op. If no complete pair can be resolved,
        /// the component is left unchanged and a debug message is emitted.</remarks>
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
        /// <param name="preferredCanvas">Optional canvas whose first TMP child is preferred as a sample.</param>
        /// <param name="key">The cache key to populate.</param>
        /// <returns><c>true</c> when a complete font/material pair is available;
        /// otherwise <c>false</c>.</returns>
        /// <remarks>Resolution order is: an existing valid pair, the first TMP
        /// child under <paramref name="preferredCanvas"/>, the TMP default font,
        /// named fallback assets (<c>OpenSans-Regular</c>, <c>ComicNeue</c>,
        /// <c>LiberationSans</c>), then any loaded TMP text with a material. This
        /// method itself does not log an error when all choices fail.</remarks>
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
        /// <returns>All loaded/in-memory TMP font assets returned by Unity's
        /// resource search, including assets not present in the active scene.</returns>
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
        /// <param name="nameContains">The case-insensitive substring to search for.</param>
        /// <returns>The first matching loaded font, or <c>null</c> when none matches.</returns>
        /// <remarks>This only searches and logs; it does not add the result to
        /// the cache. A null search string currently propagates from the
        /// lowercase conversion.</remarks>
        public static TMP_FontAsset FindFont(string nameContains)
        {
            return ListAllGameFonts()
                .FirstOrDefault(
                    f => f.name.ToLower().Contains(nameContains.ToLower())
                );
        }

        /// <summary>
        /// Applies an already-resolved font/material pair to one TMP component.
        /// </summary>
        /// <param name="text">The component to mutate.</param>
        /// <param name="key">The logical key, retained for the shared helper signature.</param>
        /// <param name="font">The resolved font asset.</param>
        /// <param name="mat">The resolved shared/material instance.</param>
        /// <remarks>Null inputs are ignored. A successful application mutates
        /// both shared and instance material properties and marks the component
        /// dirty for a UI rebuild.</remarks>
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

        /// <summary>
        /// Resolves a complete cached pair, repairing a missing material or
        /// populating the key through the fallback search when necessary.
        /// </summary>
        /// <param name="key">The logical cache key.</param>
        /// <param name="font">The resolved font, or <c>null</c> on failure.</param>
        /// <param name="mat">The resolved material, or <c>null</c> on failure.</param>
        /// <returns><c>true</c> only when both output values are non-null.</returns>
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

        /// <summary>
        /// Attempts to populate a key using the current font fallback order.
        /// </summary>
        /// <param name="key">The logical cache key to populate.</param>
        /// <returns><c>true</c> when a font candidate was cached or already
        /// exists, even if an existing cached font still lacks a material.</returns>
        /// <remarks>The order is existing non-null font, TMP default with a
        /// resolvable material, named resources, then the first loaded TMP text
        /// with a usable material. This helper does not log a missing-font error.</remarks>
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

        /// <summary>
        /// Resolves a material directly from a font, then from a loaded TMP text
        /// using that same font.
        /// </summary>
        /// <param name="font">The font whose material should be resolved.</param>
        /// <returns>The font's material or a matching text material, or
        /// <c>null</c> when none is available.</returns>
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
