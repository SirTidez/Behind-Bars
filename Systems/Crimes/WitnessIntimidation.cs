using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    /// <summary>
    /// Crime type used when a player attacks or pressures a crime witness.
    /// </summary>
    [Serializable]
    public class WitnessIntimidation : Crime
    {
        /// <summary>Gets or sets the native display label for this charge.</summary>
        public override string CrimeName
        {
            get;
            set;
        } = "Witness Intimidation";
    }
}
