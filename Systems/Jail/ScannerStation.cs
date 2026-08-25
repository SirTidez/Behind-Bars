using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using Behind_Bars.Utils;
using BBHelpers = Behind_Bars.Helpers.Helpers;



#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.AvatarFramework.Animation;
using Il2CppScheduleOne;
using Il2CppInterop.Runtime.Attributes;
using Il2CppTMPro;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne.AvatarFramework;
using ScheduleOne.AvatarFramework.Animation;
using ScheduleOne;
using TMPro;
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

        // The scanner must use the player's live right-hand IK.  The old palm
        // route renders the authored MockHand placeholder (the white rectangle
        // seen in-game) and completes without an alignment interaction.
        public bool useNewPalmScanner = false;

        // InteractableObject component for IL2CPP compatibility
        private InteractableObject interactableObject;

        public Transform scanTarget;        // The ScanTarget in Unity hierarchy
        public Transform ikTarget;          // The IkTarget that will be draggable
        public Image scanEffect;            // The scanning effect image
        public AudioSource scannerAudio;

        public Camera interactionCamera;     // Camera for palm scanner view
        public GameObject palmModel;         // The MockHand or palm prefab
        public Transform palmStartPosition;  // Where palm starts
        // The initial scanner interaction was too twitchy for a 2D alignment
        // task. This keeps a full-screen drag within the useful work area
        // while letting players make deliberate final corrections.
        public float dragSensitivity = 0.00065f;
        public float maxDragDistance = 0.3f;

        // The scanner is a right-hand interaction, so its workspace should
        // begin on the right side of the printed guide and give the hand more
        // room to travel right than left.  Keeping the fore/aft travel narrow
        // also prevents the camera from seeing into the open end of the
        // extracted forearm mesh.
        private const float HandWorkspaceLeftLimit = -0.09f;
        private const float HandWorkspaceRightLimit = 0.24f;
        private const float HandWorkspaceNearLimit = -0.06f;
        private const float HandWorkspaceFarLimit = 0.05f;
        private const float HandStartRightOffset = 0.18f;
        private const float HandStartNearOffset = -0.03f;

        // The scanner glass rises slightly away from the player. Neither the
        // authored ScanTarget nor the cinematic interaction camera carries a
        // usable surface rotation, so keep the real, physical platen slope
        // explicit instead of deriving it from their unrelated helper axes.
        private const float ScannerPaneSlopeDegrees = 12f;

        public float scanDuration = 5f;     // Max 5 seconds scanning
        public float validRange = 0.08f;    // World-space fallback range around scanTarget

        public AudioClip scanningSound;
        public AudioClip successSound;
        public AudioClip errorSound;

        private bool isScanning = false;
        private bool isDragging = false;
        private BookingProcess bookingProcess;
        private Player currentPlayer;
        private Camera playerCamera;
        private Coroutine scanCoroutine;
        private Coroutine handScanProcessCoroutine;

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
        private float originalRightHandRotationWeight;
        private Quaternion originalRightHandRotation;
        private bool ikActive = false;
        private bool handScanControlsLocked = false;
        private bool handScanExitListenerRegistered = false;
        private bool handScanCameraOverridden = false;
        private bool handScanPunchInputDisabled = false;
        private ViewmodelAvatar handScanViewmodelAvatar;
        private bool handScanViewmodelWasVisible = false;
        private Vector3 handScanViewmodelOriginalLocalPosition;
        private Quaternion handScanViewmodelOriginalLocalRotation;
        private Transform handScanViewmodelOriginalParent;
        private readonly List<SkinnedMeshRenderer> handScanBodyRenderers = new List<SkinnedMeshRenderer>();
        private readonly List<Mesh> handScanOriginalBodyMeshes = new List<Mesh>();
        private readonly List<Mesh> handScanRightArmMeshes = new List<Mesh>();
        private int handScanOriginalCameraCullingMask;
        private bool handScanCameraCullingMaskCaptured = false;

        // The scanner temporarily poses the real avatar, bakes that one
        // frame, and displays only its right forearm/hand. This deliberately
        // avoids the native first-person viewmodel, whose complete body rig
        // was responsible for the detached left arm and limb artifacts.
        private static AnimationClip fingerprintPoseClip;
        private GameObject scannerArmSnapshotRoot;
        private readonly List<Mesh> scannerArmSnapshotMeshes = new List<Mesh>();
        private Animator scannerPoseAnimator;
        private RuntimeAnimatorController scannerPoseOriginalController;
        private bool scannerPoseApplied;
        private Vector3 scannerArmArrivalStart;
        private Vector3 scannerArmDisplayOffset;
        private Vector3 scannerArmSurfaceOffset;
        private Vector3 scannerArmPalmOffset;
        private bool scannerArmArrivalActive;
        // Snapshot geometry, dragging and validation must all share one
        // immutable scanner frame. Resolving it from live player state made
        // the first entry sensitive to the order in which the station camera
        // and player avatar initialized.
        private Vector3 scannerScanAwayDirection;
        private bool scannerScanFrameLocked;

        // Persistent, scanner-specific status overlay. Notifications fade too
        // quickly to communicate the live alignment state during dragging.
        private GameObject scannerStatusPanel;
        private Image scannerStatusHighlight;
        private Outline scannerStatusOutline;
        private TextMeshProUGUI scannerStatusText;
        private bool scannerStatusVisible;
        private bool scannerStatusAligned;
        private bool scannerStatusHasState;
        // Once the fingerprint is accepted, keep the completion cue stable
        // until the scanner animation has finished.  A live alignment update
        // must not overwrite the positive result while the player is still
        // looking at the scanner.
        private bool scannerStatusLocked;
        private bool fingerprintSuccessPresentationActive;

        // Visual debugging
        private GameObject ikTargetVisualizer;
        private Renderer ikTargetRenderer;

        void Start()
        {
            // Find booking process
            bookingProcess = BBHelpers.FindObjectOfTypeSafe<BookingProcess>();

            // Set up InteractableObject component for IL2CPP compatibility
            SetupInteractableComponent();
            ModLogger.Debug("ScannerStation interaction setup completed");

            // The booking scanner is intentionally an actual-hand minigame.
            // Keep the legacy mock-palm route disabled so it cannot leave a
            // placeholder mesh over the scanner.
            useNewPalmScanner = false;
            // The old IK path had stopped calling this setup routine.  That
            // left interactionCamera null, so the scan never claimed the
            // player camera and only appeared framed if the player manually
            // stood in the correct spot.
            SetupPalmScannerComponents();
            HideLegacyPalmPlaceholder();

            // Find components using exact hierarchy paths
            if (scanTarget == null)
            {
                // Find ScanTarget: Booking/ScannerStation/ScanTarget
                scanTarget = transform.Find("ScanTarget");
                if (scanTarget != null)
                {
                    ModLogger.Debug($"Found ScanTarget: {scanTarget.name}");
                }
                else
                {
                    ModLogger.Error("ScanTarget not found in ScannerStation!");
                }
            }

            if (ikTarget == null)
            {
                ModLogger.Debug($"Searching for IkTarget. Current transform: {transform.name}");

                // Debug: List all children of this ScannerStation
                ModLogger.Debug($"ScannerStation children: {string.Join(", ", GetChildNames(transform))}");

                // Find IkTarget: Booking/ScannerStation/Draggable/IkTarget
                var draggable = transform.Find("Draggable");
                if (draggable != null)
                {
                    ModLogger.Debug($"Found Draggable. Children: {string.Join(", ", GetChildNames(draggable))}");

                    ikTarget = draggable.Find("IkTarget");
                    if (ikTarget != null)
                    {
                        ModLogger.Debug($"Found IkTarget: {ikTarget.name} at position {ikTarget.position}");
                        if (scanTarget != null)
                        {
                            PositionHandTargetAtScanStart();
                        }
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
                            ModLogger.Debug("Found scan effect image");
                        }
                    }
                }
            }

            ModLogger.Debug($"ScannerStation initialized - Mode: {(useNewPalmScanner ? "Palm Scanner" : "IK System")}, ScanTarget: {scanTarget != null}");
        }

        private void SetupInteractableComponent()
        {
            // Get or create InteractableObject component
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Debug("Added InteractableObject component to ScannerStation");
            }
            else
            {
                ModLogger.Debug("Found existing InteractableObject component on ScannerStation");
            }

            // Configure the interaction
            interactableObject.SetMessage("Scan fingerprints");
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

            ModLogger.Debug("InteractableObject component configured with event listeners");
        }

        private void SetupPalmScannerComponents()
        {
            ModLogger.Debug("Setting up palm scanner components");

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
                        ModLogger.Debug("Found InteractionCamera for palm scanner");
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
                ModLogger.Debug("InteractionCamera disabled initially");
            }

            // Hide imgScanEffect initially
            HideImgScanEffect();

            ModLogger.Debug($"Palm scanner setup complete - Camera: {interactionCamera != null}, Palm: {palmModel != null}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void ResolveScannerReferencesForInteraction()
        {
            if (scanTarget == null)
            {
                scanTarget = transform.Find("ScanTarget");
            }

            if (interactionCamera == null)
            {
                interactionCamera = transform.Find("Interaction/InteractionCamera")?.GetComponent<Camera>();
            }

            scannerScanFrameLocked = false;
            scannerScanAwayDirection = ResolveScannerAwayDirection();
            scannerScanFrameLocked = scannerScanAwayDirection.sqrMagnitude > 0.0001f;
            ModLogger.Info($"[Fingerprint Scan] Locked scanner frame: camera={(interactionCamera != null ? interactionCamera.name : "missing")}, target={(scanTarget != null ? scanTarget.name : "missing")}, away={scannerScanAwayDirection}");
        }

        private void HideLegacyPalmPlaceholder()
        {
            var mockHand = transform.Find("Draggable/IkTarget/MockHand");
            if (mockHand != null)
            {
                mockHand.gameObject.SetActive(false);
                ModLogger.Debug("[Fingerprint Scan] Disabled legacy MockHand placeholder");
            }
        }

        private void PositionHandTargetAtScanStart()
        {
            if (ikTarget == null || scanTarget == null)
            {
                return;
            }

            Vector3 surfaceNormal = GetScannerSurfaceNormal();
            Vector3 surfaceAway = Vector3.ProjectOnPlane(GetScannerAwayDirection(), surfaceNormal);
            if (surfaceAway.sqrMagnitude < 0.0001f)
            {
                surfaceAway = Vector3.Cross(surfaceNormal, GetScannerSurfaceRight());
            }

            surfaceAway.Normalize();
            Vector3 start = scanTarget.position +
                            GetScannerSurfaceRight() * HandStartRightOffset +
                            surfaceAway * HandStartNearOffset;
            ikTarget.position = ClampHandTargetToScannerWorkspace(start);
            ModLogger.Debug("[Fingerprint Scan] Positioned right hand at the scanner's right-side start point");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Vector3 ClampHandTargetToScannerWorkspace(Vector3 position)
        {
            if (scanTarget == null)
            {
                return position;
            }

            Vector3 surfaceNormal = GetScannerSurfaceNormal();
            Vector3 surfaceRight = GetScannerSurfaceRight();
            Vector3 surfaceAway = Vector3.ProjectOnPlane(GetScannerAwayDirection(), surfaceNormal);
            if (surfaceAway.sqrMagnitude < 0.0001f)
            {
                surfaceAway = Vector3.Cross(surfaceNormal, surfaceRight);
            }

            surfaceAway.Normalize();
            Vector3 fromScanner = Vector3.ProjectOnPlane(position - scanTarget.position, surfaceNormal);
            float lateral = Mathf.Clamp(Vector3.Dot(fromScanner, surfaceRight), HandWorkspaceLeftLimit, HandWorkspaceRightLimit);
            float foreAft = Mathf.Clamp(Vector3.Dot(fromScanner, surfaceAway), HandWorkspaceNearLimit, HandWorkspaceFarLimit);
            return ProjectToScannerSurface(scanTarget.position + surfaceRight * lateral + surfaceAway * foreAft);
        }

        private void EnterHandScanInteraction()
        {
            if (handScanControlsLocked)
            {
                return;
            }

            handScanControlsLocked = true;
            var movement = PlayerSingleton<PlayerMovement>.Instance;
            if (movement != null)
            {
                movement.CanMove = false;
            }

            DisablePunchInputForHandScan();

            var livePlayerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (livePlayerCamera != null)
            {
                if (interactionCamera != null)
                {
                    // Use the station-authored camera transform with the live
                    // player camera.  This is the same ownership seam as the
                    // mugshot station and keeps the scanner centered without
                    // activating a second rendering camera.
                    livePlayerCamera.OverrideFOV(60f, 0.15f);
                    livePlayerCamera.OverrideTransform(
                        interactionCamera.transform.position,
                        interactionCamera.transform.rotation,
                        0.15f);
                    handScanCameraOverridden = true;
                }
                else
                {
                    ModLogger.Error("[Fingerprint Scan] InteractionCamera was not found; cannot frame the scanner");
                }

                livePlayerCamera.FreeMouse();
            }

            if (Singleton<HUD>.Instance != null)
            {
                Singleton<HUD>.Instance.SetCrosshairVisible(false);
            }

            if (!handScanExitListenerRegistered)
            {
#if !MONO
                GameInput.RegisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner, priority: 2);
#else
                GameInput.RegisterExitListener(OnExitPalmScanner, priority: 2);
#endif
                handScanExitListenerRegistered = true;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForScannerCameraOverride(float timeoutSeconds = 0.60f)
        {
            // OverrideTransform interpolates on the live PlayerCamera.  The
            // right-arm snapshot must not be placed while that camera is
            // still looking from the player's previous position: doing so
            // made the first scanner frame bake correctly but render well
            // outside the scanner view.
            if (interactionCamera == null)
            {
                yield break;
            }

            var livePlayerCamera = PlayerSingleton<PlayerCamera>.Instance;
            Camera liveCamera = livePlayerCamera != null ? livePlayerCamera.Camera : null;
            if (liveCamera == null)
            {
                ModLogger.Warn("[Fingerprint Scan] Live player camera was unavailable while waiting for scanner framing");
                yield break;
            }

            float elapsed = 0f;
            const float positionTolerance = 0.025f;
            const float facingTolerance = 0.9995f;
            while (elapsed < timeoutSeconds)
            {
                float positionError = Vector3.Distance(liveCamera.transform.position, interactionCamera.transform.position);
                float facingAlignment = Vector3.Dot(liveCamera.transform.forward, interactionCamera.transform.forward);
                if (positionError <= positionTolerance && facingAlignment >= facingTolerance)
                {
                    ModLogger.Info($"[Fingerprint Scan] Scanner camera framing settled in {elapsed:F2}s (position-error={positionError:F3}m, facing={facingAlignment:F4})");
                    yield break;
                }

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            float finalPositionError = Vector3.Distance(liveCamera.transform.position, interactionCamera.transform.position);
            float finalFacingAlignment = Vector3.Dot(liveCamera.transform.forward, interactionCamera.transform.forward);
            ModLogger.Warn($"[Fingerprint Scan] Scanner camera framing did not fully settle before timeout (position-error={finalPositionError:F3}m, facing={finalFacingAlignment:F4}); continuing with authored scanner frame");
        }

        private void ExitHandScanInteraction()
        {
            if (!handScanControlsLocked)
            {
                HideScannerStatusIndicator();
                return;
            }

            handScanControlsLocked = false;
            HideScannerStatusIndicator();
            var movement = PlayerSingleton<PlayerMovement>.Instance;
            if (movement != null)
            {
                movement.CanMove = true;
            }

            RestoreNativeViewmodelHands();
            RestoreScannerArmSnapshot();
            RestoreFingerprintPose();
            scannerScanFrameLocked = false;
            scannerScanAwayDirection = Vector3.zero;

            RestorePunchInputAfterHandScan();

            var livePlayerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (livePlayerCamera != null)
            {
                if (handScanCameraOverridden)
                {
                    livePlayerCamera.StopFOVOverride(0.15f);
                    livePlayerCamera.StopTransformOverride(0.15f);
                    handScanCameraOverridden = false;
                }

                livePlayerCamera.LockMouse();
            }

            if (Singleton<HUD>.Instance != null)
            {
                Singleton<HUD>.Instance.SetCrosshairVisible(true);
            }

            if (handScanExitListenerRegistered)
            {
#if !MONO
                GameInput.DeregisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner);
#else
                GameInput.DeregisterExitListener(OnExitPalmScanner);
#endif
                handScanExitListenerRegistered = false;
            }
        }

        /// <summary>
        /// Releases scanner-owned input, camera, and temporary hand state during a scene exit.
        /// Normal completion reaches the same cleanup through StartScanProcess.
        /// </summary>
        public void CancelForSceneExit()
        {
            isScanning = false;
            isDragging = false;
            isPalmScanning = false;
            inScannerView = false;
            fingerprintSuccessPresentationActive = false;
            scannerStatusLocked = false;

            if (scanCoroutine != null)
            {
                MelonCoroutines.Stop(scanCoroutine);
                scanCoroutine = null;
            }

            if (handScanProcessCoroutine != null)
            {
                MelonCoroutines.Stop(handScanProcessCoroutine);
                handScanProcessCoroutine = null;
            }

            ExitHandScanInteraction();
            RestoreNativeViewmodelHands();
            RestoreScannerArmSnapshot();
            RestoreFingerprintPose();
            if (palmModel != null)
            {
                palmModel.SetActive(false);
            }

            if (interactableObject != null)
            {
                interactableObject.SetMessage("Scan fingerprints");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
        }

        private void OnDisable()
        {
            CancelForSceneExit();
        }

        private void OnDestroy()
        {
            CancelForSceneExit();
        }

        private void SnapPlayerToHandScanPose()
        {
            var localPlayer = currentPlayer ?? Player.Local;
            if (localPlayer == null || interactionCamera == null || scanTarget == null)
            {
                ModLogger.Warn("[Fingerprint Scan] Cannot anchor player for hand scan; scanner camera or target is missing");
                return;
            }

            Vector3 directionToScanner = scanTarget.position - interactionCamera.transform.position;
            directionToScanner.y = 0f;
            if (directionToScanner.sqrMagnitude < 0.001f)
            {
                directionToScanner = transform.forward;
                directionToScanner.y = 0f;
            }

            directionToScanner.Normalize();

            // Place the body just behind the station camera. This keeps the
            // torso out of the shot while putting its actual right hand inside
            // normal IK reach of the scanner surface.
            Vector3 posePosition = interactionCamera.transform.position - directionToScanner * 0.35f;
            posePosition.y = localPlayer.transform.position.y;
            localPlayer.transform.position = posePosition;
            localPlayer.transform.rotation = Quaternion.LookRotation(directionToScanner, Vector3.up);

            ModLogger.Info($"[Fingerprint Scan] Anchored real player avatar at {posePosition} for scanner reach");
        }

        private void DisablePunchInputForHandScan()
        {
            // The current game keeps PunchController on a different hierarchy
            // than the old CameraContainer path. Gate its native UpdateInput
            // directly instead of relying on a GameObject-name lookup.
            Behind_Bars.Harmony.HarmonyPatches.SetFingerprintScanInputLocked(true);
            handScanPunchInputDisabled = true;
        }

        private void RestorePunchInputAfterHandScan()
        {
            Behind_Bars.Harmony.HarmonyPatches.SetFingerprintScanInputLocked(false);

            handScanPunchInputDisabled = false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool ApplyFingerprintPose(Player player)
        {
            if (player == null || player.Avatar == null || player.Avatar.Animation == null ||
                player.Avatar.Animation.animator == null)
            {
                ModLogger.Error("[Fingerprint Scan] Local player avatar animator was unavailable");
                return false;
            }

            if (fingerprintPoseClip == null)
            {
                fingerprintPoseClip = Utils.AssetBundleUtils.LoadAnimationClipFromBundle(
                    "Behind_Bars.behind_bars_scanner_pose",
                    // Keep this in lockstep with FingerprintPoseBundleBuilder.PosePath.
                    // Unity normalizes AssetBundle asset names to lowercase.
                    "assets/behindbars/generated/fingerprintscan_rightarmpose.anim");
            }

            if (fingerprintPoseClip == null)
            {
                ModLogger.Error("[Fingerprint Scan] Could not load BehindBars_FingerprintScan_RightArm from the pose bundle");
                return false;
            }

            scannerPoseAnimator = player.Avatar.Animation.animator;
            scannerPoseOriginalController = scannerPoseAnimator.runtimeAnimatorController;
            if (scannerPoseOriginalController == null)
            {
                ModLogger.Error("[Fingerprint Scan] Local player avatar has no runtime animator controller");
                return false;
            }

 #if MONO
            var scannerOverride = new AnimatorOverrideController(scannerPoseOriginalController);
 #else
            var scannerOverride = new AnimatorOverrideController(scannerPoseOriginalController.Pointer);
 #endif
            scannerOverride["RightArm_Hold_OpenHand"] = fingerprintPoseClip;
            scannerPoseAnimator.runtimeAnimatorController = scannerOverride;
            player.SetAnimationTrigger("RightArm_Hold_OpenHand");
            scannerPoseApplied = true;
            ModLogger.Info("[Fingerprint Scan] Applied dedicated right-arm scanner pose; left arm remains in its idle animation");
            return true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void RestoreFingerprintPose()
        {
            if (!scannerPoseApplied)
            {
                return;
            }

            try
            {
                var localPlayer = currentPlayer ?? Player.Local;
                if (localPlayer != null)
                {
                    localPlayer.SetAnimationTrigger("EndAction");
                }

                if (scannerPoseAnimator != null && scannerPoseOriginalController != null)
                {
                    scannerPoseAnimator.runtimeAnimatorController = scannerPoseOriginalController;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"[Fingerprint Scan] Failed to restore the player animator after pose capture: {ex.Message}");
            }
            finally
            {
                scannerPoseAnimator = null;
                scannerPoseOriginalController = null;
                scannerPoseApplied = false;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool CreateScannerArmSnapshot(Player player)
        {
            RestoreScannerArmSnapshot();
            if (player == null || player.Avatar == null || ikTarget == null || scanTarget == null)
            {
                return false;
            }

            if (!TryResolveAvatarRightArm(player, out Transform rightForearm, out Transform rightHand))
            {
                ModLogger.Error("[Fingerprint Scan] Could not locate the local avatar's right forearm and hand bones");
                return false;
            }

            scannerArmSnapshotRoot = new GameObject("BehindBars_Fingerprint_RightArm");
            // This is a world-space static snapshot, not the native
            // first-person viewmodel. Render it on the station's normal layer
            // so the camera that is overriding to the scanner transform sees
            // it without relying on the player's Viewmodel culling mask.
            scannerArmSnapshotRoot.layer = gameObject.layer;

            Vector3 surfaceNormal = GetScannerSurfaceNormal();
            Vector3 palmNormal = GetPalmNormal(rightHand);
            Quaternion surfaceRotation = Quaternion.FromToRotation(palmNormal, -surfaceNormal);
            ModLogger.Info($"[Fingerprint Scan] Scanner surface frame: normal={surfaceNormal}, " +
                           $"target-up={(scanTarget != null ? scanTarget.up : Vector3.zero)}, " +
                           $"target-forward={(scanTarget != null ? scanTarget.forward : Vector3.zero)}, " +
                           $"camera-forward={(interactionCamera != null ? interactionCamera.transform.forward : Vector3.zero)}, " +
                           $"camera-up={(interactionCamera != null ? interactionCamera.transform.up : Vector3.zero)}");
            int capturedMeshes = 0;
            Quaternion capturedRendererRotation = Quaternion.identity;
            Quaternion capturedSnapshotRotation = Quaternion.identity;
            Vector3 capturedRendererScale = Vector3.one;
            bool capturedMeshUsesWorldSpace = false;
            Vector3 capturedMeshBoundsCenter = Vector3.zero;
            Vector3 capturedMeshBoundsSize = Vector3.zero;

            var renderers = new List<SkinnedMeshRenderer>();
            if (player.Avatar.BodyMeshes != null)
            {
                foreach (SkinnedMeshRenderer renderer in player.Avatar.BodyMeshes)
                {
                    if (renderer != null && !renderers.Contains(renderer))
                    {
                        renderers.Add(renderer);
                    }
                }
            }

            // Local-player avatars can leave their LOD renderer disabled or
            // omit the serialized BodyMeshes references. Query the complete
            // live hierarchy as well; BakeMesh remains valid for a disabled
            // renderer and is exactly what we need for the frozen snapshot.
            foreach (SkinnedMeshRenderer renderer in player.Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer != null && !renderers.Contains(renderer))
                {
                    renderers.Add(renderer);
                }
            }

            // Avatar is not always the root of the local visual hierarchy on
            // IL2CPP. Search the complete player tree too, then de-duplicate
            // against the Avatar-owned renderers above.
            foreach (SkinnedMeshRenderer renderer in player.transform.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer != null && !renderers.Contains(renderer))
                {
                    renderers.Add(renderer);
                }
            }

            // The active local avatar uses this exact 4,952-vertex skin. Do
            // not bury its diagnostics behind unrelated eyes, shoes, and LOD
            // fallbacks while we validate the native skinning surface.
            SkinnedMeshRenderer primaryBody = renderers.Find(renderer =>
                renderer != null && renderer.sharedMesh != null &&
                renderer.name.Equals("Body_LOD0", System.StringComparison.Ordinal) &&
                renderer.sharedMesh.vertexCount == 4952);
            if (primaryBody != null)
            {
                renderers.Clear();
                renderers.Add(primaryBody);
                ModLogger.Info("[Fingerprint Scan] Targeting primary 4,952-vertex Body_LOD0 only");
            }

            ModLogger.Info($"[Fingerprint Scan] Evaluating {renderers.Count} live avatar renderer(s) for right-arm capture");

            for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
            {
                SkinnedMeshRenderer source = renderers[rendererIndex];
                if (source == null || source.sharedMesh == null)
                {
                    string rendererName = source != null ? source.name : "<null>";
                    bool isEnabled = source != null && source.enabled;
                    bool isActive = source != null && source.gameObject.activeInHierarchy;
                    ModLogger.Info($"[Fingerprint Scan] Renderer {rendererIndex} '{rendererName}' skipped: sharedMesh={(source != null && source.sharedMesh != null)}, enabled={isEnabled}, active={isActive}");
                    continue;
                }

                int sourceBoneCount = source.bones != null ? source.bones.Length : 0;
                ModLogger.Info($"[Fingerprint Scan] Candidate {rendererIndex} '{source.name}': enabled={source.enabled}, active={source.gameObject.activeInHierarchy}, vertices={source.sharedMesh.vertexCount}, bones={sourceBoneCount}");
                Mesh baked = null;
                Mesh rightArmMesh = null;
                try
                {
                    baked = new Mesh { name = source.sharedMesh.name + "_BehindBars_Baked" };
                    source.BakeMesh(baked);
                    capturedMeshBoundsCenter = baked.bounds.center;
                    capturedMeshBoundsSize = baked.bounds.size;
                    // BakeMesh coordinate space differs between the native
                    // local-player renderer and ordinary authoring meshes.
                    // A center near the renderer's world position means the
                    // vertices have already been baked in world space.
                    capturedMeshUsesWorldSpace = Vector3.Distance(
                        capturedMeshBoundsCenter,
                        source.transform.position) < 5f;
                    rightArmMesh = CreateBakedRightArmMesh(source, baked, rightForearm, rightHand);
                    if (rightArmMesh == null)
                    {
                        // Some current avatar LODs are optimized and expose
                        // no usable skin-bone array. Fall back to the baked
                        // mesh's real world-space forearm-to-hand region.
                        rightArmMesh = CreateSpatialRightArmMesh(source, baked, rightForearm, rightHand);
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Warn($"[Fingerprint Scan] Could not bake candidate '{source.name}': {ex.Message}");
                }
                finally
                {
                    if (baked != null)
                    {
                        UnityEngine.Object.Destroy(baked);
                    }
                }

                if (rightArmMesh == null)
                {
                    ModLogger.Info($"[Fingerprint Scan] Candidate {rendererIndex} '{source.name}' contained no usable right forearm/hand geometry");
                    continue;
                }

                GameObject visual = new GameObject("RightForearmAndHand");
                visual.layer = scannerArmSnapshotRoot.layer;
                visual.transform.SetParent(scannerArmSnapshotRoot.transform, false);
                if (capturedMeshUsesWorldSpace)
                {
                    // The baked vertices are absolute positions. Translate
                    // the real wrist to the snapshot origin and rotate there.
                    visual.transform.localPosition = -rightHand.position;
                    capturedRendererRotation = Quaternion.identity;
                    capturedRendererScale = Vector3.one;
                }
                else
                {
                    // The baked vertices are renderer-local. Preserve the
                    // renderer rotation before rotating around its wrist.
                    // BakeMesh has already applied the renderer scale on the
                    // current native avatar; applying lossyScale again turns
                    // a normal arm into a 100x snapshot behind the camera.
                    Vector3 wristLocal = source.transform.InverseTransformPoint(rightHand.position);
                    visual.transform.localPosition = -wristLocal;
                    capturedRendererRotation = source.transform.rotation;
                    capturedRendererScale = Vector3.one;
                }
                visual.transform.localRotation = Quaternion.identity;

                // Use the visual alignment path that produces the correct
                // open-palm scanner pose on the live avatar: first make the
                // palm face the glass, then perform the one in-plane roll
                // required to point the wrist away across the scanner.
                // Scanner direction is now fixed at interaction start, so
                // this no longer varies with the player's approach heading.
                Vector3 currentForearmDirection = rightHand.position - rightForearm.position;
                Vector3 alignedForearmDirection = Vector3.ProjectOnPlane(
                    surfaceRotation * currentForearmDirection,
                    surfaceNormal);
                Vector3 desiredForearmDirection = Vector3.ProjectOnPlane(GetScannerAwayDirection(), surfaceNormal);
                if (alignedForearmDirection.sqrMagnitude > 0.0001f && desiredForearmDirection.sqrMagnitude > 0.0001f)
                {
                    float rollDegrees = Vector3.SignedAngle(
                        alignedForearmDirection,
                        desiredForearmDirection,
                        -surfaceNormal);
                    capturedSnapshotRotation = Quaternion.AngleAxis(rollDegrees, -surfaceNormal) *
                                               surfaceRotation * capturedRendererRotation;
                    ModLogger.Info($"[Fingerprint Scan] Applied fixed-direction palm-down scanner pose (roll={rollDegrees:F1}, away={desiredForearmDirection.normalized})");
                }
                else
                {
                    capturedSnapshotRotation = Quaternion.FromToRotation(palmNormal, -surfaceNormal) * capturedRendererRotation;
                    ModLogger.Warn("[Fingerprint Scan] Could not derive an in-plane forearm direction; using palm-normal alignment only");
                }

                // The native animation can report either wrist twist on the frame in
                // which the override pose settles. Both twists keep the forearm pointed
                // at the scanner, but one renders the right palm upward. Resolve that
                // final 180-degree ambiguity from actual right-hand landmarks: with the
                // palm down and fingers pointing away from the player, a right thumb is
                // on the scanner's left side. This is independent of the approach angle
                // and remains stable across first use and repeat scans.
                Transform thumbForTwist = FindNamedTransform(rightHand, "RightHandThumb1");
                if (thumbForTwist != null && desiredForearmDirection.sqrMagnitude > 0.0001f)
                {
                    Quaternion twistCheckRotation = capturedSnapshotRotation * Quaternion.Inverse(capturedRendererRotation);
                    Vector3 displayedThumbDirection = Vector3.ProjectOnPlane(
                        twistCheckRotation * (thumbForTwist.position - rightHand.position),
                        surfaceNormal);
                    Vector3 expectedThumbDirection = -GetScannerSurfaceRight();
                    if (displayedThumbDirection.sqrMagnitude > 0.0001f &&
                        Vector3.Dot(displayedThumbDirection.normalized, expectedThumbDirection.normalized) < 0f)
                    {
                        capturedSnapshotRotation = Quaternion.AngleAxis(180f, desiredForearmDirection.normalized) *
                                                   capturedSnapshotRotation;
                        ModLogger.Info("[Fingerprint Scan] Corrected inverted wrist twist using the native right-thumb landmark");
                    }
                    else
                    {
                        ModLogger.Debug("[Fingerprint Scan] Confirmed canonical palm-down wrist twist using the native right-thumb landmark");
                    }
                }
                else
                {
                    ModLogger.Warn("[Fingerprint Scan] Right-thumb landmark was unavailable; scanner wrist twist could not be canonically verified");
                }

                // The snapshot root is the wrist because the baked mesh is
                // translated around RightHand. Build a palm-center anchor
                // from the live knuckles, then carry it through the exact
                // same pose transform as the rendered mesh. This is the
                // visible hand location the player is aligning to the print.
                Transform indexKnuckle = FindNamedTransform(rightHand, "RightHandIndex1");
                Transform littleKnuckle = FindNamedTransform(rightHand, "RightHandLittle1");
                Transform thumbKnuckle = FindNamedTransform(rightHand, "RightHandThumb1");
                Vector3 palmCenter = rightHand.position;
                int palmPointCount = 0;
                if (indexKnuckle != null) { palmCenter += indexKnuckle.position; palmPointCount++; }
                if (littleKnuckle != null) { palmCenter += littleKnuckle.position; palmPointCount++; }
                if (thumbKnuckle != null) { palmCenter += thumbKnuckle.position; palmPointCount++; }
                if (palmPointCount > 0)
                {
                    palmCenter /= palmPointCount + 1;
                }

                Quaternion snapshotPoseRotation = capturedSnapshotRotation * Quaternion.Inverse(capturedRendererRotation);
                scannerArmPalmOffset = snapshotPoseRotation * (palmCenter - rightHand.position);
                ModLogger.Info($"[Fingerprint Scan] Visible palm anchor is {scannerArmPalmOffset.magnitude:F3}m from the wrist");

                MeshFilter filter = visual.AddComponent<MeshFilter>();
                filter.sharedMesh = rightArmMesh;
                MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = source.sharedMaterials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                scannerArmSnapshotMeshes.Add(rightArmMesh);
                capturedMeshes++;
                ModLogger.Info($"[Fingerprint Scan] Snapshot coordinates: {(capturedMeshUsesWorldSpace ? "world" : "renderer-local")}, baked-center={capturedMeshBoundsCenter}, baked-size={capturedMeshBoundsSize}, renderer={source.transform.position}, renderer-scale={source.transform.lossyScale}, wrist={rightHand.position}");

                // A single active avatar LOD is expected. Capturing another
                // would duplicate the arm in the scanner view.
                break;
            }

            if (capturedMeshes == 0)
            {
                RestoreScannerArmSnapshot();
                return false;
            }

            scannerArmSnapshotRoot.transform.rotation = capturedSnapshotRotation;
            scannerArmSnapshotRoot.transform.localScale = capturedRendererScale;
            scannerArmArrivalStart = ProjectToScannerSurface(ikTarget.position - GetScannerSurfaceRight() * 0.10f);
            scannerArmSnapshotRoot.transform.position = scannerArmArrivalStart;
            CalibrateScannerArmDisplayOffset();
            SeatScannerArmOnPlaten();
            scannerArmArrivalStart = scannerArmSnapshotRoot.transform.position;
            scannerArmArrivalActive = true;
            LogScannerArmSnapshotViewport();
            ModLogger.Info($"[Fingerprint Scan] Baked {capturedMeshes} real-player right-arm mesh for scanner-only rendering");
            return true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Mesh CreateBakedRightArmMesh(SkinnedMeshRenderer source, Mesh baked, Transform rightForearm, Transform rightHand)
        {
            Mesh sourceMesh = source.sharedMesh;
            Transform[] bones = source.bones;
            BoneWeight[] weights = GetSourceBoneWeights(sourceMesh);
            if (baked == null || sourceMesh == null || bones == null || weights == null || weights.Length == 0)
            {
                return null;
            }

            HashSet<int> rightArmBoneIndices = FindRightArmBoneIndices(bones, rightForearm, rightHand);

            if (rightArmBoneIndices.Count == 0)
            {
                LogRendererBoneNames(source, bones);
                return null;
            }

            ModLogger.Info($"[Fingerprint Scan] Native skin data for '{source.name}': weights={weights.Length}, right-arm bones={rightArmBoneIndices.Count}");
            if (source.name.Equals("Body_LOD0", System.StringComparison.Ordinal) && sourceMesh.vertexCount == 4952)
            {
                LogPrimaryBodySkinDiagnostics(source, bones, weights, rightArmBoneIndices);
            }

            // Build this mask through the same native-field reads used by the
            // diagnostic report above.  On the current IL2CPP runtime that
            // report correctly finds 963 right-arm vertices, whereas
            // re-evaluating a BoneWeight inside the triangle loop does not.
            // Select topology from the already-verified vertex mask instead.
            bool[] rightArmVertices = BuildRightArmVertexMask(weights, rightArmBoneIndices);

            Mesh result = UnityEngine.Object.Instantiate(baked);
            result.name = sourceMesh.name + "_BehindBars_RightForearm";
            // The normal triangle accessors honor Unity's unreadable-mesh
            // guard on the local-player skin and return empty arrays. The
            // generated IL2CPP Mesh binding exposes the native non-alloc
            // reader; use it on IL2CPP while retaining the normal Mono API.
            result.subMeshCount = 1;
            int selectedTriangles = 0;
            int sourceTriangles = 0;
            var selected = new List<int>();
            for (int subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
            {
 #if MONO
                var sourceTopology = sourceMesh.GetTriangles(subMesh);
 #else
                uint nativeTriangleCount = sourceMesh.GetTrianglesCountImpl(subMesh);
                var sourceTopology = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>((int)nativeTriangleCount * 3);
                if (nativeTriangleCount > 0)
                {
                    sourceMesh.GetTrianglesNonAllocImpl(sourceTopology, subMesh, true);
                }
 #endif
                for (int triangle = 0; triangle + 2 < sourceTopology.Length; triangle += 3)
                {
                    sourceTriangles++;
                    int matches = 0;
                    int firstVertex = sourceTopology[triangle];
                    int secondVertex = sourceTopology[triangle + 1];
                    int thirdVertex = sourceTopology[triangle + 2];
                    if (firstVertex >= 0 && firstVertex < rightArmVertices.Length && rightArmVertices[firstVertex]) matches++;
                    if (secondVertex >= 0 && secondVertex < rightArmVertices.Length && rightArmVertices[secondVertex]) matches++;
                    if (thirdVertex >= 0 && thirdVertex < rightArmVertices.Length && rightArmVertices[thirdVertex]) matches++;
                    // The current player body uses blended seam vertices: a
                    // right-arm triangle can carry its arm influence on just
                    // one vertex, while the other two are weighted to the
                    // adjacent torso bone. Retain that triangle so the
                    // forearm is not punched through at its seam.
                    if (matches >= 1)
                    {
                        selected.Add(firstVertex);
                        selected.Add(secondVertex);
                        selected.Add(thirdVertex);
                    }
                }
            }

            selectedTriangles = selected.Count;
 #if MONO
            result.SetTriangles(selected.ToArray(), 0, false);
 #else
            result.SetTriangles(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(selected.ToArray()), 0, false);
 #endif

            if (selectedTriangles == 0)
            {
                ModLogger.Warn($"[Fingerprint Scan] Native skin topology for '{source.name}' had {sourceTriangles} triangles but no triangle referenced the {CountTrue(rightArmVertices)} verified right-arm vertices");
                UnityEngine.Object.Destroy(result);
                return null;
            }

            result.RecalculateBounds();
            ModLogger.Info($"[Fingerprint Scan] Native skin selection for '{source.name}' retained {selectedTriangles / 3} of {sourceTriangles} triangles from {CountTrue(rightArmVertices)} verified right-arm vertices");
            return result;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void LogPrimaryBodySkinDiagnostics(SkinnedMeshRenderer source, Transform[] bones, BoneWeight[] weights, HashSet<int> rightArmBoneIndices)
        {
            var influenceCounts = new int[bones.Length];
            int validInfluences = 0;
            int invalidInfluences = 0;
            int rightArmInfluences = 0;
            int rightArmVertices = 0;

            for (int vertex = 0; vertex < weights.Length; vertex++)
            {
                bool vertexBelongsToRightArm = false;
#if MONO
                AccumulateSkinInfluence(weights[vertex].boneIndex0, weights[vertex].weight0, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].boneIndex1, weights[vertex].weight1, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].boneIndex2, weights[vertex].weight2, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].boneIndex3, weights[vertex].weight3, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
#else
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex0, weights[vertex].m_Weight0, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex1, weights[vertex].m_Weight1, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex2, weights[vertex].m_Weight2, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex3, weights[vertex].m_Weight3, influenceCounts, rightArmBoneIndices, ref validInfluences, ref invalidInfluences, ref rightArmInfluences, ref vertexBelongsToRightArm);
#endif
                if (vertexBelongsToRightArm)
                {
                    rightArmVertices++;
                }
            }

            var armBoneSummary = new List<string>();
            foreach (int boneIndex in rightArmBoneIndices)
            {
                string boneName = boneIndex >= 0 && boneIndex < bones.Length && bones[boneIndex] != null
                    ? bones[boneIndex].name
                    : "<invalid>";
                int count = boneIndex >= 0 && boneIndex < influenceCounts.Length ? influenceCounts[boneIndex] : 0;
                armBoneSummary.Add($"{boneIndex}:{boneName}={count}");
            }

            ModLogger.Info($"[Fingerprint Scan] Body_LOD0 skin report: valid={validInfluences}, invalid={invalidInfluences}, right-arm influences={rightArmInfluences}, right-arm vertices={rightArmVertices}; {string.Join(", ", armBoneSummary.ToArray())}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void AccumulateSkinInfluence(int boneIndex, float weight, int[] influenceCounts, HashSet<int> rightArmBoneIndices, ref int validInfluences, ref int invalidInfluences, ref int rightArmInfluences, ref bool vertexBelongsToRightArm)
        {
            if (weight < 0.0001f)
            {
                return;
            }

            if (boneIndex < 0 || boneIndex >= influenceCounts.Length)
            {
                invalidInfluences++;
                return;
            }

            validInfluences++;
            influenceCounts[boneIndex]++;
            if (rightArmBoneIndices.Contains(boneIndex))
            {
                rightArmInfluences++;
                vertexBelongsToRightArm = true;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static bool[] BuildRightArmVertexMask(BoneWeight[] weights, HashSet<int> rightArmBoneIndices)
        {
            bool[] result = new bool[weights.Length];
            int ignoredValidInfluences = 0;
            int ignoredInvalidInfluences = 0;
            int ignoredRightArmInfluences = 0;
            int[] noOpInfluenceCounts = new int[64];

            for (int vertex = 0; vertex < weights.Length; vertex++)
            {
                bool belongsToRightArm = false;
#if MONO
                AccumulateSkinInfluence(weights[vertex].boneIndex0, weights[vertex].weight0, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].boneIndex1, weights[vertex].weight1, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].boneIndex2, weights[vertex].weight2, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].boneIndex3, weights[vertex].weight3, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
#else
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex0, weights[vertex].m_Weight0, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex1, weights[vertex].m_Weight1, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex2, weights[vertex].m_Weight2, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
                AccumulateSkinInfluence(weights[vertex].m_BoneIndex3, weights[vertex].m_Weight3, noOpInfluenceCounts, rightArmBoneIndices, ref ignoredValidInfluences, ref ignoredInvalidInfluences, ref ignoredRightArmInfluences, ref belongsToRightArm);
#endif
                result[vertex] = belongsToRightArm;
            }

            return result;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static int CountTrue(bool[] values)
        {
            int count = 0;
            for (int index = 0; index < values.Length; index++)
            {
                if (values[index]) count++;
            }
            return count;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static BoneWeight[] GetSourceBoneWeights(Mesh sourceMesh)
        {
            if (sourceMesh == null)
            {
                return null;
            }

#if MONO
            return sourceMesh.boneWeights;
#else
            // Unity 2022 stores skinning as variable-length BoneWeight1
            // entries (float weight + Int32 bone index), not fixed 32-byte
            // BoneWeight records. The compact count was 11,808 for the
            // 4,952-vertex player mesh in live IL2CPP. Rebuild one classical
            // BoneWeight per vertex using Unity's companion count buffer.
            int packedInfluenceCount = sourceMesh.GetAllBoneWeightsArraySize();
            int vertexCount = sourceMesh.vertexCount;
            System.IntPtr packedInfluenceBuffer = sourceMesh.GetAllBoneWeightsArray();
            System.IntPtr influencesPerVertexBuffer = sourceMesh.GetBonesPerVertexArray();
            if (packedInfluenceCount == 0 || vertexCount == 0 ||
                packedInfluenceBuffer == System.IntPtr.Zero || influencesPerVertexBuffer == System.IntPtr.Zero)
            {
                return null;
            }

            var weights = new BoneWeight[vertexCount];
            unsafe
            {
                byte* influenceCounts = (byte*)influencesPerVertexBuffer.ToPointer();
                byte* packedInfluences = (byte*)packedInfluenceBuffer.ToPointer();
                int packedIndex = 0;
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    int influenceCount = influenceCounts[vertex];
                    BoneWeight reconstructed = default;
                    for (int influence = 0; influence < influenceCount && packedIndex < packedInfluenceCount; influence++, packedIndex++)
                    {
                        byte* entry = packedInfluences + packedIndex * 8;
                        float weight = *(float*)entry;
                        int boneIndex = *(int*)(entry + 4);
                        // Unity orders the compact entries by descending
                        // influence. Keep its first four—the legacy
                        // BoneWeight surface supports exactly four slots.
                        switch (influence)
                        {
                            case 0:
                                reconstructed.m_Weight0 = weight;
                                reconstructed.m_BoneIndex0 = boneIndex;
                                break;
                            case 1:
                                reconstructed.m_Weight1 = weight;
                                reconstructed.m_BoneIndex1 = boneIndex;
                                break;
                            case 2:
                                reconstructed.m_Weight2 = weight;
                                reconstructed.m_BoneIndex2 = boneIndex;
                                break;
                            case 3:
                                reconstructed.m_Weight3 = weight;
                                reconstructed.m_BoneIndex3 = boneIndex;
                                break;
                        }
                    }
                    weights[vertex] = reconstructed;
                }
            }
            return weights;
#endif
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Mesh CreateSpatialRightArmMesh(SkinnedMeshRenderer source, Mesh baked, Transform rightForearm, Transform rightHand)
        {
            if (source == null || baked == null || rightForearm == null || rightHand == null)
            {
                return null;
            }

            Vector3 elbow = rightForearm.position;
            Vector3 wrist = rightHand.position;
            Vector3 forearm = wrist - elbow;
            float forearmLength = forearm.magnitude;
            if (forearmLength < 0.03f)
            {
                return null;
            }

            Vector3 direction = forearm / forearmLength;
            Vector3 localElbow = source.transform.InverseTransformPoint(elbow);
            Vector3 localWrist = source.transform.InverseTransformPoint(wrist);
            Vector3 localForearm = localWrist - localElbow;
            float localForearmLength = localForearm.magnitude;
            Vector3 localDirection = localForearmLength > 0.03f ? localForearm / localForearmLength : Vector3.zero;
            Vector3[] vertices = baked.vertices;
            Mesh sourceMesh = source.sharedMesh;
            var rendererLocalTriangles = new List<List<int>>();
            var bakedWorldTriangles = new List<List<int>>();
            int rendererLocalTriangleCount = 0;
            int bakedWorldTriangleCount = 0;

            for (int subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
            {
                int[] triangles = sourceMesh.GetTriangles(subMesh);
                var rendererLocalSelected = new List<int>();
                var bakedWorldSelected = new List<int>();
                for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                {
                    int rendererLocalMatches = 0;
                    int bakedWorldMatches = 0;
                    if (localForearmLength > 0.03f && VertexFallsOnRightForearm(vertices[triangles[triangle]], localElbow, localDirection, localForearmLength)) rendererLocalMatches++;
                    if (localForearmLength > 0.03f && VertexFallsOnRightForearm(vertices[triangles[triangle + 1]], localElbow, localDirection, localForearmLength)) rendererLocalMatches++;
                    if (localForearmLength > 0.03f && VertexFallsOnRightForearm(vertices[triangles[triangle + 2]], localElbow, localDirection, localForearmLength)) rendererLocalMatches++;
                    if (VertexFallsOnRightForearm(vertices[triangles[triangle]], elbow, direction, forearmLength)) bakedWorldMatches++;
                    if (VertexFallsOnRightForearm(vertices[triangles[triangle + 1]], elbow, direction, forearmLength)) bakedWorldMatches++;
                    if (VertexFallsOnRightForearm(vertices[triangles[triangle + 2]], elbow, direction, forearmLength)) bakedWorldMatches++;

                    if (rendererLocalMatches >= 2)
                    {
                        rendererLocalSelected.Add(triangles[triangle]);
                        rendererLocalSelected.Add(triangles[triangle + 1]);
                        rendererLocalSelected.Add(triangles[triangle + 2]);
                    }

                    if (bakedWorldMatches >= 2)
                    {
                        bakedWorldSelected.Add(triangles[triangle]);
                        bakedWorldSelected.Add(triangles[triangle + 1]);
                        bakedWorldSelected.Add(triangles[triangle + 2]);
                    }
                }

                rendererLocalTriangles.Add(rendererLocalSelected);
                bakedWorldTriangles.Add(bakedWorldSelected);
                rendererLocalTriangleCount += rendererLocalSelected.Count / 3;
                bakedWorldTriangleCount += bakedWorldSelected.Count / 3;
            }

            bool useBakedWorldCoordinates = bakedWorldTriangleCount > rendererLocalTriangleCount;
            List<List<int>> selectedBySubMesh = useBakedWorldCoordinates ? bakedWorldTriangles : rendererLocalTriangles;
            int selectedTriangles = useBakedWorldCoordinates ? bakedWorldTriangleCount : rendererLocalTriangleCount;
            ModLogger.Info($"[Fingerprint Scan] Spatial selection for '{source.name}': renderer-local={rendererLocalTriangleCount}, baked-world={bakedWorldTriangleCount}, using={(useBakedWorldCoordinates ? "baked-world" : "renderer-local")}");
            if (selectedTriangles == 0)
            {
                return null;
            }

            Mesh result = UnityEngine.Object.Instantiate(baked);
            result.name = sourceMesh.name + "_BehindBars_RightForearmSpatial";
            result.subMeshCount = sourceMesh.subMeshCount;
            for (int subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
            {
#if MONO
                result.SetTriangles(selectedBySubMesh[subMesh].ToArray(), subMesh, false);
#else
                result.SetTriangles(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(selectedBySubMesh[subMesh].ToArray()), subMesh, false);
#endif
            }

            if (useBakedWorldCoordinates)
            {
                Vector3[] resultVertices = result.vertices;
                for (int vertex = 0; vertex < resultVertices.Length; vertex++)
                {
                    resultVertices[vertex] = source.transform.InverseTransformPoint(resultVertices[vertex]);
                }
                result.vertices = resultVertices;
            }

            result.RecalculateBounds();
            ModLogger.Info($"[Fingerprint Scan] Used spatial right-arm extraction for '{source.name}' ({selectedTriangles} triangles)");
            return result;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static bool VertexFallsOnRightForearm(Vector3 vertex, Vector3 elbow, Vector3 direction, float forearmLength)
        {
            Vector3 relative = vertex - elbow;
            float alongArm = Vector3.Dot(relative, direction);
            if (alongArm < -0.18f || alongArm > forearmLength * 2.40f)
            {
                return false;
            }

            float radialDistance = (relative - direction * alongArm).magnitude;
            return radialDistance <= 0.32f;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator AnimateScannerArmArrival(float duration)
        {
            if (scannerArmSnapshotRoot == null)
            {
                yield break;
            }

            float elapsed = 0f;
            Vector3 end = ProjectToScannerSurface(ikTarget.position) + scannerArmDisplayOffset + scannerArmSurfaceOffset;
            while (elapsed < duration && scannerArmSnapshotRoot != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                scannerArmSnapshotRoot.transform.position = Vector3.Lerp(scannerArmArrivalStart, end, t);
                yield return null;
            }

            scannerArmArrivalActive = false;
            UpdateScannerArmSnapshotPosition();
        }

        private void UpdateScannerArmSnapshotPosition()
        {
            if (scannerArmSnapshotRoot != null && !scannerArmArrivalActive && ikTarget != null)
            {
                scannerArmSnapshotRoot.transform.position = ProjectToScannerSurface(ikTarget.position) + scannerArmDisplayOffset + scannerArmSurfaceOffset;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void ShowScannerStatusIndicator()
        {
            if (scannerStatusPanel == null && !CreateScannerStatusIndicator())
            {
                return;
            }

            scannerStatusPanel.SetActive(true);
            scannerStatusVisible = true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool CreateScannerStatusIndicator()
        {
            Canvas hudCanvas = GetScannerStatusCanvas();
            if (hudCanvas == null)
            {
                ModLogger.Warn("[Fingerprint Scan] HUD canvas unavailable; scanner alignment indicator will not be shown");
                return false;
            }

            if (!TMPFontFix.EnsureFontCached(hudCanvas))
            {
                ModLogger.Warn("[Fingerprint Scan] No valid HUD font was available for the scanner alignment indicator");
                return false;
            }

            scannerStatusPanel = new GameObject("BehindBars_FingerprintAlignmentStatus");
            scannerStatusPanel.transform.SetParent(hudCanvas.transform, false);
            RectTransform panelRect = scannerStatusPanel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.58f);
            panelRect.anchorMax = new Vector2(0.5f, 0.58f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(385f, 78f);

            scannerStatusHighlight = scannerStatusPanel.AddComponent<Image>();
            scannerStatusHighlight.raycastTarget = false;
            scannerStatusOutline = scannerStatusPanel.AddComponent<Outline>();
            scannerStatusOutline.effectDistance = new Vector2(2f, -2f);

            GameObject statusTextObject = new GameObject("StatusText");
            statusTextObject.transform.SetParent(scannerStatusPanel.transform, false);
            RectTransform textRect = statusTextObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 8f);
            textRect.offsetMax = new Vector2(-14f, -8f);

            scannerStatusText = statusTextObject.AddComponent<TextMeshProUGUI>();
            scannerStatusText.raycastTarget = false;
            scannerStatusText.fontSize = 18f;
            scannerStatusText.fontStyle = FontStyles.Bold;
            scannerStatusText.alignment = TextAlignmentOptions.Center;
            scannerStatusText.enableWordWrapping = true;
            TMPFontFix.FixAllTMPFonts(scannerStatusPanel, "base");

            scannerStatusPanel.SetActive(false);
            scannerStatusVisible = false;
            ModLogger.Info("[Fingerprint Scan] Created persistent red/green scanner alignment indicator");
            return true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Canvas GetScannerStatusCanvas()
        {
#if !MONO
            try
            {
                var hud = Singleton<Il2CppScheduleOne.UI.HUD>.Instance;
                return hud != null && hud.Pointer != System.IntPtr.Zero ? hud.canvas : null;
            }
            catch
            {
                return null;
            }
#else
            try
            {
                return Singleton<HUD>.Instance?.canvas;
            }
            catch
            {
                return null;
            }
#endif
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SetScannerStatusIndicator(bool aligned)
        {
            if (!scannerStatusVisible || scannerStatusPanel == null || scannerStatusHighlight == null || scannerStatusText == null)
            {
                return;
            }

            if (scannerStatusLocked)
            {
                return;
            }

            if (scannerStatusHasState && scannerStatusAligned == aligned)
            {
                return;
            }

            scannerStatusAligned = aligned;
            scannerStatusHasState = true;
            if (aligned)
            {
                scannerStatusHighlight.color = new Color(0.04f, 0.42f, 0.14f, 0.88f);
                scannerStatusOutline.effectColor = new Color(0.35f, 1f, 0.52f, 1f);
                scannerStatusText.color = new Color(0.84f, 1f, 0.88f, 1f);
                scannerStatusText.text = "ALIGNED\nHold still to complete the fingerprint scan";
            }
            else
            {
                scannerStatusHighlight.color = new Color(0.48f, 0.05f, 0.05f, 0.88f);
                scannerStatusOutline.effectColor = new Color(1f, 0.30f, 0.30f, 1f);
                scannerStatusText.color = new Color(1f, 0.86f, 0.86f, 1f);
                scannerStatusText.text = "NOT ALIGNED\nDrag your palm over the fingerprint guide";
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SetFingerprintSuccessIndicator()
        {
            ShowScannerStatusIndicator();
            if (scannerStatusPanel == null || scannerStatusHighlight == null || scannerStatusText == null)
            {
                return;
            }

            scannerStatusLocked = true;
            scannerStatusAligned = true;
            scannerStatusHasState = true;
            scannerStatusHighlight.color = new Color(0.03f, 0.50f, 0.12f, 0.94f);
            if (scannerStatusOutline != null)
            {
                scannerStatusOutline.effectColor = new Color(0.46f, 1f, 0.59f, 1f);
            }

            scannerStatusText.color = new Color(0.90f, 1f, 0.91f, 1f);
            scannerStatusText.text = "FINGERPRINT CAPTURED\nScan complete";
            ModLogger.Info("[Fingerprint Scan] Locked green completion indicator until scanner animation ends");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void HideScannerStatusIndicator()
        {
            if (scannerStatusPanel != null)
            {
                scannerStatusPanel.SetActive(false);
            }

            scannerStatusVisible = false;
            scannerStatusAligned = false;
            scannerStatusHasState = false;
            scannerStatusLocked = false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private float GetFingerprintAlignmentDistance(out Vector3 visiblePalmPosition)
        {
            visiblePalmPosition = Vector3.zero;
            if (scanTarget == null)
            {
                return float.PositiveInfinity;
            }

            // The player sees and moves the baked snapshot, not the hidden
            // wrist target. Validate against that snapshot's palm anchor so
            // a visually correct placement is also a successful placement.
            if (scannerArmSnapshotRoot != null)
            {
                visiblePalmPosition = ProjectToScannerSurface(
                    scannerArmSnapshotRoot.transform.position + scannerArmPalmOffset);
                float anchorDistance = Vector3.Distance(visiblePalmPosition, ProjectToScannerSurface(scanTarget.position));
                float meshCoverageDistance = GetVisiblePalmCoverageDistance(visiblePalmPosition);
                // The palm anchor keeps a useful diagnostic center point,
                // while the rendered geometry is the authoritative hit area.
                // This allows the player to align the visible hand guide
                // rather than an invisible wrist/bone pivot.
                return Mathf.Min(anchorDistance, meshCoverageDistance);
            }

            if (ikTarget != null)
            {
                visiblePalmPosition = ikTarget.position;
                return Vector3.Distance(ikTarget.position, scanTarget.position);
            }

            return float.PositiveInfinity;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private float GetVisiblePalmCoverageDistance(Vector3 visiblePalmPosition)
        {
            if (scannerArmSnapshotRoot == null || scanTarget == null)
            {
                return float.PositiveInfinity;
            }

            Vector3 target = ProjectToScannerSurface(scanTarget.position);
            const float palmFootprintRadius = 0.18f;
            float nearestDistance = float.PositiveInfinity;
            foreach (MeshFilter filter in scannerArmSnapshotRoot.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                {
                    int first = triangles[triangle];
                    int second = triangles[triangle + 1];
                    int third = triangles[triangle + 2];
                    if (first < 0 || second < 0 || third < 0 ||
                        first >= vertices.Length || second >= vertices.Length || third >= vertices.Length)
                    {
                        continue;
                    }

                    Vector3 a = filter.transform.TransformPoint(vertices[first]);
                    Vector3 b = filter.transform.TransformPoint(vertices[second]);
                    Vector3 c = filter.transform.TransformPoint(vertices[third]);
                    Vector3 triangleCenter = ProjectToScannerSurface((a + b + c) / 3f);
                    if (Vector3.Distance(triangleCenter, visiblePalmPosition) > palmFootprintRadius)
                    {
                        continue;
                    }

                    nearestDistance = Mathf.Min(nearestDistance, DistanceToTriangle(target, a, b, c));
                }
            }

            return nearestDistance;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static float DistanceToTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = point - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return Vector3.Distance(point, a);

            Vector3 bp = point - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return Vector3.Distance(point, b);

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                return Vector3.Distance(point, a + v * ab);
            }

            Vector3 cp = point - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return Vector3.Distance(point, c);

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                return Vector3.Distance(point, a + w * ac);
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                Vector3 bc = c - b;
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return Vector3.Distance(point, b + w * bc);
            }

            float denominator = 1f / (va + vb + vc);
            float faceV = vb * denominator;
            float faceW = vc * denominator;
            return Vector3.Distance(point, a + ab * faceV + ac * faceW);
        }

        private float GetFingerprintValidRange()
        {
            // The printed hand guide is larger than the prior 8cm wrist
            // point. Retain a precise target without rejecting a palm that
            // visibly covers the scanner's intended contact area.
            return Mathf.Max(validRange, 0.10f);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool IsFingerprintAlignmentValid(
            out float distance,
            out float validDistance,
            out string measurement,
            out Vector3 visiblePalmPosition)
        {
            visiblePalmPosition = Vector3.zero;
            distance = float.PositiveInfinity;
            validDistance = GetFingerprintValidRange();
            measurement = "world-palm";

            // The printed guide belongs to the stationary interaction-camera
            // composition. Its authoring point is offset in world space from
            // the synthetic platen plane, but it is still at the correct
            // screen location. Match the rendered hand to that screen-space
            // point so visual placement and acceptance are identical.
            float screenDistance = GetVisibleArmScreenGuideDistance(out Vector2 guideViewport, out Vector2 palmViewport);
            if (!float.IsInfinity(screenDistance))
            {
                distance = screenDistance;
                // This is normalized viewport distance. Restore the proven
                // broad visual coverage behavior so the minigame reliably
                // completes once the player brings the right hand over the
                // scanner, rather than rejecting a visibly good placement.
                validDistance = 0.040f;
                measurement = $"screen-hand-mesh guide={guideViewport}, palm={palmViewport}";
                if (scannerArmSnapshotRoot != null)
                {
                    visiblePalmPosition = ProjectToScannerSurface(
                        scannerArmSnapshotRoot.transform.position + scannerArmPalmOffset);
                }
                return distance <= validDistance;
            }

            // A rendered snapshot must always be judged in the same screen
            // space that the player sees. Falling back to the much looser
            // world-space mesh test here can instantly accept an arm merely
            // because its forearm is near the station.
            if (scannerArmSnapshotRoot != null)
            {
                measurement = "screen-palm unavailable";
                return false;
            }

            distance = GetFingerprintAlignmentDistance(out visiblePalmPosition);
            return distance <= validDistance;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private float GetVisibleArmScreenGuideDistance(out Vector2 guideViewport, out Vector2 palmViewport)
        {
            guideViewport = Vector2.zero;
            palmViewport = Vector2.zero;
            if (scannerArmSnapshotRoot == null || scanTarget == null)
            {
                return float.PositiveInfinity;
            }

            Camera camera = PlayerSingleton<PlayerCamera>.Instance != null
                ? PlayerSingleton<PlayerCamera>.Instance.Camera
                : interactionCamera;
            if (camera == null)
            {
                return float.PositiveInfinity;
            }

            Vector3 guide = camera.WorldToViewportPoint(scanTarget.position);
            if (guide.z <= 0f)
            {
                return float.PositiveInfinity;
            }

            guideViewport = new Vector2(guide.x, guide.y);
            if (scannerArmPalmOffset.sqrMagnitude <= 0.0001f)
            {
                return float.PositiveInfinity;
            }

            Vector3 palmWorldPosition = scannerArmSnapshotRoot.transform.position + scannerArmPalmOffset;
            Vector3 palm = camera.WorldToViewportPoint(palmWorldPosition);
            if (palm.z <= 0f)
            {
                return float.PositiveInfinity;
            }

            palmViewport = new Vector2(palm.x, palm.y);
            // Use the broad hand coverage used by the original working
            // scanner path. The task now prioritizes reliable completion with
            // the correct visible right hand over a tiny precision hitbox.
            float nearestDistance = float.PositiveInfinity;
            foreach (MeshFilter filter in scannerArmSnapshotRoot.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
                {
                    int first = triangles[triangle];
                    int second = triangles[triangle + 1];
                    int third = triangles[triangle + 2];
                    if (first < 0 || second < 0 || third < 0 ||
                        first >= vertices.Length || second >= vertices.Length || third >= vertices.Length)
                    {
                        continue;
                    }

                    Vector3 firstWorld = filter.transform.TransformPoint(vertices[first]);
                    Vector3 secondWorld = filter.transform.TransformPoint(vertices[second]);
                    Vector3 thirdWorld = filter.transform.TransformPoint(vertices[third]);
                    Vector3 firstViewport = camera.WorldToViewportPoint(firstWorld);
                    Vector3 secondViewport = camera.WorldToViewportPoint(secondWorld);
                    Vector3 thirdViewport = camera.WorldToViewportPoint(thirdWorld);
                    if (firstViewport.z <= 0f || secondViewport.z <= 0f || thirdViewport.z <= 0f)
                    {
                        continue;
                    }

                    nearestDistance = Mathf.Min(nearestDistance, DistanceToTriangle(
                        guideViewport,
                        new Vector2(firstViewport.x, firstViewport.y),
                        new Vector2(secondViewport.x, secondViewport.y),
                        new Vector2(thirdViewport.x, thirdViewport.y)));
                }
            }

            return nearestDistance;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static float DistanceToTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            float crossOne = Cross(b - a, point - a);
            float crossTwo = Cross(c - b, point - b);
            float crossThree = Cross(a - c, point - c);
            if ((crossOne >= 0f && crossTwo >= 0f && crossThree >= 0f) ||
                (crossOne <= 0f && crossTwo <= 0f && crossThree <= 0f))
            {
                return 0f;
            }

            return Mathf.Min(
                DistanceToSegment(point, a, b),
                Mathf.Min(DistanceToSegment(point, b, c), DistanceToSegment(point, c, a)));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 edge = end - start;
            float lengthSquared = edge.sqrMagnitude;
            if (lengthSquared < 0.000001f)
            {
                return Vector2.Distance(point, start);
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - start, edge) / lengthSquared);
            return Vector2.Distance(point, start + edge * t);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static float Cross(Vector2 left, Vector2 right)
        {
            return left.x * right.y - left.y * right.x;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void CalibrateScannerArmDisplayOffset()
        {
            scannerArmDisplayOffset = Vector3.zero;
            if (scannerArmSnapshotRoot == null || ikTarget == null)
            {
                return;
            }

            // The draggable target is the printed palm guide, not the wrist.
            // The baked mesh is rooted at the wrist, and its palm can be far
            // forward of that origin.  Centering the whole mesh made the arm
            // initially appear beyond the scanner and made the usable plane
            // depend on forearm length.  Offset the root by the measured
            // palm landmark instead so the displayed palm always begins at
            // the scanner workspace target.
            Vector3 surfaceNormal = GetScannerSurfaceNormal();
            scannerArmDisplayOffset = -Vector3.ProjectOnPlane(scannerArmPalmOffset, surfaceNormal);
            scannerArmSnapshotRoot.transform.position += scannerArmDisplayOffset;
            ModLogger.Info($"[Fingerprint Scan] Calibrated visible palm to scanner target by {scannerArmDisplayOffset} on the scanner plane");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void SeatScannerArmOnPlaten()
        {
            scannerArmSurfaceOffset = Vector3.zero;
            if (scannerArmSnapshotRoot == null || scanTarget == null)
            {
                return;
            }

            Plane platen = new Plane(GetScannerSurfaceNormal(), scanTarget.position);
            float nearestDistance = float.PositiveInfinity;
            int sampledVertices = 0;
            foreach (MeshFilter filter in scannerArmSnapshotRoot.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }

                // The source snapshot retains the original vertex buffer but
                // rewrites its topology. Sample only vertices actually used
                // by the right-arm triangles, not the unused torso vertices.
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                var usedVertices = new HashSet<int>();
                for (int i = 0; i < triangles.Length; i++)
                {
                    int index = triangles[i];
                    if (index >= 0 && index < vertices.Length && usedVertices.Add(index))
                    {
                        float distance = platen.GetDistanceToPoint(filter.transform.TransformPoint(vertices[index]));
                        nearestDistance = Mathf.Min(nearestDistance, distance);
                        sampledVertices++;
                    }
                }
            }

            if (sampledVertices == 0 || float.IsInfinity(nearestDistance))
            {
                ModLogger.Warn("[Fingerprint Scan] Could not sample the rendered arm surface for platen seating");
                return;
            }

            const float palmClearance = 0.004f;
            scannerArmSurfaceOffset = GetScannerSurfaceNormal() * (palmClearance - nearestDistance);
            scannerArmSnapshotRoot.transform.position += scannerArmSurfaceOffset;
            ModLogger.Info($"[Fingerprint Scan] Seated rendered arm on platen: nearest={nearestDistance:F3}m, clearance={palmClearance:F3}m, offset={scannerArmSurfaceOffset}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void LogScannerArmSnapshotViewport()
        {
            if (scannerArmSnapshotRoot == null)
            {
                return;
            }

            MeshRenderer meshRenderer = scannerArmSnapshotRoot.GetComponentInChildren<MeshRenderer>(true);
            Camera activeCamera = PlayerSingleton<PlayerCamera>.Instance != null
                ? PlayerSingleton<PlayerCamera>.Instance.Camera
                : null;
            if (meshRenderer == null || activeCamera == null)
            {
                ModLogger.Warn($"[Fingerprint Scan] Snapshot render diagnostic unavailable: renderer={meshRenderer != null}, camera={activeCamera != null}");
                return;
            }

            Vector3 viewport = activeCamera.WorldToViewportPoint(meshRenderer.bounds.center);
            Vector3 targetViewport = scanTarget != null
                ? activeCamera.WorldToViewportPoint(scanTarget.position)
                : Vector3.zero;
            Vector3 palmWorldPosition = scannerArmSnapshotRoot.transform.position + scannerArmPalmOffset;
            Vector3 palmViewport = activeCamera.WorldToViewportPoint(palmWorldPosition);
            bool layerVisible = (activeCamera.cullingMask & (1 << scannerArmSnapshotRoot.layer)) != 0;
            string authoredCameraDiagnostic = string.Empty;
            if (interactionCamera != null)
            {
                float cameraPositionError = Vector3.Distance(activeCamera.transform.position, interactionCamera.transform.position);
                float cameraFacingAlignment = Vector3.Dot(activeCamera.transform.forward, interactionCamera.transform.forward);
                Vector3 authoredTargetViewport = interactionCamera.WorldToViewportPoint(scanTarget.position);
                authoredCameraDiagnostic = $", authored-target=({authoredTargetViewport.x:F2}, {authoredTargetViewport.y:F2}, {authoredTargetViewport.z:F2}), camera-error={cameraPositionError:F3}m/{cameraFacingAlignment:F4}";
            }

            ModLogger.Info($"[Fingerprint Scan] Snapshot render diagnostic: layer={scannerArmSnapshotRoot.layer}, camera-sees-layer={layerVisible}, enabled={meshRenderer.enabled}, bounds-viewport=({viewport.x:F2}, {viewport.y:F2}, {viewport.z:F2}), target-viewport=({targetViewport.x:F2}, {targetViewport.y:F2}, {targetViewport.z:F2}), palm-viewport=({palmViewport.x:F2}, {palmViewport.y:F2}, {palmViewport.z:F2}), bounds={meshRenderer.bounds.size}{authoredCameraDiagnostic}");
        }

        private void RestoreScannerArmSnapshot()
        {
            if (scannerArmSnapshotRoot != null)
            {
                UnityEngine.Object.Destroy(scannerArmSnapshotRoot);
            }

            foreach (Mesh mesh in scannerArmSnapshotMeshes)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.Destroy(mesh);
                }
            }

            scannerArmSnapshotMeshes.Clear();
            scannerArmSnapshotRoot = null;
            scannerArmArrivalActive = false;
            scannerArmDisplayOffset = Vector3.zero;
            scannerArmSurfaceOffset = Vector3.zero;
            scannerArmPalmOffset = Vector3.zero;
        }

        private Transform FindNamedTransform(Transform root, string token)
        {
            if (root == null) return null;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform candidate in transforms)
            {
                if (candidate == null)
                {
                    continue;
                }

                string candidateName = candidate.name;
                if (candidateName.Equals(token, System.StringComparison.OrdinalIgnoreCase) ||
                    candidateName.EndsWith(":" + token, System.StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private bool TryResolveAvatarRightArm(Player player, out Transform rightForearm, out Transform rightHand)
        {
            rightForearm = null;
            rightHand = null;
            if (player == null || player.Avatar == null)
            {
                return false;
            }

            // Humanoid bone slots are stable across the player's cosmetic rigs,
            // unlike their imported transform names. Prefer them before the
            // legacy name lookup so the selected transforms can be matched by
            // reference against each SkinnedMeshRenderer's bone array.
            Animator[] animators = player.Avatar.GetComponentsInChildren<Animator>(true);
            foreach (Animator animator in animators)
            {
                if (animator == null || !animator.isHuman)
                {
                    continue;
                }

                Transform humanoidForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                Transform humanoidHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (humanoidForearm != null && humanoidHand != null)
                {
                    rightForearm = humanoidForearm;
                    rightHand = humanoidHand;
                    ModLogger.Info($"[Fingerprint Scan] Resolved right arm via humanoid rig: forearm='{rightForearm.name}', hand='{rightHand.name}'");
                    return true;
                }
            }

            rightForearm = FindNamedTransform(player.Avatar.transform, "RightForeArm");
            rightHand = FindNamedTransform(player.Avatar.transform, "RightHand");
            if (rightForearm != null && rightHand != null)
            {
                ModLogger.Warn($"[Fingerprint Scan] Humanoid rig mapping was unavailable; using named bones: forearm='{rightForearm.name}', hand='{rightHand.name}'");
                return true;
            }

            return false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static HashSet<int> FindRightArmBoneIndices(Transform[] bones, Transform rightForearm, Transform rightHand)
        {
            var matches = new HashSet<int>();
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                // The renderer's bone array points into the same live rig.
                // Capture from the elbow down. Including the right shoulder
                // and upper arm made this wrist-anchored snapshot extend far
                // above and left of the scanner, and is not part of the
                // intended fingerprint interaction.
                if (bone == rightForearm || bone == rightHand ||
                    (rightForearm != null && bone.IsChildOf(rightForearm)) ||
                    IsRightArmCaptureBoneName(bone.name))
                {
                    matches.Add(i);
                }
            }

            return matches;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static bool IsRightArmCaptureBoneName(string boneName)
        {
            if (string.IsNullOrEmpty(boneName))
            {
                return false;
            }

            var characters = new List<char>(boneName.Length);
            foreach (char character in boneName)
            {
                if (char.IsLetterOrDigit(character))
                {
                    characters.Add(char.ToLowerInvariant(character));
                }
            }

            string normalized = new string(characters.ToArray());
            return normalized.Contains("rightforearm") || normalized.Contains("rightlowerarm") ||
                   normalized.Contains("forearmright") || normalized.Contains("lowerarmright") ||
                   normalized.Contains("rforearm") || normalized.Contains("rlowerarm") ||
                   normalized.Contains("righthand") || normalized.Contains("handright") ||
                   normalized.Contains("rhand");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void LogRendererBoneNames(SkinnedMeshRenderer source, Transform[] bones)
        {
            var names = new List<string>();
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null)
                {
                    names.Add($"{i}:{bones[i].name}");
                }
            }

            ModLogger.Warn($"[Fingerprint Scan] Candidate '{source.name}' exposed no matching right-arm bones. Bones: {string.Join(", ", names.ToArray())}");
        }

        private Vector3 GetPalmNormal(Transform rightHand)
        {
            Transform index = FindNamedTransform(rightHand, "RightHandIndex1");
            Transform little = FindNamedTransform(rightHand, "RightHandLittle1");
            Vector3 across = index != null && little != null ? index.position - little.position : rightHand.right;
            Vector3 along = index != null ? index.position - rightHand.position : rightHand.forward;
            Vector3 normal = Vector3.Cross(across, along).normalized;
            if (normal.sqrMagnitude < 0.001f) normal = rightHand.up;
            // InteractionCamera is authored on the scanner and therefore
            // stable. Do not use the live player camera here: its approach
            // angle changes before the override takes effect and can invert
            // the apparent palm side on a repeat attempt.
            if (interactionCamera != null && Vector3.Dot(normal, interactionCamera.transform.position - rightHand.position) < 0f)
            {
                normal = -normal;
            }
            return normal;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Vector3 GetScannerAwayDirection()
        {
            if (scannerScanFrameLocked)
            {
                return scannerScanAwayDirection;
            }

            return ResolveScannerAwayDirection();
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Vector3 ResolveScannerAwayDirection()
        {
            // The station camera and its target are static prefab data. This
            // supplies a consistent "away from player" direction regardless
            // of where or from which heading the player opened the station.
            Vector3 direction = interactionCamera != null && scanTarget != null
                ? scanTarget.position - interactionCamera.transform.position
                : transform.forward;
            direction = Vector3.ProjectOnPlane(direction, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            }

            return direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.forward;
        }

        private Vector3 GetScannerSurfaceNormal()
        {
            // The target is a position marker only; its axes are not aligned
            // to the glass. Establish a shallow physical plane which slopes
            // upward away from the player toward the scanner. This makes the
            // near edge lower, exactly like the scanner's visible platen.
            Vector3 awayFromPlayer = GetScannerAwayDirection();
            float slope = Mathf.Tan(ScannerPaneSlopeDegrees * Mathf.Deg2Rad);
            return (Vector3.up - awayFromPlayer * slope).normalized;
        }

        private Vector3 GetScannerSurfaceRight()
        {
            Vector3 right = Vector3.ProjectOnPlane(interactionCamera != null ? interactionCamera.transform.right : transform.right, GetScannerSurfaceNormal());
            return right.sqrMagnitude > 0.001f ? right.normalized : transform.right;
        }

        private Vector3 ProjectToScannerSurface(Vector3 position)
        {
            if (scanTarget == null) return position;
            Plane plane = new Plane(GetScannerSurfaceNormal(), scanTarget.position);
            return plane.ClosestPointOnPlane(position);
        }

        private void ShowNativeViewmodelHands()
        {
            if (!Singleton<ViewmodelAvatar>.InstanceExists)
            {
                ModLogger.Warn("[Fingerprint Scan] Native ViewmodelAvatar is not ready; the hand cannot be rendered yet");
                return;
            }

            handScanViewmodelAvatar = Singleton<ViewmodelAvatar>.Instance;
            if (handScanViewmodelAvatar == null)
            {
                ModLogger.Warn("[Fingerprint Scan] Native ViewmodelAvatar instance was unavailable");
                return;
            }

            handScanViewmodelWasVisible = handScanViewmodelAvatar.IsVisible;
            handScanViewmodelOriginalParent = handScanViewmodelAvatar.transform.parent;
            handScanViewmodelOriginalLocalPosition = handScanViewmodelAvatar.transform.localPosition;
            handScanViewmodelOriginalLocalRotation = handScanViewmodelAvatar.transform.localRotation;
            handScanViewmodelAvatar.SetVisibility(true);

            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (playerCamera != null && playerCamera.Camera != null)
            {
                // The native avatar already lives beneath the player's camera
                // hierarchy. Keep that authored parent and orientation intact:
                // moving or rotating the complete rig as though it were a
                // world-space hand is what produced the detached/glitching
                // limbs seen in the prior build.
                handScanViewmodelAvatar.SetOffset(Vector3.zero);
                if (!FilterNativeViewmodelToRightArm())
                {
                    // Never fall through to the unfiltered full-body native
                    // rig. A failed isolation must leave the station ready
                    // for a safe retry, not render both arms/legs.
                    handScanViewmodelAvatar.SetVisibility(handScanViewmodelWasVisible);
                    handScanViewmodelAvatar = null;
                    return;
                }

                handScanOriginalCameraCullingMask = playerCamera.Camera.cullingMask;
                handScanCameraCullingMaskCaptured = true;

                int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
                if (viewmodelLayer >= 0)
                {
                    playerCamera.Camera.cullingMask |= 1 << viewmodelLayer;
                    ModLogger.Info("[Fingerprint Scan] Enabled the native Viewmodel render layer on the scanner camera");
                }
                else
                {
                    ModLogger.Warn("[Fingerprint Scan] Viewmodel render layer was not defined");
                }
            }
            else
            {
                ModLogger.Warn("[Fingerprint Scan] Active player camera was unavailable; cannot attach native hand viewmodel");
            }

            string controllerName = handScanViewmodelAvatar.Animator != null &&
                handScanViewmodelAvatar.Animator.runtimeAnimatorController != null
                ? handScanViewmodelAvatar.Animator.runtimeAnimatorController.name
                : "none";
            ModLogger.Info($"[Fingerprint Scan] Showing native first-person hand viewmodel (controller={controllerName})");
        }

        private void RestoreNativeViewmodelHands()
        {
            if (handScanViewmodelAvatar != null)
            {
                RestoreNativeViewmodelMeshes();
                handScanViewmodelAvatar.SetVisibility(handScanViewmodelWasVisible);
                if (handScanViewmodelAvatar.transform.parent != handScanViewmodelOriginalParent)
                {
                    handScanViewmodelAvatar.transform.SetParent(handScanViewmodelOriginalParent, false);
                    handScanViewmodelAvatar.SetOffset(handScanViewmodelOriginalLocalPosition);
                    handScanViewmodelAvatar.transform.localRotation = handScanViewmodelOriginalLocalRotation;
                }
            }

            if (handScanCameraCullingMaskCaptured)
            {
                var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
                if (playerCamera != null && playerCamera.Camera != null)
                {
                    playerCamera.Camera.cullingMask = handScanOriginalCameraCullingMask;
                }
            }

            handScanViewmodelAvatar = null;
            handScanViewmodelWasVisible = false;
            handScanViewmodelOriginalParent = null;
            handScanCameraCullingMaskCaptured = false;
        }

        private bool FilterNativeViewmodelToRightArm()
        {
            RestoreNativeViewmodelMeshes();

            if (handScanViewmodelAvatar == null || handScanViewmodelAvatar.Avatar == null)
            {
                ModLogger.Warn("[Fingerprint Scan] Native viewmodel body meshes were unavailable; cannot isolate the right arm");
                return false;
            }

            int preparedMeshes = 0;
            // The current runtime does not populate Avatar.BodyMeshes on the
            // live viewmodel instance, despite the authored prefab containing
            // those references. Query its native hierarchy directly instead.
            // This includes the active LOD renderer rather than relying on
            // stale serialized references.
            SkinnedMeshRenderer[] nativeRenderers = handScanViewmodelAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (SkinnedMeshRenderer renderer in nativeRenderers)
            {
                if (renderer == null || renderer.sharedMesh == null)
                {
                    continue;
                }

                Mesh armOnlyMesh = CreateRightArmMesh(renderer);
                if (armOnlyMesh == null)
                {
                    continue;
                }

                handScanBodyRenderers.Add(renderer);
                handScanOriginalBodyMeshes.Add(renderer.sharedMesh);
                handScanRightArmMeshes.Add(armOnlyMesh);
                renderer.sharedMesh = armOnlyMesh;
                preparedMeshes++;
            }

            if (preparedMeshes == 0)
            {
                ModLogger.Error("[Fingerprint Scan] Could not derive a native right-arm mesh; scan cancelled rather than showing both arms");
                RestoreNativeViewmodelMeshes();
                return false;
            }

            ModLogger.Info($"[Fingerprint Scan] Isolated the native player right arm across {preparedMeshes} viewmodel LOD mesh(es)");
            return true;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Mesh CreateRightArmMesh(SkinnedMeshRenderer sourceRenderer)
        {
            var sourceMesh = sourceRenderer.sharedMesh;
            var bones = sourceRenderer.bones;
            if (sourceMesh == null || bones == null || bones.Length == 0)
            {
                return null;
            }

            var rightArmBoneIndices = new HashSet<int>();
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                string boneName = bone.name;
                if (boneName.IndexOf("RightShoulder", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    boneName.IndexOf("RightArm", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    boneName.IndexOf("RightForeArm", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    boneName.IndexOf("RightHand", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rightArmBoneIndices.Add(i);
                }
            }

            if (rightArmBoneIndices.Count == 0)
            {
                ModLogger.Warn($"[Fingerprint Scan] No right-arm bones found on native mesh '{sourceMesh.name}'");
                return null;
            }

            var weights = sourceMesh.boneWeights;
            var triangles = sourceMesh.triangles;
            if (weights == null || triangles == null || triangles.Length < 3)
            {
                return null;
            }

            var rightArmTriangles = new List<int>();
            for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
            {
                int rightArmVertices = 0;
                // Il2CppStructArray's indexer is not ref-returning. Copy the
                // three native values before passing them to the shared
                // selector; the live-player snapshot path retains direct ref
                // access to its managed reconstructed array.
                BoneWeight firstWeight = weights[triangles[triangle]];
                BoneWeight secondWeight = weights[triangles[triangle + 1]];
                BoneWeight thirdWeight = weights[triangles[triangle + 2]];
                if (VertexBelongsToRightArm(ref firstWeight, rightArmBoneIndices)) rightArmVertices++;
                if (VertexBelongsToRightArm(ref secondWeight, rightArmBoneIndices)) rightArmVertices++;
                if (VertexBelongsToRightArm(ref thirdWeight, rightArmBoneIndices)) rightArmVertices++;

                // Keeping triangles with two right-arm-influenced vertices
                // retains the native sleeve/shoulder boundary without leaking
                // the torso, legs, or left arm into the scanner view.
                if (rightArmVertices >= 2)
                {
                    rightArmTriangles.Add(triangles[triangle]);
                    rightArmTriangles.Add(triangles[triangle + 1]);
                    rightArmTriangles.Add(triangles[triangle + 2]);
                }
            }

            if (rightArmTriangles.Count == 0)
            {
                ModLogger.Warn($"[Fingerprint Scan] Native mesh '{sourceMesh.name}' had no right-arm triangles");
                return null;
            }

            Mesh armOnlyMesh = UnityEngine.Object.Instantiate(sourceMesh);
            armOnlyMesh.name = $"{sourceMesh.name}_BehindBars_RightArm";
            armOnlyMesh.triangles = rightArmTriangles.ToArray();
            armOnlyMesh.RecalculateBounds();
            return armOnlyMesh;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static bool VertexBelongsToRightArm(ref BoneWeight weight, HashSet<int> rightArmBoneIndices)
        {
            const float minInfluence = 0.05f;
#if MONO
            return (weight.weight0 >= minInfluence && rightArmBoneIndices.Contains(weight.boneIndex0)) ||
                   (weight.weight1 >= minInfluence && rightArmBoneIndices.Contains(weight.boneIndex1)) ||
                   (weight.weight2 >= minInfluence && rightArmBoneIndices.Contains(weight.boneIndex2)) ||
                   (weight.weight3 >= minInfluence && rightArmBoneIndices.Contains(weight.boneIndex3));
#else
            // BoneWeight's IL2CPP property getters invoke native methods for
            // each field. The source list has already provided the native
            // struct verbatim, so read its generated field layout directly.
            // This preserves the exact 19-30 right-arm index range confirmed
            // on the extracted 4,952-vertex player mesh.
            return (weight.m_Weight0 >= minInfluence && rightArmBoneIndices.Contains(weight.m_BoneIndex0)) ||
                   (weight.m_Weight1 >= minInfluence && rightArmBoneIndices.Contains(weight.m_BoneIndex1)) ||
                   (weight.m_Weight2 >= minInfluence && rightArmBoneIndices.Contains(weight.m_BoneIndex2)) ||
                   (weight.m_Weight3 >= minInfluence && rightArmBoneIndices.Contains(weight.m_BoneIndex3));
#endif
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void RestoreNativeViewmodelMeshes()
        {
            for (int i = 0; i < handScanBodyRenderers.Count; i++)
            {
                SkinnedMeshRenderer renderer = handScanBodyRenderers[i];
                if (renderer != null)
                {
                    renderer.sharedMesh = handScanOriginalBodyMeshes[i];
                }

                Mesh armOnlyMesh = handScanRightArmMeshes[i];
                if (armOnlyMesh != null)
                {
                    UnityEngine.Object.Destroy(armOnlyMesh);
                }
            }

            handScanBodyRenderers.Clear();
            handScanOriginalBodyMeshes.Clear();
            handScanRightArmMeshes.Clear();
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

            currentPlayer = Player.Local;
            if (currentPlayer != null)
            {
                playerCamera = currentPlayer.GetComponentInChildren<Camera>();
                handScanProcessCoroutine = MelonCoroutines.Start(StartScanProcess(currentPlayer)) as Coroutine;
            }
            else
            {
                ModLogger.Error("No local player found for scanner!");
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
            MelonCoroutines.Start(SimplifiedScanProcess());
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

            // Free mouse for dragging interaction and hide cursor
            playerCamera.FreeMouse();
            Singleton<HUD>.Instance.SetCrosshairVisible(false);
            Cursor.visible = false; // Hide cursor - palm will act as cursor

            // Show palm model and position it correctly for scanning
            if (palmModel != null)
            {
                palmModel.SetActive(true);
                
                // Position palm at scanner target location (or slightly above/forward)
                if (scanTarget != null)
                {
                    Vector3 startPos = scanTarget.position;
                    // Position slightly in front and above scanner for better visibility
                    Vector3 cameraForward = interactionCamera.transform.forward;
                    startPos += cameraForward * 0.1f; // Slightly in front
                    startPos.y += 0.05f; // Slightly above
                    
                    palmModel.transform.position = startPos;
                    originalPalmPosition = startPos; // Update original position to scanner location
                    ModLogger.Info($"Palm model activated at scanner position: {palmModel.transform.position}");
                }
                else
                {
                    palmModel.transform.position = originalPalmPosition;
                    ModLogger.Info($"Palm model activated: {palmModel.name} at {palmModel.transform.position}");
                }
            }

            ModLogger.Info("Camera locked to scanner view");

            // Register exit listener for escape key
#if !MONO
            GameInput.RegisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner, priority: 2);
#else
            GameInput.RegisterExitListener(OnExitPalmScanner, priority: 2);
#endif
        }

        /// <summary>
        /// Main scan process - simplified version that doesn't require hand movement (same as exit scanner)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator SimplifiedScanProcess()
        {
            ModLogger.Info("Starting scan animation for palm scanner");

            // Start scan animation (same as exit scanner)
            yield return MelonCoroutines.Start(StartScanAnimation(false));

            // Complete the scan
            CompletePalmScan();
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator StartScanAnimation(bool useSuccessHighlight)
        {
            ModLogger.Info($"Starting scan animation (successHighlight={useSuccessHighlight})");

            // Prefer the image resolved at startup. The booking scanner lives
            // under ScannerDisplay while the old simple fallback keeps Holder
            // directly beneath the station, so use either authoring shape.
            Image imgScanEffect = scanEffect;
            Transform canvasTransform = imgScanEffect != null ? imgScanEffect.transform.parent : null;
            if (canvasTransform == null)
            {
                var holder = transform.Find("Holder") ??
                             transform.parent?.Find("ScannerDisplay/Holder") ??
                             transform.parent?.Find("Holder");
                canvasTransform = holder?.Find("Canvas");
            }

            if (canvasTransform == null)
            {
                ModLogger.Error("Canvas not found for scan animation");
                yield break;
            }

            var canvas = canvasTransform.GetComponent<Canvas>();
            if (canvas == null)
            {
                ModLogger.Error("Canvas component not found on Canvas GameObject");
                yield break;
            }

            ModLogger.Info("Found Canvas at ScannerStation/Holder/Canvas/");

            // Find imgScanEffect, Start, and End GameObjects
            if (imgScanEffect == null)
            {
                imgScanEffect = canvas.transform.Find("imgScanEffect")?.GetComponent<Image>();
            }

            var startObj = canvas.transform.Find("Start");
            var endObj = canvas.transform.Find("End");

            if (imgScanEffect == null || startObj == null || endObj == null)
            {
                ModLogger.Error($"Missing animation components - imgScanEffect: {imgScanEffect != null}, Start: {startObj != null}, End: {endObj != null}");
                yield break;
            }

            RectTransform scanRect = imgScanEffect.GetComponent<RectTransform>();
            RectTransform startRect = startObj.GetComponent<RectTransform>();
            RectTransform endRect = endObj.GetComponent<RectTransform>();

            if (scanRect == null || startRect == null || endRect == null)
            {
                ModLogger.Error("Missing RectTransform components for animation");
                yield break;
            }

            // Get positions from Start and End GameObjects
            Vector2 startPos = startRect.anchoredPosition;
            Vector2 endPos = endRect.anchoredPosition;

            ModLogger.Info($"Animation positions - Start: {startPos}, End: {endPos}");

            // The authored fallback scan uses this exact moving image. Tint it
            // green only for an accepted real-hand scan, then put the shared
            // image back exactly as it was for subsequent attempts.
            Color originalColor = imgScanEffect.color;
            if (useSuccessHighlight)
            {
                imgScanEffect.color = new Color(0.21f, 1f, 0.38f, originalColor.a);
            }

            // Make sure scan image is visible.
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
            imgScanEffect.color = originalColor;

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
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
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
            
            // Restore cursor visibility
            Cursor.visible = true;

            // Hide palm model
            if (palmModel != null)
                palmModel.SetActive(false);

            // Deregister exit listener
#if !MONO
            GameInput.DeregisterExitListener((Il2CppScheduleOne.GameInput.ExitDelegate)OnExitPalmScanner);
#else
            GameInput.DeregisterExitListener(OnExitPalmScanner);
#endif

            // Update final state
            if (interactableObject != null)
            {
                if (bookingProcess != null && bookingProcess.fingerprintComplete)
                {
                    interactableObject.SetMessage("Palm scan complete");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Label);
                }
                else
                {
                    interactableObject.SetMessage("Scan fingerprints");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
            }

            ModLogger.Info("Camera view ended");
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

            // Show palm model
            if (palmModel != null)
            {
                palmModel.SetActive(true);
                palmModel.transform.position = originalPalmPosition;
                ModLogger.Info($"Palm model activated: {palmModel.name} at {palmModel.transform.position}");
            }

            // Show instructions
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
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
            if (!action.Used && action.Type == ExitType.Primary)
            {
                if (inScannerView)
                {
                    action.Use();
                    EndPalmScannerView();
                }
                else if (isScanning && !fingerprintSuccessPresentationActive)
                {
                    action.Use();
                    isScanning = false;
                }
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
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
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
            
            // Restore cursor visibility
            Cursor.visible = true;

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
            ResolveScannerReferencesForInteraction();
            isScanning = true;
            fingerprintSuccessPresentationActive = false;
            if (interactableObject != null)
            {
                interactableObject.SetMessage("Drag your hand to the scanner...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }

            ModLogger.Info("Starting fingerprint scan process");

            EnterHandScanInteraction();
            // The camera transition takes 0.15 seconds.  Do not build the
            // scanner-only mesh during that transition: it has no second
            // chance to re-anchor once the camera reaches the station view.
            yield return WaitForScannerCameraOverride();
            PositionHandTargetAtScanStart();

            // Pose the real avatar once, capture its right forearm/hand into
            // a static mesh, and immediately return the player animator to
            // normal. The scanner thus never renders a whole viewmodel body.
            if (!ApplyFingerprintPose(player))
            {
                ModLogger.Error("[Fingerprint Scan] Scanner pose asset was unavailable; scan cancelled without a placeholder");
                isScanning = false;
                ExitHandScanInteraction();
                if (interactableObject != null)
                {
                    interactableObject.SetMessage("Scan fingerprints");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                yield break;
            }

            // The first use can be the frame in which the override controller
            // and animation clip become live. Allow that pose to settle before
            // baking so entry one has the same right-arm pose as retries.
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForFixedUpdate();
            bool snapshotReady = CreateScannerArmSnapshot(player);
            RestoreFingerprintPose();
            if (!snapshotReady)
            {
                ModLogger.Error("[Fingerprint Scan] Could not bake the posed player right arm; scan cancelled without a placeholder");
                isScanning = false;
                ExitHandScanInteraction();
                if (interactableObject != null)
                {
                    interactableObject.SetMessage("Scan fingerprints");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
                yield break;
            }

            yield return AnimateScannerArmArrival(0.32f);
            LogScannerArmSnapshotViewport();
            ShowScannerStatusIndicator();
            SetScannerStatusIndicator(false);

            // Show instruction
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification("Click and drag to move your hand to the scanner", NotificationType.Instruction);
            }

            // Start the scan timer
            scanCoroutine = MelonCoroutines.Start(ScanTimer()) as Coroutine;

            // Handle dragging
            while (isScanning && (bookingProcess == null || !bookingProcess.fingerprintComplete))
            {
                // Freeze the accepted hand during the success sweep. Without
                // this guard, mouse movement can pull the snapshot away while
                // the positive animation/text is deliberately being held on
                // screen for the player to read.
                if (!fingerprintSuccessPresentationActive)
                {
                    HandleMouseDrag();
                    UpdateScannerArmSnapshotPosition();
                }
                yield return null;
            }

            // Clean up
            ExitHandScanInteraction();

            if (scanCoroutine != null)
            {
                MelonCoroutines.Stop(scanCoroutine);
                scanCoroutine = null;
            }

            // Reset interaction state
            isScanning = false;
            handScanProcessCoroutine = null;

            // Scene teardown can clear the booking object after the loop
            // exits but before this coroutine resumes. Treat that as an
            // interrupted scan and restore the normal interaction state;
            // never dereference a stale IL2CPP component here.
            if (bookingProcess != null && bookingProcess.fingerprintComplete)
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

        private bool SetupHandIK(Player player)
        {
            try
            {
                ModLogger.Info("[Fingerprint Scan] Binding the real player-avatar right hand to scanner IK target");

                // Use Player.Local to get the local player properly
                var localPlayer = Player.Local;
                if (localPlayer == null)
                {
                    ModLogger.Error("No local player found");
                    return false;
                }

                // Schedule I's current runtime owns the native hand solver on
                // the player body avatar, not the first-person viewmodel.
                // The scanner camera therefore renders the locally-visible
                // body hand while this controller drives it to the target.
                ikController = FindPlayerBodyHandIK(localPlayer.transform);

                if (ikController == null)
                {
                    ModLogger.Error("[Fingerprint Scan] No player-avatar hand IK controller found; scan cancelled without a placeholder");
                    return false;
                }

                if (ikController != null && ikController.BodyIK != null)
                {
                    ModLogger.Info($"Using IK controller: {ikController.name} with BodyIK. Enabled: {ikController.BodyIK.enabled}");
                    ModLogger.Info($"Right hand solver exists: {ikController.BodyIK.solvers.rightHand != null}");

                    // Check if the right hand solver exists and is valid
                    if (ikController.BodyIK.solvers.rightHand == null)
                    {
                        ModLogger.Error("Right hand solver is null - cannot set up IK");
                        return false;
                    }

                    // Store original right hand target (may be null)
                    originalRightHandTarget = ikController.BodyIK.solvers.rightHand.target;
                    originalRightHandRotationWeight = ikController.BodyIK.solvers.rightHand.IKRotationWeight;
                    originalRightHandRotation = ikController.BodyIK.solvers.rightHand.IKRotation;
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
                        ikController.BodyIK.solvers.rightHand.IKRotation = ikTarget.rotation;
                        ikController.BodyIK.solvers.rightHand.IKRotationWeight = 1f;
                        ikController.BodyIK.solvers.rightHand.SetBendPlaneToCurrent();
                        ModLogger.Info("IK weight set successfully");

                        ModLogger.Info($"Set IK target to: {ikTarget.position}, Weight: {ikController.BodyIK.solvers.rightHand.IKPositionWeight}");

                        ikActive = true;

                        ModLogger.Info($"IK activated. BodyIK enabled: {ikController.BodyIK.enabled}");
                        ModLogger.Info($"IK setup successful - right hand targeting IkTarget at {ikTarget.position}");
                        return true;
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

            return false;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private AvatarIKController FindPlayerBodyHandIK(Transform playerRoot)
        {
            var candidates = playerRoot.GetComponentsInChildren<AvatarIKController>(true);

            // Prefer the explicit body container when the hierarchy exposes
            // one.  It is the same native solver used by the player avatar,
            // rather than a fabricated scanner visual.
            foreach (var candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                string path = GetTransformPath(candidate.transform);
                if (path.IndexOf("BodyContainer", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ModLogger.Info($"[Fingerprint Scan] Using real player body-hand IK at {path}");
                    return candidate;
                }
            }

            // Keep the selection resilient to hierarchy-name changes while
            // still requiring a native right-hand solver from the player.
            foreach (var candidate in candidates)
            {
                if (candidate != null && candidate.BodyIK != null &&
                    candidate.BodyIK.solvers.rightHand != null)
                {
                    ModLogger.Info($"[Fingerprint Scan] Using player hand IK at {GetTransformPath(candidate.transform)}");
                    return candidate;
                }
            }

            return null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void LogLiveHandPose()
        {
            if (ikTarget == null || handScanViewmodelAvatar == null)
            {
                return;
            }

            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (playerCamera == null || playerCamera.Camera == null)
            {
                return;
            }

            Vector3 viewportTarget = playerCamera.Camera.WorldToViewportPoint(ikTarget.position);
            Transform rightHand = handScanViewmodelAvatar.RightHandContainer;
            Vector3 viewportHand = rightHand != null
                ? playerCamera.Camera.WorldToViewportPoint(rightHand.position)
                : Vector3.zero;
            ModLogger.Info($"[Fingerprint Scan] Native right hand viewport=({viewportHand.x:F2}, {viewportHand.y:F2}, {viewportHand.z:F2}); scanner target viewport=({viewportTarget.x:F2}, {viewportTarget.y:F2}, {viewportTarget.z:F2})");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private void UpdateNativeViewmodelHandOffset()
        {
            if (ikTarget == null || handScanViewmodelAvatar == null)
            {
                return;
            }

            var playerCamera = PlayerSingleton<PlayerCamera>.Instance;
            if (playerCamera == null || playerCamera.Camera == null)
            {
                return;
            }

            Vector3 viewportTarget = playerCamera.Camera.WorldToViewportPoint(ikTarget.position);
            if (viewportTarget.z <= 0f)
            {
                ModLogger.Warn("[Fingerprint Scan] Scanner target fell behind the active camera; keeping the native hand at its default viewmodel pose");
                return;
            }

            Transform rightHand = handScanViewmodelAvatar.RightHandContainer;
            if (rightHand == null)
            {
                ModLogger.Warn("[Fingerprint Scan] Native first-person right-hand container was unavailable");
                return;
            }

            Vector3 viewportHand = playerCamera.Camera.WorldToViewportPoint(rightHand.position);
            if (viewportHand.z <= 0f)
            {
                ModLogger.Warn("[Fingerprint Scan] Native first-person hand was behind the active camera");
                return;
            }

            // The viewmodel is authored at the near camera plane (normally
            // about 3 cm away), which is behind this scanner camera's near
            // clip range. Bring it forward in *camera-local* space and align
            // only its screen position. Never rotate the complete avatar rig
            // toward a world-space target: it is a dual-arm skinned character,
            // not a world-space hand transform.
            const float scannerHandDepth = 0.45f;
            Vector3 desiredHandPosition = playerCamera.Camera.ViewportToWorldPoint(
                new Vector3(viewportTarget.x, viewportTarget.y, scannerHandDepth));
            Vector3 cameraLocalCorrection = playerCamera.Camera.transform.InverseTransformVector(
                desiredHandPosition - rightHand.position);

            const float maxCameraLocalCorrection = 0.75f;
            if (cameraLocalCorrection.sqrMagnitude > maxCameraLocalCorrection * maxCameraLocalCorrection)
            {
                cameraLocalCorrection = cameraLocalCorrection.normalized * maxCameraLocalCorrection;
            }

            handScanViewmodelAvatar.SetOffset(handScanViewmodelAvatar.transform.localPosition + cameraLocalCorrection);

            if (Time.frameCount % 30 == 0)
            {
                ModLogger.Debug($"[Fingerprint Scan] Aligned native right hand from viewport ({viewportHand.x:F2}, {viewportHand.y:F2}, {viewportHand.z:F2}) to ({viewportTarget.x:F2}, {viewportTarget.y:F2}, {scannerHandDepth:F2})");
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
                    ikController.BodyIK.solvers.rightHand.IKRotation = originalRightHandRotation;
                    ikController.BodyIK.solvers.rightHand.IKRotationWeight = originalRightHandRotationWeight;

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

        private bool HandleMouseDrag()
        {
            // The free cursor drives the live-hand IK target on the scanner plane.
            bool mouseButtonDown = Input.GetMouseButtonDown(0);
            bool mouseButtonHeld = Input.GetMouseButton(0);
            bool mouseButtonUp = Input.GetMouseButtonUp(0);

            if (mouseButtonDown)
            {
                isDragging = true;
                mouseStartPos = Input.mousePosition;
                dragStartWorldPos = ikTarget.position;
            }
            else if (mouseButtonHeld && !isDragging)
            {
                isDragging = true;
                mouseStartPos = Input.mousePosition;
                dragStartWorldPos = ikTarget.position;
            }

            if (isDragging)
            {
                if (mouseButtonHeld)
                {
                    Vector3 mousePos = Input.mousePosition;
                    Vector3 mouseOffset = mousePos - mouseStartPos;
                    Camera dragCamera = interactionCamera != null
                        ? interactionCamera
                        : PlayerSingleton<PlayerCamera>.Instance != null
                            ? PlayerSingleton<PlayerCamera>.Instance.Camera
                            : null;
                    Vector3 surfaceNormal = GetScannerSurfaceNormal();
                    Vector3 dragRight = dragCamera != null
                        ? Vector3.ProjectOnPlane(dragCamera.transform.right, surfaceNormal).normalized
                        : GetScannerSurfaceRight();
                    Vector3 dragForward = dragCamera != null
                        ? Vector3.ProjectOnPlane(dragCamera.transform.up, surfaceNormal).normalized
                        : Vector3.Cross(surfaceNormal, dragRight).normalized;
                    Vector3 worldOffset = (dragRight * mouseOffset.x + dragForward * mouseOffset.y) * dragSensitivity;

                    // The scanner is a two-dimensional surface interaction:
                    // never let pointer input move the hand toward/away from
                    // the scanner or change its height above the platen.
                    Vector3 newPos = ProjectToScannerSurface(dragStartWorldPos + worldOffset);

                    // Use an asymmetric scanner-plane workspace instead of a
                    // circular clamp. The right hand starts to the right of
                    // the guide, has limited left travel, and cannot be
                    // dragged far enough fore/aft to expose the arm interior.
                    newPos = ClampHandTargetToScannerWorkspace(newPos);

                    // Update IK target position
                    Vector3 oldPos = ikTarget.position;
                    ikTarget.position = newPos;

                    if (Vector3.Distance(oldPos, newPos) > 0.001f && Time.frameCount % 30 == 0)
                    {
                        ModLogger.Debug($"[Fingerprint Scan] Live hand target moved to {ikTarget.position}");
                    }

                    return Vector3.Distance(oldPos, newPos) > 0.0001f;
                }
                else if (mouseButtonUp)
                {
                    isDragging = false;
                }
            }

            return false;
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
                // Validate the displayed palm anchor, not the hidden wrist
                // target. The root target exists solely to drive dragging;
                // its position is deliberately offset from the palm mesh.
                if (ikTarget != null && scanTarget != null)
                {
                    bool isAligned = IsFingerprintAlignmentValid(
                        out float distance,
                        out float validDistance,
                        out string measurement,
                        out Vector3 visiblePalmPosition);
                    SetScannerStatusIndicator(isAligned);

                    if (isAligned)
                    {
                        if (!scanStarted)
                        {
                            // Start scanning
                            scanStarted = true;
                            ModLogger.Info($"[Fingerprint Scan] Visible palm in position - starting scan ({measurement}, distance={distance:F3}, range={validDistance:F3}, palm={visiblePalmPosition})");

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
                            if (Core.ResolveUIManager() != null)
                            {
                                Core.ResolveUIManager().ShowNotification("Scanning... Hold still!", NotificationType.Progress);
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
                            ModLogger.Info($"[Fingerprint Scan] Visible palm moved out of position - scan paused ({measurement}, distance={distance:F3}, range={validDistance:F3}, palm={visiblePalmPosition})");

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
                            if (Core.ResolveUIManager() != null)
                            {
                                Core.ResolveUIManager().ShowNotification("Move hand back to scanner!", NotificationType.Warning);
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
                bool finalAlignment = IsFingerprintAlignmentValid(
                    out float finalDistance,
                    out float validDistance,
                    out string finalMeasurement,
                    out Vector3 finalPalmPosition);
                if (finalAlignment)
                {
                    // Success!
                    ModLogger.Info($"[Fingerprint Scan] Visible palm accepted ({finalMeasurement}, distance={finalDistance:F3}, range={validDistance:F3}, palm={finalPalmPosition})");
                    fingerprintSuccessPresentationActive = true;
                    SetFingerprintSuccessIndicator();
                    // Match the proven simple-scanner presentation now that
                    // the live-hand scan has earned success. Keeping the
                    // scanner view active here makes both the green sweep and
                    // the completion text readable before cleanup restores
                    // normal player control.
                    yield return StartScanAnimation(true);
                    fingerprintSuccessPresentationActive = false;
                    CompleteScan();
                }
                else
                {
                    // Failed - hand not in position
                    ModLogger.Info($"[Fingerprint Scan] Visible palm missed final alignment ({finalMeasurement}, distance={finalDistance:F3}, range={validDistance:F3}, palm={finalPalmPosition})");
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
            fingerprintSuccessPresentationActive = false;

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
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification("Fingerprint scan complete!", NotificationType.Progress);
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
            fingerprintSuccessPresentationActive = false;

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
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification("Scan failed - try again!", NotificationType.Warning);
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

            // Palm scanner no longer requires dragging - it just plays animation and completes
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
                    // URP no longer guarantees the legacy Unlit/Color shader is available.
                    var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                    if (shader == null)
                    {
                        ModLogger.Warn("IkTarget visualizer skipped because no compatible debug shader is available");
                        UnityEngine.Object.Destroy(ikTargetVisualizer);
                        ikTargetVisualizer = null;
                        ikTargetRenderer = null;
                        return;
                    }

                    var material = new Material(shader);
                    material.color = Color.red;
                    ikTargetRenderer.material = material;
                    ModLogger.Debug($"Applied debug material '{shader.name}' to IkTarget visualizer");
                }

                // Start hidden
                ikTargetVisualizer.SetActive(false);

                ModLogger.Debug($"IkTarget visualizer created at world position: {ikTargetVisualizer.transform.position}");
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

#if !MONO
        [HideFromIl2Cpp]
#endif
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
                            ModLogger.Debug($"Found existing MockHand for palm scanner: {palmModel.name}");
                        }
                    }
                }
            }

            // Store original position for reset
            if (palmModel != null)
            {
                originalPalmPosition = palmModel.transform.position;
                palmModel.SetActive(false); // Hide initially
                ModLogger.Debug($"Palm model setup complete: {palmModel.name} at position {palmModel.transform.position}");
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

                // Current player hierarchies are not guaranteed to expose
                // CameraContainer at the scene root. Prefer the live local
                // player when the root lookup did not find it.
                if (punchContainer == null)
                {
                    var localPlayer = currentPlayer ?? Player.Local;
                    var playerPunchController = localPlayer != null
                        ? localPlayer.transform.Find("CameraContainer/PunchController")
                        : null;
                    if (playerPunchController != null)
                    {
                        punchContainer = playerPunchController.gameObject;
                    }
                }

                if (punchContainer != null)
                {
                    ModLogger.Info($"Found PunchContainer: {punchContainer.name}");
                }
            }
        }

        private void HandlePalmDragging()
        {
            if (palmModel == null || interactionCamera == null) return;

            // TODO: Palm scanner plane positioning needs refinement
            // The palm should follow the cursor on the correct scanning plane for proper interaction.
            // Current implementation may need adjustments to:
            // - Better align with scanner surface orientation
            // - Handle different camera angles
            // - Improve depth calculation accuracy

            // Always update palm position to follow cursor directly (no drag requirement)
            Vector3 mousePos = Input.mousePosition;
            
            // Get scanner target position for reference
            Vector3 scannerWorldPos = scanTarget != null ? scanTarget.position : originalPalmPosition;
            
            // Calculate the distance from camera to scanner target
            float cameraToScannerDistance = Vector3.Distance(interactionCamera.transform.position, scannerWorldPos);
            
            // Convert mouse position to world position at the scanner's depth
            // Use ScreenToWorldPoint with the calculated distance
            Vector3 screenPointWithDepth = new Vector3(mousePos.x, mousePos.y, cameraToScannerDistance);
            Vector3 worldPos = interactionCamera.ScreenToWorldPoint(screenPointWithDepth);
            
            // Project onto a plane at the scanner target's position to keep it aligned with scanner surface
            // Calculate plane normal (perpendicular to camera forward, aligned with scanner surface)
            Vector3 cameraForward = interactionCamera.transform.forward;
            
            // Create plane at scanner position, perpendicular to camera view
            Plane scannerPlane = new Plane(cameraForward, scannerWorldPos);
            
            // Project the world position onto this plane
            Vector3 projectedPos = worldPos;
            float distanceToPlane = scannerPlane.GetDistanceToPoint(projectedPos);
            projectedPos -= cameraForward * distanceToPlane;
            
            // Constrain to max drag distance from scanner center
            Vector3 fromScanner = projectedPos - scannerWorldPos;
            if (fromScanner.magnitude > maxDragDistance)
            {
                fromScanner = fromScanner.normalized * maxDragDistance;
                projectedPos = scannerWorldPos + fromScanner;
            }
            
            // Ensure palm stays at proper scanning height (slightly above scanner surface)
            if (scanTarget != null)
            {
                projectedPos.y = scanTarget.position.y + 0.05f; // Slightly above scanner for visibility
            }
            
            // Update palm position to follow cursor
            palmModel.transform.position = projectedPos;
        }

        private void CompletePalmScan()
        {
            ModLogger.Info("Palm scan completed successfully!");

            // Play success sound
            if (scannerAudio != null && successSound != null)
            {
                scannerAudio.clip = successSound;
                scannerAudio.loop = false;
                scannerAudio.Play();
            }

            // Mark as complete in booking process
            if (bookingProcess != null)
            {
                bookingProcess.SetFingerprintComplete("PALM_SCAN_" + System.DateTime.Now.Ticks);
            }

            // Show success notification
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    "Scan complete!",
                    NotificationType.Progress
                );
            }

            // Auto-exit scanner view after short delay
            MelonCoroutines.Start(DelayedExitScannerView());
        }
        
        private void FailPalmScan()
        {
            ModLogger.Info("Palm scan failed - time expired or palm not in position");

            // Play error sound
            if (scannerAudio != null && errorSound != null)
            {
                scannerAudio.clip = errorSound;
                scannerAudio.loop = false;
                scannerAudio.Play();
            }

            // Show failure notification
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    "Scan failed - try again!",
                    NotificationType.Warning
                );
            }

            // Exit scanner view
            EndCameraView();
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator DelayedExitScannerView()
        {
            yield return new WaitForSeconds(2f);
            EndCameraView();
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
                        ModLogger.Debug("imgScanEffect hidden initially");
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
