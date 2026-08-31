using Behind_Bars.Systems.CrimeTracking;

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Interface for parole conditions that can be activated, checked, and enforced.
    /// Each condition defines when it applies, what violation it maps to, and the compliance penalty.
    /// </summary>
    public interface IParoleCondition
    {
        /// <summary>
        /// Unique identifier for save/load persistence
        /// </summary>
        string ConditionId { get; }

        /// <summary>
        /// Display name shown to the player
        /// </summary>
        string ConditionName { get; }

        /// <summary>
        /// Description shown on the release UI
        /// </summary>
        string ConditionDescription { get; }

        /// <summary>
        /// Determines if this condition should be activated given the player's criminal history and LSI level
        /// </summary>
        bool IsApplicable(RapSheet rapSheet);

        /// <summary>
        /// The violation type recorded when this condition is breached
        /// </summary>
        ViolationType ViolationType { get; }

        /// <summary>
        /// How much compliance score to deduct on violation
        /// </summary>
        float CompliancePenalty { get; }
    }
}
