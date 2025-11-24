using UnityEngine;
using UnityEngine.UI;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using System.Collections;

#if !MONO
using Il2CppTMPro;
#else
using TMPro;
#endif
using Object = UnityEngine.Object;

namespace Behind_Bars.UI
{
    /// <summary>
    /// UI component for displaying update notifications at the top-center of the screen
    /// </summary>
    public class UpdateNotificationUI : MonoBehaviour
    {
#if !MONO
        public UpdateNotificationUI(System.IntPtr ptr) : base(ptr) { }
#endif

        private GameObject _notificationPanel;
        private GameObject _canvasObject;
        private Canvas _canvas;
        private CanvasGroup _canvasGroup;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _descriptionText;
        private Button _viewDetailsButton;
        private Button _dismissButton;
        private Button _closeButton;
        private ScrollRect _scrollRect;

        private bool _isInitialized = false;
        private bool _isVisible = false;
        private object _fadeCoroutine;
        private object _autoDismissCoroutine;
        private VersionInfo? _currentVersionInfo;

        /// <summary>
        /// Show update notification with version information
        /// </summary>
        public void Show(VersionInfo versionInfo)
        {
            if (versionInfo == null || string.IsNullOrEmpty(versionInfo.version))
            {
                ModLogger.Error("Cannot show update notification - invalid version info");
                return;
            }

            try
            {
                if (!_isInitialized)
                {
                    CreateUI();
                }

                if (_isInitialized)
                {
                    _currentVersionInfo = versionInfo; // Store for button click
                    UpdateContent(versionInfo);
                    SetVisible(true);
                    
                    // Start auto-dismiss timer (45 seconds)
                    if (_autoDismissCoroutine != null)
                    {
                        MelonLoader.MelonCoroutines.Stop(_autoDismissCoroutine);
                    }
                    _autoDismissCoroutine = MelonLoader.MelonCoroutines.Start(AutoDismissAfterDelay(45f));
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error showing update notification: {e.Message}");
            }
        }

        /// <summary>
        /// Hide the update notification
        /// </summary>
        public void Hide()
        {
            SetVisible(false);
            
            if (_autoDismissCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_autoDismissCoroutine);
                _autoDismissCoroutine = null;
            }
        }

        /// <summary>
        /// Create the UI hierarchy programmatically
        /// </summary>
        private void CreateUI()
        {
            try
            {
                if (_isInitialized)
                {
                    ModLogger.Debug("UpdateNotificationUI already initialized");
                    return;
                }

                ModLogger.Info("Creating UpdateNotificationUI...");

                // Create overlay canvas
                _canvasObject = new GameObject("UpdateNotificationCanvas");
                _canvas = _canvasObject.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 2000; // Very high to appear above all other UI

                // Add CanvasScaler for proper scaling
                var scaler = _canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;

                // Add GraphicRaycaster for UI interaction
                _canvasObject.AddComponent<GraphicRaycaster>();

                // Don't destroy on load so it persists across scenes
                Object.DontDestroyOnLoad(_canvasObject);

                // Create main panel
                _notificationPanel = new GameObject("NotificationPanel");
                _notificationPanel.transform.SetParent(_canvas.transform, false);

                RectTransform panelRect = _notificationPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0.5f, 0.5f); // Center of screen
                panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                panelRect.pivot = new Vector2(0.5f, 0.5f); // Center pivot
                panelRect.anchoredPosition = new Vector2(0f, 0f); // Centered
                panelRect.sizeDelta = new Vector2(650f, 320f); // Larger and more spacious

                // Add CanvasGroup for fade animations
                _canvasGroup = _notificationPanel.AddComponent<CanvasGroup>();
                _canvasGroup.alpha = 0f; // Start invisible
                _canvasGroup.interactable = false;
                _canvasGroup.blocksRaycasts = false;

                // Add background image - matching other UI
                Image panelBg = _notificationPanel.AddComponent<Image>();
                panelBg.color = new Color(0f, 0f, 0f, 0.85f); // Dark semi-transparent like ParoleStatusUI

                // Add subtle border - matching other UI
                var outline = _notificationPanel.AddComponent<Outline>();
                outline.effectColor = new Color(0.3f, 0.3f, 0.3f, 1f); // Gray like other UI
                outline.effectDistance = new Vector2(1, -1);

                // Create header with title and close button
                CreateHeader();

                // Create scrollable description area
                CreateDescriptionArea();

                // Create button bar
                CreateButtonBar();

                _isInitialized = true;
                ModLogger.Debug("✓ UpdateNotificationUI created successfully");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error creating UpdateNotificationUI: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Create header with title and close button
        /// </summary>
        private void CreateHeader()
        {
            // Title text
            GameObject titleObj = new GameObject("TitleText");
            titleObj.transform.SetParent(_notificationPanel.transform, false);

            RectTransform titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 0.75f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.offsetMin = new Vector2(10f, 0f);
            titleRect.offsetMax = new Vector2(-40f, -5f); // Leave space for close button

            _titleText = titleObj.AddComponent<TextMeshProUGUI>();
            _titleText.text = "BEHIND BARS - UPDATE AVAILABLE";
            _titleText.fontSize = 14f;
            _titleText.color = new Color(1f, 0.9f, 0.3f); // Yellow-gold like other UI
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.alignment = TextAlignmentOptions.Center;

            // Close button (X)
            GameObject closeBtnObj = new GameObject("CloseButton");
            closeBtnObj.transform.SetParent(_notificationPanel.transform, false);

            RectTransform closeBtnRect = closeBtnObj.AddComponent<RectTransform>();
            closeBtnRect.anchorMin = new Vector2(1f, 1f);
            closeBtnRect.anchorMax = new Vector2(1f, 1f);
            closeBtnRect.pivot = new Vector2(1f, 1f);
            closeBtnRect.anchoredPosition = new Vector2(-5f, -5f);
            closeBtnRect.sizeDelta = new Vector2(25f, 25f);

            Image closeBtnBg = closeBtnObj.AddComponent<Image>();
            closeBtnBg.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);

            _closeButton = closeBtnObj.AddComponent<Button>();
#if !MONO
            _closeButton.onClick.AddListener(new System.Action(() => OnDismissClicked()));
#else
            _closeButton.onClick.AddListener(() => OnDismissClicked());
#endif

            // X text
            GameObject closeTextObj = new GameObject("CloseText");
            closeTextObj.transform.SetParent(closeBtnObj.transform, false);

            RectTransform closeTextRect = closeTextObj.AddComponent<RectTransform>();
            closeTextRect.anchorMin = Vector2.zero;
            closeTextRect.anchorMax = Vector2.one;
            closeTextRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI closeText = closeTextObj.AddComponent<TextMeshProUGUI>();
            closeText.text = "×";
            closeText.fontSize = 20f;
            closeText.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            closeText.alignment = TextAlignmentOptions.Center;
        }

