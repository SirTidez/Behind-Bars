using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Discovers jail monitor objects and assigns static or rotating security cameras.
    /// Auto-rotation is a local presentation feature; it does not change camera capture
    /// ownership or add a gameplay surveillance rule.
    /// </summary>
#if MONO
    public sealed class JailMonitorController : MonoBehaviour
#else
    public sealed class JailMonitorController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
#if MONO
        [Header("Monitor System")]
#endif
        // Assignment records are rebuilt by Initialize. Each record owns its camera list,
        // while securityCameras is the caller-supplied discovery pool.
        public List<MonitorAssignment> monitorAssignments = new List<MonitorAssignment>();
        public List<SecurityCamera> securityCameras = new List<SecurityCamera>();

#if MONO
        [Header("Debug")]
#endif
        // Diagnostic logging only; it does not alter rotation or assignment behavior.
        public bool showDebugInfo = false;

        // Performance optimization: throttle Update() checks and pool list allocations
        private float _rotationCheckInterval = 0.5f;  // Check rotations every 0.5 seconds instead of every frame
        private float _lastRotationCheck = 0f;
        private readonly List<SecurityCamera> _pooledCamerasInUse = new List<SecurityCamera>();

        /// <summary>
        /// Camera rotation state for one monitor surface.
        /// </summary>
        [System.Serializable]
        public class MonitorAssignment
        {
            // Authored monitor reference and the cameras eligible for that surface.
            public MonitorController monitor;
            public List<SecurityCamera> availableCameras = new List<SecurityCamera>();
            // Rotation policy and mutable cursor/time state. currentCameraIndex is clamped
            // only when a current camera is requested.
            public bool autoRotate = false;
            public float rotationInterval = 10f;
            public int currentCameraIndex = 0;
            public float lastRotationTime = 0f;

            /// <summary>
            /// Return the current camera, clamping the cursor to the available list.
            /// </summary>
            public SecurityCamera GetCurrentCamera()
            {
                if (availableCameras.Count == 0) return null;
                currentCameraIndex = Mathf.Clamp(currentCameraIndex, 0, availableCameras.Count - 1);
                return availableCameras[currentCameraIndex];
            }

            /// <summary>
            /// Advance the cursor by one, wrapping around the available list.
            /// </summary>
            public SecurityCamera GetNextCamera()
            {
                if (availableCameras.Count == 0) return null;
                currentCameraIndex = (currentCameraIndex + 1) % availableCameras.Count;
                return availableCameras[currentCameraIndex];
            }

            /// <summary>
            /// Advance to the next camera not currently used by another assignment when possible.
            /// </summary>
            /// <param name="camerasInUse">Cameras reserved by other assignments.</param>
            /// <returns>An unused camera when one exists; otherwise the current cursor camera.</returns>
            public SecurityCamera GetNextAvailableCamera(List<SecurityCamera> camerasInUse)
            {
                if (availableCameras.Count == 0) return null;

                int attempts = 0;
                int startIndex = currentCameraIndex;

                do
                {
                    currentCameraIndex = (currentCameraIndex + 1) % availableCameras.Count;
                    SecurityCamera candidate = availableCameras[currentCameraIndex];

                    if (!camerasInUse.Contains(candidate))
                    {
                        return candidate;
                    }

                    attempts++;
                } while (attempts < availableCameras.Count && currentCameraIndex != startIndex);

                return availableCameras[currentCameraIndex];
            }

            /// <summary>
            /// Move the cursor back by one, wrapping around the available list.
            /// </summary>
            public SecurityCamera GetPreviousCamera()
            {
                if (availableCameras.Count == 0) return null;
                currentCameraIndex = (currentCameraIndex - 1 + availableCameras.Count) % availableCameras.Count;
                return availableCameras[currentCameraIndex];
            }
        }

        void Update()
        {
            // Throttle rotation checks to every 0.5 seconds instead of every frame
            // Reduces overhead by 30x (from 60 fps to 2 checks per second)
            if (Time.time - _lastRotationCheck >= _rotationCheckInterval)
            {
                UpdateMonitorRotations();
                _lastRotationCheck = Time.time;
            }
        }

        /// <summary>
        /// Replace the camera pool and rebuild static/rotating monitor assignments from the jail hierarchy.
        /// </summary>
        /// <param name="jailRoot">Root containing <c>Monitors/StaticMonitors</c> and <c>Monitors/RotatingMonitors</c>.</param>
        /// <param name="cameras">Camera pool filtered by each monitor group during discovery.</param>
        public void Initialize(Transform jailRoot, List<SecurityCamera> cameras)
        {
            securityCameras = cameras;
            SetupMonitorAssignments(jailRoot);
        }

        void UpdateMonitorRotations()
        {
            float currentTime = Time.time;

            foreach (var assignment in monitorAssignments)
            {
                if (assignment.autoRotate && assignment.availableCameras.Count > 1)
                {
                    if (currentTime - assignment.lastRotationTime >= assignment.rotationInterval)
                    {
                        RotateMonitorCamera(assignment);
                        assignment.lastRotationTime = currentTime;
                    }
                }
            }
        }

        void RotateMonitorCamera(MonitorAssignment assignment)
        {
            // Reuse pooled list instead of allocating new one every rotation
            _pooledCamerasInUse.Clear();
            GetCamerasCurrentlyInUse(assignment, _pooledCamerasInUse);
            SecurityCamera nextCamera = assignment.GetNextAvailableCamera(_pooledCamerasInUse);

            if (nextCamera != null && assignment.monitor != null)
            {
                SetMonitorCamera(assignment.monitor, nextCamera);
                if (showDebugInfo)
                {
                    ModLogger.Debug($"Auto-rotated monitor {assignment.monitor.name} to camera {nextCamera.cameraName} (avoiding {_pooledCamerasInUse.Count} cameras in use)");
                }
            }
        }

        // Modified to accept output list parameter instead of creating and returning new list
        void GetCamerasCurrentlyInUse(MonitorAssignment excludeAssignment, List<SecurityCamera> outList)
        {
            foreach (var assignment in monitorAssignments)
            {
                if (assignment == excludeAssignment) continue;

                SecurityCamera currentCamera = assignment.GetCurrentCamera();
                if (currentCamera != null)
                {
                    outList.Add(currentCamera);
                }
            }
        }

        /// <summary>
        /// Assign a camera's render texture to a monitor, creating the texture when the camera has none.
        /// </summary>
        /// <param name="monitor">Monitor surface to update.</param>
        /// <param name="camera">Security camera to display.</param>
        /// <remarks>Mono forces an immediate camera render; IL2CPP relies on Unity's render pipeline and does not call Render explicitly.</remarks>
        public void SetMonitorCamera(MonitorController monitor, SecurityCamera camera)
        {
            if (monitor == null || camera == null)
            {
                Debug.LogWarning($"SetMonitorCamera: monitor={monitor != null}, camera={camera != null}");
                return;
            }

            if (camera.renderTexture == null)
            {
                Debug.LogWarning($"Camera {camera.cameraName} has no render texture! Creating one...");
                camera.SetupRenderTexture();
            }

#if MONO
            if (camera.renderTexture != null && !camera.renderTexture.IsCreated())
            {
                camera.renderTexture.Create();
                Debug.Log($"Mono: Force-created render texture for {camera.cameraName}");
            }

            if (camera.cameraComponent != null)
            {
                camera.cameraComponent.enabled = false;
                camera.cameraComponent.targetTexture = camera.renderTexture;
                camera.cameraComponent.enabled = true;
            }

             MelonCoroutines.Start(SetMonitorCameraDelayed(monitor, camera));
#else
            // IL2CPP path - Let Unity's rendering pipeline handle camera renders automatically
            // Removed explicit Render() call to fix double/triple rendering bottleneck

            monitor.SetCamera(camera);

            if (camera.renderTexture != null && monitor.screenImage != null)
            {
                monitor.screenImage.texture = camera.renderTexture;

                // Performance: Only log when debug info is enabled
                if (showDebugInfo)
                {
                    ModLogger.Debug($"✓ Monitor {monitor.name} → {camera.cameraName} (texture: {camera.renderTexture.width}x{camera.renderTexture.height})");
                }
            }
#endif
        }

