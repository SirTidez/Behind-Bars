using System;
#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Crimes
{
    /// <summary>
    /// Crime type used for an intentional NPC death, with a victim-role display variant.
    /// </summary>
    [Serializable]
    public class Murder : Crime
    {
        /// <summary>Gets or sets the current display label for this murder charge.</summary>
        public override string CrimeName
        {
            get;
            set;
        } = "Murder";
        
        /// <summary>Victim role used to select the display label; defaults to Civilian.</summary>
        public string VictimType { get; set; } = "Civilian";
        
        /// <summary>Creates a civilian murder charge using the default label.</summary>
        public Murder() { }
        
        /// <summary>Creates a murder charge for the supplied victim role.</summary>
        /// <param name="victimType">Role such as Police, Civilian, or Employee.</param>
        public Murder(string victimType)
        {
            VictimType = victimType;
            UpdateCrimeName();
        }
        
        private void UpdateCrimeName()
        {
            // Only the three recognized roles receive specialized labels. Unknown or
            // missing roles intentionally fall back to the generic Murder label.
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
