using UnityEngine;
using UnityEngine.UI;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
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
    /// Persistent UI component that displays officer commands at the top-left of the screen
    /// Shows current objective and stage progress during booking/release processes
    /// </summary>
    public class OfficerCommandUI : MonoBehaviour
    {
#if !MONO
        public OfficerCommandUI(System.IntPtr ptr) : base(ptr) { }
#endif

        private GameObject _commandPanel;
        private Image _backgroundImage;
        private TextMeshProUGUI _officerTypeText;
        private TextMeshProUGUI _commandText;
        private TextMeshProUGUI _progressText;
        private TextMeshProUGUI _escortIndicator;
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
        /// Create the persistent command UI elements
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
                    ModLogger.Warn("OfficerCommandUI: Player HUD Canvas not found on first attempt, waiting...");
                    MelonLoader.MelonCoroutines.Start(WaitForCanvasAndCreate());
                    return;
                }

                CreateUIWithCanvas(hudCanvas);
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating OfficerCommandUI: {ex.Message}");
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
                    ModLogger.Debug("OfficerCommandUI: Already initialized, skipping");
                    return;
                }

                // Create the command panel
                _commandPanel = new GameObject("OfficerCommandPanel");
                _commandPanel.transform.SetParent(mainCanvas.transform, false);

                // Add RectTransform component - TOP-LEFT positioning
                RectTransform panelRect = _commandPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0f, 1f); // Top-left corner
                panelRect.anchorMax = new Vector2(0f, 1f);
                panelRect.pivot = new Vector2(0f, 1f);
                panelRect.anchoredPosition = new Vector2(10f, -10f); // 10 pixels from corner
                panelRect.sizeDelta = new Vector2(320f, 90f); // Slightly larger for more content

                // Add CanvasGroup for fade animations
                _canvasGroup = _commandPanel.AddComponent<CanvasGroup>();
                _canvasGroup.alpha = 0f; // Start invisible

                // Add background image
                _backgroundImage = _commandPanel.AddComponent<Image>();
                _backgroundImage.color = new Color(0f, 0f, 0f, 0.8f); // Darker background for persistence

                // Add subtle border
                var outline = _commandPanel.AddComponent<Outline>();
                outline.effectColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                outline.effectDistance = new Vector2(1, -1);

                // Create officer type text (header)
                GameObject officerTypeObj = new GameObject("OfficerTypeText");
                officerTypeObj.transform.SetParent(_commandPanel.transform, false);

                RectTransform officerTypeRect = officerTypeObj.AddComponent<RectTransform>();
                officerTypeRect.anchorMin = new Vector2(0f, 0.7f);
                officerTypeRect.anchorMax = new Vector2(1f, 1f);
                officerTypeRect.offsetMin = new Vector2(10f, 0f);
                officerTypeRect.offsetMax = new Vector2(-10f, -5f);

                _officerTypeText = officerTypeObj.AddComponent<TextMeshProUGUI>();
                _officerTypeText.text = "OFFICER";
                _officerTypeText.fontSize = 12f;
                _officerTypeText.color = new Color(1f, 0.9f, 0.3f); // Yellow-gold color
                _officerTypeText.fontStyle = FontStyles.Bold;
                _officerTypeText.alignment = TextAlignmentOptions.TopLeft;

                // Create command text (main instruction)
                GameObject commandTextObj = new GameObject("CommandText");
                commandTextObj.transform.SetParent(_commandPanel.transform, false);

                RectTransform commandTextRect = commandTextObj.AddComponent<RectTransform>();
                commandTextRect.anchorMin = new Vector2(0f, 0.35f);
                commandTextRect.anchorMax = new Vector2(1f, 0.7f);
                commandTextRect.offsetMin = new Vector2(10f, 0f);
                commandTextRect.offsetMax = new Vector2(-10f, 0f);

                _commandText = commandTextObj.AddComponent<TextMeshProUGUI>();
                _commandText.text = "";
                _commandText.fontSize = 15f;
                _commandText.color = Color.white;
                _commandText.alignment = TextAlignmentOptions.Left;
                _commandText.enableWordWrapping = true;

                // Create progress text (stage indicator)
                GameObject progressTextObj = new GameObject("ProgressText");
                progressTextObj.transform.SetParent(_commandPanel.transform, false);

                RectTransform progressTextRect = progressTextObj.AddComponent<RectTransform>();
                progressTextRect.anchorMin = new Vector2(0f, 0.05f);
                progressTextRect.anchorMax = new Vector2(0.5f, 0.35f);
                progressTextRect.offsetMin = new Vector2(10f, 5f);
                progressTextRect.offsetMax = new Vector2(0f, 0f);

                _progressText = progressTextObj.AddComponent<TextMeshProUGUI>();
                _progressText.text = "";
                _progressText.fontSize = 10f;
                _progressText.color = new Color(0.7f, 0.7f, 0.7f); // Gray
                _progressText.fontStyle = FontStyles.Italic;
                _progressText.alignment = TextAlignmentOptions.BottomLeft;

                // Create escort indicator (optional)
                GameObject escortIndicatorObj = new GameObject("EscortIndicator");
                escortIndicatorObj.transform.SetParent(_commandPanel.transform, false);

                RectTransform escortIndicatorRect = escortIndicatorObj.AddComponent<RectTransform>();
                escortIndicatorRect.anchorMin = new Vector2(0.5f, 0.05f);
                escortIndicatorRect.anchorMax = new Vector2(1f, 0.35f);
                escortIndicatorRect.offsetMin = new Vector2(0f, 5f);
                escortIndicatorRect.offsetMax = new Vector2(-10f, 0f);

                _escortIndicator = escortIndicatorObj.AddComponent<TextMeshProUGUI>();
                _escortIndicator.text = "";
                _escortIndicator.fontSize = 10f;
                _escortIndicator.color = new Color(0.5f, 1f, 0.5f); // Light green
                _escortIndicator.alignment = TextAlignmentOptions.BottomRight;

                // Start hidden
                _commandPanel.SetActive(false);

                // Apply font fixes (including emoji fallbacks) to all text components
                TMPFontFix.FixAllTMPFonts(_commandPanel, "base");

                _isInitialized = true;
                ModLogger.Debug("OfficerCommandUI created successfully at top-left");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating OfficerCommandUI with canvas: {ex.Message}");
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
                    ModLogger.Info($"OfficerCommandUI: Player HUD Canvas found after {attempts + 1} attempts");
                    CreateUIWithCanvas(hudCanvas);
                    yield break;
                }

                attempts++;
            }

            ModLogger.Error($"OfficerCommandUI: Could not find Player HUD Canvas after {maxAttempts} attempts");
        }

        /// <summary>
        /// Show officer command with data
        /// </summary>
        public void ShowCommand(OfficerCommandData data)
        {
            if (!_isInitialized)
            {
                ModLogger.Warn("OfficerCommandUI: Not initialized, cannot show command");
                return;
            }

            try
            {
                // Update text content
                _officerTypeText.text = data.OfficerType;
                _commandText.text = data.CommandText;
                _progressText.text = $"Stage {data.CurrentStage}/{data.TotalStages}";

                // Show escort indicator if escorting
                if (data.IsEscorting)
                {
                    _escortIndicator.text = ">> FOLLOW";
                }
                else
                {
                    _escortIndicator.text = "";
                }

                // Activate and fade in
                _commandPanel.SetActive(true);

                // Stop any existing fade coroutine
                if (_fadeCoroutine != null)
                {
                    MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
                }

                var fadeInCoroutine = FadeIn();
                _fadeCoroutine = MelonLoader.MelonCoroutines.Start(fadeInCoroutine) as Coroutine;

                ModLogger.Debug($"OfficerCommandUI: Showing command - {data.CommandText}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing officer command: {ex.Message}");
            }
        }

        /// <summary>
        /// Update command without re-fading
        /// </summary>
        public void UpdateCommand(OfficerCommandData data)
        {
            if (!_isInitialized || !_commandPanel.activeSelf)
            {
                ShowCommand(data);
                return;
            }

            try
            {
                // Null check all components before updating
                if (_officerTypeText == null || _commandText == null || _progressText == null || _escortIndicator == null)
                {
                    ModLogger.Error("OfficerCommandUI: One or more text components are null, recreating UI");
                    ShowCommand(data);
                    return;
                }

                _officerTypeText.text = data.OfficerType;
                _commandText.text = data.CommandText;
                _progressText.text = $"Stage {data.CurrentStage}/{data.TotalStages}";

                if (data.IsEscorting)
                {
                    _escortIndicator.text = ">> FOLLOW";
                }
                else
                {
                    _escortIndicator.text = "";
                }

                ModLogger.Debug($"OfficerCommandUI: Updated command - {data.CommandText}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating officer command: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Hide the command UI with fade out
        /// </summary>
        public void Hide()
        {
            if (!_isInitialized || !_commandPanel.activeSelf)
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

                ModLogger.Debug("OfficerCommandUI: Hiding command");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding officer command: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if command UI is currently visible
        /// </summary>
        public bool IsVisible()
        {
            return _isInitialized && _commandPanel != null && _commandPanel.activeSelf && _canvasGroup.alpha > 0;
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
            _commandPanel.SetActive(false);
        }
    }
}
