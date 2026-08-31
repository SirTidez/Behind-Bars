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
        /// <summary>Possession of an item prohibited by parole conditions.</summary>
        ContrabandPossession,
        /// <summary>Failure to attend a required check-in.</summary>
        MissedCheckIn,
        /// <summary>New criminal offense while on parole.</summary>
        NewCrime,
        /// <summary>Entering an area restricted by parole conditions.</summary>
        RestrictedAreaViolation,
        /// <summary>Being present outside the permitted curfew window.</summary>
        CurfewViolation,
        /// <summary>Contact with a person disallowed by parole conditions.</summary>
        ContactWithKnownCriminals,
        /// <summary>Violation that does not fit another category.</summary>
        Other,

        // Append-only: violation enum values are persisted as integers. Do not reorder
        // existing entries or old parole records will deserialize as different violations.
        /// <summary>Weapon possession recorded as a parole-specific violation.</summary>
        IllegalWeaponPossession
    }

    /// <summary>
    /// Records a specific parole violation incident.
    /// Uses SaveableField attributes for automatic serialization by SaveableSerializer.
    /// </summary>
    [Serializable]
    public class ViolationRecord
    {
        // These fields are private so SaveableField remains the persistence contract;
        // properties normalize nullable strings for runtime callers.
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
        /// <summary>Gets or sets the parole violation category.</summary>
        public ViolationType ViolationType
        {
            get => _violationType;
            set => _violationType = value;
        }

        /// <summary>Gets or sets the local wall-clock time at which the violation was recorded.</summary>
        public DateTime ViolationTime
        {
            get => _violationTime;
            set => _violationTime = value;
        }

        /// <summary>Gets or sets explanatory violation text, normalized to empty when null.</summary>
        public string Details
        {
            get => _details ?? "";
            set => _details = value ?? "";
        }

        /// <summary>Gets or sets the severity used by parole/compliance calculations.</summary>
        public float Severity
        {
            get => _severity;
            set => _severity = value;
        }

        /// <summary>Gets or sets a human-readable location description.</summary>
        public string LocationDescription
        {
            get => _locationDescription ?? "";
            set => _locationDescription = value ?? "";
        }

        /// <summary>Creates an empty violation with the current local timestamp.</summary>
        public ViolationRecord()
        {
            _violationTime = DateTime.Now;
            _details = "";
            _locationDescription = "";
        }

        /// <summary>Creates a violation with its category, details, and severity.</summary>
        /// <param name="type">Violation category.</param>
        /// <param name="details">Explanatory text; null becomes empty.</param>
        /// <param name="severity">Severity value used by policy calculations.</param>
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
