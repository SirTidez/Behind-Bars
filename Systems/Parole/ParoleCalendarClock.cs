using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.GameTime;
#else
using ScheduleOne.GameTime;
#endif

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Converts Schedule I's persisted native calendar into a monotonic minute value.
    /// </summary>
    public static class ParoleCalendarClock
    {
        private const int MinutesPerDay = 1440;

        /// <summary>
        /// Tries to read an absolute game minute that remains stable across save/load.
        /// </summary>
        public static bool TryGetAbsoluteGameMinute(out long absoluteGameMinute)
        {
            absoluteGameMinute = 0L;

            try
            {
                var timeManager = TimeManager.Instance;
                if (timeManager == null || timeManager.DayIndex < 0)
                {
                    return false;
                }

                int currentTime = timeManager.CurrentTime;
                int hour = currentTime / 100;
                int minute = currentTime % 100;
                if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
                {
                    ModLogger.Warn($"[PAROLE CLOCK] Native time value {currentTime} is invalid; persisted timestamp deferred");
                    return false;
                }

                absoluteGameMinute = ((long)timeManager.DayIndex * MinutesPerDay) + (hour * 60L) + minute;
                return true;
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"[PAROLE CLOCK] Native calendar unavailable: {ex.Message}");
                return false;
            }
        }
    }
}
