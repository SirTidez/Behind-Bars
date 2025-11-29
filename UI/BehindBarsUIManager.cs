using System;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.CrimeTracking;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;
using Object = UnityEngine.Object;

#if !MONO
using Il2CppTMPro;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI.Phone;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppInterop.Runtime.Attributes;
#else
using TMPro;
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
                ModLogger.Debug("Initializing BehindBarsUIManager...");
                
                // Initialize font caching first
                InitializeFontCache();
                
                // Load UI prefab from asset bundle
                LoadUIPrefab();
                
                // Initialize parole status UI
                InitializeParoleStatusUI();
                
                // Initialize wanted level UI
                InitializeWantedLevelUI();
                
                _isInitialized = true;
                ModLogger.Debug("✓ BehindBarsUIManager initialized successfully");
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
            if (Core.EnableDebugLogging)
            {
                TMPFontFix.ListAllGameFonts();
            }
        }

        /// <summary>
        /// Load the UI prefab from the asset bundle
        /// </summary>
        private void LoadUIPrefab()
        {
            LoadUIPrefabInternal();
        }

        /// <summary>
        /// Retry loading the UI prefab (public method for retry after bundle is loaded)
        /// </summary>
        public void RetryLoadUIPrefab()
        {
            if (_uiPrefab == null)
            {
                ModLogger.Debug("Retrying to load UI prefab...");
                LoadUIPrefabInternal();
            }
        }

        /// <summary>
        /// Internal method to load the UI prefab from the asset bundle
        /// </summary>
        private void LoadUIPrefabInternal()
        {
            try
            {
                ModLogger.Debug("Loading BehindBarsUI prefab from asset bundle...");
                
                // Use the cached jail bundle which contains the UI prefab
                var bundle = Core.CachedJailBundle;
                if (bundle == null)
                {
                    ModLogger.Error("Jail asset bundle not loaded - cannot load UI prefab");
                    ModLogger.Debug("Will retry when bundle becomes available");
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
                    ModLogger.Debug($"✓ Loaded BehindBarsUI prefab: {_uiPrefab.name}");
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

                ModLogger.Debug($"✓ Jail info UI created in overlay canvas '{canvas.name}' with sorting order {canvas.sortingOrder}");
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

        // === PAROLE STATUS SYSTEM ===

        private GameObject? _paroleStatusManager;
        private ParoleStatusUI? _paroleStatusUI;
        private Coroutine? _paroleStatusUpdateCoroutine;
        private bool _isSubscribedToArrestEvents = false;
        
        // === BAIL UI SYSTEM ===

        private GameObject? _bailManager;
        private BailUI? _bailUI;
        
        // === PAROLE CONDITIONS UI SYSTEM ===

        private GameObject? _paroleConditionsManager;
        private ParoleConditionsUI? _paroleConditionsUI;
        
        // === WANTED LEVEL UI SYSTEM ===

        private GameObject? _wantedLevelManager;
        private WantedLevelUI? _wantedLevelUI;
        
        // === UPDATE NOTIFICATION SYSTEM ===

        private UpdateNotificationUI? _updateNotificationUI;

        /// <summary>
        /// Show update notification UI
        /// </summary>
        public void ShowUpdateNotification(Utils.VersionInfo versionInfo)
        {
            try
            {
                if (versionInfo == null || string.IsNullOrEmpty(versionInfo.version))
                {
                    ModLogger.Error("Cannot show update notification - invalid version info");
                    return;
                }

                // Create UpdateNotificationUI component if needed
                if (_updateNotificationUI == null)
                {
                    GameObject updateUIObj = new GameObject("UpdateNotificationUIManager");
                    Object.DontDestroyOnLoad(updateUIObj);
                    
#if !MONO
                    _updateNotificationUI = updateUIObj.AddComponent<UpdateNotificationUI>();
#else
                    _updateNotificationUI = updateUIObj.AddComponent<UpdateNotificationUI>();
#endif
                }

                if (_updateNotificationUI != null)
                {
                    _updateNotificationUI.Show(versionInfo);
                    ModLogger.Info($"Update notification displayed for version {versionInfo.version}");
                }
                else
                {
                    ModLogger.Error("Failed to create UpdateNotificationUI component");
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error showing update notification: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Hide update notification UI
        /// </summary>
        public void HideUpdateNotification()
        {
            try
            {
                _updateNotificationUI?.Hide();
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error hiding update notification: {e.Message}");
            }
        }
        
        // === LOADING SCREEN SYSTEM ===

        private GameObject? _loadingScreenUI;
        private Text? _loadingText;
        private UnityEngine.UI.Slider? _loadingProgressBar;
        private Text? _loadingProgressText;
        private Text? _loadingWarningText;
        private Text? _messageOfTheDayText;
        
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

                ModLogger.Debug("OfficerCommandUI manager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating officer command UI: {ex.Message}");
            }
        }

        // === PAROLE STATUS SYSTEM ===

        /// <summary>
        /// Initialize parole status UI
        /// </summary>
        public void InitializeParoleStatusUI()
        {
            try
            {
                if (_paroleStatusUI == null)
                {
                    CreateParoleStatusUI();
                }

                if (_paroleStatusUI != null && _paroleStatusUpdateCoroutine == null)
                {
                    // Start update coroutine
                    _paroleStatusUpdateCoroutine = MelonLoader.MelonCoroutines.Start(UpdateParoleStatusCoroutine()) as Coroutine;
                    ModLogger.Debug("Parole status UI update coroutine started");
                }
                
                // Subscribe to arrest/release events for immediate UI updates
                SubscribeToArrestReleaseEvents();
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initializing parole status UI: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Subscribe to Player.local.onArrested and ReleaseManager.OnReleaseCompleted events
        /// for immediate parole status UI visibility control
        /// </summary>
        private void SubscribeToArrestReleaseEvents()
        {
            if (_isSubscribedToArrestEvents)
            {
                return;
            }
            
            // Subscribe to ReleaseManager events (static, always available)
            ReleaseManager.OnReleaseCompleted += HandlePlayerReleased;
            ModLogger.Debug("ParoleStatusUI subscribed to ReleaseManager.OnReleaseCompleted");
            
            // Start coroutine to subscribe to Player.local.onArrested when available
            MelonLoader.MelonCoroutines.Start(WaitForPlayerAndSubscribeToArrest());
            
            _isSubscribedToArrestEvents = true;
        }
        
        /// <summary>
        /// Coroutine that waits for Player.Local to become available, then subscribes to onArrested
        /// </summary>
        private IEnumerator WaitForPlayerAndSubscribeToArrest()
        {
            ModLogger.Debug("ParoleStatusUI waiting for Player.Local to subscribe to onArrested...");
            
            int attempts = 0;
            const int maxAttempts = 300; // 30 seconds max wait
            
            while (attempts < maxAttempts)
            {
                Player localPlayer = null;
                
                try
                {
#if !MONO
                    localPlayer = Player.Local;
                    if (localPlayer != null && localPlayer.Pointer != IntPtr.Zero)
                    {
                        // Subscribe to onArrested
                        localPlayer.onArrested.AddListener(new Action(HandlePlayerArrested));
                        ModLogger.Info($"ParoleStatusUI subscribed to Player.local.onArrested for {localPlayer.name}");
                        yield break;
                    }
#else
                    localPlayer = Player.Local;
                    if (localPlayer != null)
                    {
                        // Subscribe to onArrested
                        localPlayer.onArrested.AddListener(HandlePlayerArrested);
                        ModLogger.Info($"ParoleStatusUI subscribed to Player.local.onArrested for {localPlayer.name}");
                        yield break;
                    }
#endif
                }
                catch (Exception ex)
                {
                    // Player.Local not available yet
                    if (attempts % 50 == 0) // Log every 5 seconds
                    {
                        ModLogger.Debug($"ParoleStatusUI still waiting for Player.Local... ({attempts / 10}s elapsed)");
                    }
                }
                
                attempts++;
                yield return new WaitForSeconds(0.1f);
            }
            
            ModLogger.Warn("ParoleStatusUI: Gave up waiting for Player.Local after 30 seconds");
        }
        
        /// <summary>
        /// Event handler called when Player.local.onArrested fires - hide parole status UI immediately
        /// </summary>
        private void HandlePlayerArrested()
        {
            try
            {
                ModLogger.Info("ParoleStatusUI: HandlePlayerArrested event received - hiding UI immediately");
                HideParoleStatus();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleStatusUI: Error in HandlePlayerArrested: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Event handler called when ReleaseManager.OnReleaseCompleted fires
        /// Note: ShowParoleStatus() is called after release summary UI is dismissed
        /// </summary>
        private void HandlePlayerReleased(Player player, ReleaseManager.ReleaseType releaseType)
        {
            try
            {
                ModLogger.Info($"ParoleStatusUI: HandlePlayerReleased event received for {player?.name} (type: {releaseType})");
                // ShowParoleStatus() will be called after release summary UI is dismissed in ReleaseManager
                // The coroutine will automatically show the UI if player is on parole
            }
            catch (Exception ex)
            {
                ModLogger.Error($"ParoleStatusUI: Error in HandlePlayerReleased: {ex.Message}");
            }
        }

        /// <summary>
        /// Create the parole status UI component
        /// </summary>
        private void CreateParoleStatusUI()
        {
            try
            {
                // Create a persistent manager object
                _paroleStatusManager = new GameObject("ParoleStatusManager");
                GameObject.DontDestroyOnLoad(_paroleStatusManager);

                // Add the ParoleStatusUI component
#if !MONO
                // IL2CPP-safe component addition
                var componentType = Il2CppInterop.Runtime.Il2CppType.Of<ParoleStatusUI>();
                var component = _paroleStatusManager.AddComponent(componentType);
                _paroleStatusUI = component.Cast<ParoleStatusUI>();
#else
                _paroleStatusUI = _paroleStatusManager.AddComponent<ParoleStatusUI>();
#endif

                // Manually initialize the UI immediately
                if (_paroleStatusUI != null)
                {
                    _paroleStatusUI.CreateUI();
                    ModLogger.Debug("ParoleStatusUI CreateUI() called manually");
                }

                ModLogger.Debug("ParoleStatusUI manager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating parole status UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Show parole status UI
        /// </summary>
        public void ShowParoleStatus()
        {
            try
            {
                if (_paroleStatusUI == null)
                {
                    CreateParoleStatusUI();
                }

                if (_paroleStatusUI == null)
                {
                    ModLogger.Error("Failed to create parole status UI");
                    return;
                }

                var statusData = GetParoleStatusData();
                if (statusData != null && statusData.IsOnParole)
                {
                    _paroleStatusUI.Show(statusData);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing parole status: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide parole status UI
        /// </summary>
        public void HideParoleStatus()
        {
            try
            {
                if (_paroleStatusUI != null)
                {
                    _paroleStatusUI.Hide();
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding parole status: {ex.Message}");
            }
        }

        /// <summary>
        /// Update parole status UI
        /// </summary>
        public void UpdateParoleStatus()
        {
            try
            {
                if (_paroleStatusUI == null)
                {
                    return;
                }

                var statusData = GetParoleStatusData();
                if (statusData != null)
                {
                    if (statusData.IsOnParole)
                    {
                        _paroleStatusUI.UpdateStatus(statusData);
                    }
                    else
                    {
                        _paroleStatusUI.Hide();
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating parole status: {ex.Message}");
            }
        }

        /// <summary>
        /// Get parole status data for current player
        /// </summary>
        private ParoleStatusData? GetParoleStatusData()
        {
            try
            {
#if !MONO
                var player = Il2CppScheduleOne.PlayerScripts.Player.Local;
#else
                var player = ScheduleOne.PlayerScripts.Player.Local;
#endif
                if (player == null)
                {
                    return null;
                }

                // CRITICAL: Don't show parole status UI if player is in jail
                // Use IsInJail to check jail status (set immediately on arrest, before sentence tracking starts)
                if (JailTimeTracker.Instance != null && JailTimeTracker.Instance.IsInJail(player))
                {
                    return new ParoleStatusData { IsOnParole = false };
                }

                // Get cached rap sheet (loads from file only once)
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet == null)
                {
                    // No rap sheet available - player is not on parole
                    return new ParoleStatusData { IsOnParole = false };
                }

                var paroleRecord = rapSheet.CurrentParoleRecord;
                if (paroleRecord == null || !paroleRecord.IsOnParole())
                {
                    return new ParoleStatusData { IsOnParole = false };
                }

                var (isParole, remainingTime) = paroleRecord.GetParoleStatus();
                var searchProbability = rapSheet.GetSearchProbability();
                var searchPercent = Mathf.RoundToInt(searchProbability * 100f);

                return new ParoleStatusData
                {
                    IsOnParole = true,
                    TimeRemaining = remainingTime,
                    TimeRemainingFormatted = FormatTimeRemaining(remainingTime),
                    SupervisionLevel = rapSheet.LSILevel,
                    SearchProbabilityPercent = searchPercent,
                    ViolationCount = paroleRecord.GetViolationCount()
                };
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error getting parole status data: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Format time remaining in a human-readable format (now uses game time)
        /// </summary>
        private string FormatTimeRemaining(float gameMinutes)
        {
            if (gameMinutes <= 0)
            {
                return "Expired";
            }

            // Use GameTimeManager to format game time
            return GameTimeManager.FormatGameTime(gameMinutes);
        }

        /// <summary>
        /// Coroutine to periodically update parole status display.
        /// NOTE: Primary visibility control is event-driven via HandlePlayerArrested (from Player.local.onArrested)
        /// and HandlePlayerReleased (from ReleaseManager.OnReleaseCompleted).
        /// This coroutine handles:
        ///   - Periodic data updates (time remaining, violations)
        ///   - Safety fallback for jail status (in case events miss)
        ///   - Auto-showing UI when player is on parole after release
        /// </summary>
        private IEnumerator UpdateParoleStatusCoroutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(1f); // Update every second

                try
                {
                    var statusData = GetParoleStatusData();
                    if (statusData == null)
                        continue;

                    // SAFETY FALLBACK: Hide UI if player is in jail
                    // Primary hiding is done by HandlePlayerArrested event, but this catches edge cases
                    // where the event might not fire or JailTimeTracker.SetInJail was called independently
#if !MONO
                    var player = Il2CppScheduleOne.PlayerScripts.Player.Local;
#else
                    var player = ScheduleOne.PlayerScripts.Player.Local;
#endif
                    if (player != null && JailTimeTracker.Instance != null && JailTimeTracker.Instance.IsInJail(player))
                    {
                        if (_paroleStatusUI != null && _paroleStatusUI.IsVisible())
                        {
                            _paroleStatusUI.Hide();
                            ModLogger.Debug("ParoleStatusUI: Safety fallback hid UI (JailTimeTracker.IsInJail = true)");
                        }
                        continue;
                    }

                    // Update parole status display based on current state
                    if (statusData.IsOnParole)
                    {
                        if (_paroleStatusUI == null)
                        {
                            CreateParoleStatusUI();
                        }

                        if (_paroleStatusUI != null)
                        {
                            if (!_paroleStatusUI.IsVisible())
                            {
                                _paroleStatusUI.Show(statusData);
                            }
                            else
                            {
                                _paroleStatusUI.UpdateStatus(statusData);
                            }
                        }
                    }
                    else
                    {
                        // Not on parole - hide UI
                        if (_paroleStatusUI != null && _paroleStatusUI.IsVisible())
                        {
                            _paroleStatusUI.Hide();
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error in parole status update coroutine: {ex.Message}");
                }
            }
        }

        // === BAIL UI SYSTEM ===

        /// <summary>
        /// Show bail payment prompt
        /// </summary>
        public void ShowBailUI(float bailAmount)
        {
            try
            {
                // Create bail UI if it doesn't exist
                if (_bailUI == null)
                {
                    CreateBailUI();
                }

                if (_bailUI == null)
                {
                    ModLogger.Error("Failed to create bail UI");
                    return;
                }

                _bailUI.ShowBail(bailAmount);
                ModLogger.Debug($"Showing bail UI: ${bailAmount:F0}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing bail UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Update bail amount in UI
        /// </summary>
        public void UpdateBailUI(float bailAmount)
        {
            try
            {
                if (_bailUI == null)
                {
                    ShowBailUI(bailAmount);
                    return;
                }

                _bailUI.UpdateBailAmount(bailAmount);
                ModLogger.Debug($"Updated bail UI: ${bailAmount:F0}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating bail UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide bail payment prompt
        /// </summary>
        public void HideBailUI()
        {
            try
            {
                if (_bailUI != null)
                {
                    _bailUI.Hide();
                    ModLogger.Debug("Hiding bail UI");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding bail UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if bail UI is currently visible
        /// </summary>
        public bool IsBailUIVisible()
        {
            return _bailUI != null && _bailUI.IsVisible();
        }

        /// <summary>
        /// Get current bail amount being displayed
        /// </summary>
        public float GetCurrentBailAmount()
        {
            return _bailUI != null ? _bailUI.GetCurrentBailAmount() : 0f;
        }

        // === PAROLE CONDITIONS UI SYSTEM ===

        /// <summary>
        /// Show parole conditions UI with release summary data
        /// </summary>
        public void ShowParoleConditionsUI(Player player, float bailAmountPaid, float fineAmount, float termLengthGameMinutes, LSILevel lsiLevel,
            (int totalScore, int crimeCountScore, int severityScore, int violationScore, int pastParoleScore, LSILevel resultingLevel) lsiBreakdown,
            (float originalSentenceTime, float timeServed) jailTimeInfo, List<string> recentCrimes, List<string> generalConditions, List<string> specialConditions)
        {
            try
            {
                // Create parole conditions UI if it doesn't exist
                if (_paroleConditionsUI == null)
                {
                    CreateParoleConditionsUI();
                }

                if (_paroleConditionsUI == null)
                {
                    ModLogger.Error("Failed to create parole conditions UI");
                    return;
                }

                _paroleConditionsUI.Show(bailAmountPaid, fineAmount, termLengthGameMinutes, lsiLevel, lsiBreakdown, jailTimeInfo, recentCrimes, generalConditions, specialConditions);
                ModLogger.Info($"Showing parole conditions UI - Bail: ${bailAmountPaid:F0}, Fine: ${fineAmount:F0}, Term: {GameTimeManager.FormatGameTime(termLengthGameMinutes)}, LSI: {lsiLevel}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing parole conditions UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide parole conditions UI
        /// </summary>
        public void HideParoleConditionsUI()
        {
            try
            {
                if (_paroleConditionsUI != null)
                {
                    _paroleConditionsUI.Hide();
                    ModLogger.Debug("Hiding parole conditions UI");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding parole conditions UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if parole conditions UI is currently visible
        /// </summary>
        public bool IsParoleConditionsUIVisible()
        {
            return _paroleConditionsUI != null && _paroleConditionsUI.IsVisible();
        }

        /// <summary>
        /// Create the parole conditions UI component
        /// </summary>
        private void CreateParoleConditionsUI()
        {
            try
            {
                // Create a persistent manager object
                _paroleConditionsManager = new GameObject("ParoleConditionsManager");
                GameObject.DontDestroyOnLoad(_paroleConditionsManager);

                // Add the ParoleConditionsUI component
#if !MONO
                // IL2CPP-safe component addition
                var componentType = Il2CppInterop.Runtime.Il2CppType.Of<ParoleConditionsUI>();
                var component = _paroleConditionsManager.AddComponent(componentType);
                _paroleConditionsUI = component.Cast<ParoleConditionsUI>();
#else
                _paroleConditionsUI = _paroleConditionsManager.AddComponent<ParoleConditionsUI>();
#endif

                // Manually initialize the UI immediately
                if (_paroleConditionsUI != null)
                {
                    _paroleConditionsUI.CreateUI();
                    ModLogger.Info("ParoleConditionsUI CreateUI() called manually");
                }

                ModLogger.Debug("ParoleConditionsUI manager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating parole conditions UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Create the bail UI component
        /// </summary>
        private void CreateBailUI()
        {
            try
            {
                // Create a persistent manager object
                _bailManager = new GameObject("BailManager");
                GameObject.DontDestroyOnLoad(_bailManager);

                // Add the BailUI component
#if !MONO
                // IL2CPP-safe component addition
                var componentType = Il2CppInterop.Runtime.Il2CppType.Of<BailUI>();
                var component = _bailManager.AddComponent(componentType);
                _bailUI = component.Cast<BailUI>();
#else
                _bailUI = _bailManager.AddComponent<BailUI>();
#endif

                // Manually initialize the UI immediately
                if (_bailUI != null)
                {
                    _bailUI.CreateUI();
                    ModLogger.Info("BailUI CreateUI() called manually");
                }

                ModLogger.Debug("BailUI manager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating bail UI: {ex.Message}");
            }
        }

        // === WANTED LEVEL UI SYSTEM ===

        /// <summary>
        /// Initialize wanted level UI
        /// </summary>
        public void InitializeWantedLevelUI()
        {
            try
            {
                if (_wantedLevelUI == null)
                {
                    CreateWantedLevelUI();
                }

                ModLogger.Debug("WantedLevelUI initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initializing wanted level UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Create the wanted level UI component
        /// </summary>
        private void CreateWantedLevelUI()
        {
            try
            {
                // Create a persistent manager object
                _wantedLevelManager = new GameObject("WantedLevelManager");
                GameObject.DontDestroyOnLoad(_wantedLevelManager);

                // Add the WantedLevelUI component
#if !MONO
                // IL2CPP-safe component addition
                var componentType = Il2CppInterop.Runtime.Il2CppType.Of<WantedLevelUI>();
                var component = _wantedLevelManager.AddComponent(componentType);
                _wantedLevelUI = component.Cast<WantedLevelUI>();
#else
                _wantedLevelUI = _wantedLevelManager.AddComponent<WantedLevelUI>();
#endif

                // Manually initialize the UI immediately
                if (_wantedLevelUI != null)
                {
                    _wantedLevelUI.CreateWantedLevelUI();
                    ModLogger.Debug("WantedLevelUI CreateWantedLevelUI() called manually");
                }

                ModLogger.Debug("WantedLevelUI manager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating wanted level UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Show detailed crime information (for debugging)
        /// </summary>
        public void ShowCrimeDetails()
        {
            _wantedLevelUI?.ShowCrimeDetails();
        }

        // === LOADING SCREEN SYSTEM ===

        /// <summary>
        /// Show loading screen with progress bar
        /// </summary>
        public void ShowLoadingScreen(string message = "Loading Behind Bars...")
        {
            try
            {
                if (_loadingScreenUI == null)
                {
                    CreateLoadingScreenUI();
                }

                if (_loadingScreenUI != null)
                {
                    if (_loadingText != null)
                        _loadingText.text = message;
                    
                    // Update Message of the Day text in case it changed
                    if (_messageOfTheDayText != null)
                    {
                        _messageOfTheDayText.text = GetMessageOfTheDay();
                    }
                    
                    _loadingScreenUI.SetActive(true);
                    ModLogger.Debug($"Showing loading screen: {message}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing loading screen: {ex.Message}");
            }
        }

        /// <summary>
        /// Show Message of the Day / Instructions screen (can be called anytime)
        /// </summary>
        public void ShowInstructions()
        {
            ShowLoadingScreen("Behind Bars - Instructions");
        }

        /// <summary>
        /// Update loading progress (0.0 to 1.0)
        /// </summary>
        public void UpdateLoadingProgress(float progress, string statusMessage = "")
        {
            try
            {
                if (_loadingScreenUI == null || !_loadingScreenUI.activeInHierarchy)
                    return;

                progress = Mathf.Clamp01(progress);

                if (_loadingProgressBar != null)
                {
                    _loadingProgressBar.value = progress;
                }

                if (_loadingProgressText != null)
                {
                    int percent = Mathf.RoundToInt(progress * 100f);
                    _loadingProgressText.text = statusMessage != "" ? $"{statusMessage} ({percent}%)" : $"{percent}%";
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating loading progress: {ex.Message}");
            }
        }

        /// <summary>
        /// Hide loading screen
        /// </summary>
        public void HideLoadingScreen()
        {
            try
            {
                if (_loadingScreenUI != null && _loadingScreenUI.activeInHierarchy)
                {
                    _loadingScreenUI.SetActive(false);
                    ModLogger.Debug("Loading screen hidden");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding loading screen: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if loading screen is currently visible
        /// </summary>
        public bool IsLoadingScreenVisible()
        {
            return _loadingScreenUI != null && _loadingScreenUI.activeInHierarchy;
        }

        /// <summary>
        /// Create the loading screen UI elements
        /// </summary>
        private void CreateLoadingScreenUI()
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
                    var allCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                    if (allCanvases != null && allCanvases.Length > 0)
                    {
                        canvas = allCanvases[0];
                    }
                }
#else
                canvas = Singleton<HUD>.Instance?.canvas;
                if (canvas == null)
                {
                    canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                }
#endif
                
                if (canvas == null)
                {
                    ModLogger.Error("No canvas found for loading screen UI - creating overlay canvas");
                    canvas = FindOrCreateCanvas();
                }

                if (canvas == null)
                {
                    ModLogger.Error("Failed to find or create canvas for loading screen UI");
                    return;
                }

                // Create loading screen container
                GameObject loadingGO = new GameObject("LoadingScreen");
                loadingGO.transform.SetParent(canvas.transform, false);

                // Set up RectTransform to cover full screen
                RectTransform loadingRect = loadingGO.AddComponent<RectTransform>();
                loadingRect.anchorMin = Vector2.zero;
                loadingRect.anchorMax = Vector2.one;
                loadingRect.offsetMin = Vector2.zero;
                loadingRect.offsetMax = Vector2.zero;

                // Add semi-transparent background
                Image background = loadingGO.AddComponent<Image>();
                background.color = new Color(0, 0, 0, 0.8f);

                // Create loading text (top-center, above Message of the Day)
                GameObject loadingTextObj = new GameObject("LoadingText");
                loadingTextObj.transform.SetParent(loadingGO.transform, false);

                RectTransform loadingTextRect = loadingTextObj.AddComponent<RectTransform>();
                loadingTextRect.anchorMin = new Vector2(0.5f, 0.93f);
                loadingTextRect.anchorMax = new Vector2(0.5f, 0.93f);
                loadingTextRect.pivot = new Vector2(0.5f, 0.5f);
                loadingTextRect.anchoredPosition = Vector2.zero;
                loadingTextRect.sizeDelta = new Vector2(600, 50);

                _loadingText = loadingTextObj.AddComponent<Text>();
                _loadingText.text = "Loading Behind Bars...";
                _loadingText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                _loadingText.fontSize = 28;
                _loadingText.color = Color.white;
                _loadingText.alignment = TextAnchor.MiddleCenter;
                _loadingText.fontStyle = FontStyle.Bold;

                // Create progress bar container (bottom-center)
                GameObject progressBarContainer = new GameObject("ProgressBarContainer");
                progressBarContainer.transform.SetParent(loadingGO.transform, false);

                RectTransform progressBarRect = progressBarContainer.AddComponent<RectTransform>();
                progressBarRect.anchorMin = new Vector2(0.5f, 0.15f);
                progressBarRect.anchorMax = new Vector2(0.5f, 0.15f);
                progressBarRect.pivot = new Vector2(0.5f, 0.5f);
                progressBarRect.anchoredPosition = Vector2.zero;
                progressBarRect.sizeDelta = new Vector2(600, 40);

                // Create progress bar background
                GameObject progressBarBG = new GameObject("ProgressBarBG");
                progressBarBG.transform.SetParent(progressBarContainer.transform, false);

                RectTransform progressBarBGRect = progressBarBG.AddComponent<RectTransform>();
                progressBarBGRect.anchorMin = Vector2.zero;
                progressBarBGRect.anchorMax = Vector2.one;
                progressBarBGRect.offsetMin = new Vector2(0, 15);
                progressBarBGRect.offsetMax = new Vector2(0, 25);

                Image progressBarBGImage = progressBarBG.AddComponent<Image>();
                progressBarBGImage.color = new Color(0.2f, 0.2f, 0.2f, 1f);

                // Create progress bar fill
                GameObject progressBarFill = new GameObject("ProgressBarFill");
                progressBarFill.transform.SetParent(progressBarBG.transform, false);

                RectTransform progressBarFillRect = progressBarFill.AddComponent<RectTransform>();
                progressBarFillRect.anchorMin = Vector2.zero;
                progressBarFillRect.anchorMax = new Vector2(0, 1f);
                progressBarFillRect.offsetMin = Vector2.zero;
                progressBarFillRect.offsetMax = Vector2.zero;

                Image progressBarFillImage = progressBarFill.AddComponent<Image>();
                progressBarFillImage.color = new Color(0.2f, 0.6f, 1f, 1f); // Blue progress bar

                // Create slider component
                _loadingProgressBar = progressBarBG.AddComponent<UnityEngine.UI.Slider>();
                _loadingProgressBar.fillRect = progressBarFillRect;
                _loadingProgressBar.targetGraphic = progressBarFillImage;
                _loadingProgressBar.minValue = 0f;
                _loadingProgressBar.maxValue = 1f;
                _loadingProgressBar.value = 0f;

                // Create progress text (below progress bar)
                GameObject progressTextObj = new GameObject("ProgressText");
                progressTextObj.transform.SetParent(progressBarContainer.transform, false);

                RectTransform progressTextRect = progressTextObj.AddComponent<RectTransform>();
                progressTextRect.anchorMin = Vector2.zero;
                progressTextRect.anchorMax = Vector2.one;
                progressTextRect.offsetMin = new Vector2(0, -5);
                progressTextRect.offsetMax = new Vector2(0, 15);

                _loadingProgressText = progressTextObj.AddComponent<Text>();
                _loadingProgressText.text = "0%";
                _loadingProgressText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                _loadingProgressText.fontSize = 14;
                _loadingProgressText.color = Color.white;
                _loadingProgressText.alignment = TextAnchor.MiddleCenter;

                // Create Message of the Day (large centered text)
                GameObject motdObj = new GameObject("MessageOfTheDay");
                motdObj.transform.SetParent(loadingGO.transform, false);

                RectTransform motdRect = motdObj.AddComponent<RectTransform>();
                motdRect.anchorMin = new Vector2(0.5f, 0.35f);
                motdRect.anchorMax = new Vector2(0.5f, 0.65f);
                motdRect.pivot = new Vector2(0.5f, 0.5f);
                motdRect.anchoredPosition = Vector2.zero;
                motdRect.offsetMin = new Vector2(-500, 0);
                motdRect.offsetMax = new Vector2(500, 0);

                _messageOfTheDayText = motdObj.AddComponent<Text>();
                _messageOfTheDayText.text = GetMessageOfTheDay();
                _messageOfTheDayText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                _messageOfTheDayText.fontSize = 18;
                _messageOfTheDayText.color = Color.white;
                _messageOfTheDayText.alignment = TextAnchor.MiddleCenter;
                _messageOfTheDayText.fontStyle = FontStyle.Bold;
                _messageOfTheDayText.horizontalOverflow = HorizontalWrapMode.Wrap;
                _messageOfTheDayText.verticalOverflow = VerticalWrapMode.Overflow;
                _messageOfTheDayText.lineSpacing = 1.2f;

                // Create warning text (bottom)
                GameObject warningTextObj = new GameObject("WarningText");
                warningTextObj.transform.SetParent(loadingGO.transform, false);

                RectTransform warningTextRect = warningTextObj.AddComponent<RectTransform>();
                warningTextRect.anchorMin = new Vector2(0.5f, 0.1f);
                warningTextRect.anchorMax = new Vector2(0.5f, 0.1f);
                warningTextRect.pivot = new Vector2(0.5f, 0.5f);
                warningTextRect.anchoredPosition = Vector2.zero;
                warningTextRect.sizeDelta = new Vector2(900, 30);

                _loadingWarningText = warningTextObj.AddComponent<Text>();
                _loadingWarningText.text = "Loading assets and spawning NPCs... Please wait";
                _loadingWarningText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                _loadingWarningText.fontSize = 11;
                _loadingWarningText.color = new Color(0.8f, 0.8f, 0.8f); // Light gray
                _loadingWarningText.alignment = TextAnchor.MiddleCenter;

                _loadingScreenUI = loadingGO;

                // Start hidden
                _loadingScreenUI.SetActive(false);

                ModLogger.Debug("Loading screen UI created successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating loading screen UI: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Get the Message of the Day content
        /// </summary>
        private string GetMessageOfTheDay()
        {
            return "BEHIND BARS - EARLY ACCESS\n\n" +
                   "This mod is currently in early access. Bugs and issues are expected.\n\n" +
                   "Please report any bugs or issues in the Behind Bars Discord server.\n\n" +
                   "Note: Temporary FPS drops during initialization and asset spawning are normal and expected. " +
                   "This is due to the mod spawning multiple NPCs and assets.\n\n" +
                   "Door Controls:\n" +
                   "• Alt+1: Toggle Prison Entry Door\n" +
                   "• Alt+2: Toggle Booking Inner Door\n" +
                   "• Alt+3: Toggle Guard Door\n" +
                   "• Alt+4: Toggle Holding Cell Door 0\n" +
                   "• Alt+5: Toggle Holding Cell Door 1\n\n" +
                   "Jail Management:\n" +
                   "• Alt+L: Emergency Lockdown (locks all doors, emergency lighting)\n" +
                   "• Alt+U: Unlock All (unlocks all doors, normal lighting)\n" +
                   "• Alt+O: Open All Cells\n" +
                   "• Alt+C: Close All Cells\n" +
                   "• Alt+H: Blackout Lighting\n" +
                   "• Alt+N: Normal Lighting\n\n" +
                   "• Alt+0: Show this instructions screen";
        }
    }
}