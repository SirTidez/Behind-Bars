using UnityEngine;
using UnityEngine.UI;

#if MONO
    public sealed class MonitorController : MonoBehaviour
#else
public sealed class MonitorController(IntPtr ptr) : MonoBehaviour(ptr)
#endif
{
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

    public RawImage screenImage;
    public MonitorType monitorType;
    
    public SecurityCamera assignedCamera;
    public bool isStaticAssignment = true;
    
    public bool showDebugInfo = false;
    
    private RenderTexture currentTexture;
    
    public enum MonitorType
    {
        MainFrontLeft,      // Static - Front Left camera
        MainFrontRight,     // Static - Front Right camera  
        MainBackLeft,       // Static - Back Left camera
        MainBackRight,      // Static - Back Right camera
        SideLeft,           // Rotating - Phone/Holding/Hall cameras
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
        Debug.Log($"Monitor {gameObject.name} now showing camera: {camera.cameraName} (texture: {currentTexture.name}, size: {currentTexture.width}x{currentTexture.height})");
    }
    
    public void ClearDisplay()
    {
        assignedCamera = null;
        currentTexture = null;
        
        if (screenImage != null)
        {
            screenImage.texture = null;
        }
    }
    
    public bool HasCamera()
    {
        return assignedCamera != null;
    }
    
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