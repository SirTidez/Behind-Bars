using Behind_Bars.Systems.CrimeTracking;

namespace Behind_Bars.Systems.Parole.Conditions
{
    /// <summary>
    /// Enforces curfew hours based on LSI level.
    /// Severe: 8:00 PM, High: 10:00 PM, Medium: Midnight, Minimum: No curfew.
    /// Grace period of 15 game minutes after curfew hour.
    /// </summary>
    public class CurfewCondition : IParoleCondition
    {
        public string ConditionId => "curfew";
        public string ConditionName => "Curfew";
        public string ConditionDescription => "Maintain curfew hours as assigned by supervision level";
        public ViolationType ViolationType => ViolationType.CurfewViolation;
        public float CompliancePenalty => 5f;

        /// <summary>
        /// Grace period in game minutes after curfew hour before violation is triggered
        /// </summary>
        public const float GRACE_PERIOD_MINUTES = 15f;

        public bool IsApplicable(RapSheet rapSheet)
        {
            // Curfew applies to all LSI levels except Minimum
            if (rapSheet == null) return false;
            return rapSheet.LSILevel != LSILevel.Minimum && rapSheet.LSILevel != LSILevel.None;
        }

        /// <summary>
        /// Get the curfew hour (in 24h format) for a given LSI level
        /// </summary>
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
        public static int GetCurfewStartMinuteOfDay(LSILevel lsiLevel)
        {
            int hour = GetCurfewHour(lsiLevel);
            if (hour < 0) return -1;
            return hour * 60;
        }

        /// <summary>
        /// Check if the current game time is past curfew (including grace period)
        /// </summary>
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
