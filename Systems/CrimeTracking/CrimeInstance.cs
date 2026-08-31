using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Utils.Saveable;
using Behind_Bars.Systems;
#if !MONO
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.NPCs;
#else
using Newtonsoft.Json;
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
        // Native object identity is process-local and cannot be saved. StoredCrimeTypeName
        // below is the durable type hint used after hydration.
        [NonSerialized]
        private Crime _crime;

        // Serialized primitive snapshot of the native crime identity; "Crime" itself is
        // intentionally not reconstructed by CrimeInstanceSaveData.ToCrimeInstance().
        [SaveableField("crimeTypeName")]
        private string _crimeTypeName; // Store the type name instead of the object

        [SaveableField("timestamp")]
        private float _timestamp; // Game-clock minutes, not wall-clock time.

        [SaveableField("location")]
        private Vector3 _location;

        [SaveableField("witnessIds")]
        private List<string> _witnessIds;

        [SaveableField("severity")]
        private float _severity;

        [SaveableField("description")]
        private string _description;

        // Assigned at the native AddCrime seam (or when Behind Bars invents a charge).
        // This survives save/load and is the only identifier used for arrest deduplication.
        [SaveableField("incidentId")]
        private string _incidentId = "";

        [SaveableField("source")]
        private string _source = "";

        [SaveableField("enhancements")]
        private List<CrimeEnhancement> _enhancements = new List<CrimeEnhancement>();

        // Custody-only violations remain on the prisoner's record, but must never
        // contribute to the street wanted meter or trigger a new police pursuit.
        [SaveableField("countsTowardWantedLevel")]
        private bool _countsTowardWantedLevel = true;

        // Properties for safe access
        /// <summary>
        /// Gets or sets the native crime object for this incident. The object is not serialized;
        /// assigning it also captures its type name and initializes the display description.
        /// </summary>
#if !MONO
        [System.Text.Json.Serialization.JsonIgnore]
#else
        [Newtonsoft.Json.JsonIgnore]
