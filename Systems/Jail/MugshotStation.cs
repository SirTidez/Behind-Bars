using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Handles player mugshot capture during booking process
    /// </summary>
    public class MugshotStation : MonoBehaviour
    {
#if !MONO
        public MugshotStation(System.IntPtr ptr) : base(ptr) { }
#endif
        
        public Camera mugshotCamera;
        public RawImage displayMonitor;
        public Transform attachmentPoint;
        public Transform backdrop;
        public Transform flashLight;
        private Light flashLightComponent;
        
        // InteractableObject component for IL2CPP compatibility
        private InteractableObject interactableObject;
        
        // Camera switching support
        private bool inMugshotView = false;
        
        public float positioningDuration = 0.5f; // Quick positioning
        public float holdDuration = 0.5f; // Quick hold
        public Vector3 cameraOffset = new Vector3(0, 1f, -3f); // Position camera in front of and slightly above player
        
        private bool isCapturing = false;
        private BookingProcess bookingProcess;
        private Player currentPlayer;
        
        void Start()
        {
            // Find booking process
            bookingProcess = FindObjectOfType<BookingProcess>();
            
            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Info("MugshotStation interaction setup completed");
            
            
            // Find mugshot camera using static structure: MugshotStation/MugshotCamera
            if (mugshotCamera == null)
            {
                ModLogger.Info($"Looking for MugshotCamera in {transform.name}");
                Transform cameraTransform = transform.Find("MugshotCamera");
                if (cameraTransform != null)
                {
                    ModLogger.Info($"Found MugshotCamera GameObject: {cameraTransform.name}");
                    mugshotCamera = cameraTransform.GetComponent<Camera>();
                    if (mugshotCamera == null)
                    {
                        // Add camera component to existing GameObject
                        mugshotCamera = cameraTransform.gameObject.AddComponent<Camera>();
                        ModLogger.Info("Added Camera component to existing MugshotCamera GameObject");
                    }
                    else
                    {
                        ModLogger.Info("Found existing Camera component on MugshotCamera");
                    }
                    
                    // Configure the camera for mugshots
                    mugshotCamera.enabled = false; // Only enable during photo process
                    mugshotCamera.nearClipPlane = 0.1f;
                    mugshotCamera.farClipPlane = 10f;
                    
                    // Add Player layer to culling mask so camera can see the avatar
                    int playerLayer = LayerMask.NameToLayer("Player");
                    if (playerLayer != -1)
                    {
                        mugshotCamera.cullingMask |= (1 << playerLayer);
                        ModLogger.Info($"Added Player layer ({playerLayer}) to MugshotCamera culling mask");
                    }
                    else
                    {
                        ModLogger.Warn("Player layer not found - MugshotCamera may not see avatar");
                    }
                    
                    ModLogger.Info("Configured mugshot camera settings");
                }
                else
                {
                    ModLogger.Error("MugshotCamera not found in MugshotStation!");
                    
                    // Debug: List all children to see what's available
                    ModLogger.Info($"Available children of {transform.name}:");
                    for (int i = 0; i < transform.childCount; i++)
                    {
                        Transform child = transform.GetChild(i);
                        ModLogger.Info($"  - {child.name}");
                    }
                }
            }
                
            // Find display monitor using static structure: MugshotStation/MugshotMonitor/Holder/Canvas/imgDisplay
            if (displayMonitor == null)
            {
                var monitor = transform.Find("MugshotMonitor");
                if (monitor != null)
                {
                    var holder = monitor.Find("Holder");
                    if (holder != null)
                    {
                        var canvas = holder.Find("Canvas");
                        if (canvas != null)
                        {
                            displayMonitor = canvas.Find("imgDisplay")?.GetComponent<RawImage>();
                            if (displayMonitor != null)
                            {
                                ModLogger.Info($"Found imgDisplay RawImage: {displayMonitor.name}");
                            }
                            else
                            {
                                ModLogger.Error("imgDisplay found but no RawImage component!");
                            }
                        }
                        else
                        {
                            ModLogger.Error("Canvas not found in MugshotMonitor/Holder!");
                        }
                    }
                    else
                    {
                        ModLogger.Error("Holder not found in MugshotMonitor!");
                    }
                }
                else
                {
                    ModLogger.Error("MugshotMonitor not found in MugshotStation!");
                }
            }
            
            // Find StandingPoint using static structure: MugshotStation/StandingPoint
            if (attachmentPoint == null)
            {
                attachmentPoint = transform.Find("StandingPoint");
                if (attachmentPoint != null)
                {
                    ModLogger.Info($"Found StandingPoint: {attachmentPoint.name} at position {attachmentPoint.position}");
                }
                else
                {
                    ModLogger.Error("StandingPoint not found in MugshotStation!");
                }
            }
            
            // Find Flash light using static structure: MugshotStation/MugshotCamera/Flash
            if (flashLight == null && mugshotCamera != null)
            {
                flashLight = mugshotCamera.transform.Find("Flash");
                if (flashLight != null)
                {
                    ModLogger.Info($"Found Flash: {flashLight.name}");
                    
                    // Find the Light component on the Flash GameObject
                    flashLightComponent = flashLight.GetComponent<Light>();
                    if (flashLightComponent != null)
                    {
                        // Ensure flash light is initially disabled
                        flashLightComponent.enabled = false;
                        ModLogger.Info($"Found Flash Light component - initially disabled");
                    }
                    else
                    {
                        ModLogger.Error("Light component not found on Flash GameObject!");
                    }
                }
                else
                {
                    ModLogger.Error("Flash not found in MugshotCamera!");
                }
            }
            
            // The interaction should be handled by the existing Interaction child GameObject
            // (just like ScannerStation), not by adding colliders to the main object
            
            ModLogger.Info($"MugshotStation initialized - Camera: {mugshotCamera != null}, Monitor: {displayMonitor != null}, AttachmentPoint: {attachmentPoint != null}, Flash: {flashLight != null}");
            
            // Additional debug info
            if (mugshotCamera != null)
            {
                ModLogger.Info($"MugshotCamera details - Position: {mugshotCamera.transform.position}, Enabled: {mugshotCamera.enabled}");
            }
        }
        
        private void SetupInteractableComponent()
        {
            // Get or create InteractableObject component
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Info("Added InteractableObject component to MugshotStation");
            }
            else
            {
                ModLogger.Info("Found existing InteractableObject component on MugshotStation");
            }
            
            // Configure the interaction
            interactableObject.SetMessage("Take mugshot");
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
        
        private void OnInteractStart()
        {
            if (isCapturing)
            {
                interactableObject.SetMessage("Mugshot in progress...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                return;
            }
            
            // Get player first
            currentPlayer = Player.Local;
            if (currentPlayer == null)
            {
                ModLogger.Error("No local player found for mugshot!");
                return;
            }
            
            // Position player at StandingPoint X/Z only, keep current Y position
            if (attachmentPoint != null)
            {
                Vector3 currentPos = currentPlayer.transform.position;
                Vector3 targetPos = new Vector3(attachmentPoint.position.x, currentPos.y, attachmentPoint.position.z);
                
                currentPlayer.transform.position = targetPos;
                currentPlayer.transform.rotation = attachmentPoint.rotation;
                
                ModLogger.Info($"Positioned player at StandingPoint X/Z: ({attachmentPoint.position.x}, {currentPos.y}, {attachmentPoint.position.z})");
                ModLogger.Info($"Player rotation set to StandingPoint rotation: {attachmentPoint.rotation}");
            }
            else
            {
                ModLogger.Warn("StandingPoint not found - using current player position");
            }
            
            // Now start camera view with player in correct position
            StartCameraView();
            
            // Start mugshot process
            MelonCoroutines.Start(CaptureMugshot(currentPlayer));
        }
        
        private void StartCameraView()
        {
            if (mugshotCamera == null) return;

            inMugshotView = true;

            // Use player camera with ViewAvatar functionality to see the player
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (playerCamera != null && currentPlayer != null)
            {
                // Disable player movement and inventory during photo
#if MONO
                PlayerSingleton<PlayerMovement>.Instance.CanMove = false;
#else
                PlayerSingleton<PlayerMovement>.Instance.canMove = false;
#endif
                PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(false);
                PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(false);

                // Enable mugshot mode to override player visibility
                Behind_Bars.Harmony.HarmonyPatches.SetMugshotInProgress(true);
                currentPlayer.SetVisibleToLocalPlayer(true);

                // Override camera position to mugshot camera position and rotation
                playerCamera.OverrideTransform(
                    mugshotCamera.transform.position,
                    mugshotCamera.transform.rotation,
                    0.3f
                );

                // Free mouse and hide crosshair for photo session
                playerCamera.FreeMouse();
                Singleton<HUD>.Instance.SetCrosshairVisible(false);

                ModLogger.Info("Switched to mugshot camera view with player visible");
            }
        }
        
        private void ExitCameraView()
        {
            if (!inMugshotView) return;
            
            inMugshotView = false;
            
            // Restore player camera and controls
            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (playerCamera != null && currentPlayer != null)
            {
                // Stop camera overrides using proper methods
                playerCamera.StopTransformOverride(0.3f);
                playerCamera.LockMouse();
                Singleton<HUD>.Instance.SetCrosshairVisible(true);

                // Disable mugshot mode and switch player avatar back to "Invisible" layer
                Behind_Bars.Harmony.HarmonyPatches.SetMugshotInProgress(false);
                currentPlayer.SetVisibleToLocalPlayer(false);

                // Restore player controls
#if MONO
                PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
                PlayerSingleton<PlayerMovement>.Instance.canMove = true;
#endif
                PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(true);

                // No extra avatar cleanup needed

                ModLogger.Info("Restored main camera view and player visibility");
            }
            
            // Reset interaction
            isCapturing = false;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Take mugshot");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator CaptureMugshot(Player player)
        {
            isCapturing = true;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Taking mugshot...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }
            
            ModLogger.Info($"Starting mugshot capture for {player.name}");
            
            // Note: Player is already positioned at StandingPoint in StartInteract()
            
            // Use main player camera for photo capture (player is now visible)
            ModLogger.Info("Using main player camera for photo capture (player is now visible)");
            
            // Show notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification("Hold still for mugshot...", NotificationType.Instruction);
            }
            
            // Wait for positioning
            yield return new WaitForSeconds(positioningDuration);

            // Flash effect before capture
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification("3... 2... 1...", NotificationType.Instruction);
            }
            yield return new WaitForSeconds(1.5f);
            
            // Camera flash effect
            if (flashLightComponent != null)
            {
                // Activate flash light component
                flashLightComponent.enabled = true;
                ModLogger.Info("Flash light activated");
            }
            
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification("*FLASH*", NotificationType.Progress);
            }
            
            // Wait a tiny moment for the flash to fully activate, then capture
            yield return new WaitForSeconds(0.1f);
            
            // Capture the photo EXACTLY when flash is at peak
            Texture2D mugshot = null;
            try
            {
                ModLogger.Info("Capturing photo at peak flash");
                mugshot = CapturePhoto();
                
                // Display on monitor
                if (mugshot != null && displayMonitor != null)
                {
                    displayMonitor.texture = mugshot;
                    ModLogger.Info($"Mugshot displayed on monitor: {displayMonitor.name}");
                    
                    // Store in booking process
                    if (bookingProcess != null)
                    {
                        bookingProcess.SetMugshotComplete(mugshot);
                        ModLogger.Info("Mugshot saved to booking process");
                    }
                    
                    // Show success notification
                    if (BehindBarsUIManager.Instance != null)
                    {
                        BehindBarsUIManager.Instance.ShowNotification("Mugshot captured!", NotificationType.Progress);
                    }
                }
                else
                {
                    ModLogger.Error($"Failed to display mugshot - Photo: {mugshot != null}, Monitor: {displayMonitor != null}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error during photo capture: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
            
            // Hold pose briefly
            yield return new WaitForSeconds(holdDuration);
            
            // Disable flash after photo
            if (flashLightComponent != null)
            {
                flashLightComponent.enabled = false;
                ModLogger.Info("Flash light disabled");
            }
            
            // Exit camera view and restore normal view
            ExitCameraView();
            
            ModLogger.Info("Mugshot capture completed successfully");
        }
        
        private Texture2D CapturePhoto()
        {
            try
            {
                ModLogger.Info("Capturing photo using direct screen capture of 3rd person view");

                // Hide UI elements during capture to avoid text overlays
                bool hudWasVisible = false;
                try
                {
                    var hud = Singleton<HUD>.Instance;
                    if (hud != null)
                    {
                        hudWasVisible = hud.gameObject.activeInHierarchy;
                        hud.gameObject.SetActive(false);
                        ModLogger.Info("Hidden HUD for clean screen capture");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Could not hide HUD: {ex.Message}");
                }

                // SCREEN CAPTURE APPROACH: Capture exactly what's displayed on screen (like V key 3rd person)
                // Small delay to ensure UI changes take effect
                System.Threading.Thread.Sleep(100);

                // Capture the current screen
                Texture2D screenCapture = ScreenCapture.CaptureScreenshotAsTexture();

                // Restore HUD immediately after capture
                try
                {
                    var hud = Singleton<HUD>.Instance;
                    if (hud != null && hudWasVisible)
                    {
                        hud.gameObject.SetActive(true);
                        ModLogger.Info("Restored HUD after screen capture");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Debug($"Could not restore HUD: {ex.Message}");
                }

                if (screenCapture != null)
                {
                    // Crop to the center portion (where the player would be in 3rd person view)
                    int cropSize = 512;
                    int centerX = screenCapture.width / 2 - cropSize / 2;
                    int centerY = screenCapture.height / 2 - cropSize / 2;

                    // Make sure crop coordinates are valid
                    centerX = Mathf.Clamp(centerX, 0, screenCapture.width - cropSize);
                    centerY = Mathf.Clamp(centerY, 0, screenCapture.height - cropSize);

                    // Create cropped texture
                    Texture2D photo = new Texture2D(cropSize, cropSize, TextureFormat.RGB24, false);
                    Color[] pixels = screenCapture.GetPixels(centerX, centerY, cropSize, cropSize);
                    photo.SetPixels(pixels);
                    photo.Apply();

                    // Clean up screen capture
                    UnityEngine.Object.Destroy(screenCapture);

                    ModLogger.Info($"Screen capture mugshot created: {photo.width}x{photo.height}");
                    return photo;
                }
                else
                {
                    ModLogger.Error("Screen capture returned null");
                    return null;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error during screen capture mugshot: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }
        
        public bool IsComplete()
        {
            return bookingProcess != null && bookingProcess.mugshotComplete;
        }
        
        void Update()
        {
            // Update interaction state - allow re-doing mugshots
            if (!isCapturing && interactableObject != null)
            {
                interactableObject.SetMessage("Take mugshot");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
        }
    }
    
}