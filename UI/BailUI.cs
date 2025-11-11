using UnityEngine;
using UnityEngine.UI;
using Behind_Bars.Helpers;
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
    /// Persistent UI component that displays bail payment prompt
    /// Shows "Press [B] to make bail: $X,XXX" when player is in cell and can afford bail
    /// </summary>
    public class BailUI : MonoBehaviour
    {
#if !MONO
        public BailUI(System.IntPtr ptr) : base(ptr) { }
#endif

        private GameObject _bailPanel;
        private Image _backgroundImage;
        private TextMeshProUGUI _bailText;
        private CanvasGroup _canvasGroup;

        private bool _isInitialized = false;
        private Coroutine _fadeCoroutine;
        private float _currentBailAmount = 0f;
        private bool _isVisible = false;

        public void Start()
        {
            if (!_isInitialized)
            {
                CreateUI();
            }
        }

        /// <summary>
        /// Create the persistent bail UI elements
        /// </summary>
        public void CreateUI()
        {
            try
            {
                // Get the player HUD canvas
                Canvas hudCanvas = GetPlayerHUDCanvas();

                // If canvas not found, wait a bit and try again
                if (hudCanvas == null)
                {
                    ModLogger.Warn("BailUI: Player HUD Canvas not found on first attempt, waiting...");
                    MelonLoader.MelonCoroutines.Start(WaitForCanvasAndCreate());
                    return;
                }

                CreateUIWithCanvas(hudCanvas);
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating BailUI: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the player's HUD canvas
        /// </summary>
        private Canvas GetPlayerHUDCanvas()
        {
            Canvas canvas = null;

#if !MONO
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
                    ModLogger.Debug("BailUI: Already initialized, skipping");
                    return;
                }

                // Create the bail panel
                _bailPanel = new GameObject("BailPanel");
                _bailPanel.transform.SetParent(mainCanvas.transform, false);

                // Add RectTransform component - BOTTOM-CENTER positioning (above hotbar)
                RectTransform panelRect = _bailPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0.5f, 0f); // Bottom-center
                panelRect.anchorMax = new Vector2(0.5f, 0f);
                panelRect.pivot = new Vector2(0.5f, 0f);
                panelRect.anchoredPosition = new Vector2(0f, 100f); // 100 pixels above bottom (above hotbar)
                panelRect.sizeDelta = new Vector2(400f, 50f);

                // Add CanvasGroup for fade animations
                _canvasGroup = _bailPanel.AddComponent<CanvasGroup>();
                _canvasGroup.alpha = 0f; // Start invisible

                // Add background image
                _backgroundImage = _bailPanel.AddComponent<Image>();
                _backgroundImage.color = new Color(0f, 0f, 0f, 0.7f); // Semi-transparent black

                // Add subtle border
                var outline = _bailPanel.AddComponent<Outline>();
                outline.effectColor = new Color(0.3f, 0.3f, 0.3f, 1f);
                outline.effectDistance = new Vector2(1, -1);

                // Create bail text
                GameObject bailTextObj = new GameObject("BailText");
                bailTextObj.transform.SetParent(_bailPanel.transform, false);

                RectTransform bailTextRect = bailTextObj.AddComponent<RectTransform>();
                bailTextRect.anchorMin = Vector2.zero;
                bailTextRect.anchorMax = Vector2.one;
                bailTextRect.offsetMin = new Vector2(10f, 5f);
                bailTextRect.offsetMax = new Vector2(-10f, -5f);

                _bailText = bailTextObj.AddComponent<TextMeshProUGUI>();
                _bailText.text = "";
                _bailText.fontSize = 18f;
                _bailText.color = new Color(0.5f, 1f, 0.5f); // Light green/cyan
                _bailText.fontStyle = FontStyles.Bold;
                _bailText.alignment = TextAlignmentOptions.Center;

                // Start hidden
                _bailPanel.SetActive(false);

                _isInitialized = true;
                ModLogger.Info("BailUI created successfully at bottom-center (above hotbar)");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating BailUI with canvas: {ex.Message}");
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
                    ModLogger.Info($"BailUI: Player HUD Canvas found after {attempts + 1} attempts");
                    CreateUIWithCanvas(hudCanvas);
                    yield break;
                }

                attempts++;
            }

            ModLogger.Error($"BailUI: Could not find Player HUD Canvas after {maxAttempts} attempts");
        }

        /// <summary>
        /// Show bail UI with specified bail amount
        /// </summary>
        public void ShowBail(float bailAmount)
        {
            if (!_isInitialized)
            {
                ModLogger.Warn("BailUI: Not initialized, cannot show bail");
                return;
            }

            try
            {
                _currentBailAmount = bailAmount;
                string keyName = Constants.BAIL_PAYMENT_KEY.ToString().Replace("KeyCode.", "");
                _bailText.text = $"Press [{keyName}] to make bail: ${bailAmount:F0}";

                // Activate and fade in
                _bailPanel.SetActive(true);
                _isVisible = true;

                // Stop any existing fade coroutine
                if (_fadeCoroutine != null)
                {
                    MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
                }

                var fadeInCoroutine = FadeIn();
                _fadeCoroutine = MelonLoader.MelonCoroutines.Start(fadeInCoroutine) as Coroutine;

                ModLogger.Debug($"BailUI: Showing bail prompt - ${bailAmount:F0}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing bail UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Update bail amount without re-fading
        /// </summary>
        public void UpdateBailAmount(float bailAmount)
        {
            if (!_isInitialized || !_bailPanel.activeSelf)
            {
                ShowBail(bailAmount);
                return;
            }

            try
            {
                _currentBailAmount = bailAmount;
                string keyName = Constants.BAIL_PAYMENT_KEY.ToString().Replace("KeyCode.", "");
                _bailText.text = $"Press [{keyName}] to make bail: ${bailAmount:F0}";

                ModLogger.Debug($"BailUI: Updated bail amount - ${bailAmount:F0}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating bail UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide the bail UI with fade out
        /// </summary>
        public void Hide()
        {
            if (!_isInitialized || !_bailPanel.activeSelf)
                return;

            try
            {
                _isVisible = false;

                // Stop any existing fade coroutine
                if (_fadeCoroutine != null)
                {
                    MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
                }

                var fadeOutCoroutine = FadeOut();
                _fadeCoroutine = MelonLoader.MelonCoroutines.Start(fadeOutCoroutine) as Coroutine;

                ModLogger.Debug("BailUI: Hiding bail prompt");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding bail UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if bail UI is currently visible
        /// </summary>
        public bool IsVisible()
        {
            return _isInitialized && _bailPanel != null && _bailPanel.activeSelf && _canvasGroup.alpha > 0 && _isVisible;
        }

        /// <summary>
        /// Get current bail amount being displayed
        /// </summary>
        public float GetCurrentBailAmount()
        {
            return _currentBailAmount;
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
            _bailPanel.SetActive(false);
        }
    }
}

