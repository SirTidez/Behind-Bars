using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Types of parole violations
    /// </summary>
    [Serializable]
    public enum ViolationType
    {
        ContrabandPossession,
        MissedCheckIn,
        NewCrime,
        RestrictedAreaViolation,
        CurfewViolation,
        ContactWithKnownCriminals,
        Other
    }

    /// <summary>
    /// Records a specific parole violation incident
    /// </summary>
    [Serializable]
    public class ViolationRecord
    {
        [JsonProperty("violationType")]
        public ViolationType ViolationType;

        [JsonProperty("violationTime")]
        public DateTime ViolationTime;

        [JsonProperty("details")]
        public string Details;

        [JsonProperty("severity")]
        public float Severity = 1.0f;

        [JsonProperty("locationDescription")]
        public string LocationDescription;

        public ViolationRecord()
        {
            ViolationTime = DateTime.Now;
            Details = "";
            LocationDescription = "";
        }

        public ViolationRecord(ViolationType type, string details, float severity = 1.0f)
        {
            ViolationType = type;
            ViolationTime = DateTime.Now;
            Details = details;
            Severity = severity;
            LocationDescription = "";
        }
    }
}
