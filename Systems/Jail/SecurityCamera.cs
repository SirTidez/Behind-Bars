using UnityEngine;


#if MONO
    public sealed class SecurityCamera : MonoBehaviour
#else
public sealed class SecurityCamera(IntPtr ptr) : MonoBehaviour(ptr)
#endif
{
    public Camera cameraComponent;
    public RenderTexture renderTexture;
    public Transform head;

    public int renderTextureSize = 128;  // Reduced from 256 for better performance
    public float targetFramerate = 5f;   // Reduced from 10fps to 5fps

    public float downwardAngle = 15f;
    public Vector2 panRange = new Vector2(-30f, 30f);
    public Vector2 tiltRange = new Vector2(-10f, 20f);

    public string cameraName;
    public CameraType cameraType;

    private float lastRenderTime;
    private float renderInterval;

    public enum CameraType
    {
        MainView,       // Front/Back cameras (always on main monitors)
        PhoneArea,      // Rotating cameras for side monitors
        HoldingCell,    // Rotating cameras for side monitors
        Hall,           // Rotating cameras for side monitors
        Other           // Other cameras
    }

    void Awake()
    {
        SetupCamera();
        SetupRenderTexture();
        renderInterval = 1f / targetFramerate;
    }

    void Start()
    {
        ApplyDownwardAngle();
        DetermineCameraType();
    }

    void SetupCamera()
    {
        if (cameraComponent == null)
        {
            cameraComponent = GetComponentInChildren<Camera>();
            if (cameraComponent == null)
            {
                GameObject cameraObj = new GameObject("SecurityCamera");
                cameraObj.transform.SetParent(head != null ? head : transform);
                cameraObj.transform.localPosition = Vector3.zero;
                cameraObj.transform.localRotation = Quaternion.identity;
                cameraComponent = cameraObj.AddComponent<Camera>();
            }
        }

        // Configure camera for security monitoring
        cameraComponent.cullingMask = ~0; // Render all layers
        cameraComponent.nearClipPlane = 0.1f;
        cameraComponent.farClipPlane = 100f;
        cameraComponent.fieldOfView = 60f;
    }

    public void SetupRenderTexture()
    {
        if (renderTexture == null)
        {
            renderTexture = new RenderTexture(renderTextureSize, renderTextureSize, 16);
            renderTexture.name = $"SecurityCam_{gameObject.name}";
            renderTexture.filterMode = FilterMode.Bilinear;
            
#if MONO
            // Mono-specific: Ensure render texture is created immediately
            renderTexture.Create();
#endif
        }

        if (cameraComponent != null)
        {
            cameraComponent.targetTexture = renderTexture;
#if MONO
            // Mono-specific: Force camera to recognize the render texture
            cameraComponent.enabled = false;
            cameraComponent.enabled = true;
#endif
        }
    }

    void Update()
    {
        // Only render if camera is enabled and at target framerate for performance
        if (cameraComponent != null && cameraComponent.enabled && Time.time - lastRenderTime >= renderInterval)
        {
            RenderCamera();
            lastRenderTime = Time.time;
        }
    }

    void RenderCamera()
    {
        if (cameraComponent != null && cameraComponent.enabled)
        {
            cameraComponent.Render();
        }
    }

    void ApplyDownwardAngle()
    {
        if (head != null)
        {
            Vector3 currentRotation = head.eulerAngles;
            head.eulerAngles = new Vector3(currentRotation.x + downwardAngle, currentRotation.y, currentRotation.z);
        }
        else if (cameraComponent != null)
        {
            Vector3 currentRotation = cameraComponent.transform.eulerAngles;
            cameraComponent.transform.eulerAngles = new Vector3(currentRotation.x + downwardAngle, currentRotation.y, currentRotation.z);
        }
    }

    void DetermineCameraType()
    {
        cameraName = gameObject.name;

        if (cameraName.Contains("Front") || cameraName.Contains("Back"))
        {
            cameraType = CameraType.MainView;
        }
        else if (cameraName.Contains("Phones"))
        {
            cameraType = CameraType.PhoneArea;
        }
        else if (cameraName.Contains("Holding"))
        {
            cameraType = CameraType.HoldingCell;
        }
        else if (cameraName.Contains("Hall"))
        {
            cameraType = CameraType.Hall;
        }
        else
        {
            cameraType = CameraType.Other;
        }
    }

    public void SetPanTilt(float pan, float tilt)
    {
        if (head != null)
        {
            pan = Mathf.Clamp(pan, panRange.x, panRange.y);
            tilt = Mathf.Clamp(tilt, tiltRange.x, tiltRange.y);

            head.localEulerAngles = new Vector3(tilt + downwardAngle, pan, 0);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (cameraComponent != null)
        {
            cameraComponent.enabled = enabled;
        }
    }

    void OnDestroy()
    {
        if (renderTexture != null)
        {
            renderTexture.Release();
            DestroyImmediate(renderTexture);
        }
    }

    void OnValidate()
    {
        if (renderTexture != null && renderTexture.width != renderTextureSize)
        {
            SetupRenderTexture();
        }
    }
}