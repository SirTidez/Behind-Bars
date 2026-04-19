using Behind_Bars.Systems.CrimeTracking;

namespace Behind_Bars.UI
{
    /// <summary>
    /// Data structure for parole status information displayed in the UI
    /// </summary>
    public class ParoleStatusData
    {
        /// <summary>
        /// Whether the player is currently on parole
        /// </summary>
        public bool IsOnParole { get; set; }

        /// <summary>
        /// Time remaining on parole in seconds
        /// </summary>
        public float TimeRemaining { get; set; }

        /// <summary>
        /// Formatted time remaining string (e.g., "5d 3h 45m")
        /// </summary>
        public string TimeRemainingFormatted { get; set; } = "";

        /// <summary>
        /// Current LSI supervision level
        /// </summary>
        public LSILevel SupervisionLevel { get; set; } = LSILevel.None;

        /// <summary>
        /// Search probability as a percentage (0-100)
        /// </summary>
        public int SearchProbabilityPercent { get; set; } = 0;

        /// <summary>
        /// Number of parole violations
        /// </summary>
        public int ViolationCount { get; set; } = 0;

        /// <summary>
        /// Curfew time display string (e.g., "10:00 PM"), empty if no curfew
        /// </summary>
        public string CurfewTime { get; set; } = "";

        /// <summary>
        /// Consecutive high compliance days for step-down progress
        /// </summary>
        public int ComplianceStreakDays { get; set; } = 0;

        /// <summary>
        /// Required consecutive days for next step-down
        /// </summary>
        public int ComplianceStreakRequired { get; set; } = 3;

        /// <summary>
        /// Outstanding parole fees owed
        /// </summary>
        public float OutstandingFees { get; set; } = 0f;
    }
}

