using System;
using System.Collections;
using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
// Try to use game's EMapRegion if available, otherwise use fallback
// Note: If game's enum is not accessible, we'll use the fallback enum defined below
#else
using ScheduleOne.PlayerScripts;
// Try to use game's EMapRegion if available, otherwise use fallback
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Monitors player movement and emits events when player changes regions or moves significantly.
    /// Uses event-driven architecture instead of coroutines for better performance and responsiveness.
    /// </summary>
    public class PlayerLocationTracker : MonoBehaviour
    {
#if !MONO
        public PlayerLocationTracker(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Events

        /// <summary>
        /// Event fired when player changes map region
        /// </summary>
        public static event Action<Player, EMapRegion> OnPlayerRegionChanged;

        /// <summary>
        /// Event fired when player moves significantly (beyond threshold)
        /// </summary>
        public static event Action<Player, Vector3> OnPlayerSignificantMovement;

        #endregion

        #region Configuration

        /// <summary>
        /// Interval in seconds to check for region changes
        /// </summary>
        private const float REGION_CHECK_INTERVAL = 2f;

        /// <summary>
        /// Distance threshold in meters for significant movement detection
        /// </summary>
        private const float SIGNIFICANT_MOVEMENT_THRESHOLD = 50f;

        #endregion

        #region State

        private EMapRegion currentRegion;
        private Vector3 lastCheckedPosition;
        private Player trackedPlayer;
        private float lastRegionCheckTime;
        private bool isInitialized = false;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Ensure singleton behavior
            if (Instance != null && Instance != this)
            {
                ModLogger.Warn("PlayerLocationTracker: Multiple instances detected, destroying duplicate");
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            Initialize();
        }

        private void Update()
        {
            if (!isInitialized) return;

            // Check for region changes periodically
            if (Time.time - lastRegionCheckTime >= REGION_CHECK_INTERVAL)
            {
                CheckRegionChange();
                lastRegionCheckTime = Time.time;
            }

            // Check for significant movement
            CheckSignificantMovement();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the location tracker
        /// </summary>
        public void Initialize()
        {
            try
            {
                // Get local player
                trackedPlayer = GetLocalPlayer();
                if (trackedPlayer == null)
                {
                    ModLogger.Debug("PlayerLocationTracker: Local player not found, will retry");
                    StartCoroutine(RetryInitialize());
                    return;
                }

                // Initialize state
                lastCheckedPosition = trackedPlayer.transform.position;
                currentRegion = GetRegionForPosition(lastCheckedPosition);
                lastRegionCheckTime = Time.time;
                isInitialized = true;

                ModLogger.Debug($"PlayerLocationTracker initialized for player {trackedPlayer.name} in region {currentRegion}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PlayerLocationTracker: Error during initialization: {ex.Message}");
            }
        }

        /// <summary>
        /// Retry initialization if player not found initially
        /// </summary>
        private IEnumerator RetryInitialize()
        {
            int retries = 0;
            const int maxRetries = 10;
            const float retryInterval = 1f;

            while (retries < maxRetries && trackedPlayer == null)
            {
                yield return new WaitForSeconds(retryInterval);
                trackedPlayer = GetLocalPlayer();
                retries++;
            }

            if (trackedPlayer != null)
            {
                Initialize();
            }
            else
            {
                ModLogger.Error("PlayerLocationTracker: Failed to find local player after retries");
            }
        }

        #endregion

        #region Player Access

        /// <summary>
        /// Get the local player instance
        /// </summary>
        private Player GetLocalPlayer()
        {
            try
            {
#if !MONO
                return Il2CppScheduleOne.PlayerScripts.Player.Local;
#else
                return ScheduleOne.PlayerScripts.Player.Local;
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PlayerLocationTracker: Error getting local player: {ex.Message}");
                return null;
            }
        }

        // MULTIPLAYER SUPPORT (commented out for singleplayer focus)
        /*
        /// <summary>
        /// Get all players in multiplayer scenario
        /// </summary>
        private List<Player> GetAllPlayers()
        {
            try
            {
#if !MONO
                return Il2CppScheduleOne.PlayerScripts.Player.AllPlayers;
#else
                return ScheduleOne.PlayerScripts.Player.AllPlayers;
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PlayerLocationTracker: Error getting all players: {ex.Message}");
                return new List<Player>();
            }
        }
        */

        #endregion

        #region Region Detection

        /// <summary>
        /// Check if player has changed regions
        /// </summary>
        private void CheckRegionChange()
        {
            if (trackedPlayer == null)
            {
                trackedPlayer = GetLocalPlayer();
                if (trackedPlayer == null) return;
            }

            try
            {
                Vector3 currentPosition = trackedPlayer.transform.position;
                EMapRegion newRegion = GetRegionForPosition(currentPosition);

                if (newRegion != currentRegion)
                {
                    ModLogger.Debug($"PlayerLocationTracker: Region changed from {currentRegion} to {newRegion}");
                    currentRegion = newRegion;
                    OnPlayerRegionChanged?.Invoke(trackedPlayer, newRegion);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PlayerLocationTracker: Error checking region change: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the map region for a given position
        /// Uses EMapRegion enum from ScheduleOne.Map
        /// </summary>
        private EMapRegion GetRegionForPosition(Vector3 position)
        {
            try
            {
                // Try to use game's built-in region detection if available
                // Fallback to coordinate-based detection if enum not accessible
#if !MONO
                // Attempt to use game's region detection system
                // This may need adjustment based on actual game API
                var mapRegion = Il2CppScheduleOne.Map.MapRegionDetector.GetRegion(position);
                if (mapRegion != null)
                {
                    return (EMapRegion)mapRegion;
                }
#endif

                // Fallback: Use coordinate-based region detection
                return DetectRegionByCoordinates(position);
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"PlayerLocationTracker: Error getting region, using fallback: {ex.Message}");
                return DetectRegionByCoordinates(position);
            }
        }

        /// <summary>
        /// Fallback region detection based on world coordinates
        /// Maps routes to approximate regions based on waypoint locations
        /// </summary>
        private EMapRegion DetectRegionByCoordinates(Vector3 position)
        {
            // Approximate region boundaries based on route waypoints
            // Police Station area: ~(20-40, 0-60)
            if (position.x >= 20f && position.x <= 40f && position.z >= 0f && position.z <= 60f)
            {
                return EMapRegion.Downtown; // Assuming Downtown is police station area
            }

            // East/Uptown area: ~(40-160, -30-20)
            if (position.x >= 40f && position.x <= 160f && position.z >= -30f && position.z <= 20f)
            {
                return EMapRegion.Uptown; // Assuming Uptown is east area
            }

            // West area: ~(-160 to -10, 20-100)
            if (position.x <= -10f && position.x >= -160f && position.z >= 20f && position.z <= 100f)
            {
                return EMapRegion.Westside;
            }

            // North area: ~(20-70, 45-90)
            if (position.x >= 20f && position.x <= 70f && position.z >= 45f && position.z <= 90f)
            {
                return EMapRegion.Northtown;
            }

            // Canal/Docks area: ~(-90 to -10, -5-50)
            if (position.x <= -10f && position.x >= -90f && position.z >= -5f && position.z <= 50f)
            {
                return EMapRegion.Docks;
            }

            // Default to Downtown if unknown
            return EMapRegion.Downtown;
        }

        #endregion

        #region Movement Detection

        /// <summary>
        /// Check if player has moved significantly
        /// </summary>
        private void CheckSignificantMovement()
        {
            if (trackedPlayer == null) return;

            try
            {
                Vector3 currentPosition = trackedPlayer.transform.position;
                float distance = Vector3.Distance(currentPosition, lastCheckedPosition);

                if (distance >= SIGNIFICANT_MOVEMENT_THRESHOLD)
                {
                    ModLogger.Debug($"PlayerLocationTracker: Significant movement detected: {distance:F1}m");
                    lastCheckedPosition = currentPosition;
                    OnPlayerSignificantMovement?.Invoke(trackedPlayer, currentPosition);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PlayerLocationTracker: Error checking movement: {ex.Message}");
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Get current tracked player
        /// </summary>
        public Player GetTrackedPlayer() => trackedPlayer;

        /// <summary>
        /// Get current region
        /// </summary>
        public EMapRegion GetCurrentRegion() => currentRegion;

        /// <summary>
        /// Get current player position
        /// </summary>
        public Vector3 GetCurrentPosition()
        {
            if (trackedPlayer == null) return Vector3.zero;
            return trackedPlayer.transform.position;
        }

        /// <summary>
        /// Force a region check (useful for testing or teleportation)
        /// </summary>
        public void ForceRegionCheck()
        {
            CheckRegionChange();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Cleanup resources
        /// </summary>
        private void Cleanup()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            ModLogger.Debug("PlayerLocationTracker: Cleaned up");
        }

        #endregion

        #region Singleton

        public static PlayerLocationTracker Instance { get; private set; }

        #endregion
    }

    /// <summary>
    /// Map region enum - matches ScheduleOne.Map.EMapRegion
    /// Fallback enum if game's enum is not accessible
    /// </summary>
    public enum EMapRegion
    {
        Downtown = 0,
        Uptown = 1,
        Westside = 2,
        Northtown = 3,
        Docks = 4,
        Unknown = 99
    }
}

