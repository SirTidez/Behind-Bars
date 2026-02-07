using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
#endif

namespace Behind_Bars.Systems.CrimeDetection
{
    /// <summary>
    /// Manages UI components related to the crime detection system
    /// </summary>
    public class CrimeUIManager
    {
        private static CrimeUIManager _instance;
        public static CrimeUIManager Instance => _instance ??= new CrimeUIManager();
        
        private GameObject _uiManager;
        private WantedLevelUI _wantedLevelUI;
        private bool _isInitialized = false;
        
        public void Initialize()
        {
            if (_isInitialized)
            {
                ModLogger.Debug("CrimeUIManager already initialized");
                return;
            }
                
            try
            {
                ModLogger.Info("Initializing CrimeUIManager...");
                
                // Create a persistent UI manager object
                _uiManager = new GameObject("CrimeUIManager");
                GameObject.DontDestroyOnLoad(_uiManager);
                
                // Add the WantedLevelUI component using IL2CPP-safe method
#if !MONO
                // IL2CPP-safe component addition
                _wantedLevelUI = BBHelpers.AddComponentSafe<WantedLevelUI>(_uiManager);
#else
                _wantedLevelUI = _uiManager.AddComponent<WantedLevelUI>();
#endif

                // Manually initialize the UI immediately (don't wait for Unity Start() to be called)
                if (_wantedLevelUI != null)
                {
                    _wantedLevelUI.CreateWantedLevelUI();
                    ModLogger.Info("WantedLevelUI CreateWantedLevelUI() called manually");
                }
                else
                {
                    ModLogger.Error("Failed to create WantedLevelUI component");
                }
                
                _isInitialized = true;
                ModLogger.Info("✓ CrimeUIManager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initializing CrimeUIManager: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }
        
        public void Cleanup()
        {
            try
            {
                if (_uiManager != null)
                {
                    GameObject.Destroy(_uiManager);
                    _uiManager = null;
                }
                
                _wantedLevelUI = null;
                _isInitialized = false;
                
                ModLogger.Info("CrimeUIManager cleaned up");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error cleaning up CrimeUIManager: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Show detailed crime information
        /// </summary>
        public void ShowCrimeDetails()
        {
            _wantedLevelUI?.ShowCrimeDetails();
        }
        
        /// <summary>
        /// Check if UI is ready
        /// </summary>
        public bool IsInitialized => _isInitialized;
    }
}