#if MONO
        private IEnumerator SetMonitorCameraDelayed(MonitorController monitor, SecurityCamera camera)
        {
            Debug.Log($"Mono: Starting delayed camera assignment for monitor {monitor.name}");

            yield return null;

            Debug.Log($"Mono: Processing delayed assignment - Camera: {camera.cameraName}, RenderTexture: {camera.renderTexture != null}");

            if (camera.cameraComponent != null)
            {
                camera.cameraComponent.Render();
                Debug.Log($"Mono: Forced camera render for {camera.cameraName}");
            }

            monitor.SetCamera(camera);
            Debug.Log($"Mono: Set camera reference on monitor {monitor.name}");

            if (camera.renderTexture != null && monitor.screenImage != null)
            {
                monitor.screenImage.texture = camera.renderTexture;
                Debug.Log($"✓ Mono Monitor {monitor.name} → {camera.cameraName} (texture: {camera.renderTexture.width}x{camera.renderTexture.height}) - ASSIGNMENT COMPLETE");
            }
            else
            {
                Debug.LogError($"Mono: Failed to assign texture: Camera.renderTexture={camera.renderTexture != null}, Monitor.screenImage={monitor.screenImage != null}");
            }
        }
#endif

        // Repeated initialization clears assignment records before exact-name discovery;
        // it does not destroy MonitorController components or camera render textures.
        void SetupMonitorAssignments(Transform jailRoot)
        {
            monitorAssignments.Clear();

            AutoDiscoverAndAssignMonitors(jailRoot);

            ModLogger.Debug($"✓ Monitor system initialized with {monitorAssignments.Count} assignments");
        }

        // Discovery is limited to the two authored monitor folders. Missing folders are
        // warnings and produce no assignments; there is no broad scene fallback here.
        void AutoDiscoverAndAssignMonitors(Transform jailRoot)
        {
            Transform staticMonitorsParent = jailRoot.Find("Monitors/StaticMonitors");
            if (staticMonitorsParent != null)
            {
                SetupStaticMonitors(staticMonitorsParent);
            }
            else
            {
                ModLogger.Warn("StaticMonitors folder not found at Monitors/StaticMonitors/");
            }

            Transform rotatingMonitorsParent = jailRoot.Find("Monitors/RotatingMonitors");
            if (rotatingMonitorsParent != null)
            {
                SetupRotatingMonitors(rotatingMonitorsParent);
            }
            else
            {
                ModLogger.Warn("RotatingMonitors folder not found at Monitors/RotatingMonitors/");
            }

            ModLogger.Debug($"Auto-discovery completed: {monitorAssignments.Count} monitors assigned");
        }

        void SetupStaticMonitors(Transform staticMonitorsParent)
        {
            var staticCameras = securityCameras.Where(c => c.cameraType == SecurityCamera.CameraType.MainView).ToList();

            ModLogger.Debug($"Found {staticCameras.Count} static cameras for {staticMonitorsParent.childCount} static monitors");

            foreach (var cam in staticCameras)
            {
                ModLogger.Debug($"  Static camera: {cam.cameraName} (type: {cam.cameraType})");
            }

            int successfulAssignments = 0;
            for (int i = 0; i < staticMonitorsParent.childCount && i < staticCameras.Count; i++)
            {
                Transform monitorTransform = staticMonitorsParent.GetChild(i);

                MonitorController monitor = FindMonitorController(monitorTransform);

                if (monitor == null)
                {
                    Debug.LogWarning($"✗ No MonitorController found/created on {monitorTransform.name} or its children");
                    continue;
                }

                MonitorAssignment assignment = new MonitorAssignment();
                assignment.monitor = monitor;
                assignment.availableCameras.Add(staticCameras[i]);
                assignment.autoRotate = false;

                monitorAssignments.Add(assignment);

                SetMonitorCamera(monitor, staticCameras[i]);
                successfulAssignments++;
                Debug.Log($"✓ Static monitor {monitorTransform.name} → {staticCameras[i].cameraName}");
            }

            Debug.Log($"Static monitor setup completed: {successfulAssignments}/{staticMonitorsParent.childCount} monitors assigned successfully");
        }

        bool IsMonitorObject(Transform obj)
        {
            if (obj.name.ToLower().Contains("monitor")) return true;
            if (obj.name.ToLower().Contains("screen")) return true;
            if (obj.name.ToLower().Contains("display")) return true;

            if (obj.GetComponent<MonitorController>() != null) return true;

            foreach (Transform child in obj)
            {
                if (child.GetComponent<MonitorController>() != null) return true;
                if (child.name.ToLower().Contains("screen")) return true;
                if (child.name.ToLower().Contains("display")) return true;
                if (child.name.ToLower().Contains("monitor")) return true;
            }

            return false;
        }

        void SetupRotatingMonitors(Transform rotatingMonitorsParent)
        {
            var rotatingCameras = securityCameras.Where(c => c.cameraType == SecurityCamera.CameraType.PhoneArea || c.cameraType == SecurityCamera.CameraType.HoldingCell || c.cameraType == SecurityCamera.CameraType.Hall).ToList();

            Debug.Log($"Found {rotatingCameras.Count} rotating cameras for {rotatingMonitorsParent.childCount} rotating monitors");

            foreach (var cam in rotatingCameras)
            {
                Debug.Log($"  Rotating camera: {cam.cameraName} (type: {cam.cameraType})");
            }

            int successfulAssignments = 0;
            for (int i = 0; i < rotatingMonitorsParent.childCount; i++)
            {
                Transform monitorTransform = rotatingMonitorsParent.GetChild(i);

                MonitorController monitor = FindMonitorController(monitorTransform);

                if (monitor == null)
                {
                    Debug.LogWarning($"✗ No MonitorController found/created on {monitorTransform.name} or its children");
                    continue;
                }

                if (rotatingCameras.Count == 0)
                {
                    Debug.LogWarning($"✗ No rotating cameras available for monitor {monitorTransform.name}");
                    continue;
                }

                MonitorAssignment assignment = new MonitorAssignment();
                assignment.monitor = monitor;
                assignment.availableCameras.AddRange(rotatingCameras);
                assignment.autoRotate = true;
                assignment.rotationInterval = 8f + (i * 2f);
                assignment.currentCameraIndex = i % rotatingCameras.Count;
                assignment.lastRotationTime = Time.time + (i * 2f);

                monitorAssignments.Add(assignment);

                if (rotatingCameras.Count > 0)
                {
                    SecurityCamera initialCamera = assignment.GetCurrentCamera();
                    SetMonitorCamera(monitor, initialCamera);
                    successfulAssignments++;
                    Debug.Log($"✓ Rotating monitor {monitorTransform.name} → {initialCamera.cameraName} (every {assignment.rotationInterval}s, starting after {i * 2f}s delay)");
                }
            }

            Debug.Log($"Rotating monitor setup completed: {successfulAssignments}/{rotatingMonitorsParent.childCount} monitors assigned successfully");
        }

        // Prefer an authored MonitorController, then a Resources prefab. The final
        // AddComponent fallback is a legacy/prototype recovery path: IL2CPP registration
        // must already exist for the injected component or this may fail at runtime.
        MonitorController FindMonitorController(Transform monitorTransform)
        {
            MonitorController monitor = monitorTransform.GetComponent<MonitorController>();
            if (monitor != null)
            {
                return monitor;
            }

            monitor = monitorTransform.GetComponentInChildren<MonitorController>();
            if (monitor != null)
            {
                return monitor;
            }

            GameObject monitorPrefab = Resources.Load<GameObject>("MonitorController");
            if (monitorPrefab != null)
            {
                GameObject monitorInstance = Instantiate(monitorPrefab, monitorTransform);
                monitor = monitorInstance.GetComponent<MonitorController>();
                if (monitor != null)
                {
                    Debug.Log($"✓ Created MonitorController instance on {monitorTransform.name}");
                    return monitor;
                }
            }

            monitor = monitorTransform.gameObject.AddComponent<MonitorController>();
            Debug.Log($"✓ Added MonitorController component to {monitorTransform.name}");
            return monitor;
        }

        /// <summary>
        /// Advance every assignment with more than one eligible camera once.
        /// </summary>
        /// <remarks>This is an immediate diagnostic/manual rotation and ignores autoRotate flags.</remarks>
        public void RotateAllMonitors()
        {
            foreach (var assignment in monitorAssignments)
            {
                if (assignment.availableCameras.Count > 1)
                {
                    RotateMonitorCamera(assignment);
                }
            }
            Debug.Log("Rotated all monitors to next camera");
        }

        /// <summary>
        /// Log camera-pool and assignment state for diagnostics.
        /// </summary>
        public void TestMonitorSystem()
        {
            Debug.Log("=== TESTING MONITOR SYSTEM ===");
            Debug.Log($"Total security cameras: {securityCameras.Count}");
            Debug.Log($"Total monitor assignments: {monitorAssignments.Count}");

            foreach (var assignment in monitorAssignments)
            {
                SecurityCamera currentCamera = assignment.GetCurrentCamera();
                string cameraName = currentCamera?.cameraName ?? "NONE";
                Debug.Log($"Monitor: {assignment.monitor?.name ?? "NULL"} → Camera: {cameraName} (Auto-rotate: {assignment.autoRotate}, Available: {assignment.availableCameras.Count})");
            }

            Debug.Log("=== END MONITOR TEST ===");
        }

        /// <summary>
        /// Clear assignment records and rerun monitor discovery from this object's parent root.
        /// </summary>
        /// <remarks>Existing monitor/camera components are retained; only assignment records are rebuilt.</remarks>
        public void ForceSetupAllMonitors()
        {
            monitorAssignments.Clear();
            Debug.Log("Cleared existing monitor assignments. Re-running setup...");

            Transform jailRoot = transform.parent ?? transform;
            SetupMonitorAssignments(jailRoot);

            Debug.Log($"Force setup completed: {monitorAssignments.Count} monitor assignments created");
        }
    }
}
