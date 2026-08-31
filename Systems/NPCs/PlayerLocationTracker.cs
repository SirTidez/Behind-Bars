using System;
using System.Collections;
using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppInterop.Runtime.Attributes;
// Try to use game's EMapRegion if available, otherwise use fallback
// Note: If game's enum is not accessible, we'll use the fallback enum defined below
#else
using ScheduleOne.PlayerScripts;
// Try to use game's EMapRegion if available, otherwise use fallback
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Tracks the local player and emits Mono-only notifications for coarse region
    /// changes or large movements. Region identity is a coordinate approximation;
    /// it is not a query against the game's authoritative map-region service.
    /// </summary>
    public class PlayerLocationTracker : MonoBehaviour
    {
#if !MONO
        public PlayerLocationTracker(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Events

        /// <summary>
        /// Mono-only event raised when the coordinate-derived region enum changes;
        /// it is not emitted by the IL2CPP build and does not represent a native
        /// map-region transition.
        /// </summary>
#if MONO
        public static event Action<Player, EMapRegion> OnPlayerRegionChanged;
#endif

        /// <summary>
        /// Mono-only event raised when the local player moves at least fifty world
        /// metres from the last emitted movement position.
        /// </summary>
#if MONO
        public static event Action<Player, Vector3> OnPlayerSignificantMovement;
#endif

        #endregion

        #region Configuration

        /// <summary>
        /// Scaled Unity-time interval between coordinate-region checks.
        /// </summary>
        private const float REGION_CHECK_INTERVAL = 2f;

        /// <summary>
        /// World-unit distance threshold for the Mono-only movement notification.
        /// </summary>
        private const float SIGNIFICANT_MOVEMENT_THRESHOLD = 50f;

        #endregion

        #region State

        // All state belongs to Player.Local; multiplayer tracking remains disabled
        // in this implementation.
        private EMapRegion currentRegion;
        private Vector3 lastCheckedPosition;
        private Player trackedPlayer;
        // Uses scaled Unity seconds for the two-second region polling cadence.
        private float lastRegionCheckTime;
        private bool isInitialized = false;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Enforces the single tracker instance. A duplicate component is destroyed
        /// and never reaches initialization.
        /// </summary>
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

        /// <summary>Starts local-player resolution and initial region capture.</summary>
        private void Start()
        {
            Initialize();
        }

        /// <summary>
        /// Polls region changes every two scaled seconds and, on Mono only, large
        /// movement events every frame. IL2CPP has no exposed event subscribers;
        /// its parole manager performs its own reconciliation pass.
        /// </summary>
        private void Update()
        {
            if (!isInitialized) return;

            // Check for region changes periodically
            if (Time.time - lastRegionCheckTime >= REGION_CHECK_INTERVAL)
            {
                CheckRegionChange();
                lastRegionCheckTime = Time.time;
            }

#if MONO
            // IL2CPP has no exposed subscribers for these Mono-only events. Its parole
            // officer manager already performs a periodic spawn reconciliation pass.
            CheckSignificantMovement();
#endif
        }

        /// <summary>Clears the singleton reference when this tracker is destroyed.</summary>
        private void OnDestroy()
        {
            Cleanup();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Resolves the local player, captures its starting position/region, and
        /// enables polling. If the player is not ready, initialization retries in
        /// a coroutine rather than marking the tracker initialized.
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
                    MelonLoader.MelonCoroutines.Start(RetryInitialize());
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
        /// Retries local-player resolution up to ten times at one-second intervals.
        /// A failed retry sequence leaves the tracker uninitialized.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
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
        /// Gets the game's local player reference for the active runtime.
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
        /// Re-evaluates the local player's coordinate region and raises the
        /// Mono-only region event only when the coarse enum changes.
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
#if MONO
                    OnPlayerRegionChanged?.Invoke(trackedPlayer, newRegion);
#endif
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PlayerLocationTracker: Error checking region change: {ex.Message}");
            }
        }

        /// <summary>
        /// Maps a world position through the coordinate fallback. The current game
        /// build does not expose a native map-region detector to this component.
        /// </summary>
        private EMapRegion GetRegionForPosition(Vector3 position)
        {
            // Use coordinate-based region detection
            // MapRegionDetector is not available in the current game build
            return DetectRegionByCoordinates(position);
        }

        /// <summary>
        /// Classifies a world position using broad route-derived rectangles. Checks
        /// are ordered, overlapping rectangles take the first matching region, and
        /// unknown positions default to Downtown; boundaries are maintenance data,
        /// not authoritative game regions.
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
        /// Compares the local player's position with the last emitted movement
        /// position and raises the Mono-only event after fifty metres.
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
#if MONO
                    OnPlayerSignificantMovement?.Invoke(trackedPlayer, currentPosition);
#endif
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
        /// Gets the exact local player currently retained by the tracker.
        /// </summary>
        /// <returns>The tracked local player, or null before initialization.</returns>
        public Player GetTrackedPlayer() => trackedPlayer;

        /// <summary>
        /// Gets the most recently computed approximate region.
        /// </summary>
        /// <returns>The coordinate-derived current region.</returns>
        public EMapRegion GetCurrentRegion() => currentRegion;

        /// <summary>
        /// Gets the tracked player's current world position.
        /// </summary>
        /// <returns>The player position, or Vector3.zero when no player is tracked.</returns>
        public Vector3 GetCurrentPosition()
        {
            if (trackedPlayer == null) return Vector3.zero;
            return trackedPlayer.transform.position;
        }

        /// <summary>
        /// Runs a region check immediately, useful after teleportation. It does not
        /// bypass the coordinate approximation or alter the polling timestamp.
        /// </summary>
        public void ForceRegionCheck()
        {
            CheckRegionChange();
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Clears the singleton reference and logs tracker teardown. No event
        /// unsubscription is required because the events are static Mono-only APIs.
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
    /// Coordinate-region labels used by the tracker. These values are a local
    /// compatibility enum and should not be treated as proof of native map-region
    /// identity when the game's enum/service differs.
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

