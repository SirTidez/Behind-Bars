using System;
using Behind_Bars.Systems;

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Data structure for jail sentence information
    /// Contains sentence duration in game time and fine amount
    /// </summary>
    public class JailSentenceData
    {
        /// <summary>
        /// Total sentence duration in game minutes
        /// </summary>
        public float TotalGameMinutes { get; set; }

        /// <summary>
        /// Fine amount in dollars (independent from sentence)
        /// </summary>
        public float FineAmount { get; set; }

        /// <summary>
        /// Base sentence before multipliers (in game minutes)
        /// </summary>
        public float BaseSentenceMinutes { get; set; }

        /// <summary>
        /// Severity multiplier applied
        /// </summary>
        public float SeverityMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// Repeat offender multiplier applied
        /// </summary>
        public float RepeatOffenderMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// Witness multiplier applied
        /// </summary>
        public float WitnessMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// Parole violation multiplier applied (if player was on parole when arrested)
        /// </summary>
        public float ParoleViolationMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// Global multiplier applied
        /// </summary>
        public float GlobalMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// Formatted sentence string (e.g., "2d 3h 45m")
        /// </summary>
        public string FormattedSentence => FormatSentence();

        /// <summary>
        /// Format the sentence as a human-readable string
        /// </summary>
        private string FormatSentence()
        {
            return GameTimeManager.FormatGameTime(TotalGameMinutes);
        }

        /// <summary>
        /// Get sentence breakdown for debugging
        /// </summary>
        public string GetBreakdown()
        {
            return $"Base: {BaseSentenceMinutes}m × Severity: {SeverityMultiplier} × Repeat: {RepeatOffenderMultiplier} × Witness: {WitnessMultiplier} × Parole: {ParoleViolationMultiplier} × Global: {GlobalMultiplier} = {TotalGameMinutes}m";
        }
    }

    /// <summary>
    /// Represents a single crime's sentence contribution
    /// Used when calculating sentences for multiple crimes
    /// </summary>
    public class CrimeSentence
    {
        /// <summary>
        /// Crime class name
        /// </summary>
        public string CrimeClassName { get; set; } = "";

        /// <summary>
        /// Base sentence for this crime (in game minutes)
        /// </summary>
        public float BaseMinutes { get; set; }

        /// <summary>
        /// Severity value for this crime instance
        /// </summary>
        public float Severity { get; set; } = 1.0f;

        /// <summary>
        /// Whether this crime was witnessed
        /// </summary>
        public bool WasWitnessed { get; set; }

        /// <summary>
        /// Number of witnesses
        /// </summary>
        public int WitnessCount { get; set; }

        /// <summary>
        /// For Murder crimes, the victim type
        /// </summary>
        public string? VictimType { get; set; }
    }
}

