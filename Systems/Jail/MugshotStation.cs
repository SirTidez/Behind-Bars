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
    public class MugshotStation : InteractableObject
    {
        [Header("Mugshot Components")]
        public Camera mugshotCamera;
        public RawImage displayMonitor;
        public Transform attachmentPoint;
        public Transform backdrop;
        public Transform flashLight;
        private Light flashLightComponent;
        
        // Camera switching support
        private bool inMugshotView = false;
        
        [Header("Settings")]
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
            
            // ALWAYS set up interaction directly on this component (like ScannerStation)
            SetMessage("Take mugshot");
            SetInteractionType(EInteractionType.Key_Press);
            SetInteractableState(EInteractableState.Default); // Explicitly set to Default
            
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
        
        public override void StartInteract()
        {
            if (isCapturing)
            {
                SetMessage("Mugshot in progress...");
                SetInteractableState(EInteractableState.Invalid);
                return;
            }
            
            // Allow re-doing mugshots - remove completion check
            
            base.StartInteract();
            
            // Get player first
            currentPlayer = Player.Local;
            if (currentPlayer == null)
            {
                ModLogger.Error("No local player found for mugshot!");
                return;
            }
            
            // Remove debug logging for cleaner operation
            
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
            
            // Keep avatar modifications minimal - only what's necessary for visibility
            
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
                PlayerSingleton<PlayerMovement>.Instance.canMove = false;
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
                PlayerSingleton<PlayerMovement>.Instance.canMove = true;
                PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
                PlayerSingleton<PlayerInventory>.Instance.SetEquippingEnabled(true);
                
                // No extra avatar cleanup needed
                
                ModLogger.Info("Restored main camera view and player visibility");
            }
            
            // Reset interaction
            isCapturing = false;
            SetMessage("Take mugshot");
            SetInteractableState(EInteractableState.Default);
        }
        
        private IEnumerator CaptureMugshot(Player player)
        {
            isCapturing = true;
            SetMessage("Taking mugshot...");
            SetInteractableState(EInteractableState.Invalid);
            
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
            // Use the MugshotCamera - don't touch V camera at all
            if (mugshotCamera == null)
            {
                ModLogger.Error("MugshotCamera reference is null!");
                return null;
            }
            
            try
            {
                ModLogger.Info("Using MugshotCamera for photo capture");
                
                // Set up render texture
                RenderTexture renderTexture = new RenderTexture(512, 512, 24);
                
                // Store original settings
                RenderTexture originalTarget = mugshotCamera.targetTexture;
                RenderTexture originalActive = RenderTexture.active;
                
                // Configure MugshotCamera
                mugshotCamera.targetTexture = renderTexture;
                
                // Render using the MugshotCamera
                mugshotCamera.Render();
                ModLogger.Info("Photo captured using MugshotCamera");
                
                // Read pixels into texture2D
                RenderTexture.active = renderTexture;
                Texture2D photo = new Texture2D(512, 512, TextureFormat.RGB24, false);
                photo.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
                photo.Apply();
                
                // Restore MugshotCamera settings
                mugshotCamera.targetTexture = originalTarget;
                RenderTexture.active = originalActive;
                
                if (renderTexture != null)
                {
                    renderTexture.Release();
                }
                
                ModLogger.Info($"Third-person camera restored - Photo captured: {photo.width}x{photo.height}");
                return photo;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error moving third-person camera for photo: {ex.Message}");
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
            if (!isCapturing)
            {
                SetMessage("Take mugshot");
                SetInteractableState(EInteractableState.Default);
            }
        }
    }
    
}