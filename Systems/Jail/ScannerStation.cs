using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;



#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.AvatarFramework.Animation;
using Il2CppScheduleOne;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne.AvatarFramework;
using ScheduleOne.AvatarFramework.Animation;
using ScheduleOne;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Simple fingerprint scanner using IK hand targeting or new palm scanner interaction
    /// </summary>
    public class ScannerStation : MonoBehaviour
    {
#if !MONO
        public ScannerStation(System.IntPtr ptr) : base(ptr) { }
#endif

        public bool useNewPalmScanner = true;  // Toggle between old IK system and new palm scanner

        // InteractableObject component for IL2CPP compatibility
        private InteractableObject interactableObject;

        public Transform scanTarget;        // The ScanTarget in Unity hierarchy
        public Transform ikTarget;          // The IkTarget that will be draggable
        public Image scanEffect;            // The scanning effect image
        public AudioSource scannerAudio;

        public Camera interactionCamera;     // Camera for palm scanner view
        public GameObject palmModel;         // The MockHand or palm prefab
        public Transform palmStartPosition;  // Where palm starts
        public float dragSensitivity = 0.02f;
        public float maxDragDistance = 0.3f;

        public float scanDuration = 5f;     // Max 5 seconds scanning
        public float validRange = 0.3f;     // Range around scanTarget that's valid

        public AudioClip scanningSound;
        public AudioClip successSound;
        public AudioClip errorSound;

        private bool isScanning = false;
        private bool isDragging = false;
        private BookingProcess bookingProcess;
        private Player currentPlayer;
        private Camera playerCamera;
        private Coroutine scanCoroutine;

        // Palm scanner state
        private bool inScannerView = false;
        private bool isPalmScanning = false;
        private Vector3 originalPalmPosition;
        private Vector3 dragStartWorldPos;
        private Vector3 mouseStartPos;
        private GameObject punchContainer;

        // IK System
        private AvatarIKController ikController;
        private Transform originalRightHandTarget;
        private bool ikActive = false;

        // Visual debugging
        private GameObject ikTargetVisualizer;
        private Renderer ikTargetRenderer;

        void Start()
        {
            // Find booking process
            bookingProcess = FindObjectOfType<BookingProcess>();

            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Info("ScannerStation interaction setup completed");

            // Setup based on scanner mode
            if (useNewPalmScanner)
            {
                SetupPalmScannerComponents();
            }
            else
            {
                SetupOldIKComponents();
            }

            // Find components using exact hierarchy paths
            if (scanTarget == null)
            {
                // Find ScanTarget: Booking/ScannerStation/ScanTarget
                scanTarget = transform.Find("ScanTarget");
                if (scanTarget != null)
                {
                    ModLogger.Info($"Found ScanTarget: {scanTarget.name}");
                }
                else
                {
                    ModLogger.Error("ScanTarget not found in ScannerStation!");
                }
            }

            if (ikTarget == null)
            {
                ModLogger.Info($"Searching for IkTarget. Current transform: {transform.name}");

                // Debug: List all children of this ScannerStation
                ModLogger.Info($"ScannerStation children: {string.Join(", ", GetChildNames(transform))}");

                // Find IkTarget: Booking/ScannerStation/Draggable/IkTarget
                var draggable = transform.Find("Draggable");
                if (draggable != null)
                {
                    ModLogger.Info($"Found Draggable. Children: {string.Join(", ", GetChildNames(draggable))}");

                    ikTarget = draggable.Find("IkTarget");
                    if (ikTarget != null)
                    {
                        ModLogger.Info($"Found IkTarget: {ikTarget.name} at position {ikTarget.position}");
                        // Start IkTarget at ScanTarget position
                        ikTarget.position = scanTarget.position + Vector3.up * 0.1f;

                        // Add a visible renderer to the IkTarget for debugging
                        SetupIkTargetVisualizer();
                    }
                    else
                    {
                        ModLogger.Error("IkTarget not found in Draggable!");
                    }
                }
                else
                {
                    ModLogger.Error("Draggable not found in ScannerStation!");
                }
            }

            if (scanEffect == null)
            {
                // Find scan effect image - look for imgScanEffect in the scanner area
                var holder = transform.parent?.Find("ScannerDisplay")?.Find("Holder");
                if (holder != null)
                {
                    var canvas = holder.Find("Canvas");
                    if (canvas != null)
                    {
                        scanEffect = canvas.Find("imgScanEffect")?.GetComponent<Image>();
                        if (scanEffect != null)
                        {
                            scanEffect.gameObject.SetActive(false); // Hide initially
                            ModLogger.Info("Found scan effect image");
                        }
                    }
                }
            }

            ModLogger.Info($"ScannerStation initialized - Mode: {(useNewPalmScanner ? "Palm Scanner" : "IK System")}, ScanTarget: {scanTarget != null}");
        }

        private void SetupInteractableComponent()
        {
            // Get or create InteractableObject component
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Info("Added InteractableObject component to ScannerStation");
            }
            else
            {
                ModLogger.Info("Found existing InteractableObject component on ScannerStation");
            }

            // Configure the interaction
            string message = useNewPalmScanner ? "Scan fingerprints" : "Scan fingerprints";
            interactableObject.SetMessage(message);
            interactableObject.SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);

            // Set up event listeners with IL2CPP-safe casting
