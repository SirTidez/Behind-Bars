using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.UI;

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
                return;
                
            try
            {
                // Create a persistent UI manager object
                _uiManager = new GameObject("CrimeUIManager");
                GameObject.DontDestroyOnLoad(_uiManager);
                
                // Add the WantedLevelUI component
                _wantedLevelUI = _uiManager.AddComponent<WantedLevelUI>();
                
                _isInitialized = true;
                ModLogger.Info("CrimeUIManager initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initializing CrimeUIManager: {ex.Message}");
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