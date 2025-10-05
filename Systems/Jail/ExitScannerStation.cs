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
    /// Exit scanner station for final release - same as fingerprint scanner but teleports player out on completion
    /// </summary>
    public class ExitScannerStation : MonoBehaviour
    {
#if !MONO
        public ExitScannerStation(System.IntPtr ptr) : base(ptr) { }
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

        // Release teleport position (police station exit)
        public Vector3 releasePosition = new Vector3(13.7402f, 1.4857f, 38.1558f); // Police station exit coordinates
        public Vector3 releaseRotation = new Vector3(0f, 80.1529f, 0f); // Release rotation (facing away from wall)

        private bool isScanning = false;
        private bool isDragging = false;
        private bool isCompleted = false;
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
            ModLogger.Info("ExitScannerStation Start() called");

            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Info("ExitScannerStation interaction setup completed");

            // Setup based on scanner mode
            ModLogger.Info($"ExitScannerStation using palm scanner mode: {useNewPalmScanner}");
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
                scanTarget = transform.Find("ScanTarget");
                if (scanTarget != null)
                {
                    ModLogger.Info($"Found ExitScanner ScanTarget: {scanTarget.name}");
                }
                else
                {
                    ModLogger.Error("ScanTarget not found in ExitScannerStation!");
                }
            }

            if (ikTarget == null)
            {
                var draggable = transform.Find("Draggable");
                if (draggable != null)
                {
                    ikTarget = draggable.Find("IkTarget");
                    if (ikTarget != null)
                    {
                        ModLogger.Info($"Found ExitScanner IkTarget: {ikTarget.name} at position {ikTarget.position}");
                        ikTarget.position = scanTarget.position + Vector3.up * 0.1f;
                        SetupIkTargetVisualizer();
                    }
                    else
                    {
                        ModLogger.Error("IkTarget not found in Draggable!");
                    }
                }
                else
                {
                    ModLogger.Error("Draggable not found!");
                }
            }

            // ALWAYS enable SecuritySlots on exit door at startup for visual consistency
            EnableExitDoorSlots();
        }

        private void SetupInteractableComponent()
        {
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Info("Added InteractableObject component to ExitScannerStation");
            }

            // Configure the interaction - DISABLED by default, only enabled during release
            interactableObject.SetMessage("Exit scanner (not available)");
            interactableObject.SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid); // Disabled until release process

            // Set up event listeners with IL2CPP-safe casting
#if !MONO
            interactableObject.onInteractStart.AddListener((System.Action)OnInteractStart);
#else
            interactableObject.onInteractStart.AddListener(OnInteractStart);
