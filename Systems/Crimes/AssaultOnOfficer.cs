using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    /// <summary>
    /// A persistent Behind Bars charge for assaulting a law-enforcement officer.
    /// </summary>
    [Serializable]
    public class AssaultOnOfficer : Crime
    {
        public override string CrimeName
        {
            get;
            set;
        } = "Assault on an LEO";
    }
}