#if !MONO
            // Use System.Action for IL2CPP compatibility
            interactableObject.onInteractStart.AddListener((System.Action)OnInteractStart);
#else
            // Use UnityAction for Mono
            interactableObject.onInteractStart.AddListener(OnInteractStart);
#endif

            ModLogger.Info("InteractableObject component configured with event listeners");
        }

        private void SetupPalmScannerComponents()
        {
            ModLogger.Info("Setting up palm scanner components");

            // Find interaction camera (corrected path: Interaction/InteractionCamera)
            if (interactionCamera == null)
            {
                var interaction = transform.Find("Interaction");
                if (interaction != null)
                {
                    var interactionCameraObj = interaction.Find("InteractionCamera");
                    if (interactionCameraObj != null)
                    {
                        interactionCamera = interactionCameraObj.GetComponent<Camera>();
                        ModLogger.Info("Found InteractionCamera for palm scanner");
                    }
                }
            }

            // Setup palm model (MockHand) 
            SetupPalmModel();

            // Find PunchContainer for disabling during interaction
            FindPunchContainer();

            // Ensure interaction camera is disabled initially
            if (interactionCamera != null)
            {
                interactionCamera.gameObject.SetActive(false);
                ModLogger.Info("InteractionCamera disabled initially");
            }

            // Hide imgScanEffect initially
            HideImgScanEffect();

            ModLogger.Info($"Palm scanner setup complete - Camera: {interactionCamera != null}, Palm: {palmModel != null}");
        }

        private void SetupOldIKComponents()
        {
            ModLogger.Info("Setting up old IK system components");
            // Keep existing IK setup code...
        }

        private void OnInteractStart()
        {
            if (isScanning || isPalmScanning)
            {
                if (interactableObject != null)
                    interactableObject.SetMessage("Scanning in progress...");
                return;
            }

            // Check if already completed
            if (bookingProcess != null && bookingProcess.fingerprintComplete)
            {
                if (interactableObject != null)
                    interactableObject.SetMessage("Scan already complete");
                return;
            }

            if (useNewPalmScanner)
            {
                // Simplified palm scanner - just complete scan immediately
                StartSimplePalmScan();
            }
            else
            {
                // Start old IK scanning process
                currentPlayer = Player.Local;
                if (currentPlayer != null)
                {
                    playerCamera = currentPlayer.GetComponentInChildren<Camera>();
                    MelonCoroutines.Start(StartScanProcess(currentPlayer));
                }
                else
                {
                    ModLogger.Error("No local player found for scanner!");
                }
            }
        }

        private void StartSimplePalmScan()
        {
            ModLogger.Info("Starting palm scan with camera lock");

            // Lock camera and movement first
            if (interactionCamera == null || PlayerSingleton<PlayerCamera>.Instance == null)
            {
                ModLogger.Error("Cannot start palm scan - missing camera components");
                return;
            }

            StartCameraView();
            MelonCoroutines.Start(SimplePalmScanProcess());
        }

        private void StartCameraView()
        {
            ModLogger.Info("Starting camera view for palm scan");
            inScannerView = true;
            isPalmScanning = true;

            FindPunchContainer(); // Find runtime refs like PunchContainer

            // Disable punch container like CameraHubController 
            if (punchContainer != null)
                punchContainer.SetActive(false);

            // Freeze player movement
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
            PlayerSingleton<PlayerMovement>.Instance.canMove = false;
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

            // Free mouse for potential future interaction (but no dragging needed)
            playerCamera.FreeMouse();
            Singleton<HUD>.Instance.SetCrosshairVisible(false);

            ModLogger.Info("Camera locked to scanner view");

            // Register exit listener for escape key
#if !MONO
            GameInput.RegisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner, priority: 2);
#else
            GameInput.RegisterExitListener(OnExitPalmScanner, priority: 2);
