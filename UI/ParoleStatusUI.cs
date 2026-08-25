using UnityEngine;
using UnityEngine.UI;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems.CrimeTracking;
using System.Collections;
using System.Linq;

#if !MONO
using Il2CppTMPro;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.DevUtilities;
#else
using TMPro;
using ScheduleOne.UI;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Persistent UI component that displays parole status on the right side of the screen, vertically centered
    /// Shows time remaining, supervision level with search probability, and violation count
    /// </summary>
    public class ParoleStatusUI : MonoBehaviour
    {
#if !MONO
        public ParoleStatusUI(System.IntPtr ptr) : base(ptr) { }
#endif

        private GameObject _statusPanel;
        private Image _backgroundImage;
        private TextMeshProUGUI _headerText;
        private TextMeshProUGUI _timeRemainingText;
        private TextMeshProUGUI _supervisionLevelText;
        private TextMeshProUGUI _violationsText;
        private TextMeshProUGUI _curfewText;
        private TextMeshProUGUI _complianceStreakText;
        private TextMeshProUGUI _feesText;
        private CanvasGroup _canvasGroup;
        private TMP_FontAsset _defaultFontAsset;
        private UnityEngine.Material _defaultFontMaterial;

        private bool _isInitialized = false;
        private Coroutine _fadeCoroutine;

        public void Start()
        {
            if (!_isInitialized)
            {
                CreateUI();
            }
        }

        /// <summary>
        /// Create the persistent parole status UI elements
        /// Can be called manually or via Unity's Start()
        /// </summary>
        public void CreateUI()
        {
            try
            {
                // Get the player HUD canvas
                Canvas hudCanvas = GetPlayerHUDCanvas();

                // If canvas not found, wait a bit and try again (HUD might not be initialized yet)
                if (hudCanvas == null)
                {
                    ModLogger.Warn("ParoleStatusUI: Player HUD Canvas not found on first attempt, waiting...");
                    MelonLoader.MelonCoroutines.Start(WaitForCanvasAndCreate());
                    return;
                }

                CreateUIWithCanvas(hudCanvas);
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating ParoleStatusUI: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the player's HUD canvas
        /// </summary>
        private Canvas GetPlayerHUDCanvas()
        {
            Canvas canvas = null;

#if !MONO
            // IL2CPP version
            try
            {
                var hudInstance = Singleton<Il2CppScheduleOne.UI.HUD>.Instance;
                if (hudInstance != null && hudInstance.Pointer != System.IntPtr.Zero)
                {
                    canvas = hudInstance.canvas;
                }
            }
            catch (System.Exception)
            {
                // HUD singleton not available yet
            }
#else
            // Mono version
            try
            {
                canvas = Singleton<HUD>.Instance?.canvas;
            }
            catch (System.Exception)
            {
                // HUD singleton not available yet
            }
#endif

            return canvas;
        }

        /// <summary>
        /// Create UI with a known canvas
        /// </summary>
        private void CreateUIWithCanvas(Canvas mainCanvas)
        {
            try
            {
                if (_isInitialized)
                {
                    ModLogger.Debug("ParoleStatusUI: Already initialized, skipping");
                    return;
                }

                CacheTextDefaults(mainCanvas);
                if (_defaultFontAsset == null || _defaultFontMaterial == null)
                {
                    ModLogger.Error("ParoleStatusUI: Could not resolve a valid TMP font/material pair from HUD canvas; skipping UI creation to avoid TMP null errors");
                    return;
                }

                // Create the status panel
                _statusPanel = new GameObject("ParoleStatusPanel");
                _statusPanel.transform.SetParent(mainCanvas.transform, false);
                _statusPanel.SetActive(false);

                // Add RectTransform component - RIGHT SIDE, VERTICALLY CENTERED
                RectTransform panelRect = _statusPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(1f, 0.5f); // Right edge, vertical center
                panelRect.anchorMax = new Vector2(1f, 0.5f);
                panelRect.pivot = new Vector2(1f, 0.5f); // Right edge, vertical center
                panelRect.anchoredPosition = new Vector2(-10f, 0f); // 10 pixels from right edge, centered vertically
                panelRect.sizeDelta = new Vector2(250f, 200f); // Width 250px, Height 200px (expanded for new fields)

                // Add CanvasGroup for fade animations
                _canvasGroup = _statusPanel.AddComponent<CanvasGroup>();
                _canvasGroup.alpha = 0f; // Start invisible

                // Add background image
                _backgroundImage = _statusPanel.AddComponent<Image>();
                _backgroundImage.color = new Color(0f, 0f, 0f, 0.85f); // Dark semi-transparent background

                // Add subtle border
                var outline = _statusPanel.AddComponent<Outline>();
                outline.effectColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                outline.effectDistance = new Vector2(1, -1);

                // Create header text ("PAROLE STATUS")
                GameObject headerObj = new GameObject("HeaderText");
                headerObj.transform.SetParent(_statusPanel.transform, false);

                RectTransform headerRect = headerObj.AddComponent<RectTransform>();
                headerRect.anchorMin = new Vector2(0f, 0.86f);
                headerRect.anchorMax = new Vector2(1f, 1f);
                headerRect.offsetMin = new Vector2(10f, 0f);
                headerRect.offsetMax = new Vector2(-10f, -3f);

                _headerText = headerObj.AddComponent<TextMeshProUGUI>();
                InitializeTextComponent(_headerText);
                _headerText.text = "PAROLE STATUS";
                _headerText.fontSize = 14f;
                _headerText.color = new Color(1f, 0.9f, 0.3f);
                _headerText.fontStyle = FontStyles.Bold;
                _headerText.alignment = TextAlignmentOptions.Center;

                // Row height = ~14% each for 6 data rows below the 14% header
                // Rows from top to bottom: Time (0.72-0.86), Supervision (0.58-0.72), Violations (0.44-0.58),
                //   Curfew (0.30-0.44), Compliance (0.16-0.30), Fees (0.02-0.16)

                // Create time remaining text
                _timeRemainingText = CreateStatusRow(_statusPanel.transform, "TimeRemainingText", 0.72f, 0.86f);
                _timeRemainingText.fontSize = 12f;
                _timeRemainingText.color = Color.white;

                // Create supervision level text
                _supervisionLevelText = CreateStatusRow(_statusPanel.transform, "SupervisionLevelText", 0.58f, 0.72f);
                _supervisionLevelText.fontSize = 11f;
                _supervisionLevelText.color = new Color(0.5f, 1f, 1f);

                // Create violations text
                _violationsText = CreateStatusRow(_statusPanel.transform, "ViolationsText", 0.44f, 0.58f);
                _violationsText.fontSize = 11f;
                _violationsText.color = Color.white;

                // Create curfew text
                _curfewText = CreateStatusRow(_statusPanel.transform, "CurfewText", 0.30f, 0.44f);
                _curfewText.fontSize = 11f;
                _curfewText.color = new Color(1f, 0.85f, 0.5f);

                // Create compliance streak text
                _complianceStreakText = CreateStatusRow(_statusPanel.transform, "ComplianceStreakText", 0.16f, 0.30f);
                _complianceStreakText.fontSize = 11f;
                _complianceStreakText.color = new Color(0.5f, 1f, 0.5f);

                // Create fees text
                _feesText = CreateStatusRow(_statusPanel.transform, "FeesText", 0.02f, 0.16f);
                _feesText.fontSize = 11f;
                _feesText.color = new Color(1f, 0.6f, 0.6f);

                ApplyFontFixes();

                _isInitialized = true;
                ModLogger.Debug("ParoleStatusUI created successfully at right side, vertically centered");
                ModLogger.Debug($"ParoleStatusUI: Panel active = {_statusPanel.activeSelf}, Alpha = {_canvasGroup.alpha}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating ParoleStatusUI: {ex.Message}");
            }
        }

        /// <summary>
        /// Wait for HUD canvas to be available and then create UI
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        private IEnumerator WaitForCanvasAndCreate()
        {
            int attempts = 0;
            const int maxAttempts = 10;

            while (attempts < maxAttempts)
            {
                yield return new WaitForSeconds(0.5f);

                Canvas hudCanvas = GetPlayerHUDCanvas();
                if (hudCanvas != null)
                {
                    ModLogger.Info($"ParoleStatusUI: Player HUD Canvas found after {attempts + 1} attempts");
                    CreateUIWithCanvas(hudCanvas);
                    yield break;
                }

                attempts++;
            }

            ModLogger.Error($"ParoleStatusUI: Could not find Player HUD Canvas after {maxAttempts} attempts");
        }

        /// <summary>
        /// Show parole status UI with data
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void Show(ParoleStatusData data)
        {
            if (!_isInitialized)
            {
                ModLogger.Warn("ParoleStatusUI: Not initialized, cannot show status");
                return;
            }

            if (_statusPanel == null)
            {
                ModLogger.Error("ParoleStatusUI: _statusPanel is null!");
                return;
            }

            if (_canvasGroup == null)
            {
                ModLogger.Error("ParoleStatusUI: _canvasGroup is null!");
                return;
            }

            try
            {
                ModLogger.Debug($"ParoleStatusUI: Showing status for parole - IsOnParole: {data.IsOnParole}");

                // Check if panel is already visible - if so, just update without fading
                bool wasVisible = _statusPanel.activeSelf && _canvasGroup.alpha > 0.9f;

                ApplyFontFixes();
                UpdateStatus(data);

                // Activate panel
                _statusPanel.SetActive(true);

                // Only fade in if panel wasn't already visible
                if (!wasVisible)
                {
                    // Stop any existing fade coroutine
                    if (_fadeCoroutine != null)
                    {
                        MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
                    }

                    var fadeInCoroutine = FadeIn();
                    _fadeCoroutine = MelonLoader.MelonCoroutines.Start(fadeInCoroutine) as Coroutine;
                }
                else
                {
                    // Ensure alpha is at 1.0 if already visible
                    _canvasGroup.alpha = 1f;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing parole status: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Update status without re-fading
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        public void UpdateStatus(ParoleStatusData data)
        {
            if (!_isInitialized || data == null)
            {
                ModLogger.Warn($"ParoleStatusUI: Cannot update - Initialized: {_isInitialized}, Data null: {data == null}");
                return;
            }

            try
            {
                if (!data.IsOnParole)
                {
                    ModLogger.Debug("ParoleStatusUI: Not on parole, hiding UI");
                    Hide();
                    return;
                }

                if (_timeRemainingText == null || _supervisionLevelText == null || _violationsText == null)
                {
                    ModLogger.Error("ParoleStatusUI: One or more text components are null!");
                    return;
                }

                // Update text content
                _timeRemainingText.text = $"Time: {data.TimeRemainingFormatted}";
                _supervisionLevelText.text = $"Supervision: {FormatLSILevel(data.SupervisionLevel, data.SearchProbabilityPercent)}";
                _violationsText.text = $"Violations: {data.ViolationCount}";

                // Color violations text red if violations > 0
                _violationsText.color = data.ViolationCount > 0
                    ? new Color(1f, 0.5f, 0.5f)
                    : Color.white;

                // Curfew display
                if (_curfewText != null)
                {
                    if (!string.IsNullOrEmpty(data.CurfewTime))
                    {
                        _curfewText.text = $"Curfew: {data.CurfewTime}";
                        _curfewText.gameObject.SetActive(true);
                    }
                    else
                    {
                        _curfewText.text = "";
                        _curfewText.gameObject.SetActive(false);
                    }
                }

                // Compliance streak display
                if (_complianceStreakText != null)
                {
                    if (data.ComplianceStreakDays > 0 || data.ComplianceStreakRequired > 0)
                    {
                        _complianceStreakText.text = $"Good behavior: {data.ComplianceStreakDays}/{data.ComplianceStreakRequired} days";
                        _complianceStreakText.gameObject.SetActive(true);
                    }
                    else
                    {
                        _complianceStreakText.text = "";
                        _complianceStreakText.gameObject.SetActive(false);
                    }
                }

                // Outstanding fees display
                if (_feesText != null)
                {
                    if (data.OutstandingFees > 0f)
                    {
                        _feesText.text = $"Fees owed: ${data.OutstandingFees:F0}";
                        _feesText.gameObject.SetActive(true);
                    }
                    else
                    {
                        _feesText.text = "";
                        _feesText.gameObject.SetActive(false);
                    }
                }

                ModLogger.Debug($"ParoleStatusUI: Updated status - {data.TimeRemainingFormatted}, {data.SupervisionLevel} - {data.SearchProbabilityPercent}%, Violations: {data.ViolationCount}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating parole status: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Hide the status UI with fade out
        /// </summary>
        public void Hide()
        {
            if (!_isInitialized || !_statusPanel.activeSelf)
                return;

            try
            {
                // Stop any existing fade coroutine
                if (_fadeCoroutine != null)
                {
                    MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
                }

                var fadeOutCoroutine = FadeOut();
                _fadeCoroutine = MelonLoader.MelonCoroutines.Start(fadeOutCoroutine) as Coroutine;

                ModLogger.Debug("ParoleStatusUI: Hiding status");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding parole status: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if status UI is currently visible
        /// </summary>
        public bool IsVisible()
        {
            return _isInitialized && _statusPanel != null && _statusPanel.activeSelf && _canvasGroup.alpha > 0;
        }

        /// <summary>
        /// Ends status presentation synchronously before a scene transition.  This
        /// avoids the fade coroutine writing to a HUD CanvasGroup after it unloads.
        /// </summary>
        public void CancelForSceneExit()
        {
            if (_fadeCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
            }

            if (_statusPanel != null)
            {
                _statusPanel.SetActive(false);
            }
        }

        /// <summary>
        /// Format LSI level with search probability
        /// </summary>
        /// <summary>
        /// Helper to create a text row in the status panel
        /// </summary>
        private TextMeshProUGUI CreateStatusRow(Transform parent, string name, float anchorMinY, float anchorMaxY)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);

            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, anchorMinY);
            rect.anchorMax = new Vector2(1f, anchorMaxY);
            rect.offsetMin = new Vector2(10f, 0f);
            rect.offsetMax = new Vector2(-10f, 0f);

            TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
            InitializeTextComponent(text);
            text.text = "";
            text.alignment = TextAlignmentOptions.Left;
            return text;
        }

        private void CacheTextDefaults(Canvas canvas)
        {
            if (canvas == null)
            {
                return;
            }

            TextMeshProUGUI sampleText = canvas.GetComponentInChildren<TextMeshProUGUI>(true);
            if (sampleText != null && sampleText.font != null)
            {
                _defaultFontAsset = sampleText.font;
                _defaultFontMaterial = sampleText.fontSharedMaterial ??
                                       sampleText.fontMaterial ??
                                       _defaultFontAsset.material;

                if (_defaultFontAsset != null && _defaultFontMaterial != null)
                {
                    TMPFontFix.CacheFont("base", _defaultFontAsset, _defaultFontMaterial);
                }

                return;
            }

            if (TMP_Settings.defaultFontAsset != null)
            {
                _defaultFontAsset = TMP_Settings.defaultFontAsset;
                _defaultFontMaterial = TMP_Settings.defaultFontAsset.material;
                if (_defaultFontAsset != null && _defaultFontMaterial != null)
                {
                    TMPFontFix.CacheFont("base", _defaultFontAsset, _defaultFontMaterial);
                    return;
                }
            }

            var fallbackText = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                .FirstOrDefault(t => t != null &&
                                     t.font != null &&
                                     (t.fontSharedMaterial != null || t.fontMaterial != null || t.font.material != null));
            if (fallbackText != null)
            {
                _defaultFontAsset = fallbackText.font;
                _defaultFontMaterial = fallbackText.fontSharedMaterial ??
                                       fallbackText.fontMaterial ??
                                       fallbackText.font.material;

                if (_defaultFontAsset != null && _defaultFontMaterial != null)
                {
                    TMPFontFix.CacheFont("base", _defaultFontAsset, _defaultFontMaterial);
                }
            }
        }

        private void InitializeTextComponent(TextMeshProUGUI text)
        {
            if (text == null)
            {
                return;
            }

            if (_defaultFontAsset != null)
            {
                text.font = _defaultFontAsset;
            }

            if (_defaultFontMaterial != null)
            {
                text.fontSharedMaterial = _defaultFontMaterial;
                text.fontMaterial = _defaultFontMaterial;
            }
            else if (text.font != null && text.font.material != null)
            {
                text.fontSharedMaterial = text.font.material;
                text.fontMaterial = text.font.material;
            }

            if (_defaultFontAsset != null && _defaultFontMaterial != null)
            {
                TMPFontFix.ApplySafeFont(text, "base");
            }

            text.raycastTarget = false;
            text.havePropertiesChanged = true;
            text.SetAllDirty();
        }

        private void ApplyFontFixes()
        {
            if (_statusPanel == null)
            {
                return;
            }

            if (_defaultFontAsset == null || _defaultFontMaterial == null)
            {
                CacheTextDefaults(GetPlayerHUDCanvas());
            }

            if (_defaultFontAsset == null || _defaultFontMaterial == null)
            {
                return;
            }

            TMPFontFix.FixAllTMPFonts(_statusPanel, "base");

            var texts = _statusPanel.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var text in texts)
            {
                if (text == null)
                {
                    continue;
                }

                if (text.font == null && _defaultFontAsset != null)
                {
                    text.font = _defaultFontAsset;
                }

                if (text.fontMaterial == null)
                {
                    if (_defaultFontMaterial != null)
                    {
                        text.fontSharedMaterial = _defaultFontMaterial;
                        text.fontMaterial = _defaultFontMaterial;
                    }
                    else if (text.font != null && text.font.material != null)
                    {
                        text.fontSharedMaterial = text.font.material;
                        text.fontMaterial = text.font.material;
                    }
                }

                TMPFontFix.ApplySafeFont(text, "base");
                text.havePropertiesChanged = true;
                text.SetAllDirty();
            }
        }

#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        private string FormatLSILevel(LSILevel level, int searchPercent)
        {
            string levelName = level switch
            {
                LSILevel.None => "None",
                LSILevel.Minimum => "Minimum",
                LSILevel.Medium => "Medium",
                LSILevel.High => "High",
                LSILevel.Severe => "Severe",
                _ => "Unknown"
            };

            return $"{levelName} - {searchPercent}";
        }

        /// <summary>
        /// Fade in animation
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        private IEnumerator FadeIn()
        {
            float fadeTime = 0.3f;
            float elapsed = 0f;

            while (elapsed < fadeTime)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(0f, 1f, elapsed / fadeTime);
                yield return null;
            }

            _canvasGroup.alpha = 1f;
        }

        /// <summary>
        /// Fade out animation
        /// </summary>
#if !MONO
        [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
#endif
        private IEnumerator FadeOut()
        {
            float fadeTime = 0.5f;
            float elapsed = 0f;
            float startAlpha = _canvasGroup.alpha;

            while (elapsed < fadeTime)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / fadeTime);
                yield return null;
            }

            _canvasGroup.alpha = 0f;
            _statusPanel.SetActive(false);
        }
    }
}

