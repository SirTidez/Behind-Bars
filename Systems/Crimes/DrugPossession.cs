using System;

#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    [Serializable]
    public class DrugPossessionLow : Crime
    {
        public DrugPossessionLow()
        {
            CrimeName = "Drug Possession (Low)";
        }
    }
    
    [Serializable]
    public class DrugPossessionModerate : Crime
    {
        public DrugPossessionModerate()
        {
            CrimeName = "Drug Possession (Moderate)";
        }
    }
    
    [Serializable]
    public class DrugPossessionHigh : Crime
    {
        public DrugPossessionHigh()
        {
            CrimeName = "Drug Possession (High)";
        }
    }
    
    [Serializable]
    public class DrugTraffickingCrime : Crime
    {
        public DrugTraffickingCrime()
        {
            CrimeName = "Drug Trafficking";
        }
    }
    
    [Serializable]
    public class WeaponPossession : Crime
    {
        public WeaponPossession()
        {
            CrimeName = "Illegal Weapon Possession";
        }
    }
}