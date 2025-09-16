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
#if MONO
    public sealed class JailMonitorController : MonoBehaviour
#else
    public sealed class JailMonitorController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
    {
        [Header("Monitor System")]
        public List<MonitorAssignment> monitorAssignments = new List<MonitorAssignment>();
        public List<SecurityCamera> securityCameras = new List<SecurityCamera>();

        [Header("Debug")]
        public bool showDebugInfo = false;

        [System.Serializable]
        public class MonitorAssignment
        {
            public MonitorController monitor;
            public List<SecurityCamera> availableCameras = new List<SecurityCamera>();
            public bool autoRotate = false;
            public float rotationInterval = 10f;
            public int currentCameraIndex = 0;
            public float lastRotationTime = 0f;

            public SecurityCamera GetCurrentCamera()
            {
                if (availableCameras.Count == 0) return null;
                currentCameraIndex = Mathf.Clamp(currentCameraIndex, 0, availableCameras.Count - 1);
                return availableCameras[currentCameraIndex];
            }

            public SecurityCamera GetNextCamera()
            {
                if (availableCameras.Count == 0) return null;
                currentCameraIndex = (currentCameraIndex + 1) % availableCameras.Count;
                return availableCameras[currentCameraIndex];
            }

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

            public SecurityCamera GetPreviousCamera()
            {
                if (availableCameras.Count == 0) return null;
                currentCameraIndex = (currentCameraIndex - 1 + availableCameras.Count) % availableCameras.Count;
                return availableCameras[currentCameraIndex];
            }
        }

        void Update()
        {
            UpdateMonitorRotations();
        }

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
            List<SecurityCamera> camerasInUse = GetCamerasCurrentlyInUse(assignment);
            SecurityCamera nextCamera = assignment.GetNextAvailableCamera(camerasInUse);

            if (nextCamera != null && assignment.monitor != null)
            {
                SetMonitorCamera(assignment.monitor, nextCamera);
                if (showDebugInfo)
                {
                    Debug.Log($"Auto-rotated monitor {assignment.monitor.name} to camera {nextCamera.cameraName} (avoiding {camerasInUse.Count} cameras in use)");
                }
            }
        }

        List<SecurityCamera> GetCamerasCurrentlyInUse(MonitorAssignment excludeAssignment)
        {
            List<SecurityCamera> camerasInUse = new List<SecurityCamera>();

            foreach (var assignment in monitorAssignments)
            {
                if (assignment == excludeAssignment) continue;

                SecurityCamera currentCamera = assignment.GetCurrentCamera();
                if (currentCamera != null)
                {
                    camerasInUse.Add(currentCamera);
                }
            }

            return camerasInUse;
        }

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
            if (camera.cameraComponent != null)
            {
                camera.cameraComponent.Render();
            }

            monitor.SetCamera(camera);

            if (camera.renderTexture != null && monitor.screenImage != null)
            {
                monitor.screenImage.texture = camera.renderTexture;
                Debug.Log($"✓ Monitor {monitor.name} → {camera.cameraName} (texture: {camera.renderTexture.width}x{camera.renderTexture.height})");
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

        void SetupMonitorAssignments(Transform jailRoot)
        {
            monitorAssignments.Clear();

            AutoDiscoverAndAssignMonitors(jailRoot);

            ModLogger.Info($"✓ Monitor system initialized with {monitorAssignments.Count} assignments");
        }

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

            ModLogger.Info($"Auto-discovery completed: {monitorAssignments.Count} monitors assigned");
        }

        void SetupStaticMonitors(Transform staticMonitorsParent)
        {
            var staticCameras = securityCameras.Where(c => c.cameraType == SecurityCamera.CameraType.MainView).ToList();

            ModLogger.Info($"Found {staticCameras.Count} static cameras for {staticMonitorsParent.childCount} static monitors");

            foreach (var cam in staticCameras)
            {
                ModLogger.Info($"  Static camera: {cam.cameraName} (type: {cam.cameraType})");
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