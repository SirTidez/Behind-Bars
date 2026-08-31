using System;
using UnityEngine;

namespace Behind_Bars.Systems.CrimeTracking.Datas
{
    /// <summary>
    /// Serializable representation of a ViolationRecord for Unity JsonUtility
    /// Handles DateTime serialization (as string) and enum serialization (as int)
    /// </summary>
    [Serializable]
    public class ViolationRecordSaveData
    {
        /// <summary>ViolationType enum value serialized as an integer.</summary>
        public int violationType;              // ViolationType enum as int
        /// <summary>ISO-8601 serialization of the local violation timestamp.</summary>
        public string violationTime;           // DateTime as ISO 8601 string
        /// <summary>Explanatory violation text.</summary>
        public string details;
        /// <summary>Severity used by parole/compliance calculations.</summary>
        public float severity;
        /// <summary>Human-readable location description.</summary>
        public string locationDescription;

        /// <summary>
        /// Flattens a violation record into serializer-friendly primitive fields.
        /// </summary>
        /// <param name="violation">Violation to serialize, or null to omit.</param>
        /// <returns>A detached DTO, or null when <paramref name="violation"/> is null.</returns>
        public static ViolationRecordSaveData FromViolationRecord(ViolationRecord violation)
        {
            if (violation == null)
                return null;

            return new ViolationRecordSaveData
            {
                violationType = (int)violation.ViolationType,
                violationTime = violation.ViolationTime.ToString("O"), // ISO 8601 format
                details = violation.Details ?? "",
                severity = violation.Severity,
                locationDescription = violation.LocationDescription ?? ""
            };
        }

        /// <summary>
        /// Rehydrates a violation record from primitive save fields.
        /// </summary>
        /// <remarks>Malformed or missing timestamps fall back to the current local time.</remarks>
        public ViolationRecord ToViolationRecord()
        {
            DateTime parsedTime;
            if (!DateTime.TryParse(violationTime, out parsedTime))
            {
                parsedTime = DateTime.Now;
            }

            return new ViolationRecord
            {
                ViolationType = (ViolationType)violationType,
                ViolationTime = parsedTime,
                Details = details ?? "",
                Severity = severity,
                LocationDescription = locationDescription ?? ""
            };
        }
    }
}

