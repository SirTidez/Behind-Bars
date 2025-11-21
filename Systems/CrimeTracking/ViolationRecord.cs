using System;
using System.Collections.Generic;
using System.Text;
using Behind_Bars.Utils.Saveable;

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
    /// Records a specific parole violation incident.
    /// Uses SaveableField attributes for automatic serialization by SaveableSerializer.
    /// </summary>
    [Serializable]
    public class ViolationRecord
    {
        [SaveableField("violationType")]
        private ViolationType _violationType;

        [SaveableField("violationTime")]
        private DateTime _violationTime;

        [SaveableField("details")]
        private string _details;

        [SaveableField("severity")]
        private float _severity = 1.0f;

        [SaveableField("locationDescription")]
        private string _locationDescription;

        // Properties for safe access
        public ViolationType ViolationType
        {
            get => _violationType;
            set => _violationType = value;
        }

        public DateTime ViolationTime
        {
            get => _violationTime;
            set => _violationTime = value;
        }

        public string Details
        {
            get => _details ?? "";
            set => _details = value ?? "";
        }

        public float Severity
        {
            get => _severity;
            set => _severity = value;
        }

        public string LocationDescription
        {
            get => _locationDescription ?? "";
            set => _locationDescription = value ?? "";
        }

        public ViolationRecord()
        {
            _violationTime = DateTime.Now;
            _details = "";
            _locationDescription = "";
        }

        public ViolationRecord(ViolationType type, string details, float severity = 1.0f)
        {
            _violationType = type;
            _violationTime = DateTime.Now;
            _details = details ?? "";
            _severity = severity;
            _locationDescription = "";
        }
    }
}
