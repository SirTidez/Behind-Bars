using UnityEngine;


/// <summary>
/// Jail security-camera component that owns a monitor render target and manually refreshes it at a reduced cadence.
/// The camera and render texture are configured during Unity lifecycle callbacks; this component does not decide which
/// monitor displays the texture. Camera type is inferred once from the GameObject name, and the optional <c>head</c>
/// transform is the only target that can be pan/tilt-controlled by <see cref="SetPanTilt(float, float)"/>.
/// </summary>
#if MONO
    public sealed class SecurityCamera : MonoBehaviour
#else
public sealed class SecurityCamera(IntPtr ptr) : MonoBehaviour(ptr)
#endif
{
    // Resolved from children in Awake, or created as a child when the prefab has no Camera component.
    public Camera cameraComponent;

    // Render target treated as owned by this component: OnDestroy releases and destroys the assigned texture, including
    // a texture supplied by a prefab rather than created here.
    public RenderTexture renderTexture;

    // Optional pan/tilt pivot. SetPanTilt has no camera-transform fallback when this reference is absent.
    public Transform head;

    // Square texture dimensions used only when a render texture is created; SetupRenderTexture does not resize an
    // already-assigned texture when this value changes.
    public int renderTextureSize = 128;

    // Target refresh rate used to calculate a single render interval in Awake. It must be positive; no validation or
    // recalculation is performed if this value changes after Awake.
    public float targetFramerate = 5f;

    // Added once to the current world-space X angle in Start, and to the clamped local tilt in SetPanTilt.
    public float downwardAngle = 15f;

    // Inclusive pan and tilt limits, in degrees, applied by SetPanTilt before the head is rotated.
    public Vector2 panRange = new Vector2(-30f, 30f);
    public Vector2 tiltRange = new Vector2(-10f, 20f);

    // Filled from gameObject.name in Start. The match is case-sensitive and is not refreshed after Start.
    public string cameraName;

    // Name-derived category used by monitor-side consumers; it does not itself select a monitor or change rendering.
    public CameraType cameraType;

    // Time bookkeeping for the manual Camera.Render cadence. Values use Unity's scaled Time.time clock.
    private float lastRenderTime;
    private float renderInterval;

    /// <summary>
    /// Name-derived camera categories used by the jail monitor setup.
    /// </summary>
    public enum CameraType
    {
        // Front/Back cameras, which the current setup expects on the main monitors.
        MainView,

        // Cameras whose GameObject name contains "Phones"; the current setup treats these as rotating side-monitor views.
        PhoneArea,

        // Cameras whose GameObject name contains "Holding"; the current setup treats these as rotating side-monitor views.
        HoldingCell,

        // Cameras whose GameObject name contains "Hall"; the current setup treats these as rotating side-monitor views.
        Hall,

        // Any name that does not match the case-sensitive patterns above.
        Other
    }

    /// <summary>
    /// Configures the camera reference and render target, then computes the manual refresh interval once.
    /// A non-positive targetFramerate is not rejected and can produce a non-useful interval.
    /// </summary>
    void Awake()
    {
        SetupCamera();
        SetupRenderTexture();
        renderInterval = 1f / targetFramerate;
    }

    /// <summary>
    /// Applies the configured downward angle and derives the camera category from the GameObject name once.
    /// </summary>
    void Start()
    {
        ApplyDownwardAngle();
        DetermineCameraType();
    }

    /// <summary>
    /// Reuses an assigned child camera when present, otherwise creates one under <see cref="head"/> or this object,
    /// and then overwrites its culling mask, clip planes, and field of view with the jail-camera defaults.
    /// </summary>
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

        // Performance: only render the Default layer when it exists; otherwise fall back to all layers.
        int defaultLayer = LayerMask.GetMask("Default");
        cameraComponent.cullingMask = defaultLayer != 0 ? defaultLayer : ~0;
        cameraComponent.nearClipPlane = 0.1f;
        cameraComponent.farClipPlane = 100f;
        cameraComponent.fieldOfView = 60f;
    }

    /// <summary>
    /// Creates the component's square point-filtered render texture when the field is null and binds it to the camera.
    /// An existing texture is reused rather than resized or replaced; on Mono the texture is created immediately and
    /// the camera is toggled to force recognition, while the IL2CPP path only performs the binding.
    /// </summary>
    public void SetupRenderTexture()
    {
        if (renderTexture == null)
        {
            renderTexture = new RenderTexture(renderTextureSize, renderTextureSize, 16);
            renderTexture.name = $"SecurityCam_{gameObject.name}";
            // Point filtering keeps the low-resolution monitor feed inexpensive and deliberately unfiltered.
            renderTexture.filterMode = FilterMode.Point;
            
#if MONO
            // Mono-specific: ensure the newly allocated render texture is created immediately.
            renderTexture.Create();
#endif
        }

        if (cameraComponent != null)
        {
            cameraComponent.targetTexture = renderTexture;
#if MONO
            // Mono-specific: force the camera to recognize the render texture after binding.
            cameraComponent.enabled = false;
            cameraComponent.enabled = true;
#endif
        }
    }

    /// <summary>
    /// Manually refreshes the feed when the camera is enabled and the computed Unity-time interval has elapsed.
    /// This is a throttling path, not a guarantee of an exact wall-clock frame rate.
    /// </summary>
    void Update()
    {
        // Only render while enabled and at the configured cadence for performance.
        if (cameraComponent != null && cameraComponent.enabled && Time.time - lastRenderTime >= renderInterval)
        {
            RenderCamera();
            lastRenderTime = Time.time;
        }
    }

    /// <summary>
    /// Invokes the camera's manual render operation when its component is present and enabled.
    /// </summary>
    void RenderCamera()
    {
        if (cameraComponent != null && cameraComponent.enabled)
        {
            cameraComponent.Render();
        }
    }

    /// <summary>
    /// Adds <see cref="downwardAngle"/> to the current world-space X angle of the head, or of the camera when no head exists.
    /// The operation is additive if this lifecycle method is invoked more than once.
    /// </summary>
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

    /// <summary>
    /// Classifies the camera using case-sensitive substrings in its GameObject name, with <see cref="CameraType.Other"/>
    /// as the fallback. Classification is not updated if the object is renamed later.
    /// </summary>
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

    /// <summary>
    /// Clamps pan and tilt to their configured degree ranges and writes them to the optional head pivot.
    /// Calls are ignored when <see cref="head"/> is null; the camera transform is not used as a fallback here.
    /// </summary>
    public void SetPanTilt(float pan, float tilt)
    {
        if (head != null)
        {
            pan = Mathf.Clamp(pan, panRange.x, panRange.y);
            tilt = Mathf.Clamp(tilt, tiltRange.x, tiltRange.y);

            head.localEulerAngles = new Vector3(tilt + downwardAngle, pan, 0);
        }
    }

    /// <summary>
    /// Enables or disables only the underlying Unity Camera component; it does not release the texture or change manager state.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (cameraComponent != null)
        {
            cameraComponent.enabled = enabled;
        }
    }

    /// <summary>
    /// Releases and immediately destroys the currently assigned render texture when this component is destroyed.
    /// The camera's targetTexture reference is not cleared first.
    /// </summary>
    void OnDestroy()
    {
        if (renderTexture != null)
        {
            renderTexture.Release();
            DestroyImmediate(renderTexture);
        }
    }

    /// <summary>
    /// Rebinds the current texture when the serialized width differs, but the current SetupRenderTexture implementation
    /// does not replace a non-null texture, so editing renderTextureSize alone does not resize an existing target.
    /// </summary>
    void OnValidate()
    {
        if (renderTexture != null && renderTexture.width != renderTextureSize)
        {
            SetupRenderTexture();
        }
    }
}
