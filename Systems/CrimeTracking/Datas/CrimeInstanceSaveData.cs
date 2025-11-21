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
        // Crime data (stored as strings since Crime object can't be serialized)
        public string crimeName;              // Crime.CrimeName
        public string crimeTypeName;          // Crime.GetType().Name
        public string description;            // User-friendly description
        
        // Timestamp
        public float timestamp;               // Game time in game minutes
        
        // Location (Vector3 flattened)
        public float locationX;
        public float locationY;
        public float locationZ;
        
        // Witness data
        public List<string> witnessIds = new List<string>();
        
        // Severity
        public float severity;

        /// <summary>
        /// Creates a CrimeInstanceSaveData from a CrimeInstance
        /// </summary>
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
                // Fallback to description-based type name
                crimeTypeName = crime.GetCrimeTypeName();
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
                severity = crime.Severity
            };
        }

        /// <summary>
        /// Converts this SaveData back to a CrimeInstance
        /// Note: Crime object will be null - it needs to be reconstructed from crimeTypeName if needed
        /// </summary>
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
                Description = description ?? crimeName ?? ""
            };

            return crimeInstance;
        }

        /// <summary>
        /// Gets the location as a Vector3
        /// </summary>
        public Vector3 GetLocation()
        {
            return new Vector3(locationX, locationY, locationZ);
        }
    }
}

