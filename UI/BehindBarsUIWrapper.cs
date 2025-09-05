using UnityEngine;
using TMPro;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using System;
using System.Collections;
using System.Linq;
using UnityEngine.UI;
using MelonLoader;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Wrapper component for the BehindBarsUI panel that provides easy access to all UI elements
    /// </summary>
    public class BehindBarsUIWrapper : MonoBehaviour
    {
        [Header("UI References")]
        public GameObject panel;
        public TextMeshProUGUI title;
        public TextMeshProUGUI lblCrime;
        public TextMeshProUGUI txtCrime;
        public TextMeshProUGUI lblTime;
        public TextMeshProUGUI txtTime;
        public TextMeshProUGUI lblBail;
        public TextMeshProUGUI txtBail;
        public Button txtEntered;

        private bool _isInitialized = false;
        
        // Dynamic update tracking
        private float _remainingJailTime = 0f;
        private float _originalBailAmount = 0f;
        private float _currentBailAmount = 0f;
        private bool _isUpdating = false;
        private string _crimeText = "";
        
        // Game time scaling constants
        private const float REAL_SECONDS_PER_GAME_MINUTE = 1f; // 1 real second = 1 game minute in Schedule I
        private const float GAME_SECONDS_PER_GAME_MINUTE = 60f; // 60 game seconds in 1 game minute
        private const float BAIL_REDUCTION_INTERVAL_MINUTES = 5f; // Reduce bail every 5 game minutes

#if !MONO
        public BehindBarsUIWrapper(System.IntPtr ptr) : base(ptr) { }

        [HideFromIl2Cpp]
#endif
        void Start()
        {
            InitializeComponents();
        }

        /// <summary>
        /// Initialize UI components and cache references
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void InitializeComponents()
        {
            try
            {
                // ModLogger.Debug("Initializing BehindBarsUI components...");
                
                // Debug: Log all children to understand the structure
                LogChildrenRecursively(transform, 0);
                
                // Try multiple ways to find the panel
                panel = transform.Find("Panel")?.gameObject;
                if (panel == null)
                    panel = GetComponentInChildren<Canvas>()?.gameObject;
                if (panel == null)
                    panel = gameObject; // Use the root object if no panel found

                // ModLogger.Debug($"Using panel: {panel.name}");

                // Find all text components using multiple search strategies
                title = FindComponent<TextMeshProUGUI>("Title");
                lblCrime = FindComponent<TextMeshProUGUI>("lblCrime");
                txtCrime = FindComponent<TextMeshProUGUI>("txtCrime");
                lblTime = FindComponent<TextMeshProUGUI>("lblTime");
                txtTime = FindComponent<TextMeshProUGUI>("txtTime");
                lblBail = FindComponent<TextMeshProUGUI>("lblBail");
                txtBail = FindComponent<TextMeshProUGUI>("txtBail");
                txtEntered = FindComponent<Button>("txtEntered");

                // Log what we found
                // ModLogger.Debug($"UI Components found:");
                // ModLogger.Debug($"  Title: {(title != null ? "✓" : "✗")}");
                // ModLogger.Debug($"  lblCrime: {(lblCrime != null ? "✓" : "✗")}");
                // ModLogger.Debug($"  txtCrime: {(txtCrime != null ? "✓" : "✗")}");
                // ModLogger.Debug($"  lblTime: {(lblTime != null ? "✓" : "✗")}");
                // ModLogger.Debug($"  txtTime: {(txtTime != null ? "✓" : "✗")}");
                // ModLogger.Debug($"  lblBail: {(lblBail != null ? "✓" : "✗")}");
                // ModLogger.Debug($"  txtBail: {(txtBail != null ? "✓" : "✗")}");
                // ModLogger.Debug($"  txtEntered: {(txtEntered != null ? "✓" : "✗")}");

                // Apply font fixes
                TMPFontFix.FixAllTMPFonts(gameObject, "base");
                
                // Fix text wrapping settings
                FixTextWrapping();
                
                // Setup button if found
                if (txtEntered != null)
                {
#if !MONO
                    txtEntered.onClick.AddListener(new System.Action(OnEnteredButtonClicked));
#else
                    txtEntered.onClick.AddListener(OnEnteredButtonClicked);
#endif
                }

                _isInitialized = true;
                ModLogger.Info("✓ BehindBarsUI components initialized successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing BehindBarsUI components: {e.Message}");
            }
        }

        /// <summary>
        /// Show the UI panel
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void Show()
        {
            if (panel != null)
            {
                panel.SetActive(true);
                // ModLogger.Debug("BehindBarsUI panel shown");
            }
        }

        /// <summary>
        /// Hide the UI panel
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void Hide()
        {
            if (panel != null)
            {
                panel.SetActive(false);
                // ModLogger.Debug("BehindBarsUI panel hidden");
            }
        }

        /// <summary>
        /// Update the crime information display
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void SetCrimeInfo(string crime)
        {
            if (txtCrime != null)
            {
                txtCrime.text = crime;
                // ModLogger.Debug($"Crime info updated: {crime}");
            }
        }

        /// <summary>
        /// Update the time information display
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void SetTimeInfo(string timeInfo)
        {
            if (txtTime != null)
            {
                txtTime.text = timeInfo;
                // ModLogger.Debug($"Time info updated: {timeInfo}");
            }
        }

        /// <summary>
        /// Update the bail information display
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void SetBailInfo(string bailInfo)
        {
            if (txtBail != null)
            {
                txtBail.text = bailInfo;
                // ModLogger.Debug($"Bail info updated: {bailInfo}");
            }
        }

        /// <summary>
        /// Update all jail information at once
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void UpdateJailInfo(string crime, string timeInfo, string bailInfo)
        {
            SetCrimeInfo(crime);
            SetTimeInfo(timeInfo);
            SetBailInfo(bailInfo);
        }

        /// <summary>
        /// Start dynamic updates for jail time remaining and bail amount
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void StartDynamicUpdates(float jailTimeSeconds, float bailAmount)
        {
            // Convert jail time: if sentenced to X real minutes, make it X game hours
            // 1 real minute sentence = 1 game hour = 3600 game seconds
            float gameHours = jailTimeSeconds / 60f; // Convert real seconds to real minutes, then treat as game hours
            _remainingJailTime = gameHours * 3600f; // Convert game hours to game seconds
            
            _originalBailAmount = bailAmount;
            _currentBailAmount = bailAmount;
            _crimeText = txtCrime?.text ?? "";
            
            if (!_isUpdating && _remainingJailTime > 0)
            {
                _isUpdating = true;
                MelonCoroutines.Start(UpdateLoop());
                // ModLogger.Debug($"Started dynamic updates: Original sentence {jailTimeSeconds}s ({jailTimeSeconds/60f:F1} real minutes) -> {_remainingJailTime}s game time ({gameHours:F1} game hours), ${_currentBailAmount} bail");
            }
        }

        /// <summary>
        /// Stop dynamic updates
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void StopDynamicUpdates()
        {
            _isUpdating = false;
            // ModLogger.Debug("Stopped dynamic updates");
        }

        /// <summary>
        /// Dynamic update loop that runs every real second (1 game minute = 60 game seconds)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator UpdateLoop()
        {
            float lastBailReduction = 0f;
            
            while (_isUpdating && _remainingJailTime > 0 && gameObject != null)
            {
                yield return new WaitForSeconds(1f); // Update every real second (= 1 game minute)
                
                if (!_isUpdating) yield break;
                
                // Reduce jail time by 60 game seconds (1 game minute per real second)
                _remainingJailTime -= GAME_SECONDS_PER_GAME_MINUTE;
                if (_remainingJailTime < 0) _remainingJailTime = 0;
                
                // Track time for bail reduction (count game minutes)
                lastBailReduction += 1f; // 1 game minute passed
                if (lastBailReduction >= BAIL_REDUCTION_INTERVAL_MINUTES)
                {
                    if (_currentBailAmount > 0)
                    {
                        _currentBailAmount *= 0.9f; // 10% reduction every 5 game minutes
                        if (_currentBailAmount < 50f) _currentBailAmount = 50f; // Minimum bail
                        // ModLogger.Debug($"Bail reduced to ${_currentBailAmount:F0} after 5 game minutes");
                    }
                    lastBailReduction = 0f;
                }
                
                // Update the UI display every second for smooth countdown
                UpdateDisplayedValues();
                
                // If jail time is up, hide the UI
                if (_remainingJailTime <= 0)
                {
                    ModLogger.Info("Jail time completed - hiding UI");
                    Hide();
                    _isUpdating = false;
                    yield break;
                }
            }
            
            _isUpdating = false;
        }

        /// <summary>
        /// Update the displayed time and bail values
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void UpdateDisplayedValues()
        {
            try
            {
                // Update time remaining
                string timeText = FormatTime(_remainingJailTime);
                SetTimeInfo(timeText);
                
                // Update bail amount
                string bailText = FormatBail(_currentBailAmount);
                SetBailInfo(bailText);
                
                // ModLogger.Debug($"Updated UI: Time={timeText}, Bail={bailText}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error updating displayed values: {e.Message}");
            }
        }

        /// <summary>
        /// Format time in seconds to user-friendly display
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private string FormatTime(float timeInSeconds)
        {
            if (timeInSeconds <= 0)
                return "Released";
                
            int totalSeconds = (int)timeInSeconds;
            int days = totalSeconds / 86400;
            int hours = (totalSeconds % 86400) / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            
            if (days > 0)
                return $"{days}d {hours}h {minutes}m";
            else if (hours > 0)
                return $"{hours}h {minutes}m";
            else if (minutes > 0)
                return $"{minutes}m";
            else
                return "< 1m"; // Less than 1 minute remaining
        }

        /// <summary>
        /// Format bail amount to currency
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private string FormatBail(float amount)
        {
            if (amount <= 0)
                return "No Bail";
            else
                return $"${amount:F0}";
        }

        /// <summary>
        /// Fix text wrapping settings for all TMP components
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void FixTextWrapping()
        {
            // Fix crime text wrapping (main text that needs wrapping)
            if (txtCrime != null)
            {
                txtCrime.textWrappingMode = TextWrappingModes.Normal;
                txtCrime.overflowMode = TextOverflowModes.Overflow;
                // ModLogger.Debug("Fixed txtCrime text wrapping");
            }
            
            // Fix other text components that might need wrapping
            var allTextComponents = new[] { title, lblCrime, lblTime, txtTime, lblBail, txtBail };
            foreach (var textComp in allTextComponents)
            {
                if (textComp != null)
                {
                    textComp.textWrappingMode = TextWrappingModes.Normal;
                    textComp.overflowMode = TextOverflowModes.Overflow;
                }
            }
            
            // ModLogger.Debug("Applied text wrapping fixes to all TMP components");
        }

        /// <summary>
        /// Handle the "Entered" button click
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void OnEnteredButtonClicked()
        {
            ModLogger.Info("Entered button clicked - hiding jail info UI");
            Hide();
            
            // Could trigger additional actions here like:
            // - Mark player as having entered jail
            // - Start jail sequence
            // - etc.
        }

        /// <summary>
        /// Check if the UI wrapper is properly initialized
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Get debug information about this UI wrapper
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public string GetDebugInfo()
        {
            return $"BehindBarsUIWrapper: Initialized={_isInitialized}, " +
                   $"Components={title != null},{lblCrime != null},{txtCrime != null}," +
                   $"{lblTime != null},{txtTime != null},{lblBail != null},{txtBail != null},{txtEntered != null}";
        }

        /// <summary>
        /// Find a component using multiple search strategies
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private T FindComponent<T>(string name) where T : Component
        {
            // Strategy 1: Direct child of panel
            var direct = panel.transform.Find(name)?.GetComponent<T>();
            if (direct != null) return direct;
            
            // Strategy 2: Search recursively in panel
            var recursive = FindInChildren<T>(panel.transform, name);
            if (recursive != null) return recursive;
            
            // Strategy 3: Search by exact name match anywhere in the prefab
            var allComponents = GetComponentsInChildren<T>(true);
            foreach (var comp in allComponents)
            {
                if (comp.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return comp;
            }
            
            // ModLogger.Debug($"Could not find component {name} of type {typeof(T).Name}");
            return null;
        }

        /// <summary>
        /// Find component recursively in children
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private T FindInChildren<T>(Transform parent, string name) where T : Component
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var comp = child.GetComponent<T>();
                    if (comp != null) return comp;
                }
                
                // Recursive search
                var found = FindInChildren<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Log all children recursively for debugging
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void LogChildrenRecursively(Transform parent, int depth)
        {
            string indent = new string(' ', depth * 2);
            // ModLogger.Debug($"{indent}{parent.name} (Components: {string.Join(", ", parent.GetComponents<Component>().Select(c => c.GetType().Name))})");
            
            for (int i = 0; i < parent.childCount; i++)
            {
                LogChildrenRecursively(parent.GetChild(i), depth + 1);
            }
        }
    }
}