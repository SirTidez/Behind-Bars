using UnityEngine;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Compatibility-only component retained for scenes or older callers that still
    /// reference the former door-trigger type. It does not open, close, or poll a door.
    /// Current door ownership remains with <see cref="SecurityDoorBehavior"/> and the
    /// canonical NPC state machines; attaching this component alone has no runtime effect.
    /// </summary>
    public class DoorTriggerHandler : MonoBehaviour
    {
#if !MONO
        /// <summary>
        /// IL2CPP pointer constructor required for the injected compatibility component; it
        /// does not initialize or operate a door.
        /// </summary>
        public DoorTriggerHandler(System.IntPtr ptr) : base(ptr) { }
#endif

        /// <summary>
        /// Legacy scene reference retained for serialization compatibility. No code in this
        /// stub currently consumes it to perform a door operation.
        /// </summary>
        public JailDoor associatedDoor;

        /// <summary>
        /// Legacy configuration flag retained for serialized compatibility. Automatic door
        /// detection is not implemented by this compatibility stub.
        /// </summary>
        public bool autoDetectDoor = true;
    }
}
