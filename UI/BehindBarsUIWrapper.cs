using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems.Jail;
using System;
using System.Collections;
using System.Linq;
using UnityEngine.UI;
using MelonLoader;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppTMPro;
#else
using TMPro;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Wrapper component for the BehindBarsUI panel that provides easy access to all UI elements
    /// </summary>
    public class BehindBarsUIWrapper : MonoBehaviour
    {
#if !MONO
        public BehindBarsUIWrapper(System.IntPtr ptr) : base(ptr) { }
#endif

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
        private float _originalJailTime = 0f; // Track original sentence time for bail lerping
        private float _originalBailAmount = 0f;
        private float _currentBailAmount = 0f;
        private bool _isUpdating = false;
        private string _crimeText = "";
        private bool _earlyReleaseTriggered = false; // Track if early release has been triggered for this sentence
        
        // Game time scaling constants
        private const float REAL_SECONDS_PER_GAME_MINUTE = 1f; // 1 real second = 1 game minute in Schedule I
        private const float GAME_SECONDS_PER_GAME_MINUTE = 60f; // 60 game seconds in 1 game minute

#if !MONO
        [HideFromIl2Cpp]
#endif
        void Start()
        {
            ModLogger.Debug("BehindBarsUIWrapper.Start() called - initializing components");
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
                ModLogger.Debug("Initializing BehindBarsUI components...");
                
                // Debug: Log all children to understand the structure
                LogChildrenRecursively(transform, 0);
                
                // Try multiple ways to find the panel
                panel = transform.Find("Panel")?.gameObject;
                if (panel == null)
                    panel = GetComponentInChildren<Canvas>()?.gameObject;
                if (panel == null)
                    panel = gameObject; // Use the root object if no panel found

                ModLogger.Debug($"Using panel: {panel.name}");

                // Find all text components using multiple search strategies
                ModLogger.Debug("Finding UI components...");
#if !MONO
                // IL2CPP-specific component finding
                title = FindIL2CPPTextComponent("Title");
                lblCrime = FindIL2CPPTextComponent("lblCrime");
                txtCrime = FindIL2CPPTextComponent("txtCrime");
                lblTime = FindIL2CPPTextComponent("lblTime");
                txtTime = FindIL2CPPTextComponent("txtTime");
                lblBail = FindIL2CPPTextComponent("lblBail");
                txtBail = FindIL2CPPTextComponent("txtBail");
                txtEntered = FindComponent<Button>("txtEntered");
#else
                // Mono version
                title = FindComponent<TextMeshProUGUI>("Title");
                lblCrime = FindComponent<TextMeshProUGUI>("lblCrime");
                txtCrime = FindComponent<TextMeshProUGUI>("txtCrime");
                lblTime = FindComponent<TextMeshProUGUI>("lblTime");
                txtTime = FindComponent<TextMeshProUGUI>("txtTime");
                lblBail = FindComponent<TextMeshProUGUI>("lblBail");
                txtBail = FindComponent<TextMeshProUGUI>("txtBail");
                txtEntered = FindComponent<Button>("txtEntered");
#endif

                // Log what we found
                ModLogger.Debug($"UI Components found:");
                ModLogger.Debug($"  Title: {(title != null ? "✓" : "✗")}");
                ModLogger.Debug($"  lblCrime: {(lblCrime != null ? "✓" : "✗")}");
                ModLogger.Debug($"  txtCrime: {(txtCrime != null ? "✓" : "✗")}");
                ModLogger.Debug($"  lblTime: {(lblTime != null ? "✓" : "✗")}");
                ModLogger.Debug($"  txtTime: {(txtTime != null ? "✓" : "✗")}");
                ModLogger.Debug($"  lblBail: {(lblBail != null ? "✓" : "✗")}");
                ModLogger.Debug($"  txtBail: {(txtBail != null ? "✓" : "✗")}");
                ModLogger.Debug($"  txtEntered: {(txtEntered != null ? "✓" : "✗")}");

                // Apply font fixes
                ModLogger.Debug("Applying font fixes...");
                TMPFontFix.FixAllTMPFonts(gameObject, "base");
                
                // Fix text wrapping settings
                ModLogger.Debug("Fixing text wrapping...");
                FixTextWrapping();
                
                // Setup button if found
                if (txtEntered != null)
                {
                    ModLogger.Debug("Setting up button click handler...");
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
                ModLogger.Error($"Stack trace: {e.StackTrace}");
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
            _originalJailTime = _remainingJailTime; // Store original time for bail lerping
            
            _originalBailAmount = bailAmount;
            _currentBailAmount = bailAmount;
            _crimeText = txtCrime?.text ?? "";
            
            if (!_isUpdating && _remainingJailTime > 0)
            {
                _isUpdating = true;

                // Reset early release flag for new sentence
                ResetEarlyReleaseFlag();

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
        /// Completely reset the timer for a new arrest (not just stop it)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void ResetTimer()
        {
            // CRITICAL: Stop updating FIRST to prevent race condition with UpdateLoop
            _isUpdating = false;

            // Small delay to ensure UpdateLoop has stopped
            MelonCoroutines.Start(CompleteTimerReset());
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator CompleteTimerReset()
        {
            // Wait one frame to ensure UpdateLoop has exited
            yield return null;

            // Now safe to reset all values
            _remainingJailTime = 0f;
            _originalJailTime = 0f;
            _currentBailAmount = 0f;
            _originalBailAmount = 0f;
            _earlyReleaseTriggered = false;

            // Update UI to show reset state
            if (txtTime != null)
            {
                txtTime.text = "Booking in progress...";
            }
            if (txtBail != null)
            {
                txtBail.text = "Calculating...";
            }

            ModLogger.Info("Timer completely reset for new booking");
        }

        /// <summary>
        /// Reset the early release flag for a new sentence
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private void ResetEarlyReleaseFlag()
        {
            _earlyReleaseTriggered = false;
        }

        /// <summary>
        /// Dynamic update loop that runs every real second (1 game minute = 60 game seconds)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator UpdateLoop()
        {
            while (_isUpdating && _remainingJailTime > 0 && gameObject != null)
            {
                yield return new WaitForSeconds(1f); // Update every real second (= 1 game minute)
                
                if (!_isUpdating) yield break;
                
                // Reduce jail time by 60 game seconds (1 game minute per real second)
                _remainingJailTime -= GAME_SECONDS_PER_GAME_MINUTE;
                if (_remainingJailTime < 0) _remainingJailTime = 0;
                
                // Update bail amount using linear interpolation based on time remaining
                if (_originalJailTime > 0 && _originalBailAmount > 0)
                {
                    // Calculate progress: 0 = just started, 1 = time is up
                    float progress = 1f - (_remainingJailTime / _originalJailTime);
                    
                    // Lerp from original bail to minimum bail (50) based on progress
                    float minBail = 50f;
                    _currentBailAmount = Mathf.Lerp(_originalBailAmount, minBail, progress);
                    
                    // Ensure we don't go below minimum
                    if (_currentBailAmount < minBail) _currentBailAmount = minBail;
                }
                
                // Update the UI display every second for smooth countdown
                UpdateDisplayedValues();
                
                // Optimistic release: Start the release process 15 seconds early to reduce wait time
                const float EARLY_RELEASE_BUFFER = 15f * GAME_SECONDS_PER_GAME_MINUTE; // 15 game minutes = 900 game seconds

                if (_remainingJailTime <= EARLY_RELEASE_BUFFER && _remainingJailTime > 0 && !_earlyReleaseTriggered)
                {
                    // CRITICAL: Don't trigger release if booking is still in progress
                    var bookingProcess = Core.JailController?.BookingProcessController;
                    if (bookingProcess != null && bookingProcess.IsBookingInProgress())
                    {
                        ModLogger.Debug("Optimistic release window reached but booking in progress - waiting for booking to complete");
                        yield return null; // Continue loop, don't trigger release yet
                        continue;
                    }

                    ModLogger.Info($"Starting optimistic release with {_remainingJailTime / GAME_SECONDS_PER_GAME_MINUTE:F1} game minutes remaining - timer continues running");

                    // Trigger the enhanced release system early for optimistic processing
                    var jailSystem = Core.Instance?.JailSystem;
                    if (jailSystem != null)
                    {
                        jailSystem.InitiateEnhancedRelease(Player.Local, ReleaseManager.ReleaseType.TimeServed);
                        ModLogger.Info("Optimistic enhanced release triggered - guard dispatched early, timer continues");
                    }
                    else
                    {
                        ModLogger.Error("JailSystem not available - cannot trigger enhanced release");
                    }

                    _earlyReleaseTriggered = true; // Prevent multiple early releases
                    // Don't hide UI or stop updates - let timer continue normally
                }

                // Legacy fallback: If somehow we reach exactly 0 time without early release
                if (_remainingJailTime <= 0)
                {
                    // CRITICAL: Don't trigger release if booking is still in progress
                    var bookingProcess = Core.JailController?.BookingProcessController;
                    if (bookingProcess != null && bookingProcess.IsBookingInProgress())
                    {
                        ModLogger.Warn("Jail time hit 0 but booking still in progress - NOT triggering release");
                        _isUpdating = false; // Stop the update loop
                        yield break;
                    }

                    ModLogger.Info("Jail time completed - fallback release trigger");

                    var jailSystem = Core.Instance?.JailSystem;
                    if (jailSystem != null)
                    {
                        jailSystem.InitiateEnhancedRelease(Player.Local, ReleaseManager.ReleaseType.TimeServed);
                        ModLogger.Info("Fallback enhanced release triggered");
                    }

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
        /// Find IL2CPP TextMeshProUGUI component specifically
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
        private TextMeshProUGUI FindIL2CPPTextComponent(string name)
        {
            ModLogger.Debug($"Searching for IL2CPP TextMeshProUGUI component: {name}");
            
            // Strategy 1: Direct child search
            var childTransform = panel.transform.Find(name);
            if (childTransform != null)
            {
                ModLogger.Debug($"Found child transform for {name}");
                var il2cppText = childTransform.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (il2cppText != null)
                {
                    ModLogger.Debug($"Found {name} via direct child search - casting to TextMeshProUGUI");
                    try
                    {
                        var cast = il2cppText.Cast<TextMeshProUGUI>();
                        ModLogger.Debug($"Successfully cast {name} to TextMeshProUGUI");
                        return cast;
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Failed to cast {name}: {ex.Message}");
                    }
                }
                else
                {
                    ModLogger.Debug($"Child {name} found but has no Il2CppTMPro.TextMeshProUGUI component");
                }
            }
            else
            {
                ModLogger.Debug($"No direct child found for {name}");
            }
            
            // Strategy 2: Recursive search
            var recursive = FindIL2CPPTextInChildren(panel.transform, name);
            if (recursive != null)
            {
                ModLogger.Debug($"Found {name} via recursive search");
                return recursive;
            }
            
            // Strategy 3: Search all Il2CppTMPro.TextMeshProUGUI components
            ModLogger.Debug($"Searching all Il2CppTMPro.TextMeshProUGUI components for {name}");
            try
            {
                var allIL2CPPTexts = GetComponentsInChildren<Il2CppTMPro.TextMeshProUGUI>(true);
                ModLogger.Debug($"Found {allIL2CPPTexts.Length} total Il2CppTMPro.TextMeshProUGUI components");
                
                foreach (var comp in allIL2CPPTexts)
                {
                    if (comp != null)
                    {
                        ModLogger.Debug($"Checking component: {comp.name} vs {name}");
                        if (comp.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            ModLogger.Debug($"Found {name} via component search - casting");
                            try
                            {
                                var cast = comp.Cast<TextMeshProUGUI>();
                                ModLogger.Debug($"Successfully cast {name} to TextMeshProUGUI via component search");
                                return cast;
                            }
                            catch (System.Exception ex)
                            {
                                ModLogger.Error($"Failed to cast {name} via component search: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error during component search for {name}: {ex.Message}");
            }
            
            ModLogger.Debug($"Could not find IL2CPP TextMeshProUGUI component: {name}");
            return null;
        }
        
        /// <summary>
        /// Find IL2CPP TextMeshProUGUI component recursively in children
        /// </summary>
        [HideFromIl2Cpp]
        private TextMeshProUGUI FindIL2CPPTextInChildren(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    ModLogger.Debug($"Found matching child in recursive search: {name}");
                    var il2cppText = child.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                    if (il2cppText != null)
                    {
                        ModLogger.Debug($"Child {name} has Il2CppTMPro.TextMeshProUGUI component - casting");
                        try
                        {
                            var cast = il2cppText.Cast<TextMeshProUGUI>();
                            ModLogger.Debug($"Successfully cast {name} in recursive search");
                            return cast;
                        }
                        catch (System.Exception ex)
                        {
                            ModLogger.Error($"Failed to cast {name} in recursive search: {ex.Message}");
                        }
                    }
                    else
                    {
                        ModLogger.Debug($"Child {name} found but has no Il2CppTMPro.TextMeshProUGUI component");
                    }
                }
                
                // Recursive search
                var found = FindIL2CPPTextInChildren(child, name);
                if (found != null) return found;
            }
            return null;
        }
#endif

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
            
#if !MONO
            // Strategy 4: IL2CPP-specific - Search for TextMeshProUGUI components manually
            if (typeof(T) == typeof(TextMeshProUGUI))
            {
                var allMonoBehaviours = GetComponentsInChildren<MonoBehaviour>(true);
                foreach (var comp in allMonoBehaviours)
                {
                    if (comp != null && comp.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        // Try to cast to TextMeshProUGUI
                        try
                        {
                            var tmpComp = comp.TryCast<Il2CppTMPro.TextMeshProUGUI>();
                            if (tmpComp != null)
                            {
                                return tmpComp.TryCast<T>();
                            }
                        }
                        catch (System.Exception)
                        {
                            // Cast failed, continue searching
                        }
                    }
                }
            }
#endif
            
            ModLogger.Debug($"Could not find component {name} of type {typeof(T).Name}");
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
            ModLogger.Debug($"{indent}{parent.name} (Components: {string.Join(", ", parent.GetComponents<Component>().Select(c => c.GetType().Name))})");
            
            for (int i = 0; i < parent.childCount; i++)
            {
                LogChildrenRecursively(parent.GetChild(i), depth + 1);
            }
        }
    }
}