#endif
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator SimplePalmScanProcess()
        {
            // Wait a moment for camera to settle
            yield return new WaitForSeconds(0.5f);

            if (interactableObject != null)
                interactableObject.SetMessage("Scanning...");

            // Show notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Scanning in progress...",
                    NotificationType.Instruction
                );
            }

            // Start scan animation
            yield return StartScanAnimation();

            // Complete the scan
            if (bookingProcess != null)
            {
                bookingProcess.SetFingerprintComplete("HAND_SCAN_" + System.DateTime.Now.Ticks);
                ModLogger.Info("Hand scan completed successfully");
            }

            // Show success notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Scan complete!",
                    NotificationType.Progress
                );
            }

            // Wait a moment before exit
            yield return new WaitForSeconds(1f);

            // Auto-exit scanner view
            EndCameraView();
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator StartScanAnimation()
        {
            ModLogger.Info("Starting scan animation");

            // Use the known structure: ScannerStation/Holder/Canvas/
            var holder = transform.Find("Holder");
            if (holder == null)
            {
                ModLogger.Error("Holder not found for scan animation");
                yield return new WaitForSeconds(2f);
                yield break;
            }

            var canvasTransform = holder.Find("Canvas");
            if (canvasTransform == null)
            {
                ModLogger.Error("Canvas not found under Holder for scan animation");
                yield return new WaitForSeconds(2f);
                yield break;
            }

            var canvas = canvasTransform.GetComponent<Canvas>();
            if (canvas == null)
            {
                ModLogger.Error("Canvas component not found on Canvas GameObject");
                yield return new WaitForSeconds(2f);
                yield break;
            }

            ModLogger.Info("Found Canvas at ScannerStation/Holder/Canvas/");

            // Find imgScanEffect, Start, and End GameObjects
            var imgScanEffect = canvas.transform.Find("imgScanEffect");
            var startObj = canvas.transform.Find("Start");
            var endObj = canvas.transform.Find("End");

            if (imgScanEffect == null || startObj == null || endObj == null)
            {
                ModLogger.Error($"Missing animation components - imgScanEffect: {imgScanEffect != null}, Start: {startObj != null}, End: {endObj != null}");
                yield return new WaitForSeconds(2f);
                yield break;
            }

            RectTransform scanRect = imgScanEffect.GetComponent<RectTransform>();
            RectTransform startRect = startObj.GetComponent<RectTransform>();
            RectTransform endRect = endObj.GetComponent<RectTransform>();

            if (scanRect == null || startRect == null || endRect == null)
            {
                ModLogger.Error("Missing RectTransform components for animation");
                yield return new WaitForSeconds(2f);
                yield break;
            }

            // Get positions from Start and End GameObjects
            Vector2 startPos = startRect.anchoredPosition;
            Vector2 endPos = endRect.anchoredPosition;

            ModLogger.Info($"Animation positions - Start: {startPos}, End: {endPos}");

            // Make sure scan image is visible
            imgScanEffect.gameObject.SetActive(true);

            // Animation: Start -> End -> Start
            float animTime = 1.5f; // Time for each segment

            // Phase 1: Start -> End
            ModLogger.Info("Animation Phase 1: Start -> End");
            scanRect.anchoredPosition = startPos;

            float elapsed = 0f;
            while (elapsed < animTime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / animTime;
                scanRect.anchoredPosition = Vector2.Lerp(startPos, endPos, t);
                yield return null;
            }

            // Phase 2: End -> Start
            ModLogger.Info("Animation Phase 2: End -> Start");
            elapsed = 0f;
            while (elapsed < animTime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / animTime;
                scanRect.anchoredPosition = Vector2.Lerp(endPos, startPos, t);
                yield return null;
            }

            // Ensure final position is at start
            scanRect.anchoredPosition = startPos;

            // Hide the scan effect
            imgScanEffect.gameObject.SetActive(false);

            ModLogger.Info("Scan animation completed: Start -> End -> Start");
        }

        private void EndCameraView()
        {
            ModLogger.Info("Ending camera view");
            inScannerView = false;
            isPalmScanning = false;

            // Re-enable punch container
            if (punchContainer != null)
                punchContainer.SetActive(true);

            // Restore player state
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
            PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
            PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(true);

            // Camera reset
            if (playerCamera != null)
            {
                playerCamera.StopFOVOverride(0.15f);
                playerCamera.StopTransformOverride(0.15f);
                playerCamera.LockMouse();
                Singleton<HUD>.Instance.SetCrosshairVisible(true);
            }

            // Deregister exit listener
#if !MONO
            GameInput.DeregisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner);
#else
            GameInput.DeregisterExitListener(OnExitPalmScanner);
