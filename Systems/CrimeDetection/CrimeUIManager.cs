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

        /// <summary>
        /// Gets the process-wide crime UI coordinator, creating it lazily on first access.
        /// </summary>
        public static CrimeUIManager Instance => _instance ??= new CrimeUIManager();

        // The GameObject is kept separate from the manager so Unity owns its lifetime;
        // Initialize makes it persistent and Cleanup explicitly destroys it.
        private GameObject _uiManager;
        private WantedLevelUI _wantedLevelUI;
        private bool _isInitialized = false;

        /// <summary>
        /// Creates the persistent crime UI host and its WantedLevelUI component once.
        /// </summary>
        /// <remarks>
        /// Once setup completes, the initialization guard makes later calls no-ops. The UI
        /// is manually created immediately so callers do not have to wait for Unity's normal
        /// component Start lifecycle. Setup failures are logged; the current implementation
        /// may leave a partially created host behind for cleanup or a later retry.
        /// </remarks>
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
        
        /// <summary>
        /// Destroys the persistent crime UI host and resets initialization state.
        /// </summary>
        /// <remarks>Calling cleanup is safe when initialization did not complete.</remarks>
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
        /// Requests the WantedLevelUI to show detailed crime information when available.
        /// </summary>
        public void ShowCrimeDetails()
        {
            _wantedLevelUI?.ShowCrimeDetails();
        }
        
        /// <summary>
        /// Gets whether the crime UI manager completed initialization.
        /// </summary>
        public bool IsInitialized => _isInitialized;
    }
}
