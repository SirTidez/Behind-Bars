using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    [Serializable]
    public class Manslaughter : Crime
    {
        public override string CrimeName { get; set; } = "Involuntary Manslaughter";
    }
}