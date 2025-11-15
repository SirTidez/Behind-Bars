using UnityEngine;
using UnityEngine.UI;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using System.Collections;

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
        private CanvasGroup _canvasGroup;

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

                // Create the status panel
                _statusPanel = new GameObject("ParoleStatusPanel");
                _statusPanel.transform.SetParent(mainCanvas.transform, false);

                // Add RectTransform component - RIGHT SIDE, VERTICALLY CENTERED
                RectTransform panelRect = _statusPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(1f, 0.5f); // Right edge, vertical center
                panelRect.anchorMax = new Vector2(1f, 0.5f);
                panelRect.pivot = new Vector2(1f, 0.5f); // Right edge, vertical center
                panelRect.anchoredPosition = new Vector2(-10f, 0f); // 10 pixels from right edge, centered vertically
                panelRect.sizeDelta = new Vector2(250f, 140f); // Width 250px, Height 140px

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
                headerRect.anchorMin = new Vector2(0f, 0.75f);
                headerRect.anchorMax = new Vector2(1f, 1f);
                headerRect.offsetMin = new Vector2(10f, 0f);
                headerRect.offsetMax = new Vector2(-10f, -5f);

                _headerText = headerObj.AddComponent<TextMeshProUGUI>();
                _headerText.text = "PAROLE STATUS";
                _headerText.fontSize = 14f;
                _headerText.color = new Color(1f, 0.9f, 0.3f); // Yellow-gold color
                _headerText.fontStyle = FontStyles.Bold;
                _headerText.alignment = TextAlignmentOptions.Center;

                // Create time remaining text
                GameObject timeObj = new GameObject("TimeRemainingText");
                timeObj.transform.SetParent(_statusPanel.transform, false);

                RectTransform timeRect = timeObj.AddComponent<RectTransform>();
                timeRect.anchorMin = new Vector2(0f, 0.5f);
                timeRect.anchorMax = new Vector2(1f, 0.75f);
                timeRect.offsetMin = new Vector2(10f, 0f);
                timeRect.offsetMax = new Vector2(-10f, 0f);

                _timeRemainingText = timeObj.AddComponent<TextMeshProUGUI>();
                _timeRemainingText.text = "";
                _timeRemainingText.fontSize = 13f;
                _timeRemainingText.color = Color.white;
                _timeRemainingText.alignment = TextAlignmentOptions.Left;

                // Create supervision level text
                GameObject supervisionObj = new GameObject("SupervisionLevelText");
                supervisionObj.transform.SetParent(_statusPanel.transform, false);

                RectTransform supervisionRect = supervisionObj.AddComponent<RectTransform>();
                supervisionRect.anchorMin = new Vector2(0f, 0.25f);
                supervisionRect.anchorMax = new Vector2(1f, 0.5f);
                supervisionRect.offsetMin = new Vector2(10f, 0f);
                supervisionRect.offsetMax = new Vector2(-10f, 0f);

                _supervisionLevelText = supervisionObj.AddComponent<TextMeshProUGUI>();
                _supervisionLevelText.text = "";
                _supervisionLevelText.fontSize = 12f;
                _supervisionLevelText.color = new Color(0.5f, 1f, 1f); // Cyan color
                _supervisionLevelText.alignment = TextAlignmentOptions.Left;

                // Create violations text
                GameObject violationsObj = new GameObject("ViolationsText");
                violationsObj.transform.SetParent(_statusPanel.transform, false);

                RectTransform violationsRect = violationsObj.AddComponent<RectTransform>();
                violationsRect.anchorMin = new Vector2(0f, 0f);
                violationsRect.anchorMax = new Vector2(1f, 0.25f);
                violationsRect.offsetMin = new Vector2(10f, 5f);
                violationsRect.offsetMax = new Vector2(-10f, 0f);

                _violationsText = violationsObj.AddComponent<TextMeshProUGUI>();
                _violationsText.text = "";
                _violationsText.fontSize = 12f;
                _violationsText.color = Color.white;
                _violationsText.alignment = TextAlignmentOptions.Left;

                // Start hidden
                _statusPanel.SetActive(false);

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

                // Null check all text components
                if (_timeRemainingText == null || _supervisionLevelText == null || _violationsText == null)
                {
                    ModLogger.Error("ParoleStatusUI: One or more text components are null!");
                    ModLogger.Error($"  _timeRemainingText: {_timeRemainingText != null}");
                    ModLogger.Error($"  _supervisionLevelText: {_supervisionLevelText != null}");
                    ModLogger.Error($"  _violationsText: {_violationsText != null}");
                    return;
                }

                // Update text content
                _timeRemainingText.text = $"Time: {data.TimeRemainingFormatted}";
                _supervisionLevelText.text = $"Supervision: {FormatLSILevel(data.SupervisionLevel, data.SearchProbabilityPercent)}";
                _violationsText.text = $"Violations: {data.ViolationCount}";

                // Color violations text red if violations > 0
                _violationsText.color = data.ViolationCount > 0
                    ? new Color(1f, 0.5f, 0.5f) // Red
                    : Color.white;

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
        /// Format LSI level with search probability
        /// </summary>
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