#endif

            // Update final state
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Palm scan complete");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
            }

            ModLogger.Info("Camera view ended - scan complete");
        }

        private void StartPalmScannerView()
        {
            if (interactionCamera == null || PlayerSingleton<PlayerCamera>.Instance == null) return;

            ModLogger.Info("Starting palm scanner view");
            inScannerView = true;
            isPalmScanning = true;

            FindPunchContainer(); // Find runtime refs like PunchContainer

            // Disable punch container like CameraHubController 
            if (punchContainer != null)
                punchContainer.SetActive(false);

            // Freeze player movement
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
            PlayerSingleton<PlayerMovement>.Instance.canMove = false;
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

            // Show palm model
            if (palmModel != null)
            {
                palmModel.SetActive(true);
                palmModel.transform.position = originalPalmPosition;
                ModLogger.Info($"Palm model activated: {palmModel.name} at {palmModel.transform.position}");
            }

            // Show instructions
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Click and drag to move your palm to the scanner",
                    NotificationType.Instruction
                );
            }

            // Register exit listener
#if !MONO
            GameInput.RegisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner, priority: 2);
#else
            GameInput.RegisterExitListener(OnExitPalmScanner, priority: 2);
#endif
        }

        private void OnExitPalmScanner(ExitAction action)
        {
            if (!action.Used && inScannerView && action.exitType == ExitType.Escape)
            {
                action.Used = true;
                EndPalmScannerView();
            }
        }

        private void EndPalmScannerView()
        {
            ModLogger.Info("Ending palm scanner view");
            inScannerView = false;
            isPalmScanning = false;

            // Re-enable punch container
            if (punchContainer != null)
                punchContainer.SetActive(true);

            // Restore player state
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
            PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
            PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(true);

            // Camera reset
            if (playerCamera != null)
            {
                playerCamera.StopFOVOverride(0.15f);
                playerCamera.StopTransformOverride(0.15f);
                playerCamera.LockMouse();
                Singleton<HUD>.Instance.SetCrosshairVisible(true);
            }

            // Hide palm model
            if (palmModel != null)
                palmModel.SetActive(false);

            // Deregister exit listener
#if !MONO
            GameInput.DeregisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner);
#else
            GameInput.DeregisterExitListener(OnExitPalmScanner);
