using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    [Serializable]
    public class AssaultOnCivilian : Crime
    {
        public override string CrimeName { get; set; } = "Assault on Civilian";
    }
}