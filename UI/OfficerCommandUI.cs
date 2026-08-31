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
        /// <summary>Creates the IL2CPP wrapper for the injected officer-command component.</summary>
        public OfficerCommandUI(System.IntPtr ptr) : base(ptr) { }
#endif

        // These references belong to the dynamically-created panel under the current HUD. The
        // manager clears them at scene exit before the HUD canvas is destroyed or replaced.
        private GameObject _commandPanel;
        private Image _backgroundImage;
        private TextMeshProUGUI _officerTypeText;
        private TextMeshProUGUI _commandText;
        private TextMeshProUGUI _progressText;
        private TextMeshProUGUI _escortIndicator;
        private CanvasGroup _canvasGroup;

        // Fade/retry handles are owned by this component and must be cancelled together; a
        // command update replaces content without taking ownership away from the manager.
        private bool _isInitialized = false;
        private Coroutine _fadeCoroutine;
        private Coroutine _canvasInitializationCoroutine;

        /// <summary>Unity lifecycle entry point that lazily creates the shared command panel.</summary>
        public void Start()
        {
            if (!_isInitialized)
            {
                CreateUI();
            }
        }

        /// <summary>
        /// Creates the officer-command panel under the player HUD. It is safe to invoke from
        /// either Unity's Start or the manager's manual bootstrap and retries once the HUD exists.
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
                    if (_canvasInitializationCoroutine == null)
                    {
                        _canvasInitializationCoroutine = MelonLoader.MelonCoroutines.Start(WaitForCanvasAndCreate()) as Coroutine;
                    }
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
        /// Resolves the runtime-specific player HUD canvas and fails closed while its singleton
        /// or IL2CPP native pointer is not ready.
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
        /// Builds the fixed-width shared-slot panel under a known HUD canvas. The panel starts
        /// inactive and is initialized only once, preserving the approved command geometry.
        /// </summary>
        /// <param name="mainCanvas">HUD canvas that owns the command panel.</param>
        private void CreateUIWithCanvas(Canvas mainCanvas)
        {
            try
            {
                if (_isInitialized)
                {
                    ModLogger.Debug("OfficerCommandUI: Already initialized, skipping");
                    return;
                }

                if (!TMPFontFix.EnsureFontCached(mainCanvas))
                {
                    ModLogger.Error("OfficerCommandUI: Could not resolve a valid TMP font/material pair; skipping UI creation");
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

                // Apply font fixes to all text components before the first canvas rebuild.
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
        /// Retries HUD lookup for a bounded number of scaled-time intervals, then creates the
        /// panel and clears its retry handle on success or failure.
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
                    _canvasInitializationCoroutine = null;
                    CreateUIWithCanvas(hudCanvas);
                    yield break;
                }

                attempts++;
            }

            ModLogger.Error($"OfficerCommandUI: Could not find Player HUD Canvas after {maxAttempts} attempts");
            _canvasInitializationCoroutine = null;
        }

        /// <summary>
        /// Shows an officer command, replacing text/stage content and fading the shared slot in.
        /// The manager is responsible for suppressing lower-priority tier status while visible.
        /// </summary>
        /// <param name="data">Officer command snapshot to render.</param>
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

        /// <summary>Updates the current command content without restarting its fade.</summary>
        /// <param name="data">Updated officer command snapshot.</param>
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

        /// <summary>Fades the officer command out and releases the shared slot when complete.</summary>
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

        /// <summary>Returns whether the initialized command panel is active with visible alpha.</summary>
        public bool IsVisible()
        {
            return _isInitialized && _commandPanel != null && _canvasGroup != null && _commandPanel.activeSelf && _canvasGroup.alpha > 0;
        }

        /// <summary>
        /// Stops the UI's scene-bound routines before the HUD canvas is unloaded.
        /// The component itself may persist between scenes, but its panel is owned by the current
        /// HUD. References are cleared so the next scene can bind a fresh canvas.
        /// </summary>
        public void CancelForSceneExit()
        {
            if (_fadeCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            if (_canvasInitializationCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_canvasInitializationCoroutine);
                _canvasInitializationCoroutine = null;
            }

            if (_commandPanel != null)
            {
                _commandPanel.SetActive(false);
            }

            _commandPanel = null;
            _backgroundImage = null;
            _officerTypeText = null;
            _commandText = null;
            _progressText = null;
            _escortIndicator = null;
            _canvasGroup = null;
            _isInitialized = false;
        }

        /// <summary>
        /// Fades the command panel in using scaled frame time while the gameplay scene remains
        /// active; scene exit cancels the transition through the lifecycle guard.
        /// </summary>
        private IEnumerator FadeIn()
        {
            if (!Core.IsGameplaySceneActive || _canvasGroup == null || _commandPanel == null)
            {
                yield break;
            }

            float fadeTime = 0.3f;
            float elapsed = 0f;

            while (elapsed < fadeTime)
            {
                if (!Core.IsGameplaySceneActive || _canvasGroup == null || _commandPanel == null)
                {
                    yield break;
                }

                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(0f, 1f, elapsed / fadeTime);
                yield return null;
            }

            if (_canvasGroup == null)
            {
                yield break;
            }

            _canvasGroup.alpha = 1f;
            _fadeCoroutine = null;
        }

        /// <summary>
        /// Fades the command panel out using scaled frame time and deactivates it only after the
        /// transition completes. Scene teardown may end the coroutine before that point.
        /// </summary>
        private IEnumerator FadeOut()
        {
            if (!Core.IsGameplaySceneActive || _canvasGroup == null || _commandPanel == null)
            {
                yield break;
            }

            float fadeTime = 0.5f;
            float elapsed = 0f;
            float startAlpha = _canvasGroup.alpha;

            while (elapsed < fadeTime)
            {
                if (!Core.IsGameplaySceneActive || _canvasGroup == null || _commandPanel == null)
                {
                    yield break;
                }

                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / fadeTime);
                yield return null;
            }

            if (_canvasGroup == null || _commandPanel == null)
            {
                yield break;
            }

            _canvasGroup.alpha = 0f;
            _commandPanel.SetActive(false);
            _fadeCoroutine = null;
        }

        /// <summary>Routes destruction through the same idempotent scene-exit cleanup path.</summary>
        private void OnDestroy()
        {
            CancelForSceneExit();
        }
    }
}
