using UnityEngine;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Lives on the authored exit trigger and forwards only trigger entry from the
    /// current released player to its scanner station. It intentionally owns no
    /// delegate/event surface so it is safe to inject on IL2CPP.
    /// </summary>
    public sealed class ExitReleaseTriggerRelay : MonoBehaviour
    {
#if !MONO
        public ExitReleaseTriggerRelay(System.IntPtr ptr) : base(ptr) { }
#endif

        private ExitScannerStation owner;

#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void Configure(ExitScannerStation scanner)
        {
            owner = scanner;
        }

        private void OnTriggerEnter(Collider other)
        {
            owner?.HandleExitTriggerEntered(other);
        }

        private void OnTriggerStay(Collider other)
        {
            // The scanner can finish while the player is already overlapping the
            // authored trigger.  Stay coverage keeps that legitimate exit from being
            // lost between scan completion and the first physics enter callback.
            owner?.HandleExitTriggerEntered(other);
        }

        private void OnDestroy()
        {
            owner = null;
        }
    }
}
