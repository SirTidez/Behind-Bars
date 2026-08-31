using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems;
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
    /// Wrapper component for the Behind Bars jail-information panel. It owns presentation
    /// references and a game-time countdown, while release/custody authority remains in the jail
    /// systems that call this wrapper.
    /// </summary>
    public class BehindBarsUIWrapper : MonoBehaviour
    {
#if !MONO
        /// <summary>Creates the IL2CPP wrapper for the jail-information panel component.</summary>
        public BehindBarsUIWrapper(System.IntPtr ptr) : base(ptr) { }
#endif

        // Serialized/prefab bindings. InitializeComponents resolves these references at runtime
        // because the wrapper may be created from either the authored prefab or a fallback root.
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
        
        // Dynamic update tracking. Jail seconds are stored in game-time units after the explicit
        // real-seconds-to-game-hours conversion below; bail values remain currency amounts.
        private float _remainingJailTime = 0f;
        private float _originalJailTime = 0f; // Track original sentence time for bail lerping
        private float _originalBailAmount = 0f;
        private float _currentBailAmount = 0f;
        private bool _isUpdating = false;
        private string _crimeText = "";
        private bool _earlyReleaseTriggered = false; // Track if early release has been triggered for this sentence
        
        // Schedule I's current custody display maps one real second to one game minute. These
        // constants document the conversion boundary; they are unrelated to the tier UI's
        // intentionally real-time countdown.
        private const float REAL_SECONDS_PER_GAME_MINUTE = 1f; // 1 real second = 1 game minute in Schedule I
        private const float GAME_SECONDS_PER_GAME_MINUTE = 60f; // 60 game seconds in 1 game minute

        /// <summary>Unity lifecycle entry point that resolves prefab bindings before display calls.</summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        void Start()
        {
            ModLogger.Debug("BehindBarsUIWrapper.Start() called - initializing components");
            InitializeComponents();
        }

        /// <summary>
        /// Resolves the panel/text/button hierarchy using runtime-appropriate component lookup,
        /// applies font/wrapping fixes, and installs the entered-button listener once during
        /// component startup. Missing optional bindings are logged and leave their displays inert.
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
                ModLogger.Debug("✓ BehindBarsUI components initialized successfully");
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
        /// Updates the crime information display without changing panel visibility.
        /// </summary>
        /// <param name="crime">Display-ready crime description.</param>
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
        /// Updates the time information display without changing panel visibility.
        /// </summary>
        /// <param name="timeInfo">Display-ready time text, normally game-time formatted.</param>
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
        /// Updates the bail information display without changing panel visibility.
        /// </summary>
        /// <param name="bailInfo">Display-ready currency/status text.</param>
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
        /// Sets the panel to the terminal “Bailed Out” presentation and stops its dynamic loop.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void SetBailedOutStatus()
        {
            SetTimeInfo("Bailed Out");
            SetBailInfo("$0");
            // Stop dynamic updates since player is bailed out
            _isUpdating = false;
            ModLogger.Info("Jail UI updated to show 'Bailed Out' status");
        }

        /// <summary>
        /// Updates the crime, time, and bail displays as one forwarding operation.
        /// </summary>
        /// <param name="crime">Display-ready crime description.</param>
        /// <param name="timeInfo">Display-ready time/status text.</param>
        /// <param name="bailInfo">Display-ready currency/status text.</param>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void UpdateJailInfo(string crime, string timeInfo, string bailInfo)
        {
            SetCrimeInfo(crime);
            SetTimeInfo(timeInfo);
            SetBailInfo(bailInfo);
        }

        /// <summary>Updates the stored/displayed bail amount without restarting the jail-time loop.</summary>
        /// <param name="bailAmount">Current bail requirement in currency units.</param>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void UpdateBailAmount(float bailAmount)
        {
            _originalBailAmount = bailAmount;
            _currentBailAmount = bailAmount;
            // Update the display immediately
            SetBailInfo($"${bailAmount:F0}");
            ModLogger.Debug($"Updated bail amount to ${bailAmount:F0}");
        }

        /// <summary>
        /// Starts the one-second display loop for a sentence. The input is real-world seconds,
        /// converted here to the game's game-hour/game-second representation; the bail amount is
        /// held static while the timer runs.
        /// </summary>
        /// <param name="jailTimeSeconds">Sentence duration expressed in real-world seconds.</param>
        /// <param name="bailAmount">Bail requirement in currency units.</param>
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
        /// Stops the dynamic update loop by clearing its run flag. Existing display values remain
        /// until another caller updates or resets them.
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
        /// Stops the timer immediately and schedules a complete state reset for the next frame,
        /// preventing an in-flight update-loop iteration from writing stale values over booking UI.
        /// </summary>
        /// <param name="bookingBailAmount">Optional known booking bail; negative means unresolved.</param>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public void ResetTimer(float bookingBailAmount = -1f)
        {
            // CRITICAL: Stop updating FIRST to prevent race condition with UpdateLoop
            _isUpdating = false;

            // Small delay to ensure UpdateLoop has stopped
            MelonCoroutines.Start(CompleteTimerReset(bookingBailAmount));
        }

        /// <summary>
        /// Completes the deferred reset after one frame, then clears sentence/bail/early-release
        /// state and renders the booking placeholder values.
        /// </summary>
        /// <param name="bookingBailAmount">Optional known booking bail; negative means unresolved.</param>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator CompleteTimerReset(float bookingBailAmount)
        {
            // Wait one frame to ensure UpdateLoop has exited
            yield return null;

            // Now safe to reset all values
            _remainingJailTime = 0f;
            _originalJailTime = 0f;
            bool hasBookingBail = bookingBailAmount >= 0f;
            _currentBailAmount = hasBookingBail ? bookingBailAmount : 0f;
            _originalBailAmount = hasBookingBail ? bookingBailAmount : 0f;
            _earlyReleaseTriggered = false;

            // Update UI to show reset state
            if (txtTime != null)
            {
                txtTime.text = "Booking in progress...";
            }
            if (txtBail != null)
            {
                txtBail.text = hasBookingBail ? $"${bookingBailAmount:F0}" : "Calculating...";
            }

            ModLogger.Info(hasBookingBail
                ? $"Timer reset for new booking with bail ${bookingBailAmount:F0}"
                : "Timer completely reset for new booking");
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
        /// Runs the legacy jail-information countdown once per real second, subtracting one game
        /// minute (sixty game seconds) per tick. It owns the optimistic-release guard and stops
        /// when booking, release, or scene teardown clears the update flag.
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
                
                // Bail amount stays static - don't decrease over time
                // The bail amount displayed should match the actual bail amount required for payment
                
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
                    var jailManager = Core.Instance?.JailManager;
                    if (jailManager != null)
                    {
                        jailManager.InitiateEnhancedRelease(Player.Local, ReleaseManager.ReleaseType.TimeServed);
                        ModLogger.Info("Optimistic enhanced release triggered - guard dispatched early, timer continues");
                    }
                    else
                    {
                        ModLogger.Error("JailManager not available - cannot trigger enhanced release");
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

                    var jailManager = Core.Instance?.JailManager;
                    if (jailManager != null)
                    {
                        jailManager.InitiateEnhancedRelease(Player.Local, ReleaseManager.ReleaseType.TimeServed);
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
        /// Recomputes and applies the displayed game-time sentence and static bail values from
        /// the wrapper's current countdown state.
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
        /// Formats game-time seconds through GameTimeManager, returning <c>Released</c> at zero.
        /// This legacy panel intentionally uses game-time formatting; the tier-status panel's
        /// separate real-time contract must not be copied here.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private string FormatTime(float timeInGameSeconds)
        {
            if (timeInGameSeconds <= 0)
                return "Released";
            
            // Convert game seconds to game minutes and use GameTimeManager
            float gameMinutes = timeInGameSeconds / 60f;
            return GameTimeManager.FormatGameTime(gameMinutes);
        }

        /// <summary>Formats a non-positive bail value as <c>No Bail</c>, otherwise as whole currency.</summary>
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
        /// Handles the entered-button click by hiding this legacy panel. The commented future
        /// actions below are intentionally not executed; booking/release transitions belong to
        /// the owning jail systems.
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
