using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    /// <summary>
    /// Crime type used for an unintentional NPC death.
    /// </summary>
    [Serializable]
    public class Manslaughter : Crime
    {
        /// <summary>Gets or sets the native display label for this charge.</summary>
        public override string CrimeName
        {
            get;
            set;
        } = "Involuntary Manslaughter";
    }
}
