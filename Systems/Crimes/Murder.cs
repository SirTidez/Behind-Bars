using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    [Serializable]
    public class Murder : Crime
    {
        public override string CrimeName { get; set; } = "Murder";
        
        public string VictimType { get; set; } = "Civilian";
        
        public Murder() { }
        
        public Murder(string victimType)
        {
            VictimType = victimType;
            UpdateCrimeName();
        }
        
        private void UpdateCrimeName()
        {
            CrimeName = VictimType switch
            {
                "Police" => "Murder of a Police Officer",
                "Civilian" => "Murder",
                "Employee" => "Murder of an Employee",
                _ => "Murder"
            };
        }
    }
}