namespace Behind_Bars.Systems.Jail
{
    internal enum JailRecreationTier
    {
        Unknown = -1,
        None = 0,
        Lower = 1,
        Upper = 2
    }

    /// <summary>
    /// Read-only presentation snapshot produced by the authoritative jail schedule owner.
    /// RemainingRealSeconds is deliberately wall-clock time, not game minutes.
    /// </summary>
    internal sealed class JailRecreationStatus
    {
        public int AssignedCellNumber { get; set; }
        public JailRecreationTier AssignedTier { get; set; }
        public JailRecreationTier ActiveTier { get; set; }
        public bool IsAssignedTierActive { get; set; }
        public float RemainingRealSeconds { get; set; }
        public float PhaseProgress { get; set; }
    }

    internal static class JailRecreationSchedule
    {
        internal const int RecreationStartMinute = 7 * 60;
        internal const int BedtimeMinute = 22 * 60;
        private const int RecreationBlockMinutes = 2 * 60;

        private static readonly int[] LowerTierStartMinutes = { 7 * 60, 11 * 60, 15 * 60, 19 * 60 };
        private static readonly int[] UpperTierStartMinutes = { 9 * 60, 13 * 60, 17 * 60, 21 * 60 };

        internal static JailRecreationTier GetScheduledTier(int minuteOfDay)
        {
            int normalizedMinute = NormalizeMinuteOfDay(minuteOfDay);
            if (normalizedMinute < RecreationStartMinute || normalizedMinute >= BedtimeMinute)
            {
                return JailRecreationTier.None;
            }

            return ((normalizedMinute - RecreationStartMinute) / RecreationBlockMinutes) % 2 == 0
                ? JailRecreationTier.Lower
                : JailRecreationTier.Upper;
        }

        internal static int GetActiveBlockStartMinute(int minuteOfDay)
        {
            int normalizedMinute = NormalizeMinuteOfDay(minuteOfDay);
            if (GetScheduledTier(normalizedMinute) == JailRecreationTier.None)
            {
                return normalizedMinute < RecreationStartMinute ? -120 : BedtimeMinute;
            }

            return RecreationStartMinute +
                   (((normalizedMinute - RecreationStartMinute) / RecreationBlockMinutes) * RecreationBlockMinutes);
        }

        internal static int GetActiveBlockEndMinute(int minuteOfDay)
        {
            return System.Math.Min(BedtimeMinute, GetActiveBlockStartMinute(minuteOfDay) + RecreationBlockMinutes);
        }

        internal static int GetNextTierStartMinute(int currentMinuteOfDay, JailRecreationTier tier)
        {
            int normalizedMinute = NormalizeMinuteOfDay(currentMinuteOfDay);
            int[] starts = tier == JailRecreationTier.Upper ? UpperTierStartMinutes : LowerTierStartMinutes;

            foreach (int start in starts)
            {
                if (start > normalizedMinute)
                {
                    return start;
                }
            }

            return starts[0] + 1440;
        }

        internal static float GetPhaseProgress(float currentScheduleMinute, int phaseStartMinute, int targetMinute)
        {
            float duration = targetMinute - phaseStartMinute;
            if (duration <= 0f)
            {
                return 1f;
            }

            return UnityEngine.Mathf.Clamp01((currentScheduleMinute - phaseStartMinute) / duration);
        }

        internal static float GetRemainingRealSeconds(
            int currentMinute,
            float minuteProgress,
            int targetMinute,
            float secondsPerGameMinute)
        {
            float remainingGameMinutes = System.Math.Max(0f, targetMinute - (currentMinute + minuteProgress));
            return remainingGameMinutes * System.Math.Max(0f, secondsPerGameMinute);
        }

        private static int NormalizeMinuteOfDay(int minute)
        {
            int normalized = minute % 1440;
            return normalized < 0 ? normalized + 1440 : normalized;
        }
    }
}
