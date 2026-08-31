namespace Behind_Bars.UI
{
    /// <summary>
    /// Snapshot of the recreation schedule data rendered by <see cref="TierStatusUI"/>.
    /// The countdown fields intentionally use wall-clock seconds so the player can
    /// compare the display with real time even though most of the mod uses game time.
    /// </summary>
    internal sealed class TierStatusData
    {
        /// <summary>Short heading shown at the top of the status card.</summary>
        public string HeaderText { get; set; } = "TIER STATUS";

        /// <summary>Label describing the event represented by <see cref="TimerText"/>.</summary>
        public string TimerLabel { get; set; } = "YOUR REC IN";

        /// <summary>Preformatted real-time countdown text, normally in <c>MM:SS</c> form.</summary>
        public string TimerText { get; set; } = "00:00";

        /// <summary>Preformatted description of the tier currently using recreation time.</summary>
        public string ActiveTierText { get; set; } = "ALL TIERS LOCKED";

        /// <summary>Preformatted description of the player's assigned tier.</summary>
        public string AssignedTierText { get; set; } = "ASSIGNED · UNKNOWN TIER";

        /// <summary>Cell identifier displayed in the bounded badge on the right side of the card.</summary>
        public string CellText { get; set; } = "--";

        /// <summary>
        /// Seconds remaining according to the real-world clock, not the game's time scale.
        /// Values may be fractional and are rounded up only when formatted for display.
        /// </summary>
        public float RemainingRealSeconds { get; set; }

        /// <summary>
        /// Normalized progress for the current schedule phase. Values outside the expected
        /// <c>0</c>-<c>1</c> range are clamped by the presentation layer.
        /// </summary>
        public float PhaseProgress { get; set; }

        /// <summary>Indicates that the assigned tier is the tier currently out on recreation.</summary>
        public bool IsAssignedTierActive { get; set; }
    }

    internal static class TierStatusFormatting
    {
        /// <summary>
        /// Formats a non-negative wall-clock duration as a zero-padded minutes-and-seconds
        /// value. Ceiling preserves a visible second while a fractional second remains.
        /// </summary>
        /// <param name="remainingRealSeconds">Remaining duration in real-world seconds.</param>
        /// <returns>A zero-padded <c>MM:SS</c> countdown string.</returns>
        internal static string FormatRealCountdown(float remainingRealSeconds)
        {
            int totalSeconds = (int)System.Math.Ceiling(System.Math.Max(0f, remainingRealSeconds));
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return $"{minutes:00}:{seconds:00}";
        }
    }
}
