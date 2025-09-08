using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems;
using Behind_Bars.Systems.Jail;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.Phone;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI.Phone;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Manager class for handling the BehindBarsUI system
    /// </summary>
    public class BehindBarsUIManager
    {
        private static BehindBarsUIManager? _instance;
        public static BehindBarsUIManager Instance => _instance ??= new BehindBarsUIManager();

        private GameObject? _uiPrefab;
        private GameObject? _activeUI;
        private BehindBarsUIWrapper? _uiWrapper;
        private bool _isInitialized = false;

        /// <summary>
        /// Initialize the UI manager and load assets
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
            {
                ModLogger.Debug("BehindBarsUIManager already initialized");
                return;
            }

            try
            {
                ModLogger.Info("Initializing BehindBarsUIManager...");
                
                // Initialize font caching first
                InitializeFontCache();
                
                // Load UI prefab from asset bundle
                LoadUIPrefab();
                
                _isInitialized = true;
                ModLogger.Info("✓ BehindBarsUIManager initialized successfully");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error initializing BehindBarsUIManager: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize TMP font cache with common game fonts
        /// </summary>
        private void InitializeFontCache()
        {
            ModLogger.Debug("Caching TMP fonts for Behind Bars UI...");
            
            // Cache the base font (OpenSans-Regular is commonly available)
            if (TMPFontFix.CacheFont("base", "OpenSans-Regular"))
            {
                ModLogger.Debug("✓ Cached base font: OpenSans-Regular");
            }
            else
            {
                // Fallback to other common fonts
                if (TMPFontFix.CacheFont("base", "ComicNeue") ||
                    TMPFontFix.CacheFont("base", "LiberationSans"))
                {
                    ModLogger.Debug("✓ Cached fallback base font");
                }
                else
                {
                    ModLogger.Warn("No suitable base font found for caching");
                }
            }
            
            // List all available fonts for debugging
            if (Constants.DEBUG_LOGGING)
            {
                TMPFontFix.ListAllGameFonts();
            }
        }

        /// <summary>
        /// Load the UI prefab from the asset bundle
        /// </summary>
        private void LoadUIPrefab()
        {
            try
            {
                // Use the cached jail bundle which contains the UI prefab
                var bundle = Core.CachedJailBundle;
                if (bundle == null)
                {
                    ModLogger.Error("Jail asset bundle not loaded - cannot load UI prefab");
                    return;
                }

                // Try to load the BehindBarsUI prefab (from logs, we know the exact path)
#if MONO
                _uiPrefab = bundle.LoadAsset<GameObject>("assets/behindbars/behindbarsui.prefab") ??
                           bundle.LoadAsset<GameObject>("BehindBarsUI") ??
                           bundle.LoadAsset<GameObject>("behindbarsui");
#else
                _uiPrefab = bundle.LoadAsset("assets/behindbars/behindbarsui.prefab", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                           bundle.LoadAsset("BehindBarsUI", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                           bundle.LoadAsset("behindbarsui", Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif

                if (_uiPrefab != null)
                {
                    ModLogger.Info($"✓ Loaded BehindBarsUI prefab: {_uiPrefab.name}");
                }
                else
                {
                    ModLogger.Error("BehindBarsUI prefab not found in asset bundle!");
                    
                    // Debug: List all available assets
                    var allAssets = bundle.GetAllAssetNames();
                    ModLogger.Debug("Available assets in bundle:");
                    foreach (var asset in allAssets)
                    {
                        ModLogger.Debug($"  - {asset}");
                    }
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error loading UI prefab: {e.Message}");
            }
        }

        /// <summary>
        /// Create and display the jail info UI
        /// </summary>
        public void ShowJailInfoUI(string crime = "Unknown", string timeInfo = "Unknown", string bailInfo = "Unknown")
        {
            ShowJailInfoUI(crime, timeInfo, bailInfo, 0f, 0f);
        }

        /// <summary>
        /// Create and display the jail info UI with dynamic updates
        /// </summary>
        public void ShowJailInfoUI(string crime, string timeInfo, string bailInfo, float jailTimeSeconds, float bailAmount)
        {
            if (!_isInitialized)
            {
                ModLogger.Error("BehindBarsUIManager not initialized - call Initialize() first");
                return;
            }

            if (_uiPrefab == null)
            {
                ModLogger.Error("UI prefab not loaded - cannot show jail info UI");
                return;
            }

            try
            {
                // Destroy existing UI first (including its canvas)
                if (_activeUI != null)
                {
                    DestroyJailInfoUI();
                }

                // Create a fresh overlay canvas
                var canvas = FindOrCreateCanvas();
                if (canvas == null)
                {
                    ModLogger.Error("Cannot create overlay canvas");
                    return;
                }

                // Instantiate the UI
                _activeUI = UnityEngine.Object.Instantiate(_uiPrefab, canvas.transform);
                _activeUI.name = "[Behind Bars] Jail Info UI";

                // Add the wrapper component
                _uiWrapper = _activeUI.AddComponent<BehindBarsUIWrapper>();

                // Wait a frame for components to initialize, then update info and start dynamic updates
                MelonLoader.MelonCoroutines.Start(UpdateUIAfterFrame(crime, timeInfo, bailInfo, jailTimeSeconds, bailAmount));

                ModLogger.Info($"✓ Jail info UI created in overlay canvas '{canvas.name}' with sorting order {canvas.sortingOrder}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error showing jail info UI: {e.Message}");
            }
        }

        /// <summary>
        /// Update UI info after a frame to ensure components are initialized
        /// </summary>
        private IEnumerator UpdateUIAfterFrame(string crime, string timeInfo, string bailInfo)
        {
            yield return UpdateUIAfterFrame(crime, timeInfo, bailInfo, 0f, 0f);
        }

        /// <summary>
        /// Update UI info after a frame with dynamic updates
        /// </summary>
        private IEnumerator UpdateUIAfterFrame(string crime, string timeInfo, string bailInfo, float jailTimeSeconds, float bailAmount)
        {
            yield return null; // Wait one frame

            if (_uiWrapper != null && _uiWrapper.IsInitialized)
            {
                _uiWrapper.UpdateJailInfo(crime, timeInfo, bailInfo);
                
                // Start dynamic updates if jail time is provided
                if (jailTimeSeconds > 0)
                {
                    _uiWrapper.StartDynamicUpdates(jailTimeSeconds, bailAmount);
                    ModLogger.Debug($"✓ UI info updated with dynamic updates: {jailTimeSeconds}s jail, ${bailAmount} bail");
                }
                else
                {
                    ModLogger.Debug("✓ UI info updated (static)");
                }
            }
            else
            {
                ModLogger.Warn("UI wrapper not initialized after frame wait");
            }
        }

        /// <summary>
        /// Hide the jail info UI
        /// </summary>
        public void HideJailInfoUI()
        {
            if (_uiWrapper != null)
            {
                _uiWrapper.Hide();
            }
        }

        /// <summary>
        /// Destroy the jail info UI completely
        /// </summary>
        public void DestroyJailInfoUI()
        {
            if (_activeUI != null)
            {
                // Get the canvas before destroying the UI
                var canvas = _activeUI.transform.parent?.GetComponent<Canvas>();
                
                // Destroy the UI
                UnityEngine.Object.DestroyImmediate(_activeUI);
                _activeUI = null;
                _uiWrapper = null;
                
                // Clean up the overlay canvas if it was created by us
                if (canvas != null && canvas.name == "Behind Bars Overlay Canvas")
                {
                    UnityEngine.Object.DestroyImmediate(canvas.gameObject);
                    ModLogger.Debug("Destroyed Behind Bars overlay canvas");
                }
                
                ModLogger.Debug("Jail info UI destroyed");
            }
        }

        /// <summary>
        /// Update the displayed jail information
        /// </summary>
        public void UpdateJailInfo(string crime, string timeInfo, string bailInfo)
        {
            if (_uiWrapper != null && _uiWrapper.IsInitialized)
            {
                _uiWrapper.UpdateJailInfo(crime, timeInfo, bailInfo);
            }
        }

        /// <summary>
        /// Find existing overlay canvas or create a new one for UI
        /// </summary>
        private Canvas FindOrCreateCanvas()
        {
            // Always create a dedicated overlay canvas for the jail UI
            // This ensures it appears on top and is properly isolated
            ModLogger.Debug("Creating dedicated overlay canvas for Behind Bars UI");
            
            var canvasGO = new GameObject("Behind Bars Overlay Canvas");
            var overlayCanvas = canvasGO.AddComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 1000; // Very high sorting order to appear on top of everything
            
            // Add CanvasScaler for proper scaling
            var scaler = canvasGO.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f; // Balance between width and height matching
            
            // Add GraphicRaycaster for UI interaction
            canvasGO.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            
            // Don't destroy on load so it persists across scenes
            UnityEngine.Object.DontDestroyOnLoad(canvasGO);
            
            ModLogger.Debug("Created overlay canvas with sorting order 1000");
            return overlayCanvas;
        }

        /// <summary>
        /// Check if the UI is currently visible
        /// </summary>
        public bool IsUIVisible => _activeUI != null && _activeUI.activeInHierarchy;

        /// <summary>
        /// Get the current UI wrapper instance
        /// </summary>
        public BehindBarsUIWrapper? GetUIWrapper() => _uiWrapper;

        // === BOOKING NOTIFICATION SYSTEM ===
        
        private GameObject? _notificationUI;
        private Text? _notificationText;
        private Coroutine? _notificationCoroutine;
        
        /// <summary>
        /// Show a booking notification to the player
        /// </summary>
        public void ShowNotification(string message, NotificationType type)
        {
            try
            {
                // Create notification UI if it doesn't exist
                if (_notificationUI == null)
                {
                    CreateNotificationUI();
                }
                
                if (_notificationUI == null || _notificationText == null)
                {
                    ModLogger.Error("Failed to create notification UI");
                    return;
                }
                
                // Set message and style based on type
                _notificationText.text = message;
                SetNotificationStyle(type);
                
                // Show notification with fade animation
                MelonLoader.MelonCoroutines.Start(ShowNotificationCoroutine());
                
                ModLogger.Debug($"Showing {type} notification: {message}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing notification: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Create the notification UI elements
        /// </summary>
        private void CreateNotificationUI()
        {
            try
            {
                // Find or create canvas
                Canvas canvas = Singleton<HUD>.Instance?.canvas;
                if (canvas == null)
                {
                    // Fallback: find any canvas in scene
                    canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                }
                
                if (canvas == null)
                {
                    ModLogger.Error("No canvas found for notification UI");
                    return;
                }
                
                // Create notification container
                GameObject notificationGO = new GameObject("BookingNotification");
                notificationGO.transform.SetParent(canvas.transform, false);
                
                // Set up RectTransform for top-center positioning
                RectTransform rectTransform = notificationGO.AddComponent<RectTransform>();
                rectTransform.anchorMin = new Vector2(0.5f, 1f);
                rectTransform.anchorMax = new Vector2(0.5f, 1f);
                rectTransform.pivot = new Vector2(0.5f, 1f);
                rectTransform.anchoredPosition = new Vector2(0, -50);
                rectTransform.sizeDelta = new Vector2(400, 60);
                
                // Add background panel
                Image background = notificationGO.AddComponent<Image>();
                background.color = new Color(0, 0, 0, 0.7f);
                
                // Create text element
                GameObject textGO = new GameObject("NotificationText");
                textGO.transform.SetParent(notificationGO.transform, false);
                
                RectTransform textRect = textGO.AddComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(10, 5);
                textRect.offsetMax = new Vector2(-10, -5);
                
                Text text = textGO.AddComponent<Text>();
                text.text = "";
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                text.fontSize = 16;
                text.color = Color.white;
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                
                _notificationUI = notificationGO;
                _notificationText = text;
                
                // Start hidden
                _notificationUI.SetActive(false);
                
                ModLogger.Debug("Notification UI created successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating notification UI: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set notification visual style based on type
        /// </summary>
        private void SetNotificationStyle(NotificationType type)
        {
            if (_notificationText == null) return;
            
            switch (type)
            {
                case NotificationType.Instruction:
                    _notificationText.color = Color.white;
                    break;
                case NotificationType.Progress:
                    _notificationText.color = Color.green;
                    break;
                case NotificationType.Direction:
                    _notificationText.color = Color.cyan;
                    break;
                case NotificationType.Warning:
                    _notificationText.color = Color.red;
                    break;
            }
        }
        
        /// <summary>
        /// Coroutine to handle notification display and fade
        /// </summary>
        private IEnumerator ShowNotificationCoroutine()
        {
            if (_notificationUI == null) yield break;
            
            // Stop any existing notification
            if (_notificationCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_notificationCoroutine);
            }
            
            // Show notification
            _notificationUI.SetActive(true);
            
            // Fade in
            CanvasGroup canvasGroup = _notificationUI.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
            {
                canvasGroup = _notificationUI.AddComponent<CanvasGroup>();
            }
            
            // Fade in animation
            float fadeInTime = 0.3f;
            for (float t = 0; t < fadeInTime; t += Time.deltaTime)
            {
                canvasGroup.alpha = t / fadeInTime;
                yield return null;
            }
            canvasGroup.alpha = 1f;
            
            // Hold for display duration
            yield return new WaitForSeconds(4f);
            
            // Fade out animation
            float fadeOutTime = 0.5f;
            for (float t = 0; t < fadeOutTime; t += Time.deltaTime)
            {
                canvasGroup.alpha = 1f - (t / fadeOutTime);
                yield return null;
            }
            canvasGroup.alpha = 0f;
            
            // Hide notification
            _notificationUI.SetActive(false);
        }
        
        /// <summary>
        /// Show a task list to the player (for booking progress)
        /// </summary>
        public void ShowTaskList(List<string> tasks)
        {
            // Combine tasks into a single notification
            string taskList = string.Join("\n", tasks);
            ShowNotification($"Booking Tasks:\n{taskList}", NotificationType.Instruction);
        }
        
        /// <summary>
        /// Hide any active notification
        /// </summary>
        public void HideNotification()
        {
            if (_notificationUI != null && _notificationUI.activeInHierarchy)
            {
                _notificationUI.SetActive(false);
            }
            
            if (_notificationCoroutine != null)
            {
                MelonLoader.MelonCoroutines.Stop(_notificationCoroutine);
                _notificationCoroutine = null;
            }
        }
    }
}