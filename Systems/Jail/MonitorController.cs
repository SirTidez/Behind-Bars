using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays a security camera's render texture on an authored monitor surface.
/// Assignment is owned by JailMonitorController; this component only resolves the RawImage
/// and keeps the current camera/texture references in sync.
/// </summary>
#if MONO
    public sealed class MonitorController : MonoBehaviour
#else
public sealed class MonitorController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
{
    /// <summary>
    /// Re-apply the assigned camera's current render texture directly to the screen image.
    /// </summary>
    /// <remarks>This is a manual recovery helper and does not create a missing texture or RawImage.</remarks>
    public void ForceSetTexture()
    {
        if (assignedCamera != null && assignedCamera.renderTexture != null)
        {
            screenImage.texture = assignedCamera.renderTexture;
            Debug.Log($"Forced texture assignment: {assignedCamera.renderTexture.name}");
        }
        else
        {
            Debug.LogError($"Can't set texture - Camera: {assignedCamera != null}, RenderTexture: {assignedCamera?.renderTexture != null}");
        }
    }

    // The RawImage is normally authored or discovered under this object. A missing image
    // prevents display but does not invalidate the camera assignment itself.
    public RawImage screenImage;
    public MonitorType monitorType;
    
    // Current assignment state. RenderTexture ownership remains with SecurityCamera.
    public SecurityCamera assignedCamera;
    public bool isStaticAssignment = true;
    
    // Diagnostic logging only.
    public bool showDebugInfo = false;
    
    private RenderTexture currentTexture;
    
    /// <summary>
    /// Authored monitor placement categories used by monitor discovery/configuration.
    /// </summary>
    public enum MonitorType
    {
        /// <summary>Static front-left view.</summary>
        MainFrontLeft,      // Static - Front Left camera
        /// <summary>Static front-right view.</summary>
        MainFrontRight,     // Static - Front Right camera  
        /// <summary>Static rear-left view.</summary>
        MainBackLeft,       // Static - Back Left camera
        /// <summary>Static rear-right view.</summary>
        MainBackRight,      // Static - Back Right camera
        /// <summary>Rotating side-left view.</summary>
        SideLeft,           // Rotating - Phone/Holding/Hall cameras
        /// <summary>Rotating side-right view.</summary>
        SideRight           // Rotating - Phone/Holding/Hall cameras
    }
    
    void Awake()
    {
        SetupMonitor();
    }
    
    void SetupMonitor()
    {
        if (screenImage == null)
        {
            // First try to find on this GameObject
            screenImage = GetComponent<RawImage>();
        }
        
        if (screenImage == null)
        {
            // Search in children recursively
            screenImage = GetComponentInChildren<RawImage>();
        }
        
        if (screenImage == null)
        {
            Debug.LogError($"MonitorController on {gameObject.name} has no RawImage component in hierarchy!");
        }
        else
        {
            Debug.Log($"✓ MonitorController on {gameObject.name} found RawImage: {screenImage.gameObject.name}");
        }
    }
    
    /// <summary>
    /// Assign a security camera and display its existing render texture.
    /// </summary>
    /// <param name="camera">Camera to display, or <c>null</c> to clear the display.</param>
    /// <remarks>The method does not create a render texture. Mono may force one render; IL2CPP relies on the normal pipeline.</remarks>
    public void SetCamera(SecurityCamera camera)
    {
        if (camera == null)
        {
            Debug.LogWarning($"MonitorController {gameObject.name}: Trying to set null camera");
            ClearDisplay();
            return;
        }
        
        assignedCamera = camera;
        currentTexture = camera.renderTexture;
        
        if (screenImage == null)
        {
            Debug.LogError($"MonitorController {gameObject.name}: screenImage is null!");
            return;
        }
        
        if (currentTexture == null)
        {
            Debug.LogError($"MonitorController {gameObject.name}: camera {camera.cameraName} has null renderTexture!");
            return;
        }
        
        screenImage.texture = currentTexture;

#if MONO
        // Mono-specific: Force a render to ensure texture is populated
        if (camera.cameraComponent != null)
        {
            camera.cameraComponent.Render();
        }
#endif

        // Performance: Only log when debug info is enabled
        if (showDebugInfo)
        {
            Debug.Log($"Monitor {gameObject.name} now showing camera: {camera.cameraName} (texture: {currentTexture.name}, size: {currentTexture.width}x{currentTexture.height})");
        }
    }
    
    /// <summary>
    /// Clear the camera reference, cached texture, and RawImage texture.
    /// </summary>
    /// <remarks>The camera or render texture objects themselves are not destroyed.</remarks>
    public void ClearDisplay()
    {
        assignedCamera = null;
        currentTexture = null;
        
        if (screenImage != null)
        {
            screenImage.texture = null;
        }
    }
    
    /// <summary>Return whether a camera is currently assigned.</summary>
    public bool HasCamera()
    {
        return assignedCamera != null;
    }
    
    /// <summary>Return the assigned camera name, or <c>None</c> when unassigned.</summary>
    public string GetCameraName()
    {
        return assignedCamera != null ? assignedCamera.cameraName : "None";
    }
    
    void OnValidate()
    {
        if (screenImage == null)
        {
            screenImage = GetComponent<RawImage>();
        }
    }
}
