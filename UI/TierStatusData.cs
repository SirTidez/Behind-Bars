namespace Behind_Bars.UI
{
    internal sealed class TierStatusData
    {
        public string HeaderText { get; set; } = "TIER STATUS";
        public string TimerLabel { get; set; } = "YOUR REC IN";
        public string TimerText { get; set; } = "00:00";
        public string ActiveTierText { get; set; } = "ALL TIERS LOCKED";
        public string AssignedTierText { get; set; } = "ASSIGNED · UNKNOWN TIER";
        public string CellText { get; set; } = "--";
        public float RemainingRealSeconds { get; set; }
        public float PhaseProgress { get; set; }
        public bool IsAssignedTierActive { get; set; }
    }

    internal static class TierStatusFormatting
    {
        internal static string FormatRealCountdown(float remainingRealSeconds)
        {
            int totalSeconds = (int)System.Math.Ceiling(System.Math.Max(0f, remainingRealSeconds));
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return $"{minutes:00}:{seconds:00}";
        }
    }
}
