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
                ModLogger.Info("Loading BehindBarsUI prefab from asset bundle...");
                
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
                ModLogger.Debug("Loading UI prefab in IL2CPP mode...");
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
                ModLogger.Error($"Stack trace: {e.StackTrace}");
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
            ModLogger.Info($"ShowJailInfoUI called: initialized={_isInitialized}, prefabLoaded={_uiPrefab != null}");
            
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
                ModLogger.Info("Creating jail info UI...");
                
                // Destroy existing UI first (including its canvas)
                if (_activeUI != null)
                {
                    ModLogger.Debug("Destroying existing UI");
                    DestroyJailInfoUI();
                }

                // Create a fresh overlay canvas
                ModLogger.Debug("Creating overlay canvas");
                var canvas = FindOrCreateCanvas();
                if (canvas == null)
                {
                    ModLogger.Error("Cannot create overlay canvas");
                    return;
                }

                // Instantiate the UI
                ModLogger.Debug("Instantiating UI prefab");
                _activeUI = UnityEngine.Object.Instantiate(_uiPrefab, canvas.transform);
                _activeUI.name = "[Behind Bars] Jail Info UI";

                // Add the wrapper component
                ModLogger.Debug("Adding UI wrapper component");
#if !MONO
                // IL2CPP-safe component addition
                try
                {
                    ModLogger.Debug("Using IL2CPP component addition method");
                    var wrapperComponent = _activeUI.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<BehindBarsUIWrapper>());
                    ModLogger.Debug("IL2CPP AddComponent succeeded, casting to BehindBarsUIWrapper");
                    _uiWrapper = wrapperComponent.Cast<BehindBarsUIWrapper>();
                    ModLogger.Debug("Cast to BehindBarsUIWrapper succeeded");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Failed to add BehindBarsUIWrapper component via IL2CPP method: {ex.Message}");
                    ModLogger.Error($"Stack trace: {ex.StackTrace}");
                    return;
                }
#else
                _uiWrapper = _activeUI.AddComponent<BehindBarsUIWrapper>();
#endif

                if (_uiWrapper == null)
                {
                    ModLogger.Error("Failed to add BehindBarsUIWrapper component - wrapper is null");
                    return;
                }
                else
                {
                    ModLogger.Debug("BehindBarsUIWrapper component added successfully");
                }

#if !MONO
                // In IL2CPP, Unity Start() method may not be called automatically
                // So we manually initialize the wrapper component
                ModLogger.Debug("Manually initializing BehindBarsUIWrapper for IL2CPP");
                try
                {
                    // Call the initialization directly
                    var initMethod = _uiWrapper.GetType().GetMethod("InitializeComponents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (initMethod != null)
                    {
                        initMethod.Invoke(_uiWrapper, null);
                        ModLogger.Debug("Manual initialization succeeded");
                    }
                    else
                    {
                        ModLogger.Error("Could not find InitializeComponents method for manual initialization");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Manual initialization failed: {ex.Message}");
                }
#endif

                // Wait a frame for components to initialize, then update info and start dynamic updates
                ModLogger.Debug("Starting UI update coroutine");
                MelonLoader.MelonCoroutines.Start(UpdateUIAfterFrame(crime, timeInfo, bailInfo, jailTimeSeconds, bailAmount));

                ModLogger.Info($"✓ Jail info UI created in overlay canvas '{canvas.name}' with sorting order {canvas.sortingOrder}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error showing jail info UI: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
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

        // === OFFICER COMMAND SYSTEM ===

        private GameObject? _officerCommandManager;
        private OfficerCommandUI? _officerCommandUI;
        private OfficerCommandData? _currentCommand;
        
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
                // Find or create canvas using IL2CPP-safe methods
                Canvas canvas = null;
                
#if !MONO
                // IL2CPP-safe canvas finding
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
                    // HUD singleton not available
                }
                
                if (canvas == null)
                {
                    // Fallback: find any canvas in scene using IL2CPP-safe method
                    var allCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                    if (allCanvases != null && allCanvases.Length > 0)
                    {
                        canvas = allCanvases[0];
                    }
                }
#else
                // Mono version
                canvas = Singleton<HUD>.Instance?.canvas;
                if (canvas == null)
                {
                    // Fallback: find any canvas in scene
                    canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                }
#endif
                
                if (canvas == null)
                {
                    ModLogger.Error("No canvas found for notification UI - creating overlay canvas");
                    // Create our own canvas as last resort
                    canvas = FindOrCreateCanvas();
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

        // === OFFICER COMMAND SYSTEM ===

        /// <summary>
        /// Show a persistent officer command notification
        /// </summary>
        public void ShowOfficerCommand(OfficerCommandData data)
        {
            try
            {
                // Create officer command UI if it doesn't exist
                if (_officerCommandUI == null)
                {
                    CreateOfficerCommandUI();
                }

                if (_officerCommandUI == null)
                {
                    ModLogger.Error("Failed to create officer command UI");
                    return;
                }

                // Store current command
                _currentCommand = data;

                // Show the command
                _officerCommandUI.ShowCommand(data);

                ModLogger.Debug($"Showing officer command: {data.CommandText}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing officer command: {ex.Message}");
            }
        }

        /// <summary>
        /// Update the current officer command
        /// </summary>
        public void UpdateOfficerCommand(OfficerCommandData data)
        {
            try
            {
                if (_officerCommandUI == null)
                {
                    ShowOfficerCommand(data);
                    return;
                }

                // Store current command
                _currentCommand = data;

                // Update the command
                _officerCommandUI.UpdateCommand(data);

                ModLogger.Debug($"Updating officer command: {data.CommandText}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating officer command: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide the officer command notification
        /// </summary>
        public void HideOfficerCommand()
        {
            try
            {
                if (_officerCommandUI != null)
                {
                    _officerCommandUI.Hide();
                    _currentCommand = null;
                    ModLogger.Debug("Hiding officer command");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding officer command: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if there's an active officer command
        /// </summary>
        public bool HasActiveOfficerCommand()
        {
            return _officerCommandUI != null && _officerCommandUI.IsVisible();
        }

        /// <summary>
        /// Get the current officer command data
        /// </summary>
        public OfficerCommandData? GetCurrentOfficerCommand()
        {
            return _currentCommand;
        }

        /// <summary>
        /// Create the officer command UI component
        /// </summary>
        private void CreateOfficerCommandUI()
        {
            try
            {
                // Create a persistent manager object
                _officerCommandManager = new GameObject("OfficerCommandManager");
                GameObject.DontDestroyOnLoad(_officerCommandManager);

                // Add the OfficerCommandUI component
#if !MONO
                // IL2CPP-safe component addition
                var componentType = Il2CppInterop.Runtime.Il2CppType.Of<OfficerCommandUI>();
                var component = _officerCommandManager.AddComponent(componentType);
                _officerCommandUI = component.Cast<OfficerCommandUI>();
#else
                _officerCommandUI = _officerCommandManager.AddComponent<OfficerCommandUI>();
#endif

                // Manually initialize the UI immediately (don't wait for Unity Start() to be called)
                if (_officerCommandUI != null)
                {
                    _officerCommandUI.CreateUI();
                    ModLogger.Info("OfficerCommandUI CreateUI() called manually");
                }

                ModLogger.Info("OfficerCommandUI manager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating officer command UI: {ex.Message}");
            }
        }
    }
}