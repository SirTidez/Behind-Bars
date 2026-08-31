using Behind_Bars.Systems.CrimeTracking;

namespace Behind_Bars.Systems.Parole.Conditions
{
    /// <summary>
    /// Enforces curfew hours based on LSI level.
    /// Severe: 8:00 PM, High: 10:00 PM, Medium: Midnight, Minimum: No curfew.
    /// Grace period of 15 game minutes after curfew hour.
    /// </summary>
    /// <remarks>
    /// This implementation supplies condition metadata and a time predicate; the parole
    /// monitor owns enforcement and violation recording. All times are game-minute values.
    /// </remarks>
    public class CurfewCondition : IParoleCondition
    {
        /// <inheritdoc cref="IParoleCondition.ConditionId" />
        public string ConditionId => "curfew";
        /// <inheritdoc cref="IParoleCondition.ConditionName" />
        public string ConditionName => "Curfew";
        /// <inheritdoc cref="IParoleCondition.ConditionDescription" />
        public string ConditionDescription => "Maintain curfew hours as assigned by supervision level";
        /// <inheritdoc cref="IParoleCondition.ViolationType" />
        public ViolationType ViolationType => ViolationType.CurfewViolation;
        /// <inheritdoc cref="IParoleCondition.CompliancePenalty" />
        public float CompliancePenalty => 5f;

        /// <summary>
        /// Grace period in game minutes after curfew hour before violation is triggered
        /// </summary>
        public const float GRACE_PERIOD_MINUTES = 15f;

        /// <inheritdoc cref="IParoleCondition.IsApplicable" />
        /// <remarks>Returns false for null, None, and Minimum LSI; all other LSI levels activate curfew.</remarks>
        public bool IsApplicable(RapSheet rapSheet)
        {
            // Curfew applies to all LSI levels except Minimum
            if (rapSheet == null) return false;
            return rapSheet.LSILevel != LSILevel.Minimum && rapSheet.LSILevel != LSILevel.None;
        }

        /// <summary>
        /// Get the curfew hour (in 24h format) for a given LSI level
        /// </summary>
        /// <param name="lsiLevel">LSI level selecting the curfew hour.</param>
        /// <returns>20, 22, or 0 for severe/high/medium; -1 for no curfew.</returns>
        public static int GetCurfewHour(LSILevel lsiLevel)
        {
            switch (lsiLevel)
            {
                case LSILevel.Severe: return 20;  // 8:00 PM
                case LSILevel.High: return 22;    // 10:00 PM
                case LSILevel.Medium: return 0;   // Midnight
                default: return -1;                // No curfew
            }
        }

        /// <summary>
        /// Get the curfew start minute-of-day for a given LSI level.
        /// Returns the minute of day (0-1439) when curfew starts, or -1 if no curfew.
        /// </summary>
        /// <param name="lsiLevel">LSI level selecting the curfew start.</param>
        /// <returns>Start minute after midnight, or -1 when no curfew applies.</returns>
        public static int GetCurfewStartMinuteOfDay(LSILevel lsiLevel)
        {
            int hour = GetCurfewHour(lsiLevel);
            if (hour < 0) return -1;
            return hour * 60;
        }

        /// <summary>
        /// Check if the current game time is past curfew (including grace period)
        /// </summary>
        /// <param name="lsiLevel">LSI level selecting the curfew rule.</param>
        /// <param name="currentMinuteOfDay">Current minute after midnight.</param>
        /// <returns><see langword="true"/> during the post-grace curfew window through 6:00 AM.</returns>
        /// <remarks>
        /// A midnight curfew applies from 00:15 through 06:00. Other curfews apply after the
        /// 15-minute grace period or before 06:00 for the overnight portion; the input is
        /// expected to already be normalized to a day.
        /// </remarks>
        public static bool IsPastCurfew(LSILevel lsiLevel, int currentMinuteOfDay)
        {
            int curfewStart = GetCurfewStartMinuteOfDay(lsiLevel);
            if (curfewStart < 0) return false;

            int curfewWithGrace = curfewStart + (int)GRACE_PERIOD_MINUTES;

            // Handle midnight wrapping
            if (curfewStart == 0)
            {
                // Midnight curfew: past curfew from 0:15 to 6:00 AM
                return currentMinuteOfDay >= (int)GRACE_PERIOD_MINUTES && currentMinuteOfDay < 360;
            }

            // Normal curfew: past curfew from (curfewHour + grace) until 6:00 AM next day
            if (currentMinuteOfDay >= curfewWithGrace || currentMinuteOfDay < 360)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get a display string for the curfew time
        /// </summary>
        /// <param name="lsiLevel">LSI level selecting the display time.</param>
        /// <returns>A 12-hour display time, or <c>None</c> when no curfew applies.</returns>
        public static string GetCurfewDisplayTime(LSILevel lsiLevel)
        {
            int hour = GetCurfewHour(lsiLevel);
            if (hour < 0) return "None";
            if (hour == 0) return "12:00 AM";
            if (hour <= 12) return $"{hour}:00 AM";
            return $"{hour - 12}:00 PM";
        }
    }
}
