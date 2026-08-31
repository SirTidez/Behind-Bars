using System;
using System.Collections.Generic;
using UnityEngine;

namespace Behind_Bars.Systems.CrimeTracking.Datas
{
    /// <summary>
    /// Serializable representation of a CrimeInstance for Unity JsonUtility
    /// Handles game time timestamp (as float in game minutes), Vector3 serialization (as separate floats),
    /// and Crime object serialization (stores essential data only)
    /// </summary>
    [Serializable]
    public class CrimeInstanceSaveData
    {
        // Crime data is stored as strings because the native Crime object is not a safe
        // persistence boundary. crimeTypeName is the reconstruction hint; crimeName is
        // display text and may be empty for degraded/native-missing instances.
        /// <summary>Display name captured from the native crime.</summary>
        public string crimeName;              // Crime.CrimeName
        /// <summary>Runtime type name used as the persisted crime identity hint.</summary>
        public string crimeTypeName;          // Crime.GetType().Name
        /// <summary>User-facing description captured for degraded instances and UI.</summary>
        public string description;            // User-friendly description
        
        // Timestamp is game-clock minutes, not wall-clock seconds.
        /// <summary>Game-clock timestamp in in-game minutes.</summary>
        public float timestamp;               // Game time in game minutes
        
        // Location (Vector3 flattened)
        /// <summary>World-space X coordinate at the incident.</summary>
        public float locationX;
        /// <summary>World-space Y coordinate at the incident.</summary>
        public float locationY;
        /// <summary>World-space Z coordinate at the incident.</summary>
        public float locationZ;
        
        // Witness data
        /// <summary>Stable native witness identifiers associated with the incident.</summary>
        public List<string> witnessIds = new List<string>();
        
        // Severity
        /// <summary>Severity multiplier used by wanted/fine calculations.</summary>
        public float severity;

        /// <summary>
        /// Whether this instance contributes to wanted level. Defaults to true so saves
        /// written before the field existed retain their previous behavior.
        /// </summary>
        public bool countsTowardWantedLevel = true;

        // Correlates one persisted charge to one original native or mod-created incident;
        // empty is valid for legacy records that predate incident correlation.
        /// <summary>Persisted incident correlation identifier.</summary>
        public string incidentId = "";
        /// <summary>Source marker for the original native/mod charge path.</summary>
        public string source = "";
        /// <summary>Contextual enhancements persisted with this base charge.</summary>
        public List<CrimeEnhancement> enhancements = new List<CrimeEnhancement>();

        /// <summary>
        /// Creates a serialization DTO from a crime instance without persisting its native object.
        /// </summary>
        /// <param name="crime">Crime instance to flatten, or null to omit.</param>
        /// <returns>A detached DTO, or null when <paramref name="crime"/> is null.</returns>
        public static CrimeInstanceSaveData FromCrimeInstance(CrimeInstance crime)
        {
            if (crime == null)
                return null;

            string crimeName = "";
            string crimeTypeName = "";

            if (crime.Crime != null)
            {
                crimeName = crime.Crime.CrimeName ?? "";
                crimeTypeName = crime.Crime.GetType().Name;
            }
            else
            {
                // Degraded instances rely on the stored type hint, then the current
                // description/type fallback exposed by CrimeInstance.
                crimeTypeName = crime.StoredCrimeTypeName;
                if (string.IsNullOrWhiteSpace(crimeTypeName))
                {
                    crimeTypeName = crime.GetCrimeTypeName();
                }
            }

            return new CrimeInstanceSaveData
            {
                crimeName = crimeName,
                crimeTypeName = crimeTypeName,
                description = crime.Description ?? crime.GetCrimeName(),
                timestamp = crime.Timestamp, // Game time in game minutes
                locationX = crime.Location.x,
                locationY = crime.Location.y,
                locationZ = crime.Location.z,
                witnessIds = crime.WitnessIds != null ? new List<string>(crime.WitnessIds) : new List<string>(),
                severity = crime.Severity,
                countsTowardWantedLevel = crime.CountsTowardWantedLevel,
                incidentId = crime.IncidentId,
                source = crime.Source,
                enhancements = crime.Enhancements != null ? new List<CrimeEnhancement>(crime.Enhancements) : new List<CrimeEnhancement>()
            };
        }

        /// <summary>
        /// Rehydrates a crime instance from persisted primitive fields.
        /// </summary>
        /// <remarks>
        /// The native <c>Crime</c> object remains null by design; callers that need native
        /// compatibility must reconstruct it from <see cref="crimeTypeName"/> separately.
        /// Missing lists are replaced with empty lists and missing strings with empty text.
        /// </remarks>
        public CrimeInstance ToCrimeInstance()
        {
            Vector3 location = new Vector3(locationX, locationY, locationZ);

            var crimeInstance = new CrimeInstance
            {
                Crime = null, // Cannot reconstruct Crime object from save data
                Timestamp = timestamp, // Game time in game minutes (defaults to 0 if not set)
                Location = location,
                WitnessIds = witnessIds != null ? new List<string>(witnessIds) : new List<string>(),
                Severity = severity,
                Description = description ?? crimeName ?? "",
                StoredCrimeTypeName = crimeTypeName ?? "",
                CountsTowardWantedLevel = countsTowardWantedLevel,
                IncidentId = incidentId ?? "",
                Source = source ?? "",
                Enhancements = enhancements != null ? new List<CrimeEnhancement>(enhancements) : new List<CrimeEnhancement>()
            };

            return crimeInstance;
        }

        /// <summary>
        /// Reconstructs the incident location from its flattened coordinates.
        /// </summary>
        /// <returns>The incident world position.</returns>
        public Vector3 GetLocation()
        {
            return new Vector3(locationX, locationY, locationZ);
        }
    }
}

