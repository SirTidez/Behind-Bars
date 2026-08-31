using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.AvatarFramework;
using ScheduleOne;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Retained legacy/reduced palm-scanner interaction that combines a custom camera view with mouse-dragged hand mechanics.
    /// This class is not the authoritative fingerprint-scanning route: the current ScannerStation path owns the active
    /// booking flow. There are no direct C# callers in this repository, although a serialized scene or prefab can still
    /// reference the component, so its side effects are documented as conditional on being wired at runtime.
    /// </summary>
    public class PalmScannerInteraction : InteractableObject
    {
        // Optional camera and world-space target used by this legacy interaction view. Missing references prevent entry or
        // can make the reduced path fail later because several setup helpers assume scannerTarget is present.
        public Camera interactionCamera;
        public Transform scannerTarget;

        // Maximum world-space distance from scannerTarget at which this route advances its scan, in Unity world units.
        // The value is not validated for positive range.
        public float scanValidRange = 0.15f;
        
        // Existing MockHand/palm prefab, or a fallback capsule created by SetupPalmModel when absent.
        public GameObject palmModel;

        // Optional starting transform. SetupPalmModel snapshots its world position, then StartScannerView restores that
        // snapshot before each legacy interaction.
        public Transform palmStartPosition;

        // Retained surface-layer setting from the earlier raycast design; the active direct mouse-drag path does not read it.
        public LayerMask scannerSurfaceLayer = 1 << 8;
        
        // Retained sensitivity setting from the earlier drag design. HandleMouseDraggingDirect currently uses a local 0.02f
        // value instead, so changing this serialized field has no effect on the active reduced path.
        public float dragSensitivity = 0.001f;

        // Vertical clearance above scannerTarget enforced by ConstrainPalmPosition, in world units.
        public float hoverOffset = 0.02f;

        // Retained snap threshold; no current method reads this field, so it has no effect on the reduced path.
        public float snapRadius = 0.1f;

        // Maximum world-space distance of the dragged palm from scannerTarget, in world units. The value is not validated
        // as non-negative.
        public float maxDragDistance = 0.3f;
        
        // Scan duration in Unity seconds as advanced by Time.deltaTime. It must be positive for normal progress; no guard
        // prevents zero or negative values from producing degenerate coroutine behavior.
        public float scanDuration = 3f;

        // Optional progress image. The scan coroutine updates fillAmount and EndScannerView hides it; this class does not
        // explicitly activate the image when a scan starts.
        public UnityEngine.UI.Image scanProgressUI;

        // Optional audio source and clips used for start/beep and completion feedback.
        public AudioSource scannerAudio;
        public AudioClip scanBeepSound;
        public AudioClip scanCompleteSound;
        
        // Optional UI effect path. StartCanvasScanAnimation uses the image/transforms when all are assigned; the separate
        // scanEffectObject primitive path is retained but CreateScanEffect is not called by this class.
        public UnityEngine.UI.Image imgScanEffect;
        public Transform startTransform;
        public Transform endTransform;
        
        // Interaction state: inScannerView gates input, isDragging gates mouse-held movement, and isScanning gates the two
        // scan coroutines. scanProgress is normalized toward 1.0 and is reset when StartScanning begins.
        private bool inScannerView = false;
        private bool isDragging = false;
        private bool isScanning = false;
        private float scanProgress = 0f;
        
        // Runtime references resolved in Start or ValidateSetup. mainCamera is captured from Camera.main but is not read by
        // the active reduced path. bookingProcess is optional; if absent, this route can still run local scan visuals but
        // cannot persist completion to the booking flow.
        private Camera mainCamera;
        private PlayerCamera playerCamera;
        private BookingProcess bookingProcess;
        
        // Palm position snapshot, active scan coroutine, and optional generated scan effect. The destroy path only removes
        // the exit listener; it does not explicitly stop every coroutine or restore player state.
        private Vector3 originalPalmPosition;
        private Coroutine scanCoroutine;
        private GameObject scanEffectObject;
        
        // PunchContainer is disabled while this legacy camera view is active and re-enabled on exit when found.
        private GameObject punchContainer;
        
        // Drag-plane calculation state retained by the earlier raycast conversion helper. The active direct mouse handler
        // uses mouseStartPos/dragStartWorldPos but does not call ScreenToWorldDelta, so dragPlane is currently unused after
        // SetupDragPlane.
        private Plane dragPlane;
        private Vector3 dragStartWorldPos;
        private Vector3 mouseStartPos;

        /// <summary>
        /// Resolves scene references, prepares the fallback/assigned palm and canvas elements, disables the interaction
        /// camera, and registers the exit listener used by this legacy view. Registration is paired with OnDestroy; this
        /// method does not verify every required reference before later helpers use it.
        /// </summary>
        void Start()
        {
            // Initialize components
            mainCamera = Camera.main;
            try { playerCamera = PlayerSingleton<PlayerCamera>.Instance; }
            catch { ModLogger.Warn("PlayerCamera singleton not found"); }
            
            bookingProcess = BBHelpers.FindObjectOfTypeSafe<BookingProcess>();
            
            // Find interaction camera if not assigned (corrected path)
            if (interactionCamera == null)
            {
                var interaction = transform.Find("Interaction");
                if (interaction != null)
                {
                    var cameraObj = interaction.Find("InteractionCamera");
                    if (cameraObj != null)
                        interactionCamera = cameraObj.GetComponent<Camera>();
                }
            }
            
            // Setup scanner target if not assigned
            if (scannerTarget == null)
            {
                scannerTarget = transform.Find("ScanTarget");
            }
            
            // Setup palm model
            SetupPalmModel();
            
            // Setup Canvas UI elements
            SetupCanvasElements();
            
            // PunchController will be found in ValidateSetup when needed
            
            // Disable interaction camera initially
            if (interactionCamera != null)
                interactionCamera.gameObject.SetActive(false);
                
            // Set initial message
            UpdateInteractionMessage();
            SetInteractionType(EInteractionType.Key_Press);
            
            // Register exit listener like CameraHubController
#if !MONO
            GameInput.RegisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExit, priority: 2);
#else
            GameInput.RegisterExitListener(OnExit, priority: 2);
#endif
            
            ModLogger.Info($"PalmScannerInteraction initialized - Camera: {interactionCamera != null}, Target: {scannerTarget != null}");
        }
        
        /// <summary>
        /// Deregisters this component's exit listener. It does not call EndScannerView, stop the independent visual or exit
        /// coroutines, destroy generated objects, or restore player controls if destruction occurs while the view is active.
        /// </summary>
        void OnDestroy() 
        {
#if !MONO
            GameInput.DeregisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExit);
