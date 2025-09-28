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
    /// Simple exit door that rotates open/closed with lerp animation
    /// </summary>
    public class SimpleExitDoor : MonoBehaviour
    {
#if !MONO
        public SimpleExitDoor(System.IntPtr ptr) : base(ptr) { }
#endif

        [Header("Door Settings")]
        public float openRotation = 70f;    // Z rotation when open
        public float closedRotation = 0f;   // Z rotation when closed
        public float animationSpeed = 2f;   // Speed of rotation animation

        private bool isOpen = false;
        private bool isAnimating = false;

        void Start()
        {
            // Ensure door starts closed
            transform.rotation = Quaternion.Euler(0, 0, closedRotation);
            ModLogger.Info($"SimpleExitDoor initialized - starting at rotation Z:{closedRotation}");
        }

        /// <summary>
        /// Open the door by rotating to openRotation
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
        /// Close the door by rotating to closedRotation
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
        /// Start door rotation animation
        /// </summary>
        private void StartDoorAnimation(float targetRotation, bool willBeOpen)
        {
            isAnimating = true;
            MelonCoroutines.Start(AnimateDoor(targetRotation, willBeOpen));
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator AnimateDoor(float targetRotation, bool willBeOpen)
        {
            Vector3 startRotation = transform.eulerAngles;
            Vector3 targetEuler = new Vector3(0, 0, targetRotation);

            float elapsed = 0f;
            float duration = Mathf.Abs(targetRotation - startRotation.z) / (animationSpeed * 90f); // Normalize speed

            ModLogger.Info($"Door animation started - from Z:{startRotation.z} to Z:{targetRotation} over {duration:F2}s");

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / duration;

                // Smooth lerp with easing
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
        /// Check if door is currently open
        /// </summary>
        public bool IsOpen()
        {
            return isOpen;
        }

        /// <summary>
        /// Check if door is currently animating
        /// </summary>
        public bool IsAnimating()
        {
            return isAnimating;
        }

        /// <summary>
        /// Force door to specific rotation without animation
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