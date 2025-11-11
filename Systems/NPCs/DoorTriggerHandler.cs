using UnityEngine;

namespace Behind_Bars.Systems.NPCs
{
    // Temporary stub to fix compilation errors
    public class DoorTriggerHandler : MonoBehaviour
    {
#if !MONO
        public DoorTriggerHandler(System.IntPtr ptr) : base(ptr) { }
#endif
        public JailDoor associatedDoor;
        public bool autoDetectDoor = true;
    }
}