#else
            GameInput.DeregisterExitListener(OnExit);
#endif
        }
        
        /// <summary>
        /// Consumes an unused primary exit action while this legacy scanner view is active and returns to the normal view.
        /// Other exit types and already-used actions are ignored.
        /// </summary>
        private void OnExit(ExitAction action)
        {
            if (!action.Used && inScannerView && action.Type == ExitType.Primary)
            {
                action.Use();
                EndScannerView();
            }
        }
        
        /// <summary>
        /// Resolves MockHand from the expected Draggable/IkTarget hierarchy or creates a capsule fallback, snapshots its
        /// starting world position, places it near scannerTarget when available, and hides it until the view opens.
        /// Generated fallback objects are not tracked separately and are not destroyed by OnDestroy.
        /// </summary>
        private void SetupPalmModel()
        {
            // Look for existing MockHand in the Draggable/IkTarget hierarchy
            if (palmModel == null)
            {
                var draggable = transform.Find("Draggable");
                if (draggable != null)
                {
                    var ikTarget = draggable.Find("IkTarget");
                    if (ikTarget != null)
                    {
                        var mockHand = ikTarget.Find("MockHand");
                        if (mockHand != null)
                        {
                            palmModel = mockHand.gameObject;
                            ModLogger.Info($"Found existing MockHand for palm scanner: {palmModel.name}");
                        }
                    }
                }
            }
            
            // If still no palm model found, create one
            if (palmModel == null)
            {
                ModLogger.Warn("MockHand not found - creating fallback palm model");
                palmModel = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                palmModel.name = "PalmModel_Fallback";
                palmModel.transform.localScale = new Vector3(0.08f, 0.03f, 0.12f);
                
                // Remove collider to avoid interference
                var collider = palmModel.GetComponent<Collider>();
                if (collider != null)
                    UnityEngine.Object.DestroyImmediate(collider);
                    
                // Apply a skin-like material
                var renderer = palmModel.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var material = new Material(Shader.Find("Standard"));
                    material.color = new Color(0.9f, 0.7f, 0.6f, 1f); // Skin tone
                    material.SetFloat("_Metallic", 0f);
                    material.SetFloat("_Smoothness", 0.3f);
                    renderer.material = material;
                }
                
                palmModel.transform.SetParent(transform, false);
            }
            
            // Store original position for reset
            if (palmStartPosition != null)
            {
                originalPalmPosition = palmStartPosition.position;
            }
            else
            {
                originalPalmPosition = palmModel.transform.position;
            }
            
            // Set initial position to near the scanner target
            if (scannerTarget != null)
            {
                Vector3 startPos = scannerTarget.position;
                startPos.y += 0.1f; // Slightly above scanner
                startPos.z += 0.05f; // Slightly in front
                palmModel.transform.position = startPos;
                originalPalmPosition = startPos;
            }
            
            // Hide palm initially
            palmModel.SetActive(false);
            
            ModLogger.Info($"Palm model setup complete: {palmModel.name} at position {palmModel.transform.position}");
        }
        
        /// <summary>
        /// Resolves optional scan-effect image and Start/End transforms below Holder/Canvas when they were not assigned.
        /// It does not create missing UI elements; an image discovered by this lookup is hidden, while a preassigned image
        /// is left in its current active state.
        /// </summary>
        private void SetupCanvasElements()
        {
            // Find Canvas elements if not assigned
            var holder = transform.Find("Holder");
            if (holder != null)
            {
                var canvas = holder.Find("Canvas");
                if (canvas != null)
                {
                    if (imgScanEffect == null)
                    {
                        imgScanEffect = canvas.Find("imgScanEffect")?.GetComponent<UnityEngine.UI.Image>();
                        if (imgScanEffect != null)
                        {
                            ModLogger.Info("Found imgScanEffect in Canvas");
                            imgScanEffect.gameObject.SetActive(false); // Initially hidden
                        }
                    }
                    
                    if (startTransform == null)
                    {
                        startTransform = canvas.Find("Start");
                        if (startTransform != null)
                            ModLogger.Info("Found Start transform in Canvas");
                    }
                    
                    if (endTransform == null)
                    {
                        endTransform = canvas.Find("End");
                        if (endTransform != null)
                            ModLogger.Info("Found End transform in Canvas");
                    }
                }
            }
        }
        
        /// <summary>
        /// Lazily searches for a PunchContainer using the scene CameraContainer, scanner-local CameraContainer, direct
        /// child, and finally descendant-name fallbacks, in that order. Missing punch objects are logged but do not block
        /// this reduced route from continuing.
        /// </summary>
        private void ValidateSetup()
        {
            // Find PunchContainer like ModuleInteractionManager does
            if (punchContainer == null)
            {
                GameObject punchObj = null;

                // Try to find in scene root "CameraContainer"
                var mainCameraContainer = GameObject.Find("CameraContainer");
                if (mainCameraContainer != null)
                {
                    var punchController = mainCameraContainer.transform.Find("PunchController");
                    if (punchController != null)
                        punchObj = punchController.gameObject;
                    else
                    {
                        // Search for any punch-related component in main CameraContainer
                        foreach (Transform child in mainCameraContainer.GetComponentsInChildren<Transform>())
                        {
                            if (child.name.Contains("Punch"))
                            {
                                punchObj = child.gameObject;
                                break;
                            }
                        }
                    }
                }

                // Try CameraContainer under scanner as fallback
                if (punchObj == null)
                {
                    var localCameraContainer = transform.Find("CameraContainer");
                    if (localCameraContainer != null)
                    {
                        var punchController = localCameraContainer.Find("PunchController");
                        if (punchController != null)
                            punchObj = punchController.gameObject;
                    }
                }

                // Try finding PunchContainer directly under scanner
                if (punchObj == null)
                {
                    var punchTransform = transform.Find("PunchContainer");
                    if (punchTransform != null)
                        punchObj = punchTransform.gameObject;
                }

                // Search all children of scanner for punch components
                if (punchObj == null)
                {
                    foreach (Transform child in transform.GetComponentsInChildren<Transform>())
                    {
                        if (child.name.Contains("Punch"))
                        {
                            punchObj = child.gameObject;
                            break;
                        }
                    }
                }

                if (punchObj != null)
                {
                    punchContainer = punchObj;
                    ModLogger.Info($"[PalmScannerInteraction] Found PunchContainer: {punchContainer.name}");
                }
                else
                {
                    ModLogger.Warn($"[PalmScannerInteraction] PunchContainer not found in scene or under {name}");
                }
            }
        }

        /// <summary>
        /// Enters or exits this retained scanner view. A repeated interaction exits; a booking process already marked with
        /// fingerprintComplete is rejected; otherwise the base interaction is started before the local camera transition.
        /// If bookingProcess is absent, the completion guard is bypassed and a later scan cannot persist booking completion.
        /// </summary>
        public override void StartInteract()
        {
            if (inScannerView)
            {
                EndScannerView();
                return;
            }
            
            if (bookingProcess != null && bookingProcess.fingerprintComplete)
            {
                SetMessage("Scan already complete");
                return;
            }
            
            base.StartInteract();
            StartScannerView();
        }
        
        /// <summary>
        /// Disables player movement/inventory and the punch container, overrides the player camera, frees the mouse, and
        /// activates the palm model for direct mouse dragging. Entry is skipped when interactionCamera or playerCamera is
        /// missing; scannerTarget is assumed present by SetupDragPlane and is not revalidated here.
        /// </summary>
        private void StartScannerView()
        {
            if (interactionCamera == null || playerCamera == null) return;
            
            ModLogger.Info("Starting palm scanner view");
            inScannerView = true;
            
            ValidateSetup(); // Find runtime refs like PunchContainer
            
            // Disable punch container like CameraHubController 
            if (punchContainer != null) 
                punchContainer.SetActive(false);

            // Freeze player movement like CameraHubController
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
            PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#endif
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(false);
            PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(false);
            
            // Camera transition using CameraHubController approach
            playerCamera.OverrideFOV(60f, 0.15f);
            playerCamera.OverrideTransform(
                interactionCamera.transform.position, 
                interactionCamera.transform.rotation, 
                0.15f
            );
            
            // Free mouse for interaction
            playerCamera.FreeMouse();
            Singleton<HUD>.Instance.SetCrosshairVisible(false);
            
            // Disable interaction message (no more E<Message>)
            SetInteractableState(EInteractableState.Disabled);
            
            // Show palm model
            if (palmModel != null)
            {
                palmModel.SetActive(true);
                palmModel.transform.position = originalPalmPosition;
                ModLogger.Info($"Palm model activated: {palmModel.name} at {palmModel.transform.position}");
            }
            else
            {
                ModLogger.Error("Palm model is null - cannot show hand!");
            }
            
            // Setup drag plane based on camera view
            SetupDragPlane();
            
            // Show instructions
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    "Click and drag to move your palm to the scanner", 
                    NotificationType.Instruction
                );
            }
        }
        
        /// <summary>
        /// Stops the active scan coroutine, restores player/camera/interactable state, hides palm and scan visuals, and
        /// refreshes the interaction message. It does not restore the palm's original position, cancel every independently
        /// started coroutine, or undo side effects that may have been applied by another owner of the same player systems.
        /// </summary>
        private void EndScannerView()
        {
            ModLogger.Info("Ending palm scanner view");
            inScannerView = false;
            
            // Stop any active scanning
            if (isScanning && scanCoroutine != null)
            {
                MelonCoroutines.Stop(scanCoroutine);
                scanCoroutine = null;
                isScanning = false;
            }
            
            // Re-enable punch container like CameraHubController
            if (punchContainer != null) 
                punchContainer.SetActive(true);

            // Restore player state like CameraHubController
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#endif
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
            PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(true);
            
            // Camera reset like CameraHubController
            if (playerCamera != null)
            {
                playerCamera.StopFOVOverride(0.15f);
                playerCamera.StopTransformOverride(0.15f);
                playerCamera.LockMouse();
                Singleton<HUD>.Instance.SetCrosshairVisible(true);
                ModLogger.Info("Camera reset to normal view");
            }
            
            // Re-enable interaction component
            SetInteractableState(EInteractableState.Default);
            
            // Hide palm model
            if (palmModel != null)
                palmModel.SetActive(false);
            
            // Hide scan effects
            if (scanProgressUI != null)
                scanProgressUI.gameObject.SetActive(false);
            if (imgScanEffect != null)
                imgScanEffect.gameObject.SetActive(false);
            if (scanEffectObject != null)
                scanEffectObject.SetActive(false);
                
            // Update message based on completion state
            UpdateInteractionMessage();
        }
        
        /// <summary>
        /// Starts an untracked canvas animation only when the scan-effect image and both endpoint transforms are assigned.
        /// The caller does not retain a coroutine handle; setting isScanning false causes the animation loops to finish and
        /// hide the image on their next eligible update.
        /// </summary>
        private void StartCanvasScanAnimation()
        {
            if (imgScanEffect != null && startTransform != null && endTransform != null)
            {
                MelonCoroutines.Start(AnimateScanEffect());
            }
        }
        
        /// <summary>
        /// Moves the scan-effect image from Start to End and back over scanDuration using scaled frame time while a scan is
        /// active. A non-positive scanDuration skips the normal movement loops; the image is hidden when the routine ends.
        /// </summary>
        private IEnumerator AnimateScanEffect()
        {
            if (imgScanEffect == null || startTransform == null || endTransform == null)
                yield break;
            
            ModLogger.Info("Starting Canvas scan animation");
            imgScanEffect.gameObject.SetActive(true);
            
            float halfDuration = scanDuration / 2f;
            
            // Animate Start -> End (first half of scan time).
            float elapsed = 0f;
            while (elapsed < halfDuration && isScanning)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / halfDuration;
                
                // Use RectTransform for UI positioning
                imgScanEffect.rectTransform.position = Vector3.Lerp(
                    startTransform.position, 
                    endTransform.position, 
                    t
                );
                yield return null;
            }
            
            // Animate End -> Start (second half of scan time).
            elapsed = 0f;
            while (elapsed < halfDuration && isScanning)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / halfDuration;
                
                imgScanEffect.rectTransform.position = Vector3.Lerp(
                    endTransform.position,
                    startTransform.position, 
                    t
                );
                yield return null;
            }
            
            // Hide effect when done, including when isScanning was cleared by an exit/stop path.
            imgScanEffect.gameObject.SetActive(false);
            ModLogger.Info("Canvas scan animation completed");
        }
        
        /// <summary>
        /// Builds a plane through scannerTarget perpendicular to the interaction camera's forward vector for the retained
        /// raycast drag conversion. The current direct mouse handler does not use the resulting plane.
        /// </summary>
        private void SetupDragPlane()
        {
            // Create a drag plane perpendicular to the camera view
            Vector3 planeNormal = interactionCamera.transform.forward;
            Vector3 planePoint = scannerTarget.position;
            dragPlane = new Plane(planeNormal, planePoint);
        }

        
        /// <summary>
        /// Converts a screen-space delta into a world-space delta by intersecting rays with the retained drag plane.
        /// This helper is not called by the active HandleMouseDraggingDirect path, which uses a fixed local sensitivity and
        /// camera basis vectors instead.
        /// </summary>
        private Vector3 ScreenToWorldDelta(Vector3 screenDelta)
        {
            // Convert screen movement to world movement based on the interaction camera.
            Camera cam = interactionCamera;
            if (cam == null) return Vector3.zero;
            
            // Use a raycast approach for the retained 2D-to-3D mapping.
            Vector3 startScreenPos = mouseStartPos;
            Vector3 currentScreenPos = mouseStartPos + screenDelta;
            
            // Set a common ray depth based on the original drag point.
            startScreenPos.z = Vector3.Distance(cam.transform.position, dragStartWorldPos);
            currentScreenPos.z = startScreenPos.z;
            
            // Project both points onto the drag plane.
            Ray startRay = cam.ScreenPointToRay(startScreenPos);
            Ray currentRay = cam.ScreenPointToRay(currentScreenPos);
            
            Vector3 startWorldPos = dragStartWorldPos;
            Vector3 currentWorldPos = dragStartWorldPos;
            
            // Calculate intersections with the drag plane; failed intersections retain the drag start point.
            if (dragPlane.Raycast(startRay, out float startDistance))
            {
                startWorldPos = startRay.GetPoint(startDistance);
            }
            
            if (dragPlane.Raycast(currentRay, out float currentDistance))
            {
                currentWorldPos = currentRay.GetPoint(currentDistance);
            }
            
            return currentWorldPos - startWorldPos;
        }
        
        /// <summary>
        /// Limits a candidate palm position to maxDragDistance from scannerTarget and enforces hoverOffset above the
        /// scanner surface. The method assumes scannerTarget is non-null and uses world-space coordinates.
        /// </summary>
        private Vector3 ConstrainPalmPosition(Vector3 position)
        {
            // Constrain to max distance from scanner target.
            Vector3 fromTarget = position - scannerTarget.position;
            if (fromTarget.magnitude > maxDragDistance)
            {
                fromTarget = fromTarget.normalized * maxDragDistance;
                position = scannerTarget.position + fromTarget;
            }
            
            // Keep palm slightly above scanner surface.
            position.y = Mathf.Max(position.y, scannerTarget.position.y + hoverOffset);
            
            return position;
        }
        
        /// <summary>
        /// Starts the reduced scan once, resets normalized progress, starts optional audio/canvas/coroutine feedback, and
        /// shows a Progress notification. It does not itself verify the palm distance or explicitly activate
        /// scanProgressUI; the direct drag loop is responsible for deciding when to call it.
        /// </summary>
        private void StartScanning()
        {
            if (isScanning) return;
            
            ModLogger.Info("Starting palm scan");
            isScanning = true;
            scanProgress = 0f;
            
            // Audio feedback.
            if (scannerAudio != null && scanBeepSound != null)
            {
                scannerAudio.clip = scanBeepSound;
                scannerAudio.Play();
            }
            
            // Start the independent canvas animation when configured.
            StartCanvasScanAnimation();
            
            // Start scan coroutine. The external return is cast to Coroutine and may remain null if the runtime returns a
            // different object type.
            scanCoroutine = MelonCoroutines.Start(ScanProgressCoroutine()) as Coroutine;
            
            // Show progress notification.
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    "Scanning palm... Hold still!", 
                    NotificationType.Progress
                );
            }
        }
        
        /// <summary>
        /// Clears the reduced scan state, stops its tracked progress coroutine, hides local visual effects, and asks the UI
        /// manager to show a reposition instruction. An independently started canvas animation is not directly stopped.
        /// </summary>
        private void StopScanning()
        {
            if (!isScanning) return;
            
            ModLogger.Info("Stopping palm scan");
            isScanning = false;
            
            if (scanCoroutine != null)
            {
                MelonCoroutines.Stop(scanCoroutine);
                scanCoroutine = null;
            }
            
            // Hide visual effects. The canvas animation has its own untracked coroutine and observes isScanning separately.
            if (scanEffectObject != null)
                scanEffectObject.SetActive(false);
            if (scanProgressUI != null)
                scanProgressUI.gameObject.SetActive(false);
                
            // Show instruction to reposition.
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    "Position palm on scanner to scan", 
                    NotificationType.Instruction
                );
            }
        }
        
        /// <summary>
        /// Advances normalized scanProgress using scaled frame time only while the palm remains within scanValidRange.
        /// Leaving range pauses progress rather than cancelling it; completion calls CompleteScan, and invalid duration or
        /// missing model/target references are not guarded here.
        /// </summary>
        private IEnumerator ScanProgressCoroutine()
        {
            while (scanProgress < 1f && isScanning)
            {
                // Check if still in valid position; out-of-range frames pause rather than reset progress.
                float distance = Vector3.Distance(palmModel.transform.position, scannerTarget.position);
                if (distance > scanValidRange)
                {
                    // Out of range - pause scanning.
                    yield return null;
                    continue;
                }
                
                // Progress scanning in scaled Unity seconds.
                scanProgress += Time.deltaTime / scanDuration;
                
                // Update the assigned UI image when present; activation is left to the scene/other code.
                if (scanProgressUI != null)
                    scanProgressUI.fillAmount = scanProgress;
                    
                // Update the generated effect's emission when that optional object exists.
                if (scanEffectObject != null)
                {
                    var renderer = scanEffectObject.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        var material = renderer.material;
                        material.SetFloat("_EmissionIntensity", scanProgress * 2f);
                    }
                }
                
                yield return null;
            }
            
            if (scanProgress >= 1f)
            {
                CompleteScan();
            }
        }
        
        /// <summary>
        /// Marks the reduced scan complete, plays optional completion audio/notification, and—when a BookingProcess was
        /// found—writes a timestamped PALM_SCAN fingerprint marker before scheduling a two-second delayed view exit.
        /// This side effect is legacy behavior and does not establish this component as the authoritative booking scanner.
        /// </summary>
        private void CompleteScan()
        {
            ModLogger.Info("Palm scan completed successfully!");
            isScanning = false;
            
            // Completion audio.
            if (scannerAudio != null && scanCompleteSound != null)
            {
                scannerAudio.clip = scanCompleteSound;
                scannerAudio.Play();
            }
            
            // Success notification.
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    "Palm scan complete!", 
                    NotificationType.Progress
                );
            }
            
            // Mark completion in BookingProcess when this legacy component found one.
            if (bookingProcess != null)
            {
                bookingProcess.SetFingerprintComplete("PALM_SCAN_" + System.DateTime.Now.Ticks);
            }
            
            // Auto-exit scanner view after a fixed two-second delay; the coroutine is not tracked for cancellation.
            MelonCoroutines.Start(DelayedExitScannerView());
        }
        
        /// <summary>
        /// Waits two scaled seconds and then calls EndScannerView. The caller does not retain a handle, so destruction or a
        /// second exit path can race this delayed callback.
        /// </summary>
        private IEnumerator DelayedExitScannerView()
        {
            yield return new WaitForSeconds(2f);
            EndScannerView();
        }
        
        /// <summary>
        /// Creates or reactivates a generated cylinder scan effect at scannerTarget with a transparent cyan material.
        /// This helper is retained from the older visual path and is not called by the current class flow; generated objects
        /// are not explicitly destroyed by OnDestroy.
        /// </summary>
        private void CreateScanEffect()
        {
            if (scanEffectObject != null) 
            {
                scanEffectObject.SetActive(true);
                return;
            }
            
            // Create a glowing effect at the scanner target.
            scanEffectObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            scanEffectObject.name = "ScanEffect";
            scanEffectObject.transform.position = scannerTarget.position;
            scanEffectObject.transform.localScale = new Vector3(0.2f, 0.01f, 0.2f);
            
            // Remove collider.
            var collider = scanEffectObject.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.DestroyImmediate(collider);
                
            // Create glowing material.
            var renderer = scanEffectObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                var material = new Material(Shader.Find("Standard"));
                material.color = Color.cyan;
                material.SetFloat("_Mode", 3); // Transparent mode
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = 3000;
                
                renderer.material = material;
            }
        }
        
        /// <summary>
        /// Processes direct legacy mouse dragging while the scanner view is active; otherwise refreshes the interaction
        /// message. This is the active input loop for this reduced component, not the current ScannerStation input path.
        /// </summary>
        void Update()
        {
            if (inScannerView)
            {
                // Handle mouse dragging directly in Update.
                HandleMouseDraggingDirect();
            }
            else
            {
                // Update interaction message when not in scanner view.
                UpdateInteractionMessage();
            }
        }
        
        /// <summary>
        /// Sets the inherited interaction label/state from BookingProcess.fingerprintComplete, or to the default start
        /// prompt when no completed booking scan is observed. This reflects shared booking state and does not prove that
        /// this legacy component performed the scan.
        /// </summary>
        private void UpdateInteractionMessage()
        {
            if (bookingProcess != null && bookingProcess.fingerprintComplete)
            {
                SetMessage("Palm scan complete");
                SetInteractableState(EInteractableState.Label);
            }
            else
            {
                SetMessage("Start Palm Scanner");
                SetInteractableState(EInteractableState.Default);
            }
        }
        
        /// <summary>
        /// Reads the legacy left-mouse drag, moves the palm using interaction-camera right/up vectors and a hard-coded 0.02f
        /// sensitivity, constrains the position, and starts/stops scanning at the range boundary. The serialized
        /// dragSensitivity, snapRadius, scannerSurfaceLayer, and raycast helper are not used by this active loop.
        /// </summary>
        private void HandleMouseDraggingDirect()
        {
            if (palmModel == null)
            {
                ModLogger.Error("PalmModel is null - cannot handle dragging");
                return;
            }
            
            // Check for mouse input.
            bool mouseDown = Input.GetMouseButtonDown(0);
            bool mouseHeld = Input.GetMouseButton(0);
            bool mouseUp = Input.GetMouseButtonUp(0);
            
            // Debug mouse state every few frames.
            if (Time.frameCount % 60 == 0) // Every 60 frames (roughly 1 second at 60fps)
            {
                ModLogger.Info($"Mouse state: Down={mouseDown}, Held={mouseHeld}, Up={mouseUp}, isDragging={isDragging}");
                ModLogger.Info($"MousePos: {Input.mousePosition}, PalmPos: {palmModel.transform.position}");
            }
            
            if (mouseDown)
            {
                isDragging = true;
                mouseStartPos = Input.mousePosition;
                dragStartWorldPos = palmModel.transform.position;
                ModLogger.Info($"Started dragging MockHand - mouse at {mouseStartPos}, hand at {dragStartWorldPos}");
                
                // Stop any active scanning
                if (isScanning)
                {
                    StopScanning();
                }
            }
            else if (mouseHeld && isDragging)
            {
                    // Calculate mouse movement.
                Vector3 currentMousePos = Input.mousePosition;
                Vector3 mouseDelta = currentMousePos - mouseStartPos;
                
                    // Debug significant mouse movement.
                if (mouseDelta.magnitude > 10f) // Only log if mouse moved significantly
                {
                    ModLogger.Info($"Mouse delta: {mouseDelta}, magnitude: {mouseDelta.magnitude}");
                }
                
                    // Simple screen-to-world conversion based on camera basis vectors; this bypasses ScreenToWorldDelta.
                if (interactionCamera != null)
                {
                    // Hard-coded sensitivity retained from the reduced/debugging path; dragSensitivity is not consulted.
                    float sensitivity = 0.02f; // Much higher sensitivity for debugging
                    
                    // Get camera's right and up vectors.
                    Vector3 rightVector = interactionCamera.transform.right;
                    Vector3 upVector = interactionCamera.transform.up;
                    
                    ModLogger.Info($"Camera vectors - Right: {rightVector}, Up: {upVector}");
                    
                    // Calculate world-space movement.
                    Vector3 worldDelta = (rightVector * mouseDelta.x + upVector * mouseDelta.y) * sensitivity;
                    
                    // Apply movement to palm.
                    Vector3 newPosition = dragStartWorldPos + worldDelta;
                    
                    // Log the position change attempt.
                    ModLogger.Info($"Attempting to move palm from {palmModel.transform.position} to {newPosition}");
                    ModLogger.Info($"World delta: {worldDelta}, magnitude: {worldDelta.magnitude}");
                    
                    // Constrain to a reasonable area around the scanner.
                    Vector3 constrainedPosition = ConstrainPalmPosition(newPosition);
                    
                    // Actually move the palm.
                    palmModel.transform.position = constrainedPosition;
                    
                    // Verify the movement happened.
                    ModLogger.Info($"Palm actually moved to: {palmModel.transform.position}");
                    
                    // Check if close to scanner target and toggle the reduced scan loop at the range boundary.
                    if (scannerTarget != null)
                    {
                        float distanceToTarget = Vector3.Distance(palmModel.transform.position, scannerTarget.position);
                        
                        if (distanceToTarget <= scanValidRange && !isScanning)
                        {
                            StartScanning();
                        }
                        else if (distanceToTarget > scanValidRange && isScanning)
                        {
                            StopScanning();
                        }
                    }
                }
                else
                {
                    ModLogger.Error("InteractionCamera is null - cannot convert mouse movement to world space");
                }
            }
            else if (mouseUp)
            {
                isDragging = false;
                ModLogger.Info("Ended dragging MockHand");
            }
        }
        
    }
}