#endif
        }

        private void SetupPalmScannerComponents()
        {
            ModLogger.Info("Setting up palm scanner components for ExitScannerStation");

            // Find interaction camera - looking in Interaction child
            if (interactionCamera == null)
            {
                var interaction = transform.Find("Interaction");
                if (interaction != null)
                {
                    var cameraObj = interaction.Find("InteractionCamera");
                    if (cameraObj != null)
                    {
                        interactionCamera = cameraObj.GetComponent<Camera>();
                        if (interactionCamera != null)
                        {
                            interactionCamera.enabled = false;
                            ModLogger.Info("Found interaction camera for exit scanner");
                        }
                        else
                        {
                            ModLogger.Warn("InteractionCamera found but no Camera component");
                        }
                    }
                    else
                    {
                        ModLogger.Warn("InteractionCamera not found in Interaction");
                    }
                }
                else
                {
                    ModLogger.Warn("Interaction child not found in ExitScannerStation");
                }
            }

            // Find palm model - looking in PalmScanner child (but don't disable it)
            if (palmModel == null)
            {
                var palmScanner = transform.Find("PalmScanner");
                if (palmScanner != null)
                {
                    palmModel = palmScanner.gameObject;
                    originalPalmPosition = palmModel.transform.position;
                    // DON'T disable PalmScanner - keep it visible
                    ModLogger.Info($"Found palm model for exit scanner: {palmModel.name} at {originalPalmPosition}");
                }
                else
                {
                    ModLogger.Warn("PalmScanner not found in ExitScannerStation");
                }
            }

            // Find scan effect image
            if (scanEffect == null)
            {
                var holder = transform.Find("Holder");
                if (holder != null)
                {
                    var canvas = holder.Find("Canvas");
                    if (canvas != null)
                    {
                        var imgScanEffect = canvas.Find("imgScanEffect");
                        if (imgScanEffect != null)
                        {
                            scanEffect = imgScanEffect.GetComponent<Image>();
                            if (scanEffect != null)
                            {
                                // Hide imgScanEffect initially like working scanner
                                scanEffect.gameObject.SetActive(false);
                                ModLogger.Info("Found scan effect image for exit scanner (hidden initially)");
                            }
                        }
                    }
                }

                if (scanEffect == null)
                {
                    ModLogger.Warn("imgScanEffect not found in ExitScannerStation");
                }
            }

            // Find scanner audio
            if (scannerAudio == null)
            {
                scannerAudio = GetComponent<AudioSource>();
                if (scannerAudio != null)
                {
                    ModLogger.Info("Found AudioSource for exit scanner");
                }
                else
                {
                    ModLogger.Warn("No AudioSource found on ExitScannerStation");
                }
            }

            ModLogger.Info($"Palm scanner setup complete - Camera: {interactionCamera != null}, Palm: {palmModel != null}, Audio: {scannerAudio != null}");
        }

        private void SetupOldIKComponents()
        {
            // Old IK system setup (same as original scanner)
            ModLogger.Info("Setting up old IK system for exit scanner");
        }

        private void SetupIkTargetVisualizer()
        {
            // Add visual indicator for IK target
            if (ikTarget != null)
            {
                ikTargetVisualizer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ikTargetVisualizer.transform.SetParent(ikTarget);
                ikTargetVisualizer.transform.localPosition = Vector3.zero;
                ikTargetVisualizer.transform.localScale = Vector3.one * 0.05f;
                ikTargetRenderer = ikTargetVisualizer.GetComponent<Renderer>();
                if (ikTargetRenderer != null)
                {
                    ikTargetRenderer.material.color = Color.green;
                }
                ModLogger.Info("Created IkTarget visualizer for exit scanner");
            }
        }

        private void OnInteractStart()
        {
            ModLogger.Info("ExitScannerStation interaction started!");

            if (isScanning)
            {
                ModLogger.Info("Already scanning - ignoring interaction");
                if (interactableObject != null)
                    interactableObject.SetMessage("Scanning in progress...");
                return;
            }

            if (isCompleted)
            {
                ModLogger.Info("Already completed - ignoring interaction");
                if (interactableObject != null)
                    interactableObject.SetMessage("Already completed");
                return;
            }

            currentPlayer = Player.Local;
            if (currentPlayer == null)
            {
                ModLogger.Error("No local player found for exit scan!");
                return;
            }

            ModLogger.Info($"Starting exit scan for player: {currentPlayer.name}");
            ModLogger.Info($"Components available - Camera: {interactionCamera != null}, Palm: {palmModel != null}, ScanTarget: {scanTarget != null}");

            // Start scanning process
            if (useNewPalmScanner)
            {
                ModLogger.Info("Using new palm scanner mode for exit");
                StartPalmScanMode();
            }
            else
            {
                ModLogger.Info("Using IK scanner mode for exit");
                StartIKScanMode();
            }
        }

        private void StartPalmScanMode()
        {
            ModLogger.Info("Starting palm scan with camera lock for exit scanner");

            // Lock camera and movement first (copied from working ScannerStation)
            if (interactionCamera == null || PlayerSingleton<PlayerCamera>.Instance == null)
            {
                ModLogger.Error("Cannot start exit palm scan - missing camera components");
                return;
            }

            StartCameraView();
            MelonCoroutines.Start(SimplifiedScanProcess());
        }

        private void StartCameraView()
        {
            ModLogger.Info("Starting camera view for exit palm scan");
            inScannerView = true;
            isScanning = true;

            // Disable punch container like working scanner
            if (punchContainer != null)
                punchContainer.SetActive(false);

            // Freeze player movement (copied from working ScannerStation)
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            PlayerSingleton<PlayerMovement>.Instance.canMove = false;
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(false);
            PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(false);

            // Camera transition using same approach as working scanner
            playerCamera.OverrideFOV(60f, 0.15f);
            playerCamera.OverrideTransform(
                interactionCamera.transform.position,
                interactionCamera.transform.rotation,
                0.15f
            );

            // Free mouse and hide crosshair
            playerCamera.FreeMouse();
            Singleton<HUD>.Instance.SetCrosshairVisible(false);

            // Update interaction message
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Scanning...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }

            ModLogger.Info("Exit scanner camera locked and player frozen");
        }

        /// <summary>
        /// Main scan process - copied from working ScannerStation
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator SimplifiedScanProcess()
        {
            ModLogger.Info("Starting scan animation for exit scanner");

            // Start scan animation (copied from working ScannerStation)
            yield return StartCoroutine(StartScanAnimation());

            // Complete the scan
            CompletePalmScan();
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator StartScanAnimation()
        {
            ModLogger.Info("Starting scan animation");

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

            ModLogger.Info("Found Canvas at ExitScannerStation/Holder/Canvas/");

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

            // Animation: Start -> End -> Start (same as working scanner)
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

            ModLogger.Info("Exit scan animation completed: Start -> End -> Start");
        }

        private void StartIKScanMode()
        {
            ModLogger.Info("Starting IK scan mode for exit scanner");
            // Implement IK scanning if needed (same as original)
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator PalmScanTimer()
        {
            float elapsed = 0f;
            bool scanCompleted = false;

            // Show notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Drag your palm to the scanner",
                    NotificationType.Instruction
                );
            }

            // Play scanning sound
            if (scannerAudio != null && scanningSound != null)
            {
                scannerAudio.clip = scanningSound;
                scannerAudio.loop = true;
                scannerAudio.Play();
            }

            while (elapsed < scanDuration && !scanCompleted)
            {
                elapsed += Time.deltaTime;

                // Handle palm dragging
                if (Input.GetMouseButton(0) && palmModel != null)
                {
                    HandlePalmDrag();

                    // Check if palm is close enough to scanner target
                    if (scanTarget != null)
                    {
                        float distance = Vector3.Distance(palmModel.transform.position, scanTarget.position);
                        if (distance < validRange)
                        {
                            scanCompleted = true;
                            ModLogger.Info("Exit scan completed - palm reached target");
                        }
                    }
                }

                yield return null;
            }

            // Stop scanning sound
            if (scannerAudio != null)
            {
                scannerAudio.Stop();
            }

            if (scanCompleted)
            {
                CompletePalmScan();
            }
            else
            {
                FailPalmScan();
            }
        }

        private void HandlePalmDrag()
        {
            if (palmModel == null) return;

            Vector3 mousePos = Input.mousePosition;
            if (!isDragging)
            {
                isDragging = true;
                mouseStartPos = mousePos;
                dragStartWorldPos = palmModel.transform.position;
            }

            Vector3 mouseDelta = mousePos - mouseStartPos;
            Vector3 worldDelta = interactionCamera.ScreenToWorldPoint(new Vector3(mouseDelta.x, mouseDelta.y, interactionCamera.nearClipPlane + 1f)) - interactionCamera.ScreenToWorldPoint(Vector3.zero);

            Vector3 newPosition = dragStartWorldPos + worldDelta * dragSensitivity;

            // Clamp to max drag distance
            if (Vector3.Distance(newPosition, originalPalmPosition) <= maxDragDistance)
            {
                palmModel.transform.position = newPosition;
            }
        }

        private void CompletePalmScan()
        {
            ModLogger.Info("Exit palm scan completed successfully!");

            // Play success sound
            if (scannerAudio != null && successSound != null)
            {
                scannerAudio.clip = successSound;
                scannerAudio.loop = false;
                scannerAudio.Play();
            }

            // Show success notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Exit scan complete - you are now free!",
                    NotificationType.Progress
                );
            }

            // Open exit door after successful scan
            OpenExitDoor();

            // Mark as completed
            isCompleted = true;
            isScanning = false;

            // Reset camera
            ResetCamera();

            // Update interaction state
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Scan complete - proceed to exit");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
            }

            // Release the player after a short delay
            MelonCoroutines.Start(ReleasePlayerAfterDelay());
        }

        private void FailPalmScan()
        {
            ModLogger.Info("Exit palm scan failed - time expired");

            // Play error sound
            if (scannerAudio != null && errorSound != null)
            {
                scannerAudio.clip = errorSound;
                scannerAudio.loop = false;
                scannerAudio.Play();
            }

            // Show failure notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Scan failed - try again",
                    NotificationType.Warning
                );
            }

            isScanning = false;
            ResetCamera();

            // Reset interaction
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Scan fingerprint to complete release");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ReleasePlayerAfterDelay()
        {
            yield return new WaitForSeconds(2f);

            if (currentPlayer != null)
            {
                ModLogger.Info($"ExitScannerStation: Player {currentPlayer.name} ready for exit trigger");

                // Show notification to walk through exit
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Scan complete - walk through the exit to be released!",
                        NotificationType.Instruction
                    );
                }

                // Set up exit trigger monitoring
                MelonCoroutines.Start(MonitorExitTrigger());
            }
        }

        /// <summary>
        /// Monitors for player entering the exit trigger area
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator MonitorExitTrigger()
        {
            Transform exitTrigger = FindExitTrigger();
            if (exitTrigger == null)
            {
                ModLogger.Warn("No exit trigger found - using fallback teleport");
                // Fallback to direct teleport after delay
                yield return new WaitForSeconds(3f);
                TeleportPlayerToRelease();
                yield break;
            }

            Collider exitCollider = exitTrigger.GetComponent<Collider>();
            if (exitCollider == null || !exitCollider.isTrigger)
            {
                ModLogger.Warn("Exit trigger has no trigger collider - using fallback");
                yield return new WaitForSeconds(3f);
                TeleportPlayerToRelease();
                yield break;
            }

            ModLogger.Info($"Monitoring exit trigger: {exitTrigger.name}");

            // Monitor for up to 30 seconds
            float timeout = 30f;
            float elapsed = 0f;

            while (elapsed < timeout && currentPlayer != null && isCompleted)
            {
                // Check if player is within exit trigger bounds
                if (exitCollider.bounds.Contains(currentPlayer.transform.position))
                {
                    ModLogger.Info($"Player {currentPlayer.name} entered exit trigger - teleporting to release");
                    TeleportPlayerToRelease();
                    yield break;
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            // Timeout - teleport anyway
            if (currentPlayer != null)
            {
                ModLogger.Info("Exit trigger timeout - teleporting player to release");
                TeleportPlayerToRelease();
            }
        }

        /// <summary>
        /// Find the exit trigger transform
        /// </summary>
        private Transform FindExitTrigger()
        {
            // Try to get from ExitScannerArea
            var jailController = Core.JailController;
            if (jailController?.exitScanner?.exitTrigger != null)
            {
                return jailController.exitScanner.exitTrigger;
            }

            // Fallback: Search manually
            Transform trigger = null;

            // Try as sibling
            if (transform.parent != null)
            {
                trigger = transform.parent.Find("ExitTrigger");
            }

            // Try in jail root
            if (trigger == null && Core.JailController != null)
            {
                trigger = Core.JailController.transform.Find("ExitTrigger");
            }

            // Try searching recursively in jail
            if (trigger == null && Core.JailController != null)
            {
                trigger = FindChildRecursive(Core.JailController.transform, "ExitTrigger");
            }

            return trigger;
        }

        /// <summary>
        /// Recursive search for child transform by name
        /// </summary>
        private Transform FindChildRecursive(Transform parent, string childName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name.Equals(childName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                Transform found = FindChildRecursive(child, childName);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Final teleportation to release location
        /// </summary>
        private void TeleportPlayerToRelease()
        {
            if (currentPlayer == null) return;

            ModLogger.Info($"ExitScannerStation: Exit scan completed for {currentPlayer.name}");

            // Close the exit door for security
            CloseExitDoor();

            // Notify ReleaseManager of exit scan completion (ReleaseManager will handle teleportation)
            if (ReleaseManager.Instance != null)
            {
                ReleaseManager.Instance.OnExitScanCompleted(currentPlayer);
                ModLogger.Info("ExitScannerStation: Notified ReleaseManager of scan completion");
            }
            else
            {
                // Fallback: Do the teleportation ourselves if no ReleaseManager
                ModLogger.Warn("ExitScannerStation: No ReleaseManager found, doing direct teleportation");
                currentPlayer.transform.position = releasePosition;
                currentPlayer.transform.rotation = Quaternion.Euler(releaseRotation);

                // Final notification
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        "Release complete - you are free to go!",
                        NotificationType.Progress
                    );
                }
            }

            ModLogger.Info("ExitScannerStation: Exit scan processing completed");
        }


        private void ResetCamera()
        {
            ModLogger.Info("Ending camera view for exit scanner");
            inScannerView = false;
            isScanning = false;

            // Re-enable punch container
            if (punchContainer != null)
                punchContainer.SetActive(true);

            // Restore player state (copied from working ScannerStation)
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            PlayerSingleton<PlayerMovement>.Instance.canMove = true;
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
            PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(true);

            // Camera reset (copied from working ScannerStation)
            if (playerCamera != null)
            {
                playerCamera.StopFOVOverride(0.15f);
                playerCamera.StopTransformOverride(0.15f);
                playerCamera.LockMouse();
                Singleton<HUD>.Instance.SetCrosshairVisible(true);
            }

            // Don't disable palmModel - keep PalmScanner visible

            ModLogger.Info("Exit scanner camera reset complete");
        }

        /// <summary>
        /// Opens the exit door using the JailDoorController (same as other doors)
        /// </summary>
        private void OpenExitDoor()
        {
            ModLogger.Info("Opening exit door via JailDoorController...");

            try
            {
                var jailController = Core.JailController;
                if (jailController?.doorController != null)
                {
                    bool success = jailController.doorController.OpenExitDoor();
                    if (success)
                    {
                        ModLogger.Info("✓ Exit door opened via JailDoorController");

                        // Enable SecuritySlots for visual difference
                        if (jailController.exitScanner?.exitDoor != null)
                        {
                            EnableSecuritySlots(jailController.exitScanner.exitDoor);
                        }
                    }
                    else
                    {
                        ModLogger.Error("Failed to open exit door via JailDoorController");
                    }
                }
                else
                {
                    ModLogger.Error("No JailController or doorController found");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error opening exit door: {ex.Message}");
            }
        }


        /// <summary>
        /// Enable SecuritySlots for visual difference
        /// </summary>
        private void EnableSecuritySlots(JailDoor exitDoor)
        {
            try
            {
                if (exitDoor.doorInstance != null)
                {
                    var hingePoint = exitDoor.doorInstance.transform.Find("HingePoint");
                    if (hingePoint != null)
                    {
                        var securitySlots = hingePoint.Find("SecuritySlots");
                        if (securitySlots != null)
                        {
                            securitySlots.gameObject.SetActive(true);
                            ModLogger.Info("Enabled SecuritySlots on exit door for visual difference");
                        }
                        else
                        {
                            ModLogger.Debug("No SecuritySlots found on exit door");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error enabling SecuritySlots: {ex.Message}");
            }
        }

        /// <summary>
        /// Enable exit door slats at startup for visual consistency
        /// </summary>
        private void EnableExitDoorSlots()
        {
            try
            {
                var jailController = Core.JailController;
                if (jailController?.exitScanner?.exitDoor != null)
                {
                    EnableSecuritySlots(jailController.exitScanner.exitDoor);
                    ModLogger.Info("Enabled exit door security slats at startup");
                }
                else
                {
                    ModLogger.Debug("Exit door not yet available - will enable slats when door opens");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Error enabling exit door slats at startup: {ex.Message}");
            }
        }

        /// <summary>
        /// Enable the scanner for use during release process (called by ReleaseOfficer)
        /// </summary>
        public void EnableForRelease()
        {
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Scan fingerprint to complete release");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                ModLogger.Info("Exit scanner enabled for release process");
            }
        }

        /// <summary>
        /// Disable the scanner after release or when not in release process
        /// </summary>
        public void DisableScanner()
        {
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Exit scanner (not available)");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                ModLogger.Info("Exit scanner disabled");
            }
        }









        /// <summary>
        /// Closes the exit door using the JailDoorController (same as other doors)
        /// </summary>
        private void CloseExitDoor()
        {
            try
            {
                var jailController = Core.JailController;
                if (jailController?.doorController != null)
                {
                    bool success = jailController.doorController.CloseExitDoor();
                    if (success)
                    {
                        ModLogger.Info("✓ Exit door closed via JailDoorController");
                    }
                    else
                    {
                        ModLogger.Error("Failed to close exit door via JailDoorController");
                    }
                }
                else
                {
                    ModLogger.Error("No JailController or doorController found");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error closing exit door: {ex.Message}");
            }
        }


        void Update()
        {
            // Debug key for testing door functionality
            if (Input.GetKeyDown(KeyCode.P))
            {
                ModLogger.Info("P key pressed - testing door functionality");
                TestDoorToggle();
            }

            // Update interaction state ONLY if completed (don't re-enable automatically)
            if (interactableObject != null && !isScanning && isCompleted)
            {
                interactableObject.SetMessage("Release completed");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
            }

            // Handle escape key to exit scanner view
            if (inScannerView && Input.GetKeyDown(KeyCode.Escape))
            {
                if (isScanning)
                {
                    FailPalmScan();
                }
                else
                {
                    ResetCamera();
                }
            }
        }

        /// <summary>
        /// Debug method to test door opening/closing with P key using JailDoorController
        /// </summary>
        private void TestDoorToggle()
        {
            ModLogger.Info("Testing exit door toggle using JailDoorController");

            try
            {
                var jailController = Core.JailController;
                if (jailController?.doorController != null && jailController?.exitScanner?.exitDoor != null)
                {
                    var exitDoor = jailController.exitScanner.exitDoor;

                    if (exitDoor.IsInstantiated())
                    {
                        if (exitDoor.IsOpen())
                        {
                            ModLogger.Info("Exit door is open - closing it via JailDoorController");
                            jailController.doorController.CloseExitDoor();
                        }
                        else
                        {
                            ModLogger.Info("Exit door is closed - opening it via JailDoorController");
                            jailController.doorController.OpenExitDoor();
                        }
                    }
                    else
                    {
                        ModLogger.Error("Exit door not instantiated - check jail initialization");
                    }
                }
                else
                {
                    ModLogger.Error("No JailController, doorController, or exitDoor found for testing");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error testing door toggle: {ex.Message}");
            }
        }

        private System.Collections.Generic.List<string> GetChildNames(Transform parent)
        {
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < parent.childCount; i++)
            {
                names.Add(parent.GetChild(i).name);
            }
            return names;
        }

        public bool IsComplete()
        {
            return isCompleted;
        }
    }
}