using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Simple exit door that rotates open/closed with a time-based Z-axis lerp animation.
    /// The logical state is updated only when the coroutine reaches its target; open/close requests received while a
    /// request is animating are ignored, and the component does not retain a coroutine handle for cancellation.
    /// </summary>
    public class SimpleExitDoor : MonoBehaviour
    {
#if !MONO
        public SimpleExitDoor(System.IntPtr ptr) : base(ptr) { }
#else

        [Header("Door Settings")]
#endif
        // Target Z Euler angles, in degrees, used by OpenDoor and CloseDoor.
        public float openRotation = 70f;
        public float closedRotation = 0f;

        // Rotation-speed scalar used in duration = angle delta / (animationSpeed * 90). It must be positive; no guard is
        // applied for zero or negative values, which can leave a non-zero animation running indefinitely or complete at once.
        public float animationSpeed = 2f;

        // isOpen describes the last animation or direct-set operation that completed logically; it is not updated at the
        // start of an animation. isAnimating gates new open/close requests but is not a reference to the running coroutine.
        private bool isOpen = false;
        private bool isAnimating = false;

        /// <summary>
        /// Places the door at the configured closed angle at startup. This does not run an animation and leaves the logical
        /// fields at their declaration defaults until a later operation updates them.
        /// </summary>
        void Start()
        {
            // Ensure door starts closed
            transform.rotation = Quaternion.Euler(0, 0, closedRotation);
            ModLogger.Info($"SimpleExitDoor initialized - starting at rotation Z:{closedRotation}");
        }

        /// <summary>
        /// Starts an animation toward <see cref="openRotation"/> when the door is logically closed and idle.
        /// Requests are ignored when the door is already logically open or when any animation is marked in progress; an
        /// in-progress animation cannot be reversed through this method.
        /// </summary>
        public void OpenDoor()
        {
            if (isOpen || isAnimating)
            {
                ModLogger.Debug("Door already open or animating - ignoring open request");
                return;
            }

            ModLogger.Info($"Opening exit door - rotating to Z:{openRotation}");
            StartDoorAnimation(openRotation, true);
        }

        /// <summary>
        /// Starts an animation toward <see cref="closedRotation"/> when the door is logically open and idle.
        /// Requests are ignored when the door is already logically closed or when any animation is marked in progress; an
        /// in-progress animation cannot be reversed through this method.
        /// </summary>
        public void CloseDoor()
        {
            if (!isOpen || isAnimating)
            {
                ModLogger.Debug("Door already closed or animating - ignoring close request");
                return;
            }

            ModLogger.Info($"Closing exit door - rotating to Z:{closedRotation}");
            StartDoorAnimation(closedRotation, false);
        }

        /// <summary>
        /// Marks the door as animating and starts the external Melon coroutine that will move it toward the target.
        /// No coroutine handle is retained, so later state changes do not cancel this scheduled routine.
        /// </summary>
        private void StartDoorAnimation(float targetRotation, bool willBeOpen)
        {
            isAnimating = true;
            MelonCoroutines.Start(AnimateDoor(targetRotation, willBeOpen));
        }

        /// <summary>
        /// Advances the door toward a target Z Euler angle using SmoothStep over a duration derived from the raw angle
        /// difference, then writes the exact target and completion state. The coroutine has no cancellation check, so a
        /// previously started instance may still finish after another method changes the transform.
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator AnimateDoor(float targetRotation, bool willBeOpen)
        {
            Vector3 startRotation = transform.eulerAngles;
            Vector3 targetEuler = new Vector3(0, 0, targetRotation);

            float elapsed = 0f;
            // Duration is based on the raw Euler-angle delta; no angle wrapping or animationSpeed validation is applied.
            float duration = Mathf.Abs(targetRotation - startRotation.z) / (animationSpeed * 90f); // Normalize speed

            ModLogger.Info($"Door animation started - from Z:{startRotation.z} to Z:{targetRotation} over {duration:F2}s");

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / duration;

                // Smooth lerp with easing. Time.deltaTime is Unity's scaled frame delta.
                float easedProgress = Mathf.SmoothStep(0f, 1f, progress);
                Vector3 currentRotation = Vector3.Lerp(startRotation, targetEuler, easedProgress);

                transform.rotation = Quaternion.Euler(currentRotation);

                yield return null;
            }

            // Ensure exact final rotation
            transform.rotation = Quaternion.Euler(targetEuler);
            isOpen = willBeOpen;
            isAnimating = false;

            string state = willBeOpen ? "OPEN" : "CLOSED";
            ModLogger.Info($"Door animation complete - door is now {state} at Z:{targetRotation}");
        }

        /// <summary>
        /// Returns the last completed logical open/closed state. During animation this remains the state from before the
        /// animation began, even though the transform is already moving.
        /// </summary>
        public bool IsOpen()
        {
            return isOpen;
        }

        /// <summary>
        /// Returns whether the component has marked an animation in progress. This flag does not prove that the external
        /// coroutine is still running, because no coroutine handle is retained.
        /// </summary>
        public bool IsAnimating()
        {
            return isAnimating;
        }

        /// <summary>
        /// Immediately writes a Z rotation and logical state without starting a new animation.
        /// This clears the flag used to gate requests but does not stop an already-started external coroutine; that coroutine
        /// can subsequently overwrite the transform and logical state when it completes.
        /// </summary>
        public void SetDoorRotation(float zRotation, bool isOpenState)
        {
            isAnimating = false;
            transform.rotation = Quaternion.Euler(0, 0, zRotation);
            isOpen = isOpenState;

            ModLogger.Info($"Door rotation set to Z:{zRotation}, state: {(isOpenState ? "OPEN" : "CLOSED")}");
        }
    }
}
