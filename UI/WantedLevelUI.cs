using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Harmony;


#if !MONO
using Il2CppTMPro;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using TMPro;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Simple UI component to display the player's current wanted level
    /// </summary>
    public class WantedLevelUI : MonoBehaviour
    {
#if !MONO
        public WantedLevelUI(System.IntPtr ptr) : base(ptr) { }
#endif

        private GameObject _wantedPanel;
        private TextMeshProUGUI _wantedLevelText;
        private TextMeshProUGUI _crimeCountText;
        private bool _isInitialized = false;
        private float _updateTimer = 0f;
        private const float UPDATE_INTERVAL = 1f; // Update every second
        
        public void Start()
        {
            // Only create if not already initialized (allows manual initialization)
            if (!_isInitialized)
            {
                CreateWantedLevelUI();
            }
        }
        
        public void Update()
        {
            if (!_isInitialized)
                return;
                
            _updateTimer += Time.deltaTime;
            if (_updateTimer >= UPDATE_INTERVAL)
            {
                _updateTimer = 0f;
                UpdateWantedDisplay();
            }
        }
        
        /// <summary>
        /// Create the wanted level UI elements (can be called manually for IL2CPP compatibility)
        /// </summary>
        public void CreateWantedLevelUI()
        {
            if (_isInitialized)
            {
                ModLogger.Debug("WantedLevelUI already initialized, skipping creation");
                return;
            }
            
            try
            {
                ModLogger.Debug("Creating WantedLevelUI...");
                
                // Find canvas using IL2CPP-safe methods (similar to notification system)
                Canvas mainCanvas = null;
                
#if !MONO
                // IL2CPP-safe canvas finding
                try
                {
                    var hudInstance = Singleton<Il2CppScheduleOne.UI.HUD>.Instance;
                    if (hudInstance != null && hudInstance.Pointer != System.IntPtr.Zero)
                    {
                        mainCanvas = hudInstance.canvas;
                    }
                }
                catch (System.Exception)
                {
                    // HUD singleton not available
                }
                
                if (mainCanvas == null)
                {
                    // Fallback: find any canvas in scene using IL2CPP-safe method
                    var allCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                    if (allCanvases != null && allCanvases.Length > 0)
                    {
                        mainCanvas = allCanvases[0];
                    }
                }
#else
                // Mono version
                mainCanvas = Singleton<HUD>.Instance?.canvas;
                if (mainCanvas == null)
                {
                    // Fallback: find any canvas in scene
                    mainCanvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                }
#endif
                
                if (mainCanvas == null)
                {
                    ModLogger.Error("Could not find main Canvas for WantedLevelUI - creating overlay canvas");
                    // Create our own canvas as last resort (similar to notification system)
                    mainCanvas = FindOrCreateCanvas();
                }
                
                if (mainCanvas == null)
                {
                    ModLogger.Error("Failed to find or create canvas for WantedLevelUI");
                    return;
                }
                
                // Create the wanted level panel
                _wantedPanel = new GameObject("WantedLevelPanel");
                _wantedPanel.transform.SetParent(mainCanvas.transform, false);
                
                // Add RectTransform component
                RectTransform panelRect = _wantedPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(1f, 1f); // Top-right corner
                panelRect.anchorMax = new Vector2(1f, 1f);
                panelRect.pivot = new Vector2(1f, 1f);
                panelRect.anchoredPosition = new Vector2(-10f, -10f); // 10 pixels from corner
                panelRect.sizeDelta = new Vector2(200f, 80f);
                
                // Add background image
                var panelImage = _wantedPanel.AddComponent<UnityEngine.UI.Image>();
                panelImage.color = new Color(0f, 0f, 0f, 0.7f); // Semi-transparent black
                
                // Create wanted level text
                GameObject wantedTextObj = new GameObject("WantedLevelText");
                wantedTextObj.transform.SetParent(_wantedPanel.transform, false);
                
                RectTransform wantedTextRect = wantedTextObj.AddComponent<RectTransform>();
                wantedTextRect.anchorMin = new Vector2(0f, 0.5f);
                wantedTextRect.anchorMax = new Vector2(1f, 1f);
                wantedTextRect.offsetMin = new Vector2(5f, 0f);
                wantedTextRect.offsetMax = new Vector2(-5f, -5f);
                
                _wantedLevelText = wantedTextObj.AddComponent<TextMeshProUGUI>();
                _wantedLevelText.text = "WANTED: 0.0";
                _wantedLevelText.fontSize = 14f;
                _wantedLevelText.color = Color.red;
                _wantedLevelText.fontStyle = FontStyles.Bold;
                _wantedLevelText.alignment = TextAlignmentOptions.Center;
                
                // Create crime count text
                GameObject crimeCountObj = new GameObject("CrimeCountText");
                crimeCountObj.transform.SetParent(_wantedPanel.transform, false);
                
                RectTransform crimeCountRect = crimeCountObj.AddComponent<RectTransform>();
                crimeCountRect.anchorMin = new Vector2(0f, 0f);
                crimeCountRect.anchorMax = new Vector2(1f, 0.5f);
                crimeCountRect.offsetMin = new Vector2(5f, 5f);
                crimeCountRect.offsetMax = new Vector2(-5f, 0f);
                
                _crimeCountText = crimeCountObj.AddComponent<TextMeshProUGUI>();
                _crimeCountText.text = "Crimes: 0";
                _crimeCountText.fontSize = 10f;
                _crimeCountText.color = Color.white;
                _crimeCountText.alignment = TextAlignmentOptions.Center;
                
                _isInitialized = true;
                ModLogger.Debug($"✓ WantedLevelUI created successfully on canvas '{mainCanvas.name}'");
                
                // Do an initial update to show current wanted level
                UpdateWantedDisplay();
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating WantedLevelUI: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }
        
        private void UpdateWantedDisplay()
        {
            try
            {
                if (!_isInitialized || _wantedPanel == null)
                {
                    return;
                }
                
                var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
                if (crimeDetectionSystem == null)
                {
                    // Hide panel if no crime system
                    if (_wantedPanel != null)
                        _wantedPanel.SetActive(false);
                    return;
                }
                
                float wantedLevel = crimeDetectionSystem.GetWantedLevel();
                var crimeSummary = crimeDetectionSystem.GetCrimeSummary();
                int totalCrimes = 0;
                
                foreach (var crimeCount in crimeSummary.Values)
                {
                    totalCrimes += crimeCount;
                }
                
                // Show panel only if player has crimes or wanted level
                bool shouldShow = wantedLevel > 0.1f;
                
                if (_wantedPanel != null)
                    _wantedPanel.SetActive(shouldShow);
                
                if (!shouldShow)
                    return;
                
                // Update wanted level text with color coding
                if (_wantedLevelText != null)
                {
                    _wantedLevelText.text = $"WANTED: {wantedLevel:F1}";
                    
                    // Color code by severity
                    if (wantedLevel >= 7f)
                        _wantedLevelText.color = Color.red; // High wanted
                    else if (wantedLevel >= 4f)
                        _wantedLevelText.color = new Color(1f, 0.5f, 0f); // Orange - moderate wanted
                    else if (wantedLevel >= 1f)
                        _wantedLevelText.color = Color.yellow; // Low wanted
                    else
                        _wantedLevelText.color = Color.white; // Very low
                }
                
                // Update crime count
                if (_crimeCountText != null)
                {
                    if (totalCrimes == 0)
                    {
                        _crimeCountText.text = "No active crimes";
                    }
                    else if (totalCrimes == 1)
                    {
                        _crimeCountText.text = "1 crime";
                    }
                    else
                    {
                        _crimeCountText.text = $"{totalCrimes} crimes";
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating wanted display: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Show detailed crime information in console (for debugging)
        /// </summary>
        public void ShowCrimeDetails()
        {
            var crimeDetectionSystem = HarmonyPatches.GetCrimeDetectionSystem();
            if (crimeDetectionSystem == null)
                return;
                
            var crimeSummary = crimeDetectionSystem.GetCrimeSummary();
            float totalFines = crimeDetectionSystem.CalculateTotalFines();
            float wantedLevel = crimeDetectionSystem.GetWantedLevel();
            
            ModLogger.Info("=== CRIME SUMMARY ===");
            ModLogger.Info($"Wanted Level: {wantedLevel:F2}");
            ModLogger.Info($"Total Fines: ${totalFines:F2}");
            
            if (crimeSummary.Count == 0)
            {
                ModLogger.Info("No active crimes");
            }
            else
            {
                foreach (var crime in crimeSummary)
                {
                    ModLogger.Info($"- {crime.Key}: {crime.Value}x");
                }
            }
            
            ModLogger.Info("=====================");
        }
        
        /// <summary>
        /// Find existing overlay canvas or create a new one for UI
        /// </summary>
        private Canvas FindOrCreateCanvas()
        {
            ModLogger.Debug("Creating dedicated overlay canvas for WantedLevelUI");
            
            var canvasGO = new GameObject("WantedLevel Overlay Canvas");
            var overlayCanvas = canvasGO.AddComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = 999; // High sorting order to appear on top
            
            // Add CanvasScaler for proper scaling
            var scaler = canvasGO.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f; // Balance between width and height matching
            
            // Add GraphicRaycaster for UI interaction
            canvasGO.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            
            // Don't destroy on load so it persists across scenes
            UnityEngine.Object.DontDestroyOnLoad(canvasGO);
            
            ModLogger.Debug("Created WantedLevel overlay canvas");
            return overlayCanvas;
        }
    }
}