        /// <summary>
        /// Create scrollable description area with matching style
        /// </summary>
        private void CreateDescriptionArea()
        {
            // Scroll view container
            GameObject scrollViewObj = new GameObject("ScrollView");
            scrollViewObj.transform.SetParent(_notificationPanel.transform, false);

            RectTransform scrollRect = scrollViewObj.AddComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0f, 0.35f);
            scrollRect.anchorMax = new Vector2(1f, 0.75f);
            scrollRect.offsetMin = new Vector2(10f, 0f);
            scrollRect.offsetMax = new Vector2(-10f, 0f);

            // No background for scroll view (transparent)
            _scrollRect = scrollViewObj.AddComponent<ScrollRect>();
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;

            // Content area
            GameObject contentObj = new GameObject("Content");
            contentObj.transform.SetParent(scrollViewObj.transform, false);

            RectTransform contentRect = contentObj.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 100f);

            VerticalLayoutGroup layoutGroup = contentObj.AddComponent<VerticalLayoutGroup>();
            layoutGroup.padding = new RectOffset(5, 5, 5, 5);
            layoutGroup.spacing = 5f;
            layoutGroup.childControlHeight = false;
            layoutGroup.childControlWidth = true;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.childForceExpandWidth = true;

            ContentSizeFitter sizeFitter = contentObj.AddComponent<ContentSizeFitter>();
            sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scrollRect.content = contentRect;

            // Description text
            GameObject descObj = new GameObject("DescriptionText");
            descObj.transform.SetParent(contentObj.transform, false);

            RectTransform descRect = descObj.AddComponent<RectTransform>();
            descRect.sizeDelta = new Vector2(0f, 80f);

            _descriptionText = descObj.AddComponent<TextMeshProUGUI>();
            _descriptionText.text = "";
            _descriptionText.fontSize = 13f;
            _descriptionText.color = Color.white;
            _descriptionText.alignment = TextAlignmentOptions.Center;
            _descriptionText.enableWordWrapping = true;

            ContentSizeFitter descSizeFitter = descObj.AddComponent<ContentSizeFitter>();
            descSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        /// <summary>
        /// Create button bar at bottom
        /// </summary>
        private void CreateButtonBar()
        {
            GameObject buttonBarObj = new GameObject("ButtonBar");
            buttonBarObj.transform.SetParent(_notificationPanel.transform, false);

            RectTransform buttonBarRect = buttonBarObj.AddComponent<RectTransform>();
            buttonBarRect.anchorMin = new Vector2(0f, 0.05f);
            buttonBarRect.anchorMax = new Vector2(1f, 0.35f);
            buttonBarRect.offsetMin = new Vector2(10f, 5f);
            buttonBarRect.offsetMax = new Vector2(-10f, -5f);

            HorizontalLayoutGroup layoutGroup = buttonBarObj.AddComponent<HorizontalLayoutGroup>();
            layoutGroup.spacing = 10f;
            layoutGroup.childControlHeight = true;
            layoutGroup.childControlWidth = false;
            layoutGroup.childForceExpandHeight = true;
            layoutGroup.childForceExpandWidth = false;
            layoutGroup.childAlignment = TextAnchor.MiddleCenter;

            // View on GitHub button
            _viewDetailsButton = CreateButton(buttonBarObj.transform, "View on GitHub", new Vector2(180f, 35f));
#if !MONO
            _viewDetailsButton.onClick.AddListener(new System.Action(() => OnViewDetailsClicked()));
#else
            _viewDetailsButton.onClick.AddListener(() => OnViewDetailsClicked());
#endif

            // Dismiss button
            _dismissButton = CreateButton(buttonBarObj.transform, "Dismiss", new Vector2(120f, 35f));
#if !MONO
            _dismissButton.onClick.AddListener(new System.Action(() => OnDismissClicked()));
#else
            _dismissButton.onClick.AddListener(() => OnDismissClicked());
#endif
        }

