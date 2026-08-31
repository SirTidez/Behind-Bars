using System;

#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    /// <summary>Crime type for low-severity drug possession.</summary>
    [Serializable]
    public class DrugPossessionLow : Crime
    {
        /// <summary>Creates a low-severity drug possession charge.</summary>
        public DrugPossessionLow()
        {
            CrimeName = "Drug Possession (Low)";
        }
    }
    
    /// <summary>Crime type for moderate-severity drug possession.</summary>
    [Serializable]
    public class DrugPossessionModerate : Crime
    {
        /// <summary>Creates a moderate-severity drug possession charge.</summary>
        public DrugPossessionModerate()
        {
            CrimeName = "Drug Possession (Moderate)";
        }
    }
    
    /// <summary>Crime type for high-severity drug possession.</summary>
    [Serializable]
    public class DrugPossessionHigh : Crime
    {
        /// <summary>Creates a high-severity drug possession charge.</summary>
        public DrugPossessionHigh()
        {
            CrimeName = "Drug Possession (High)";
        }
    }
    
    /// <summary>Crime type for an aggregate drug-trafficking charge.</summary>
    [Serializable]
    public class DrugTraffickingCrime : Crime
    {
        /// <summary>Creates a drug-trafficking charge.</summary>
        public DrugTraffickingCrime()
        {
            CrimeName = "Drug Trafficking";
        }
    }
    
    /// <summary>Crime type for illegal weapon possession during parole enforcement.</summary>
    [Serializable]
    public class WeaponPossession : Crime
    {
        /// <summary>Creates an illegal weapon possession charge.</summary>
        public WeaponPossession()
        {
            CrimeName = "Illegal Weapon Possession";
        }
    }
}
