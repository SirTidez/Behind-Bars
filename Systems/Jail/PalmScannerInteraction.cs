using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;

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
    /// Advanced palm scanner interaction combining camera view with draggable hand mechanics
    /// </summary>
    public class PalmScannerInteraction : InteractableObject
    {
        public Camera interactionCamera;
        public Transform scannerTarget;
        public float scanValidRange = 0.15f;
        
        public GameObject palmModel;           // The MockHand or palm prefab
        public Transform palmStartPosition;    // Where palm starts when interaction begins
        public LayerMask scannerSurfaceLayer = 1 << 8;
        
        public float dragSensitivity = 0.001f;
        public float hoverOffset = 0.02f;
        public float snapRadius = 0.1f;
        public float maxDragDistance = 0.3f;
        
        public float scanDuration = 3f;
        public UnityEngine.UI.Image scanProgressUI;
        public AudioSource scannerAudio;
        public AudioClip scanBeepSound;
        public AudioClip scanCompleteSound;
        
        public UnityEngine.UI.Image imgScanEffect;
        public Transform startTransform;
        public Transform endTransform;
        
        private bool inScannerView = false;
        private bool isDragging = false;
        private bool isScanning = false;
        private float scanProgress = 0f;
        
        private Camera mainCamera;
        private PlayerCamera playerCamera;
        private BookingProcess bookingProcess;
        
        private Vector3 originalPalmPosition;
        private Coroutine scanCoroutine;
        private GameObject scanEffectObject;
        
        private GameObject punchContainer;
        
        // Drag plane calculation
        private Plane dragPlane;
        private Vector3 dragStartWorldPos;
        private Vector3 mouseStartPos;

        void Start()
        {
            // Initialize components
            mainCamera = Camera.main;
            try { playerCamera = PlayerSingleton<PlayerCamera>.Instance; }
            catch { ModLogger.Warn("PlayerCamera singleton not found"); }
            
            bookingProcess = FindObjectOfType<BookingProcess>();
            
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
        
        void OnDestroy() 
        {
#if !MONO
            GameInput.DeregisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExit);
#else
            GameInput.DeregisterExitListener(OnExit);
#endif
        }
        
        private void OnExit(ExitAction action)
        {
            if (!action.Used && inScannerView && action.exitType == ExitType.Escape)
            {
                action.Used = true;
                EndScannerView();
            }
        }
        
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
            PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
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
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Click and drag to move your palm to the scanner", 
                    NotificationType.Instruction
                );
            }
        }
        
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
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
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
        
        private void StartCanvasScanAnimation()
        {
            if (imgScanEffect != null && startTransform != null && endTransform != null)
            {
                MelonCoroutines.Start(AnimateScanEffect());
            }
        }
        
        private IEnumerator AnimateScanEffect()
        {
            if (imgScanEffect == null || startTransform == null || endTransform == null)
                yield break;
            
            ModLogger.Info("Starting Canvas scan animation");
            imgScanEffect.gameObject.SetActive(true);
            
            float halfDuration = scanDuration / 2f;
            
            // Animate Start → End (first half of scan time)
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
            
            // Animate End → Start (second half of scan time)
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
            
            // Hide effect when done
            imgScanEffect.gameObject.SetActive(false);
            ModLogger.Info("Canvas scan animation completed");
        }
        
        private void SetupDragPlane()
        {
            // Create a drag plane perpendicular to the camera view
            Vector3 planeNormal = interactionCamera.transform.forward;
            Vector3 planePoint = scannerTarget.position;
            dragPlane = new Plane(planeNormal, planePoint);
        }

        
        private Vector3 ScreenToWorldDelta(Vector3 screenDelta)
        {
            // Convert screen movement to world movement based on camera
            Camera cam = interactionCamera;
            if (cam == null) return Vector3.zero;
            
            // Use raycast approach for more accurate 2D->3D mapping
            // Create two rays from start and current mouse positions
            Vector3 startScreenPos = mouseStartPos;
            Vector3 currentScreenPos = mouseStartPos + screenDelta;
            
            // Convert to proper screen coordinates (handle different coordinate systems)
            startScreenPos.z = Vector3.Distance(cam.transform.position, dragStartWorldPos);
            currentScreenPos.z = startScreenPos.z;
            
            // Project both points onto the drag plane
            Ray startRay = cam.ScreenPointToRay(startScreenPos);
            Ray currentRay = cam.ScreenPointToRay(currentScreenPos);
            
            Vector3 startWorldPos = dragStartWorldPos;
            Vector3 currentWorldPos = dragStartWorldPos;
            
            // Calculate intersections with the drag plane
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
        
        private Vector3 ConstrainPalmPosition(Vector3 position)
        {
            // Constrain to max distance from scanner target
            Vector3 fromTarget = position - scannerTarget.position;
            if (fromTarget.magnitude > maxDragDistance)
            {
                fromTarget = fromTarget.normalized * maxDragDistance;
                position = scannerTarget.position + fromTarget;
            }
            
            // Keep palm slightly above scanner surface
            position.y = Mathf.Max(position.y, scannerTarget.position.y + hoverOffset);
            
            return position;
        }
        
        private void StartScanning()
        {
            if (isScanning) return;
            
            ModLogger.Info("Starting palm scan");
            isScanning = true;
            scanProgress = 0f;
            
            // Audio feedback
            if (scannerAudio != null && scanBeepSound != null)
            {
                scannerAudio.clip = scanBeepSound;
                scannerAudio.Play();
            }
            
            // Start Canvas animation
            StartCanvasScanAnimation();
            
            // Start scan coroutine
            scanCoroutine = MelonCoroutines.Start(ScanProgressCoroutine()) as Coroutine;
            
            // Show progress notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Scanning palm... Hold still!", 
                    NotificationType.Progress
                );
            }
        }
        
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
            
            // Hide visual effects
            if (scanEffectObject != null)
                scanEffectObject.SetActive(false);
            if (scanProgressUI != null)
                scanProgressUI.gameObject.SetActive(false);
                
            // Show instruction to reposition
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Position palm on scanner to scan", 
                    NotificationType.Instruction
                );
            }
        }
        
        private IEnumerator ScanProgressCoroutine()
        {
            while (scanProgress < 1f && isScanning)
            {
                // Check if still in valid position
                float distance = Vector3.Distance(palmModel.transform.position, scannerTarget.position);
                if (distance > scanValidRange)
                {
                    // Out of range - pause scanning
                    yield return null;
                    continue;
                }
                
                // Progress scanning
                scanProgress += Time.deltaTime / scanDuration;
                
                // Update UI
                if (scanProgressUI != null)
                    scanProgressUI.fillAmount = scanProgress;
                    
                // Update scan effect intensity
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
        
        private void CompleteScan()
        {
            ModLogger.Info("Palm scan completed successfully!");
            isScanning = false;
            
            // Complete audio
            if (scannerAudio != null && scanCompleteSound != null)
            {
                scannerAudio.clip = scanCompleteSound;
                scannerAudio.Play();
            }
            
            // Success notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Palm scan complete!", 
                    NotificationType.Progress
                );
            }
            
            // Mark as complete in booking process
            if (bookingProcess != null)
            {
                bookingProcess.SetFingerprintComplete("PALM_SCAN_" + System.DateTime.Now.Ticks);
            }
            
            // Auto-exit scanner view after short delay
            MelonCoroutines.Start(DelayedExitScannerView());
        }
        
        private IEnumerator DelayedExitScannerView()
        {
            yield return new WaitForSeconds(2f);
            EndScannerView();
        }
        
        private void CreateScanEffect()
        {
            if (scanEffectObject != null) 
            {
                scanEffectObject.SetActive(true);
                return;
            }
            
            // Create a glowing effect at the scanner target
            scanEffectObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            scanEffectObject.name = "ScanEffect";
            scanEffectObject.transform.position = scannerTarget.position;
            scanEffectObject.transform.localScale = new Vector3(0.2f, 0.01f, 0.2f);
            
            // Remove collider
            var collider = scanEffectObject.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.DestroyImmediate(collider);
                
            // Create glowing material
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
        
        void Update()
        {
            if (inScannerView)
            {
                // Handle mouse dragging directly in Update
                HandleMouseDraggingDirect();
            }
            else
            {
                // Update interaction message when not in scanner view
                UpdateInteractionMessage();
            }
        }
        
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
        
        private void HandleMouseDraggingDirect()
        {
            if (palmModel == null)
            {
                ModLogger.Error("PalmModel is null - cannot handle dragging");
                return;
            }
            
            // Check for mouse input
            bool mouseDown = Input.GetMouseButtonDown(0);
            bool mouseHeld = Input.GetMouseButton(0);
            bool mouseUp = Input.GetMouseButtonUp(0);
            
            // Debug mouse state every few frames
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
                // Calculate mouse movement
                Vector3 currentMousePos = Input.mousePosition;
                Vector3 mouseDelta = currentMousePos - mouseStartPos;
                
                // Debug significant mouse movement
                if (mouseDelta.magnitude > 10f) // Only log if mouse moved significantly
                {
                    ModLogger.Info($"Mouse delta: {mouseDelta}, magnitude: {mouseDelta.magnitude}");
                }
                
                // Simple screen to world conversion based on camera
                if (interactionCamera != null)
                {
                    // Try a much more aggressive sensitivity for testing
                    float sensitivity = 0.02f; // Much higher sensitivity for debugging
                    
                    // Get camera's right and up vectors
                    Vector3 rightVector = interactionCamera.transform.right;
                    Vector3 upVector = interactionCamera.transform.up;
                    
                    ModLogger.Info($"Camera vectors - Right: {rightVector}, Up: {upVector}");
                    
                    // Calculate world space movement
                    Vector3 worldDelta = (rightVector * mouseDelta.x + upVector * mouseDelta.y) * sensitivity;
                    
                    // Apply movement to palm
                    Vector3 newPosition = dragStartWorldPos + worldDelta;
                    
                    // Log the position change attempt
                    ModLogger.Info($"Attempting to move palm from {palmModel.transform.position} to {newPosition}");
                    ModLogger.Info($"World delta: {worldDelta}, magnitude: {worldDelta.magnitude}");
                    
                    // Constrain to reasonable area around scanner
                    Vector3 constrainedPosition = ConstrainPalmPosition(newPosition);
                    
                    // Actually move the palm
                    palmModel.transform.position = constrainedPosition;
                    
                    // Verify the movement happened
                    ModLogger.Info($"Palm actually moved to: {palmModel.transform.position}");
                    
                    // Check if close to scanner target
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