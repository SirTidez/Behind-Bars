namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Tier labels shared by the jail schedule and the player-facing status snapshot.
    /// Unknown means the schedule has not resolved a usable cell; None means the jail
    /// is outside a recreation block.
    /// </summary>
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
        /// <summary>Authored/runtime cell number assigned to the local player.</summary>
        public int AssignedCellNumber { get; set; }

        /// <summary>Tier inferred from the player's assigned cell geometry.</summary>
        public JailRecreationTier AssignedTier { get; set; }

        /// <summary>Tier currently scheduled for recreation, or None at bedtime.</summary>
        public JailRecreationTier ActiveTier { get; set; }

        /// <summary>True when the player's assigned tier is the active recreation tier.</summary>
        public bool IsAssignedTierActive { get; set; }

        /// <summary>
        /// Wall-clock seconds until the player's assigned tier changes phase. This is
        /// deliberately not a game-minute value so the UI can show a real-time countdown.
        /// </summary>
        public float RemainingRealSeconds { get; set; }

        /// <summary>
        /// Progress through the current status phase, clamped to the inclusive range 0-1.
        /// </summary>
        public float PhaseProgress { get; set; }
    }

    /// <summary>
    /// Pure schedule calculations used by JailLifecycleManager. Inputs and returned
    /// boundaries are minutes of day; conversion to wall-clock seconds happens only in
    /// GetRemainingRealSeconds.
    /// </summary>
    internal static class JailRecreationSchedule
    {
        /// <summary>First minute of the daily recreation window (07:00).</summary>
        internal const int RecreationStartMinute = 7 * 60;

        /// <summary>First minute after the recreation window (22:00 bedtime).</summary>
        internal const int BedtimeMinute = 22 * 60;

        // Each tier occupies an alternating two-game-hour block between the daily
        // recreation start and bedtime.
        private const int RecreationBlockMinutes = 2 * 60;

        // Explicit start arrays make the next-start calculation wrap cleanly across
        // midnight and keep the player-facing countdown tied to the same schedule.
        private static readonly int[] LowerTierStartMinutes = { 7 * 60, 11 * 60, 15 * 60, 19 * 60 };
        private static readonly int[] UpperTierStartMinutes = { 9 * 60, 13 * 60, 17 * 60, 21 * 60 };

        /// <summary>
        /// Returns the tier scheduled at a minute of day, or None before 07:00 and at or
        /// after 22:00. The input is normalized into a 24-hour day before evaluation.
        /// </summary>
        /// <param name="minuteOfDay">Game-clock minute, potentially outside 0-1439.</param>
        /// <returns>The active recreation tier or None outside recreation hours.</returns>
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

        /// <summary>
        /// Returns the start of the two-hour block containing the supplied time. Before
        /// recreation begins, the sentinel -120 represents the previous lower-tier block;
        /// after bedtime, the bedtime boundary is returned.
        /// </summary>
        /// <param name="minuteOfDay">Game-clock minute to classify.</param>
        /// <returns>Block start in the normalized schedule day or the documented sentinel.</returns>
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

        /// <summary>
        /// Returns the end of the current block, clamped to bedtime so the final block
        /// cannot extend into the overnight period.
        /// </summary>
        /// <param name="minuteOfDay">Game-clock minute to classify.</param>
        /// <returns>The block or bedtime end minute.</returns>
        internal static int GetActiveBlockEndMinute(int minuteOfDay)
        {
            return System.Math.Min(BedtimeMinute, GetActiveBlockStartMinute(minuteOfDay) + RecreationBlockMinutes);
        }

        /// <summary>
        /// Finds the next start for the requested tier after the current minute. If no
        /// same-day start remains, the first start is returned on the following day by
        /// adding 1440 minutes.
        /// </summary>
        /// <param name="currentMinuteOfDay">Current normalized or raw game-clock minute.</param>
        /// <param name="tier">Tier whose next recreation block is requested.</param>
        /// <returns>The next same-day or next-day start minute.</returns>
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

        /// <summary>
        /// Calculates phase progress between two schedule-minute boundaries and clamps it
        /// to 0-1 when the current time lies outside the interval.
        /// </summary>
        /// <param name="currentScheduleMinute">Current minute including fractional progress.</param>
        /// <param name="phaseStartMinute">Start boundary of the phase.</param>
        /// <param name="targetMinute">End boundary of the phase.</param>
        /// <returns>Clamped phase progress.</returns>
        internal static float GetPhaseProgress(float currentScheduleMinute, int phaseStartMinute, int targetMinute)
        {
            float duration = targetMinute - phaseStartMinute;
            if (duration <= 0f)
            {
                return 1f;
            }

            return UnityEngine.Mathf.Clamp01((currentScheduleMinute - phaseStartMinute) / duration);
        }

        /// <summary>
        /// Converts remaining schedule distance into wall-clock seconds. The caller supplies
        /// the native game-minute progress and the current real seconds-per-game-minute
        /// conversion; paused/invalid conversions produce zero rather than negative time.
        /// </summary>
        /// <param name="currentMinute">Whole current game-clock minute.</param>
        /// <param name="minuteProgress">Fractional progress through the current minute.</param>
        /// <param name="targetMinute">Target schedule boundary.</param>
        /// <param name="secondsPerGameMinute">Native clock duration in real seconds.</param>
        /// <returns>Non-negative real seconds until the target boundary.</returns>
        internal static float GetRemainingRealSeconds(
            int currentMinute,
            float minuteProgress,
            int targetMinute,
            float secondsPerGameMinute)
        {
            float remainingGameMinutes = System.Math.Max(0f, targetMinute - (currentMinute + minuteProgress));
            return remainingGameMinutes * System.Math.Max(0f, secondsPerGameMinute);
        }

        // Keep all public schedule calculations on a 0-1439 domain while preserving
        // next-day distance through the explicit +1440 result in GetNextTierStartMinute.
        private static int NormalizeMinuteOfDay(int minute)
        {
            int normalized = minute % 1440;
            return normalized < 0 ? normalized + 1440 : normalized;
        }
    }
}
