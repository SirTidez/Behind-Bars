using System;
using System.Collections.Generic;
using UnityEngine;
#if !MONO
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.Law;
using ScheduleOne.NPCs;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Represents a single instance of a crime with all its details
    /// </summary>
    [Serializable]
    public class CrimeInstance
    {
        public Crime Crime { get; set; }
        public DateTime Timestamp { get; set; }
        public Vector3 Location { get; set; }
        public List<string> WitnessIds { get; set; } = new List<string>();
        public float Severity { get; set; }
        public bool WasWitnessed => WitnessIds.Count > 0;
        public string Description { get; set; } = "";
        
        public CrimeInstance() { }
        
        public CrimeInstance(Crime crime, Vector3 location, float severity = 1.0f)
        {
            Crime = crime;
            Timestamp = DateTime.Now;
            Location = location;
            Severity = severity;
            Description = crime.CrimeName;
        }
        
        public void AddWitness(NPC witness)
        {
            if (witness != null && !WitnessIds.Contains(witness.ID))
            {
                WitnessIds.Add(witness.ID);
            }
        }
        
        public void AddWitness(string witnessId)
        {
            if (!string.IsNullOrEmpty(witnessId) && !WitnessIds.Contains(witnessId))
            {
                WitnessIds.Add(witnessId);
            }
        }
        
        /// <summary>
        /// Calculate how much this crime contributes to the wanted level
        /// </summary>
        public float GetWantedContribution()
        {
            float baseSeverity = Severity;
            
            // Increase severity based on witness count (more witnesses = more heat)
            float witnessFactor = 1.0f + (WitnessIds.Count * 0.2f);
            
            // Newer crimes contribute more to current wanted level
            float ageDays = (float)(DateTime.Now - Timestamp).TotalDays;
            float ageFactor = Mathf.Clamp01(1.0f - (ageDays / 7.0f)); // Fade over a week
            
            return baseSeverity * witnessFactor * ageFactor;
        }
        
        /// <summary>
        /// Check if this crime should expire (only for minor crimes)
        /// </summary>
        public bool ShouldExpire()
        {
            // Major crimes never expire
            if (Severity >= 2.0f) return false;
            
            // Minor crimes expire after 1 day if no witnesses
            if (!WasWitnessed && (DateTime.Now - Timestamp).TotalDays > 1.0)
                return true;
                
            // Witnessed minor crimes expire after 3 days
            if (WasWitnessed && (DateTime.Now - Timestamp).TotalDays > 3.0)
                return true;
                
            return false;
        }
    }
}