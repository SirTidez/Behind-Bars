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
        public int violationType;              // ViolationType enum as int
        public string violationTime;           // DateTime as ISO 8601 string
        public string details;
        public float severity;
        public string locationDescription;

        /// <summary>
        /// Creates a ViolationRecordSaveData from a ViolationRecord
        /// </summary>
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
        /// Converts this SaveData back to a ViolationRecord
        /// </summary>
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