#endif
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator StartScanProcess(Player player)
        {
            isScanning = true;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Drag your hand to the scanner...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }

            ModLogger.Info("Starting fingerprint scan process");

            // Set up IK for right hand
            SetupHandIK(player);

            // Show the IK target visualizer
            ShowIkTargetVisualizer(true);

            // Show instruction
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification("Click and drag to move your hand to the scanner", NotificationType.Instruction);
            }

            // Start the scan timer
            scanCoroutine = MelonCoroutines.Start(ScanTimer()) as Coroutine;

            // Handle dragging
            while (isScanning && !bookingProcess.fingerprintComplete)
            {
                HandleMouseDrag();

                // IK should update automatically, just log status occasionally
                if (Time.time % 1f < Time.deltaTime && ikController != null && ikActive && ikController.BodyIK != null)
                {
                    ModLogger.Info($"IK Status - Enabled: {ikController.BodyIK.enabled}, Weight: {ikController.BodyIK.solvers.rightHand.IKPositionWeight}, Target: {ikController.BodyIK.solvers.rightHand.target}");
                }

                yield return null;
            }

            // Hide the IK target visualizer
            ShowIkTargetVisualizer(false);

            // Clean up
            CleanupHandIK();

            if (scanCoroutine != null)
            {
                MelonCoroutines.Stop(scanCoroutine);
                scanCoroutine = null;
            }

            // Reset interaction state
            isScanning = false;

            if (bookingProcess.fingerprintComplete)
            {
                if (interactableObject != null)
                {
                    interactableObject.SetMessage("Fingerprint scan complete");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
                }
            }
            else
            {
                if (interactableObject != null)
                {
                    interactableObject.SetMessage("Scan fingerprints");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
            }

            ModLogger.Info("Fingerprint scan process completed");
        }

        private void SetupHandIK(Player player)
        {
            try
            {
                ModLogger.Info("=== TARGETING VIEWMODEL AVATAR FOR HAND IK ===");

                // Use Player.Local to get the local player properly
                var localPlayer = Player.Local;
                if (localPlayer == null)
                {
                    ModLogger.Error("No local player found");
                    return;
                }

                // Direct path to ViewmodelAvatar: Player_Local/CameraContainer/Camera/ViewmodalContainer/EquipContainer/ViewmodelAvatarContainer/ViewmodelAvatar
                var viewmodelAvatar = localPlayer.transform
                    .Find("Player_Local/CameraContainer/Camera/ViewmodalContainer/EquipContainer/ViewmodelAvatarContainer/ViewmodelAvatar");

                if (viewmodelAvatar != null)
                {
                    ModLogger.Info($"Found ViewmodelAvatar directly: {viewmodelAvatar.name}");

                    // Get IK controller from ViewmodelAvatar
                    ikController = viewmodelAvatar.GetComponent<AvatarIKController>();
                    if (ikController == null)
                    {
                        ikController = viewmodelAvatar.GetComponentInChildren<AvatarIKController>();
                    }

                    if (ikController != null)
                    {
                        ModLogger.Info($"Found IK controller on ViewmodelAvatar: {ikController.name}");
                    }
                    else
                    {
                        ModLogger.Warn("No IK controller found on ViewmodelAvatar");
                    }
                }
                else
                {
                    ModLogger.Error("Could not find ViewmodelAvatar at expected path");
                }

                // If ViewmodelAvatar IK not found, fall back to main body IK
                if (ikController == null)
                {
                    ModLogger.Info("Falling back to main body IK controller");
                    ikController = localPlayer.GetComponentInChildren<AvatarIKController>();
                    if (ikController != null)
                    {
                        ModLogger.Info($"Using main body IK controller: {ikController.name}");
                    }
                }

                if (ikController == null)
                {
                    ModLogger.Error("No IK controller found anywhere");
                    return;
                }

                if (ikController != null && ikController.BodyIK != null)
                {
                    ModLogger.Info($"Using IK controller: {ikController.name} with BodyIK. Enabled: {ikController.BodyIK.enabled}");
                    ModLogger.Info($"Right hand solver exists: {ikController.BodyIK.solvers.rightHand != null}");

                    // Check if the right hand solver exists and is valid
                    if (ikController.BodyIK.solvers.rightHand == null)
                    {
                        ModLogger.Error("Right hand solver is null - cannot set up IK");
                        return;
                    }

                    // Store original right hand target (may be null)
                    originalRightHandTarget = ikController.BodyIK.solvers.rightHand.target;
                    ModLogger.Info($"Original right hand target: {(originalRightHandTarget != null ? originalRightHandTarget.name : "null")}");

                    // Enable IK system first
                    ikController.SetIKActive(true);
                    ikController.BodyIK.enabled = true;

                    // Try to initiate the biped IK if not already done
                    try
                    {
                        ikController.BodyIK.InitiateBipedIK();
                        ModLogger.Info("BipedIK initiated successfully");
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Warn($"BipedIK initiation failed or already done: {ex.Message}");
                    }

                    // Now set the IK target - wrap in detailed error handling
                    try
                    {
                        ModLogger.Info("Setting IK target...");
                        ikController.BodyIK.solvers.rightHand.target = ikTarget;
                        ModLogger.Info("IK target set successfully");

                        ikController.BodyIK.solvers.rightHand.IKPositionWeight = 1f;
                        ModLogger.Info("IK weight set successfully");

                        ModLogger.Info($"Set IK target to: {ikTarget.position}, Weight: {ikController.BodyIK.solvers.rightHand.IKPositionWeight}");

                        ikActive = true;

                        ModLogger.Info($"IK activated. BodyIK enabled: {ikController.BodyIK.enabled}");
                        ModLogger.Info($"IK setup successful - right hand targeting IkTarget at {ikTarget.position}");
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Error($"Error in IK target assignment: {ex.Message}");
                        ModLogger.Error($"ikController: {ikController != null}, BodyIK: {ikController?.BodyIK != null}, rightHand: {ikController?.BodyIK?.solvers?.rightHand != null}, ikTarget: {ikTarget != null}");
                        throw; // Re-throw to be caught by outer try-catch
                    }
                }
                else
                {
                    ModLogger.Warn("No AvatarIKController or BodyIK found - IK not available");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error setting up hand IK: {ex.Message}");
                ModLogger.Info("Continuing without IK - visualizer will still work for debugging");
                ikActive = false;
            }
        }

        private void CleanupHandIK()
        {
            try
            {
                if (ikController != null && ikActive && ikController.BodyIK != null)
                {
                    // Restore original right hand target (may be null)
                    ikController.BodyIK.solvers.rightHand.target = originalRightHandTarget;
                    ikController.BodyIK.solvers.rightHand.IKPositionWeight = 0f;

                    // Disable IK
                    ikController.SetIKActive(false);
                    ikActive = false;

                    ModLogger.Info($"IK cleanup successful - restored original target: {(originalRightHandTarget != null ? originalRightHandTarget.name : "null")}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error cleaning up hand IK: {ex.Message}");
            }
        }

        private void HandleMouseDrag()
        {
            // Log that this method is being called
            if (Time.time % 0.5f < Time.deltaTime) // Log every half second to avoid spam
            {
                ModLogger.Info($"HandleMouseDrag called - isDragging: {isDragging}");
            }

            // Check for mouse input with detailed debugging
            bool mouseButtonDown = Input.GetMouseButtonDown(0);
            bool mouseButtonHeld = Input.GetMouseButton(0);
            bool mouseButtonUp = Input.GetMouseButtonUp(0);

            // Log mouse state every few frames
            if (Time.time % 0.5f < Time.deltaTime) // Log every half second
            {
                ModLogger.Info($"Mouse state - Down: {mouseButtonDown}, Held: {mouseButtonHeld}, Up: {mouseButtonUp}");
            }

            if (mouseButtonDown)
            {
                isDragging = true;
                ModLogger.Info("Started dragging hand - mouse clicked down");
            }
            else if (mouseButtonHeld && !isDragging)
            {
                ModLogger.Info("Mouse held but not dragging - trying to start drag");
                isDragging = true; // Force start dragging if mouse is held
            }

            if (isDragging)
            {
                if (mouseButtonHeld)
                {
                    // Convert mouse position to world position relative to scanner
                    Vector3 mousePos = Input.mousePosition;
                    ModLogger.Info($"Mouse position: {mousePos}");

                    // Get screen center as reference
                    Vector3 screenCenter = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);

                    // Calculate offset from screen center
                    Vector3 mouseOffset = mousePos - screenCenter;

                    // Convert screen offset to world offset (scale it down)
                    float sensitivity = 0.02f; // Increased sensitivity further
                    Vector3 worldOffset = new Vector3(mouseOffset.x * sensitivity, 0f, mouseOffset.y * sensitivity);

                    // Apply offset to scanner target position
                    Vector3 newPos = scanTarget.position + worldOffset;
                    newPos.y = scanTarget.position.y + 0.1f; // Keep at scanner height

                    // Constrain to reasonable area around scanner
                    float maxDistance = validRange * 1.5f;
                    Vector3 fromScanner = newPos - scanTarget.position;
                    fromScanner.y = 0; // Don't constrain Y axis
                    if (fromScanner.magnitude > maxDistance)
                    {
                        fromScanner = fromScanner.normalized * maxDistance;
                        newPos = scanTarget.position + fromScanner;
                        newPos.y = scanTarget.position.y + 0.1f;
                    }

                    // Update IK target position
                    Vector3 oldPos = ikTarget.position;
                    ikTarget.position = newPos;

                    // Debug logging to see if position is actually updating
                    ModLogger.Info($"IkTarget position - Old: {oldPos}, New: {ikTarget.position}, Changed: {Vector3.Distance(oldPos, newPos) > 0.001f}");
                }
                else if (mouseButtonUp)
                {
                    // Released mouse
                    isDragging = false;
                    ModLogger.Info("Stopped dragging hand - mouse released");
                }
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ScanTimer()
        {
            float timeRemaining = scanDuration;
            bool scanStarted = false;

            while (timeRemaining > 0 && isScanning)
            {
                // Check if hand is in valid position
                if (ikTarget != null && scanTarget != null)
                {
                    float distance = Vector3.Distance(ikTarget.position, scanTarget.position);

                    if (distance <= validRange)
                    {
                        if (!scanStarted)
                        {
                            // Start scanning
                            scanStarted = true;
                            ModLogger.Info("Hand in position - starting scan");

                            // Show scan effect
                            if (scanEffect != null)
                            {
                                scanEffect.gameObject.SetActive(true);
                                // Could add pulsing or animation here
                            }

                            // Play scanning sound
                            if (scannerAudio != null && scanningSound != null)
                            {
                                scannerAudio.clip = scanningSound;
                                scannerAudio.Play();
                            }

                            // Show progress notification
                            if (BehindBarsUIManager.Instance != null)
                            {
                                BehindBarsUIManager.Instance.ShowNotification("Scanning... Hold still!", NotificationType.Progress);
                            }
                        }

                        // Continue scanning - reduce timer faster when in position
                        timeRemaining -= Time.deltaTime * 2f; // Scan twice as fast when in position
                    }
                    else
                    {
                        if (scanStarted)
                        {
                            // Hand moved out of position
                            scanStarted = false;
                            ModLogger.Info("Hand moved out of position - scan paused");

                            // Hide scan effect
                            if (scanEffect != null)
                            {
                                scanEffect.gameObject.SetActive(false);
                            }

                            // Play error sound
                            if (scannerAudio != null && errorSound != null)
                            {
                                scannerAudio.clip = errorSound;
                                scannerAudio.Play();
                            }

                            // Show instruction
                            if (BehindBarsUIManager.Instance != null)
                            {
                                BehindBarsUIManager.Instance.ShowNotification("Move hand back to scanner!", NotificationType.Warning);
                            }
                        }

                        // Regular timer countdown when not scanning
                        timeRemaining -= Time.deltaTime;
                    }
                }
                else
                {
                    timeRemaining -= Time.deltaTime;
                }

                yield return null;
            }

            // Check final result
            if (scanStarted && ikTarget != null && scanTarget != null)
            {
                float finalDistance = Vector3.Distance(ikTarget.position, scanTarget.position);
                if (finalDistance <= validRange)
                {
                    // Success!
                    CompleteScan();
                }
                else
                {
                    // Failed - hand not in position
                    FailScan();
                }
            }
            else
            {
                // Time ran out
                FailScan();
            }
        }

        private void CompleteScan()
        {
            ModLogger.Info("Fingerprint scan completed successfully!");

            // Hide scan effect
            if (scanEffect != null)
            {
                scanEffect.gameObject.SetActive(false);
            }

            // Play success sound
            if (scannerAudio != null && successSound != null)
            {
                scannerAudio.clip = successSound;
                scannerAudio.Play();
            }

            // Show success notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification("Fingerprint scan complete!", NotificationType.Progress);
            }

            // Mark as complete in booking process
            if (bookingProcess != null)
            {
                bookingProcess.SetFingerprintComplete("SCAN_001");
            }

            isScanning = false;
        }

        private void FailScan()
        {
            ModLogger.Info("Fingerprint scan failed - time expired or hand not in position");

            // Hide scan effect
            if (scanEffect != null)
            {
                scanEffect.gameObject.SetActive(false);
            }

            // Play error sound
            if (scannerAudio != null && errorSound != null)
            {
                scannerAudio.clip = errorSound;
                scannerAudio.Play();
            }

            // Show failure notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification("Scan failed - try again!", NotificationType.Warning);
            }

            isScanning = false;
        }

        public bool IsComplete()
        {
            return bookingProcess != null && bookingProcess.fingerprintComplete;
        }

        void Update()
        {
            // ALWAYS update interaction state like MugshotStation - no early returns!
            if (!isScanning && !isPalmScanning && !inScannerView && interactableObject != null)
            {
                if (IsComplete())
                {
                    interactableObject.SetMessage("Scan Fingerprints");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                else
                {
                    interactableObject.SetMessage("Scan Fingerprints");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
            }

            // Handle palm scanner mouse dragging
            if (useNewPalmScanner && inScannerView)
            {
                HandlePalmDragging();
            }
        }

        private void SetupIkTargetVisualizer()
        {
            if (ikTarget == null) return;

            try
            {
                // Create a bright sphere to visualize the IK target
                ikTargetVisualizer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ikTargetVisualizer.name = "IkTarget_Visualizer";
                ikTargetVisualizer.transform.parent = ikTarget;
                ikTargetVisualizer.transform.localPosition = Vector3.zero;
                ikTargetVisualizer.transform.localScale = Vector3.one * 1.0f; // Much bigger sphere for visibility

                // Remove collider so it doesn't interfere
                var collider = ikTargetVisualizer.GetComponent<Collider>();
                if (collider != null)
                {
                    UnityEngine.Object.DestroyImmediate(collider);
                }

                // Get the renderer and make it bright red
                ikTargetRenderer = ikTargetVisualizer.GetComponent<Renderer>();
                if (ikTargetRenderer != null)
                {
                    // Use the simplest possible bright red material
                    var material = new Material(Shader.Find("Unlit/Color"));
                    material.color = Color.red;
                    ikTargetRenderer.material = material;
                    ModLogger.Info($"Applied Unlit/Color red material to visualizer");
                }

                // Start hidden
                ikTargetVisualizer.SetActive(false);

                ModLogger.Info($"IkTarget visualizer created at world position: {ikTargetVisualizer.transform.position}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating IkTarget visualizer: {ex.Message}");
            }
        }

        private void ShowIkTargetVisualizer(bool show)
        {
            if (ikTargetVisualizer != null)
            {
                ikTargetVisualizer.SetActive(show);
                if (show)
                {
                    ModLogger.Info($"IkTarget visualizer shown at position: {ikTargetVisualizer.transform.position}");
                }
                else
                {
                    ModLogger.Info("IkTarget visualizer hidden");
                }
            }
        }

        private string[] GetChildNames(Transform parent)
        {
            string[] names = new string[parent.childCount];
            for (int i = 0; i < parent.childCount; i++)
            {
                names[i] = parent.GetChild(i).name;
            }
            return names;
        }

        private string GetTransformPath(Transform transform)
        {
            string path = transform.name;
            Transform current = transform.parent;

            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
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

            // Store original position for reset
            if (palmModel != null)
            {
                originalPalmPosition = palmModel.transform.position;
                palmModel.SetActive(false); // Hide initially
                ModLogger.Info($"Palm model setup complete: {palmModel.name} at position {palmModel.transform.position}");
            }
        }

        private void FindPunchContainer()
        {
            if (punchContainer == null)
            {
                // Try to find in scene root "CameraContainer"
                var mainCameraContainer = GameObject.Find("CameraContainer");
                if (mainCameraContainer != null)
                {
                    var punchController = mainCameraContainer.transform.Find("PunchController");
                    if (punchController != null)
                        punchContainer = punchController.gameObject;
                }

                if (punchContainer != null)
                {
                    ModLogger.Info($"Found PunchContainer: {punchContainer.name}");
                }
            }
        }

        private void HandlePalmDragging()
        {
            if (palmModel == null) return;

            bool mouseDown = Input.GetMouseButtonDown(0);
            bool mouseHeld = Input.GetMouseButton(0);
            bool mouseUp = Input.GetMouseButtonUp(0);

            if (mouseDown)
            {
                isDragging = true;
                mouseStartPos = Input.mousePosition;
                dragStartWorldPos = palmModel.transform.position;
                ModLogger.Info("Started dragging palm");
            }
            else if (mouseHeld && isDragging)
            {
                Vector3 currentMousePos = Input.mousePosition;
                Vector3 mouseDelta = currentMousePos - mouseStartPos;

                if (interactionCamera != null && mouseDelta.magnitude > 1f)
                {
                    // Simple screen-to-world conversion
                    Vector3 rightVector = interactionCamera.transform.right;
                    Vector3 upVector = interactionCamera.transform.up;

                    Vector3 worldDelta = (rightVector * mouseDelta.x + upVector * mouseDelta.y) * dragSensitivity;
                    Vector3 newPosition = dragStartWorldPos + worldDelta;

                    // Constrain to max distance from scanner
                    if (scanTarget != null)
                    {
                        Vector3 fromTarget = newPosition - scanTarget.position;
                        if (fromTarget.magnitude > maxDragDistance)
                        {
                            fromTarget = fromTarget.normalized * maxDragDistance;
                            newPosition = scanTarget.position + fromTarget;
                        }

                        // Keep palm slightly above scanner
                        newPosition.y = Mathf.Max(newPosition.y, scanTarget.position.y + 0.02f);
                    }

                    palmModel.transform.position = newPosition;

                    // Check for scanning trigger
                    if (scanTarget != null)
                    {
                        float distance = Vector3.Distance(palmModel.transform.position, scanTarget.position);
                        if (distance <= validRange)
                        {
                            // Start scanning animation or complete scan
                            CompletePalmScan();
                        }
                    }
                }
            }
            else if (mouseUp)
            {
                isDragging = false;
                ModLogger.Info("Ended dragging palm");
            }
        }

        private void CompletePalmScan()
        {
            ModLogger.Info("Palm scan completed!");

            // Mark as complete in booking process
            if (bookingProcess != null)
            {
                bookingProcess.SetFingerprintComplete("PALM_SCAN_" + System.DateTime.Now.Ticks);
            }

            // Show success notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Palm scan complete!",
                    NotificationType.Progress
                );
            }

            // Auto-exit scanner view after short delay
            MelonCoroutines.Start(DelayedExitScannerView());
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator DelayedExitScannerView()
        {
            yield return new WaitForSeconds(2f);
            EndPalmScannerView();
        }

        private void HideImgScanEffect()
        {
            // Find and hide imgScanEffect on startup
            var holder = transform.Find("Holder");
            if (holder != null)
            {
                var canvasTransform = holder.Find("Canvas");
                if (canvasTransform != null)
                {
                    var imgScanEffect = canvasTransform.Find("imgScanEffect");
                    if (imgScanEffect != null)
                    {
                        imgScanEffect.gameObject.SetActive(false);
                        ModLogger.Info("imgScanEffect hidden initially");
                    }
                    else
                    {
                        ModLogger.Warn("imgScanEffect not found to hide initially");
                    }
                }
                else
                {
                    ModLogger.Warn("Canvas not found to hide imgScanEffect initially");
                }
            }
            else
            {
                ModLogger.Warn("Holder not found to hide imgScanEffect initially");
            }
        }
    }
}