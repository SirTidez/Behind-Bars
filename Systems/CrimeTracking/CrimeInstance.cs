using System;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Utils.Saveable;
using Behind_Bars.Systems;
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
    /// Represents a single instance of a crime with all its details.
    /// Uses SaveableField attributes for automatic serialization by SaveableSerializer.
    /// Note: Crime object reference is stored as type name string since it's a game object reference.
    /// </summary>
    [Serializable]
    public class CrimeInstance
    {
        // Non-serialized - Crime object reference cannot be saved
        [NonSerialized]
        private Crime _crime;

        [SaveableField("crimeTypeName")]
        private string _crimeTypeName; // Store the type name instead of the object

        [SaveableField("timestamp")]
        private float _timestamp; // Game time in game minutes

        [SaveableField("location")]
        private Vector3 _location;

        [SaveableField("witnessIds")]
        private List<string> _witnessIds;

        [SaveableField("severity")]
        private float _severity;

        [SaveableField("description")]
        private string _description;

        // Properties for safe access
        public Crime Crime
        {
            get => _crime;
            set
            {
                _crime = value;
                // Store type name for serialization (Crime object reference cannot be serialized)
                _crimeTypeName = value != null ? value.GetType().Name : "";
                // Update description if available
                if (value != null && string.IsNullOrEmpty(_description))
                    _description = value.CrimeName ?? "";
            }
        }

        /// <summary>
        /// Timestamp in game minutes (game time when crime was committed)
        /// </summary>
        public float Timestamp
        {
            get => _timestamp;
            set => _timestamp = value;
        }

        public Vector3 Location
        {
            get => _location;
            set => _location = value;
        }

        public List<string> WitnessIds
        {
            get => _witnessIds ??= new List<string>();
            set => _witnessIds = value ?? new List<string>();
        }

        public float Severity
        {
            get => _severity;
            set => _severity = value;
        }

        public bool WasWitnessed => WitnessIds.Count > 0;

        public string Description
        {
            get => _description ?? "";
            set => _description = value ?? "";
        }
        
        /// <summary>
        /// Get the crime name safely - prefers Description (user-friendly), falls back to Crime.CrimeName
        /// This ensures we always have a readable crime name even if Crime object is null
        /// </summary>
        public string GetCrimeName()
        {
            // Prefer Description as it's the user-friendly display name
            if (!string.IsNullOrEmpty(Description))
            {
                return Description;
            }
            
            // Fall back to Crime.CrimeName if Description is empty
            if (Crime != null && !string.IsNullOrEmpty(Crime.CrimeName))
            {
                return Crime.CrimeName;
            }
            
            // Last resort fallback
            return "Unknown Crime";
        }
        
        /// <summary>
        /// Get the crime type name (class name) for categorization - uses Crime type if available
        /// </summary>
        public string GetCrimeTypeName()
        {
            if (Crime != null)
            {
                return Crime.GetType().Name;
            }
            
            // If no Crime object, try to infer from Description
            if (!string.IsNullOrEmpty(Description))
            {
                // Map common descriptions to type names
                return Description switch
                {
                    "Murder" or "Murder of a Police Officer" or "Murder of an Employee" => "Murder",
                    "Involuntary Manslaughter" => "Manslaughter",
                    "Assault on Civilian" => "AssaultOnCivilian",
                    "Witness Intimidation" => "WitnessIntimidation",
                    "Drug Possession (Low)" => "DrugPossessionLow",
                    "Drug Possession (Moderate)" => "DrugPossessionModerate",
                    "Drug Possession (High)" => "DrugPossessionHigh",
                    "Drug Trafficking" => "DrugTraffickingCrime",
                    "Illegal Weapon Possession" => "WeaponPossession",
                    _ => Description.Replace(" ", "") // Fallback: remove spaces
                };
            }
            
            return "Unknown";
        }
        
        public CrimeInstance()
        {
            _witnessIds = new List<string>();
            _description = "";
        }
        
        public CrimeInstance(Crime crime, Vector3 location, float severity = 1.0f)
        {
            Crime = crime;
            // Use game time instead of real time
            _timestamp = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            _location = location;
            _severity = severity;
            _witnessIds = new List<string>();
            // Set Description to the user-friendly CrimeName from the Crime object
            _description = crime != null ? crime.CrimeName : "";
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
            // Use game time: 7 days = 10080 game minutes (7 * 24 * 60)
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float ageGameMinutes = currentGameTime - Timestamp;
            float ageGameDays = ageGameMinutes / (24f * 60f); // Convert game minutes to game days
            float ageFactor = Mathf.Clamp01(1.0f - (ageGameDays / 7.0f)); // Fade over a week
            
            return baseSeverity * witnessFactor * ageFactor;
        }
        
        /// <summary>
        /// Check if this crime should expire (only for minor crimes)
        /// Uses game time: 1 day = 1440 game minutes, 3 days = 4320 game minutes
        /// </summary>
        public bool ShouldExpire()
        {
            // Major crimes never expire
            if (Severity >= 2.0f) return false;
            
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float ageGameMinutes = currentGameTime - Timestamp;
            
            // Minor crimes expire after 1 game day (1440 game minutes) if no witnesses
            if (!WasWitnessed && ageGameMinutes > 1440f)
                return true;
                
            // Witnessed minor crimes expire after 3 game days (4320 game minutes)
            if (WasWitnessed && ageGameMinutes > 4320f)
                return true;
                
            return false;
        }
    }
}