#endif
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

        /// <summary>Gets or sets the world-space location where the incident was recorded.</summary>
        public Vector3 Location
        {
            get => _location;
            set => _location = value;
        }

        /// <summary>
        /// Gets or sets witness IDs for this incident. The getter lazily creates the mutable
        /// list because old saves may not contain witness data.
        /// </summary>
        public List<string> WitnessIds
        {
            get => _witnessIds ??= new List<string>();
            set => _witnessIds = value ?? new List<string>();
        }

        /// <summary>Gets or sets the severity used by wanted, LSI, and penalty calculations.</summary>
        public float Severity
        {
            get => _severity;
            set => _severity = value;
        }

        /// <summary>Gets whether at least one witness ID is associated with this incident.</summary>
        public bool WasWitnessed => WitnessIds.Count > 0;

        /// <summary>Gets or sets the user-facing description, normalized to an empty string when null.</summary>
        public string Description
        {
            get => _description ?? "";
            set => _description = value ?? "";
        }

        /// <summary>
        /// The native class name captured at the crime event seam.  Saved rap-sheet
        /// entries do not retain the native object, so this value must remain the
        /// calculator authority after a save/load cycle.
        /// </summary>
        public string StoredCrimeTypeName
        {
            get => _crimeTypeName ?? "";
            set => _crimeTypeName = value ?? "";
        }

        /// <summary>
        /// Gets or sets the persisted incident identity used to correlate native AddCrime
        /// events with later arrest capture. The value is not regenerated by accessors.
        /// </summary>
        public string IncidentId
        {
            get => _incidentId ?? "";
            set => _incidentId = value ?? "";
        }

        /// <summary>
        /// Native, BehindBars, or LegacyMigrated. This is intentionally a string so
        /// older saves remain compatible without an enum-deserialization dependency.
        /// </summary>
        public string Source
        {
            get => _source ?? "";
            set => _source = value ?? "";
        }

        /// <summary>
        /// Gets or sets contextual enhancements attached to this base charge. The list is
        /// normalized on access so legacy saves can be read without null checks.
        /// </summary>
        public List<CrimeEnhancement> Enhancements
        {
            get => _enhancements ??= new List<CrimeEnhancement>();
            set => _enhancements = value ?? new List<CrimeEnhancement>();
        }

        /// <summary>Returns whether an enhancement of the specified kind is already attached.</summary>
        /// <param name="kind">Enhancement kind to search for.</param>
        public bool HasEnhancement(CrimeEnhancementKind kind)
        {
            return Enhancements.Exists(enhancement => enhancement != null && enhancement.Kind == kind);
        }

        /// <summary>
        /// Adds one contextual enhancement unless it is null, has no kind, or the same kind
        /// is already present. This preserves one enhancement entry per legal consequence.
        /// </summary>
        /// <param name="enhancement">Enhancement to attach to this charge.</param>
        public void AddEnhancement(CrimeEnhancement enhancement)
        {
            if (enhancement == null || enhancement.Kind == CrimeEnhancementKind.None || HasEnhancement(enhancement.Kind))
            {
                return;
            }

            Enhancements.Add(enhancement);
        }

        /// <summary>
        /// Whether this record entry contributes to the on-street wanted display.
        /// In-custody discipline charges are intentionally recorded without creating
        /// a new wanted state for a prisoner who is already secured in the jail.
        /// </summary>
        public bool CountsTowardWantedLevel
        {
            get => _countsTowardWantedLevel;
            set => _countsTowardWantedLevel = value;
        }
        
        /// <summary>
        /// Get the crime name safely - prefers Description (user-friendly), falls back to Crime.CrimeName
        /// This ensures we always have a readable crime name even if Crime object is null
        /// </summary>
        public string GetCrimeName()
        {
            // Description is the persisted/display authority. Enhancement labels are
            // appended only for presentation and never alter the base crime identity.
            string baseName;
            // Prefer Description as it's the user-friendly display name
            if (!string.IsNullOrEmpty(Description))
            {
                baseName = Description;
            }
            else if (Crime != null && !string.IsNullOrEmpty(Crime.CrimeName))
            {
                baseName = Crime.CrimeName;
            }
            else
            {
                baseName = "Unknown Crime";
            }

            var labels = Enhancements
                .Where(enhancement => enhancement != null)
                .Select(enhancement => enhancement.GetDisplayLabel())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToList();
            return labels.Count == 0 ? baseName : $"{baseName} — {string.Join(", ", labels)}";
        }
        
        /// <summary>
        /// Get the crime type name (class name) for categorization - uses Crime type if available
        /// </summary>
        public string GetCrimeTypeName()
        {
            // Prefer the live native type, then the persisted type hint, then the small
            // description map. This ordering keeps saved/degraded incidents usable after
            // the native object reference has been lost.
            if (Crime != null && !string.Equals(Crime.GetType().Name, "Crime", StringComparison.Ordinal))
            {
                return Crime.GetType().Name;
            }

            if (!string.IsNullOrWhiteSpace(_crimeTypeName) && !string.Equals(_crimeTypeName, "Crime", StringComparison.Ordinal))
            {
                return _crimeTypeName;
            }
            
            // If no Crime object, try to infer from Description
            if (!string.IsNullOrEmpty(Description))
            {
                // Map common descriptions to type names
                return Description switch
                {
                    "Murder" or "Murder of a Police Officer" or "Murder of an Employee" => "Murder",
                    "Involuntary Manslaughter" => "Manslaughter",
                    "Assault" => "Assault",
                    "Assault on Civilian" => "AssaultOnCivilian",
                    "Assault on an LEO" => "AssaultOnOfficer",
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
        
        /// <summary>Creates an empty instance suitable for save deserialization.</summary>
        public CrimeInstance()
        {
            _witnessIds = new List<string>();
            _description = "";
            _enhancements = new List<CrimeEnhancement>();
        }
        
        /// <summary>
        /// Creates an incident at a world location and timestamps it in game minutes using the
        /// current GameTimeManager value.
        /// </summary>
        /// <param name="crime">Native or mod crime object; may be null for type-only records.</param>
        /// <param name="location">World-space location of the incident.</param>
        /// <param name="severity">Severity used by wanted and sentence calculations.</param>
        public CrimeInstance(Crime crime, Vector3 location, float severity = 1.0f)
        {
            Crime = crime;
            // Use game time instead of real time
            _timestamp = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            _location = location;
            _severity = severity;
            _witnessIds = new List<string>();
            _enhancements = new List<CrimeEnhancement>();
            // Set Description to the user-friendly CrimeName from the Crime object
            _description = crime != null ? crime.CrimeName : "";
        }
        
        /// <summary>Adds a witness's native ID when it is not already recorded.</summary>
        /// <param name="witness">Witness NPC whose ID should be associated with the incident.</param>
        public void AddWitness(NPC witness)
        {
            if (witness != null && !WitnessIds.Contains(witness.ID))
            {
                WitnessIds.Add(witness.ID);
            }
        }
        
        /// <summary>Adds a non-empty witness ID when it is not already recorded.</summary>
        /// <param name="witnessId">Stable witness identifier to associate with the incident.</param>
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
            // CountsTowardWantedLevel is the custody boundary: a retained violation can
            // remain on the rap sheet while contributing zero to street wanted heat.
            if (!CountsTowardWantedLevel)
            {
                return 0f;
            }

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
            // Expiration uses game-clock age. Major crimes (severity >= 2) are retained;
            // only minor crimes use the shorter unwitnessed or longer witnessed windows.
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