        /// <summary>
        /// Create a styled button
        /// </summary>
        private Button CreateButton(Transform parent, string text, Vector2 size)
        {
            GameObject btnObj = new GameObject($"{text}Button");
            btnObj.transform.SetParent(parent, false);

            RectTransform btnRect = btnObj.AddComponent<RectTransform>();
            btnRect.sizeDelta = size;

            Image btnBg = btnObj.AddComponent<Image>();
            btnBg.color = new Color(0.25f, 0.25f, 0.25f, 0.9f); // Gray matching UI theme

            Button button = btnObj.AddComponent<Button>();
            
            // Button text
            GameObject btnTextObj = new GameObject("Text");
            btnTextObj.transform.SetParent(btnObj.transform, false);

            RectTransform btnTextRect = btnTextObj.AddComponent<RectTransform>();
            btnTextRect.anchorMin = Vector2.zero;
            btnTextRect.anchorMax = Vector2.one;
            btnTextRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI btnText = btnTextObj.AddComponent<TextMeshProUGUI>();
            btnText.text = text;
            btnText.fontSize = 12f;
            btnText.color = Color.white;
            btnText.fontStyle = FontStyles.Normal;
            btnText.alignment = TextAlignmentOptions.Center;

            return button;
        }

        /// <summary>
        /// Update UI content with version information
        /// </summary>
        private void UpdateContent(VersionInfo versionInfo)
        {
            if (_titleText != null)
            {
                _titleText.text = "BEHIND BARS - UPDATE AVAILABLE";
            }

            if (_descriptionText != null)
            {
                // Display version and full description
                string displayText = $"Version {versionInfo.version} is available\n\n";
                
                // Add full description if available
                if (!string.IsNullOrEmpty(versionInfo.description))
                {
                    string desc = versionInfo.description.Replace("\\n", "\n");
                    displayText += desc;
                }
                
                // Add release date if available
                if (!string.IsNullOrEmpty(versionInfo.release_date))
                {
                    displayText += $"\n\nReleased: {versionInfo.release_date}";
                }

                _descriptionText.text = displayText;
            }
        }

        /// <summary>
        /// Set visibility with fade animation
        /// </summary>
        private void SetVisible(bool visible)
        {
            if (_isVisible == visible)
                return;

            _isVisible = visible;

            if (_fadeCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
            }

            _fadeCoroutine = MelonLoader.MelonCoroutines.Start(FadeAnimation(visible));
        }

        /// <summary>
        /// Fade in/out animation
        /// </summary>
        private IEnumerator FadeAnimation(bool fadeIn)
        {
            float duration = 0.3f;
            float startAlpha = _canvasGroup.alpha;
            float targetAlpha = fadeIn ? 1f : 0f;

            _canvasGroup.interactable = fadeIn;
            _canvasGroup.blocksRaycasts = fadeIn;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                // EaseOutQuad
                t = 1f - (1f - t) * (1f - t);
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
                yield return null;
            }

            _canvasGroup.alpha = targetAlpha;

            if (!fadeIn)
            {
                // Reset content when hidden
                if (_descriptionText != null)
                {
                    _descriptionText.text = "";
                }
            }
        }

        /// <summary>
        /// Auto-dismiss after delay
        /// </summary>
        private IEnumerator AutoDismissAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            Hide();
        }

        /// <summary>
        /// Handle dismiss button click
        /// </summary>
        private void OnDismissClicked()
        {
            ModLogger.Info("Update notification dismissed by user");
            Hide();
        }

        /// <summary>
        /// Handle view details button click - opens GitHub URL
        /// </summary>
        private void OnViewDetailsClicked()
        {
            // Default to base repo URL
            string url = "https://github.com/SirTidez/Behind-Bars";
            
            // Use changelog_url from version info if available (should be the base repo)
            if (_currentVersionInfo != null && !string.IsNullOrEmpty(_currentVersionInfo.changelog_url))
            {
                url = _currentVersionInfo.changelog_url;
            }
            
            ModLogger.Info($"Opening GitHub repository: {url}");
            Application.OpenURL(url);
        }

        /// <summary>
        /// Cleanup when destroyed
        /// </summary>
        private void OnDestroy()
        {
            if (_fadeCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_fadeCoroutine);
            }
            if (_autoDismissCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_autoDismissCoroutine);
            }
        }
    }
}

