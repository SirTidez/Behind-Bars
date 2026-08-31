using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    /// <summary>
    /// Crime type used for a non-law-enforcement NPC assault.
    /// </summary>
    [Serializable]
    public class AssaultOnCivilian : Crime
    {
        /// <summary>Gets or sets the native display label for this charge.</summary>
        public override string CrimeName
        {
            get;
            set;
        } = "Assault on Civilian";
    }
}
