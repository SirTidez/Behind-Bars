using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Harmony;
#if !MONO
using Il2CppTMPro;
#else
using TMPro;
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
            CreateWantedLevelUI();
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
        
        private void CreateWantedLevelUI()
        {
            try
            {
                // Find the main Canvas
                Canvas mainCanvas = FindObjectOfType<Canvas>();
                if (mainCanvas == null)
                {
                    ModLogger.Error("Could not find main Canvas for WantedLevelUI");
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
                ModLogger.Info("WantedLevelUI created successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating WantedLevelUI: {ex.Message}");
            }
        }
        
        private void UpdateWantedDisplay()
        {
            try
            {
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
                bool shouldShow = wantedLevel > 0.1f || totalCrimes > 0;
                
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
    }
}