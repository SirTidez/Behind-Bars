using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Manages security camera culling based on player monitor visibility.
    /// Disables security cameras when no players can actively see their associated monitor displays.
    /// This prevents unnecessary rendering of the jail area and significantly improves FPS.
    /// </summary>
#if MONO
    public sealed class SecurityCameraCullingManager : MonoBehaviour
#else
    public sealed class SecurityCameraCullingManager(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [UnityEngine.HeaderAttribute("Culling Configuration")]
#endif
#if MONO
        [Tooltip("Interval in seconds between visibility checks (lower = more responsive, higher = better performance)")]
#endif
        public float checkInterval = 2.0f;  // Increased from 0.5s to 2s to reduce frame impact

#if MONO
        [Tooltip("Maximum distance in meters a player can be from a monitor to view it")]
#endif
        public float viewDistance = 10f;

#if MONO
        [Tooltip("Maximum distance in meters from jail center - cameras disabled beyond this")]
#endif
        public float maxJailDistance = 50f;  // Disable all cameras if player is more than this distance from jail

#if MONO
        [Tooltip("Maximum angle in degrees from player forward direction to monitor (180 = any angle)")]
#endif
        // Retained serialized configuration value; the current visibility predicate does not read
        // this threshold, so changing it currently affects only inspector/log output.
        public float viewAngleThreshold = 75f;

#if MONO
        [Tooltip("Use frustum culling check (expensive - can cause frame spikes if enabled)")]
#endif
        public bool useFrustumCheck = false;  // Disabled by default to prevent frame stuttering

#if MONO
        [Tooltip("Enable/disable the culling system entirely")]
#endif
        // Manager-level update toggle.  This controls the culling loop and is separate from each
        // SecurityCamera.cameraComponent.enabled state managed by UpdateCameraStates.
        public bool enabled = true;

#if MONO
        [UnityEngine.HeaderAttribute("Debug")]
#endif
        public bool showDebugInfo = false;

        // Core data structures.  Initialize rebuilds these maps/lists; the visibility cache is the
        // input to camera-state decisions, while the request-time maps debounce state transitions.
        private Dictionary<SecurityCamera, HashSet<MonitorController>> _cameraToMonitorsMap = new Dictionary<SecurityCamera, HashSet<MonitorController>>();
        private Dictionary<MonitorController, bool> _monitorVisibilityCache = new Dictionary<MonitorController, bool>();
        private List<MonitorController> _allMonitors = new List<MonitorController>();
        private List<SecurityCamera> _allCameras = new List<SecurityCamera>();

        // Performance optimization: throttle checks
        private float _lastCheckTime = 0f;

        // Debouncing/hysteresis to prevent camera flickering and stuttering
        private Dictionary<SecurityCamera, float> _cameraEnableRequestTime = new Dictionary<SecurityCamera, float>();
        private Dictionary<SecurityCamera, float> _cameraDisableRequestTime = new Dictionary<SecurityCamera, float>();
        private const float ENABLE_DELAY = 1.0f;  // Wait 1.0s before enabling camera (increased to reduce stutter)
        private const float DISABLE_DELAY = 2.0f; // Wait 2.0s before disabling camera (increased to prevent stuttering)

        // Cache frustum planes to avoid expensive recalculation every check
        private Plane[] _cachedFrustumPlanes = new Plane[6];
        private float _lastFrustumUpdateTime = 0f;
        private const float FRUSTUM_UPDATE_INTERVAL = 0.5f; // Update frustum cache every 0.5s

        // Cached player references for performance
        private Player _cachedLocalPlayer;
        private Camera _cachedPlayerCamera;

        // Jail bounds for early exit culling
        private Transform _jailRootTransform;
        private Bounds? _jailBounds;
        private bool _jailBoundsCalculated = false;
        private Vector3 _jailCenter;

        void Update()
        {
            if (!enabled) return;

            // Throttle visibility checks for performance
            if (Time.time - _lastCheckTime >= checkInterval)
            {
                UpdateMonitorVisibility();
                UpdateCameraStates();
                _lastCheckTime = Time.time;
            }
        }

        /// <summary>
        /// Initializes the culling manager from the supplied cameras and monitor assignments.
        /// Existing maps, visibility caches, camera lists, and monitor lists are cleared first; current and available cameras
        /// from each assignment are then registered, and jail bounds are recalculated. Null lists are rejected with no reset.
        /// </summary>
        public void Initialize(List<SecurityCamera> cameras, List<JailMonitorController.MonitorAssignment> monitorAssignments, Transform jailRootTransform = null)
        {
            if (monitorAssignments == null || cameras == null)
            {
                ModLogger.Warn("SecurityCameraCullingManager: Cannot initialize with null cameras or monitor assignments");
                return;
            }

            _cameraToMonitorsMap.Clear();
            _monitorVisibilityCache.Clear();
            _allMonitors.Clear();
            _allCameras.Clear();

            // Store jail root transform for bounds checking
            _jailRootTransform = jailRootTransform ?? transform.parent ?? transform;
            _jailBoundsCalculated = false;
            _jailBounds = null;

            // Build camera-to-monitor mapping
            _allCameras.AddRange(cameras);
            foreach (var assignment in monitorAssignments)
            {
                if (assignment.monitor == null) continue;

                _allMonitors.Add(assignment.monitor);

                // Get current camera for this monitor
                SecurityCamera currentCamera = assignment.GetCurrentCamera();
                if (currentCamera != null)
                {
                    RegisterMonitor(assignment.monitor, currentCamera);
                }

                // Also register all available cameras for rotating monitors
                foreach (var camera in assignment.availableCameras)
                {
                    if (camera != null)
                    {
                        RegisterMonitor(assignment.monitor, camera);
                    }
                }
            }

            // Calculate jail bounds once
            CalculateJailBounds();

            ModLogger.Debug($"SecurityCameraCullingManager initialized: {_allMonitors.Count} monitors, {_cameraToMonitorsMap.Count} cameras tracked");
            
            if (showDebugInfo)
            {
                LogSystemStatus();
            }
        }

        /// <summary>
        /// Adds a monitor-camera relationship and initializes that monitor's cached visibility to false when first seen.
        /// Null arguments are ignored. Existing relationships are retained, so this method does not remove stale camera or
        /// monitor mappings when assignments change.
        /// </summary>
        public void RegisterMonitor(MonitorController monitor, SecurityCamera camera)
        {
            if (monitor == null || camera == null) return;

            if (!_cameraToMonitorsMap.ContainsKey(camera))
            {
                _cameraToMonitorsMap[camera] = new HashSet<MonitorController>();
            }

            _cameraToMonitorsMap[camera].Add(monitor);

            if (!_allMonitors.Contains(monitor))
            {
                _allMonitors.Add(monitor);
            }

            // Initialize visibility cache
            if (!_monitorVisibilityCache.ContainsKey(monitor))
            {
                _monitorVisibilityCache[monitor] = false;
            }
        }

        /// <summary>
        /// Update visibility cache for all monitors
        /// </summary>
        private void UpdateMonitorVisibility()
        {
            // Get local player and camera
            Player localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                // No player found, mark all monitors as not visible
                foreach (var monitor in _allMonitors)
                {
                    _monitorVisibilityCache[monitor] = false;
                }
                return;
            }

            // Early exit: Check if player is even inside jail bounds or close enough
            // If not, skip all monitor visibility checks and disable all cameras
            Vector3 playerPosition = localPlayer.transform.position;
            
            // First check: Simple distance check from jail center (fastest)
            float distanceToJail = Vector3.Distance(playerPosition, _jailCenter);
            if (distanceToJail > maxJailDistance)
            {
                // Player is too far from jail - mark all monitors as not visible
                foreach (var monitor in _allMonitors)
                {
                    _monitorVisibilityCache[monitor] = false;
                }
                
                if (showDebugInfo)
                {
                    ModLogger.Debug($"SecurityCameraCullingManager: Player {distanceToJail:F1}m from jail (>{maxJailDistance}m) - disabling all cameras");
                }
                return;
            }
            
            // Second check: Bounds check (more accurate)
            if (!IsPlayerInJailBounds(playerPosition))
            {
                // Player is outside jail bounds - mark all monitors as not visible
                foreach (var monitor in _allMonitors)
                {
                    _monitorVisibilityCache[monitor] = false;
                }
                
                if (showDebugInfo)
                {
                    ModLogger.Debug($"SecurityCameraCullingManager: Player outside jail bounds at ({playerPosition.x:F1}, {playerPosition.y:F1}, {playerPosition.z:F1}) - disabling all cameras");
                }
                return;
            }

            Camera playerCamera = GetPlayerCamera();
            if (playerCamera == null)
            {
                // No camera found, mark all monitors as not visible
                foreach (var monitor in _allMonitors)
                {
                    _monitorVisibilityCache[monitor] = false;
                }
                return;
            }

            Transform playerTransform = localPlayer.transform;

            // Update frustum planes cache periodically (expensive operation)
            float currentTime = Time.time;
            if (currentTime - _lastFrustumUpdateTime >= FRUSTUM_UPDATE_INTERVAL)
            {
                UnityEngine.GeometryUtility.CalculateFrustumPlanes(playerCamera, _cachedFrustumPlanes);
                _lastFrustumUpdateTime = currentTime;
            }

            // Check visibility for each monitor
            foreach (var monitor in _allMonitors)
            {
                if (monitor == null || monitor.gameObject == null)
                {
                    _monitorVisibilityCache[monitor] = false;
                    continue;
                }

                bool isVisible = CheckMonitorVisibility(monitor, playerCamera, localPlayer, playerTransform);
                _monitorVisibilityCache[monitor] = isVisible;
            }
        }

        /// <summary>
        /// Calculate the overall bounds of the jail from its transform hierarchy
        /// </summary>
        private void CalculateJailBounds()
        {
            if (_jailRootTransform == null)
            {
                _jailBounds = null;
                _jailBoundsCalculated = true;
                return;
            }

            // Try to find all renderers or colliders in the jail hierarchy to calculate bounds
            Renderer[] renderers = _jailRootTransform.GetComponentsInChildren<Renderer>();
            Collider[] colliders = _jailRootTransform.GetComponentsInChildren<Collider>();

            if (renderers.Length == 0 && colliders.Length == 0)
            {
                // Fallback: use transform bounds with a generous estimate
                _jailCenter = _jailRootTransform.position;
                _jailBounds = new Bounds(_jailCenter, new Vector3(50f, 20f, 50f));
                _jailBoundsCalculated = true;
                ModLogger.Debug($"SecurityCameraCullingManager: Using estimated jail bounds (no renderers/colliders found) - center: {_jailCenter}");
                return;
            }

            Bounds combinedBounds = new Bounds();
            bool first = true;

            // Use renderers if available (more accurate)
            if (renderers.Length > 0)
            {
                foreach (Renderer renderer in renderers)
                {
                    if (renderer != null && renderer.bounds.size != Vector3.zero)
                    {
                        if (first)
                        {
                            combinedBounds = renderer.bounds;
                            first = false;
                        }
                        else
                        {
                            combinedBounds.Encapsulate(renderer.bounds);
                        }
                    }
                }
            }
            else
            {
                // Fallback to colliders
                foreach (Collider collider in colliders)
                {
                    if (collider != null && collider.bounds.size != Vector3.zero)
                    {
                        if (first)
                        {
                            combinedBounds = collider.bounds;
                            first = false;
                        }
                        else
                        {
                            combinedBounds.Encapsulate(collider.bounds);
                        }
                    }
                }
            }

            // Add padding to bounds (extend by 10% to account for edge cases)
            Vector3 expandedSize = combinedBounds.size * 1.1f;
            _jailBounds = new Bounds(combinedBounds.center, expandedSize);
            _jailCenter = combinedBounds.center;
            _jailBoundsCalculated = true;

            ModLogger.Debug($"SecurityCameraCullingManager: Calculated jail bounds - center: {_jailCenter}, size: {expandedSize}, maxDistance: {maxJailDistance}m");
        }

        /// <summary>
        /// Check if player position is within jail bounds (early exit optimization)
        /// </summary>
        private bool IsPlayerInJailBounds(Vector3 playerPosition)
        {
            // If bounds not calculated or invalid, assume player is in jail (fail open)
            if (!_jailBoundsCalculated || !_jailBounds.HasValue)
            {
                return true;
            }

            return _jailBounds.Value.Contains(playerPosition);
        }

        /// <summary>
        /// Check if a monitor is visible to a player
        /// Uses proximity, frustum, and angle checks
        /// </summary>
        private bool CheckMonitorVisibility(MonitorController monitor, Camera playerCamera, Player player, Transform playerTransform)
        {
            if (monitor == null || monitor.transform == null || player == null || playerTransform == null)
                return false;

            Vector3 monitorPosition = monitor.transform.position;
            Vector3 playerPosition = playerTransform.position;

            // 1. Distance check (fastest, early exit)
            float distance = Vector3.Distance(playerPosition, monitorPosition);
            if (distance > viewDistance)
            {
                return false;
            }

            // 2. Visibility check using the monitor face
            Transform screenTransform = monitor.screenImage != null ? monitor.screenImage.transform : monitor.transform;
            Vector3 screenPoint = screenTransform.position + (screenTransform.forward * 0.05f);
            if (!player.IsPointVisibleToPlayer(screenPoint, viewDistance, 0.1f))
            {
                return false;
            }

            // 3. Frustum check (optional - disabled by default due to performance cost)
            // Only perform if enabled, as it can cause frame spikes
            if (useFrustumCheck && playerCamera != null)
            {
                // Use cached frustum planes instead of recalculating (expensive operation)
                Bounds monitorBounds = GetMonitorBounds(monitor);
                if (!UnityEngine.GeometryUtility.TestPlanesAABB(_cachedFrustumPlanes, monitorBounds))
                {
                    return false;
                }
            }

            // All checks passed
            return true;
        }

        /// <summary>
        /// Get the bounding box for a monitor for frustum testing
        /// </summary>
        private Bounds GetMonitorBounds(MonitorController monitor)
        {
            // Try to find a Renderer component for accurate bounds
            Renderer renderer = monitor.GetComponent<Renderer>();
            if (renderer != null)
            {
                return renderer.bounds;
            }

            // Try to find Renderer in children
            renderer = monitor.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                return renderer.bounds;
            }

            // Fallback: use a default size around the transform position
            // Assuming monitors are roughly 1x1 meter screens
            Vector3 center = monitor.transform.position;
            Vector3 size = new Vector3(1f, 1f, 0.1f);
            return new Bounds(center, size);
        }

        /// <summary>
        /// Update camera enable/disable states based on monitor visibility
        /// Uses debouncing/hysteresis to prevent rapid toggling that causes stuttering
        /// </summary>
        private void UpdateCameraStates()
        {
            float currentTime = Time.time;

            // Fast path: If player is outside jail, immediately disable all cameras (bypass debouncing)
            Player localPlayer = GetLocalPlayer();
            if (localPlayer != null)
            {
                Vector3 playerPosition = localPlayer.transform.position;
                float distanceToJail = Vector3.Distance(playerPosition, _jailCenter);
                
                if (distanceToJail > maxJailDistance || !IsPlayerInJailBounds(playerPosition))
                {
                    // Player is outside jail - immediately disable all cameras (no debouncing delay)
                    foreach (var camera in _allCameras)
                    {
                        if (camera != null && camera.cameraComponent != null && camera.cameraComponent.enabled)
                        {
                            camera.SetEnabled(false);
                            _cameraEnableRequestTime.Remove(camera);
                            _cameraDisableRequestTime.Remove(camera);
                        }
                    }
                    return;
                }
            }

            bool anyMonitorVisibleGlobal = false;
            foreach (var kvp in _monitorVisibilityCache)
            {
                if (kvp.Value)
                {
                    anyMonitorVisibleGlobal = true;
                    break;
                }
            }

            if (!anyMonitorVisibleGlobal)
            {
                // No monitors visible - disable all cameras immediately
                foreach (var camera in _allCameras)
                {
                    if (camera != null && camera.cameraComponent != null && camera.cameraComponent.enabled)
                    {
                        camera.SetEnabled(false);
                        _cameraEnableRequestTime.Remove(camera);
                        _cameraDisableRequestTime.Remove(camera);
                    }
                }
                return;
            }
            //

            foreach (var kvp in _cameraToMonitorsMap)
            {
                SecurityCamera camera = kvp.Key;
                HashSet<MonitorController> monitors = kvp.Value;

                if (camera == null || camera.cameraComponent == null) continue;

                // Check if ANY monitor showing this camera is visible
                bool anyMonitorVisible = false;
                foreach (var monitor in monitors)
                {
                    if (monitor != null && _monitorVisibilityCache.ContainsKey(monitor) && _monitorVisibilityCache[monitor])
                    {
                        anyMonitorVisible = true;
                        break;
                    }
                }

                // Also check if monitor is currently assigned to this camera (for rotating monitors)
                // A rotating monitor might have this camera in its available list but not currently showing it
                if (!anyMonitorVisible)
                {
                    foreach (var monitor in monitors)
                    {
                        if (monitor != null && monitor.assignedCamera == camera)
                        {
                            // This monitor is currently showing this camera, check if visible
                            if (_monitorVisibilityCache.ContainsKey(monitor) && _monitorVisibilityCache[monitor])
                            {
                                anyMonitorVisible = true;
                                break;
                            }
                        }
                    }
                }

                bool currentlyEnabled = camera.cameraComponent.enabled;

                // Debouncing logic to prevent rapid toggling
                if (anyMonitorVisible && !currentlyEnabled)
                {
                    // Want to enable camera - record request time
                    if (!_cameraEnableRequestTime.ContainsKey(camera))
                    {
                        _cameraEnableRequestTime[camera] = currentTime;
                        _cameraDisableRequestTime.Remove(camera); // Cancel any pending disable
                    }
                    else
                    {
                        // Check if enough time has passed since request
                        if (currentTime - _cameraEnableRequestTime[camera] >= ENABLE_DELAY)
                        {
                            camera.SetEnabled(true);
                            _cameraEnableRequestTime.Remove(camera);

                            if (showDebugInfo)
                            {
                                ModLogger.Debug($"Camera {camera.cameraName} ENABLED after {ENABLE_DELAY}s delay - {monitors.Count} monitor(s) visible");
                            }
                        }
                    }
                }
                else if (!anyMonitorVisible && currentlyEnabled)
                {
                    // Want to disable camera - record request time
                    if (!_cameraDisableRequestTime.ContainsKey(camera))
                    {
                        _cameraDisableRequestTime[camera] = currentTime;
                        _cameraEnableRequestTime.Remove(camera); // Cancel any pending enable
                    }
                    else
                    {
                        // Check if enough time has passed since request
                        if (currentTime - _cameraDisableRequestTime[camera] >= DISABLE_DELAY)
                        {
                            camera.SetEnabled(false);
                            _cameraDisableRequestTime.Remove(camera);

                            if (showDebugInfo)
                            {
                                ModLogger.Debug($"Camera {camera.cameraName} DISABLED after {DISABLE_DELAY}s delay - no monitors visible");
                            }
                        }
                    }
                }
                else
                {
                    // State is stable - clear any pending requests
                    _cameraEnableRequestTime.Remove(camera);
                    _cameraDisableRequestTime.Remove(camera);
                }
            }

            // Ensure cameras not mapped to any monitor are always disabled
            foreach (var camera in _allCameras)
            {
                if (camera == null || camera.cameraComponent == null) continue;
                if (_cameraToMonitorsMap.ContainsKey(camera)) continue;

                if (camera.cameraComponent.enabled)
                {
                    camera.SetEnabled(false);
                    _cameraEnableRequestTime.Remove(camera);
                    _cameraDisableRequestTime.Remove(camera);
                }
            }
        }

        /// <summary>
        /// Get the local player instance
        /// </summary>
        private Player GetLocalPlayer()
        {
            // Cache player reference for performance
            if (_cachedLocalPlayer == null || _cachedLocalPlayer.gameObject == null)
            {
                try
                {
#if !MONO
                    _cachedLocalPlayer = Il2CppScheduleOne.PlayerScripts.Player.Local;
#else
                    _cachedLocalPlayer = ScheduleOne.PlayerScripts.Player.Local;
#endif
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"SecurityCameraCullingManager: Error getting local player: {ex.Message}");
                    _cachedLocalPlayer = null;
                }
            }

            return _cachedLocalPlayer;
        }

        /// <summary>
        /// Get the player's camera component
        /// </summary>
        private Camera GetPlayerCamera()
        {
            // Cache camera reference for performance
            if (_cachedPlayerCamera == null || _cachedPlayerCamera.gameObject == null)
            {
                try
                {
                    var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
                    if (playerCamera != null)
                    {
                        _cachedPlayerCamera = playerCamera.GetComponent<Camera>();
                        if (_cachedPlayerCamera == null)
                        {
                            // Try to find camera in children
                            _cachedPlayerCamera = playerCamera.GetComponentInChildren<Camera>();
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"SecurityCameraCullingManager: Error getting player camera: {ex.Message}");
                    _cachedPlayerCamera = null;
                }
            }

            return _cachedPlayerCamera;
        }


        /// <summary>
        /// Log system status for debugging
        /// </summary>
        private void LogSystemStatus()
        {
            ModLogger.Info("=== SecurityCameraCullingManager Status ===");
            ModLogger.Info($"Enabled: {enabled}");
            ModLogger.Info($"Check Interval: {checkInterval}s");
            ModLogger.Info($"View Distance: {viewDistance}m");
            ModLogger.Info($"View Angle Threshold: {viewAngleThreshold}°");
            ModLogger.Info($"Total Monitors: {_allMonitors.Count}");
            ModLogger.Info($"Total Cameras Tracked: {_cameraToMonitorsMap.Count}");

            int visibleMonitors = 0;
            int enabledCameras = 0;

            foreach (var kvp in _monitorVisibilityCache)
            {
                if (kvp.Value) visibleMonitors++;
            }

            foreach (var kvp in _cameraToMonitorsMap)
            {
                if (kvp.Key != null && kvp.Key.cameraComponent != null && kvp.Key.cameraComponent.enabled)
                    enabledCameras++;
            }

            ModLogger.Info($"Visible Monitors: {visibleMonitors}/{_allMonitors.Count}");
            ModLogger.Info($"Enabled Cameras: {enabledCameras}/{_cameraToMonitorsMap.Count}");
            ModLogger.Info("===========================================");
        }

        /// <summary>
        /// Immediately runs visibility and camera-state updates regardless of the enabled flag or the normal check interval.
        /// It does not update _lastCheckTime, so the regular Update loop may perform another check as soon as its interval allows.
        /// </summary>
        public void ForceUpdate()
        {
            UpdateMonitorVisibility();
            UpdateCameraStates();
        }

        /// <summary>
        /// Returns the cached visibility for a registered monitor, or false for null/unregistered monitors. The result may be
        /// up to checkInterval old unless ForceUpdate or the regular throttled loop has refreshed it.
        /// </summary>
        public bool IsMonitorVisible(MonitorController monitor)
        {
            if (monitor == null || !_monitorVisibilityCache.ContainsKey(monitor))
                return false;

            return _monitorVisibilityCache[monitor];
        }

        /// <summary>
        /// Returns the underlying SecurityCamera.cameraComponent enabled flag, or false when the camera or component is null.
        /// This does not include whether the culling manager currently considers the camera eligible.
        /// </summary>
        public bool IsCameraEnabled(SecurityCamera camera)
        {
            if (camera == null || camera.cameraComponent == null)
                return false;

            return camera.cameraComponent.enabled;
        }

        /// <summary>
        /// Register the new camera relationship when a monitor changes assignment.  The current
        /// implementation ignores <paramref name="oldCamera"/>, does not remove stale mappings,
        /// and does not invoke <see cref="ForceUpdate"/>.  Because visibility is evaluated against
        /// the retained map first, an old camera may remain eligible while the monitor is visible;
        /// normal throttled processing observes the change on a later update.
        /// </summary>
        public void OnMonitorCameraChanged(MonitorController monitor, SecurityCamera oldCamera, SecurityCamera newCamera)
        {
            if (monitor == null) return;

            // Register the new camera if not already registered
            if (newCamera != null)
            {
                RegisterMonitor(monitor, newCamera);
            }

            // No immediate visibility/state update or old-camera removal occurs here.  The next
            // scheduled Update (or an explicit ForceUpdate call by the caller) evaluates the map.
        }
    }
}
