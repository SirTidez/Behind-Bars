using System;
using Behind_Bars.Systems;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Defines the parole patrol coverage roster.  The schedule deliberately keeps
    /// the active field complement small: two route officers are outside during
    /// each four-hour block while the remaining officers stay in the courthouse.
    /// </summary>
    internal static class ParoleOfficerRosterSchedule
    {
        private const int MinutesPerShift = 240;

        private static readonly ParoleOfficerAssignment[][] PatrolShifts =
        {
            new[] { ParoleOfficerAssignment.DocksPatrol, ParoleOfficerAssignment.NorthtownPatrol },
            new[] { ParoleOfficerAssignment.WestsidePatrol, ParoleOfficerAssignment.PoliceStationPatrol },
            new[] { ParoleOfficerAssignment.UptownPatrol, ParoleOfficerAssignment.NorthtownPatrol },
            new[] { ParoleOfficerAssignment.PoliceStationPatrol, ParoleOfficerAssignment.WestsidePatrol },
            new[] { ParoleOfficerAssignment.UptownPatrol, ParoleOfficerAssignment.DocksPatrol },
            new[] { ParoleOfficerAssignment.NorthtownPatrol, ParoleOfficerAssignment.PoliceStationPatrol }
        };

        internal static bool IsPatrolActive(ParoleOfficerAssignment assignment)
        {
            if (!IsPatrolAssignment(assignment))
            {
                return false;
            }

            int currentMinute = GetCurrentMinuteOfDay();
            int shiftIndex = Math.Clamp(currentMinute / MinutesPerShift, 0, PatrolShifts.Length - 1);
            var activeAssignments = PatrolShifts[shiftIndex];

            foreach (var activeAssignment in activeAssignments)
            {
                if (activeAssignment == assignment)
                {
                    return true;
                }
            }

            return false;
        }

        internal static string GetCurrentShiftLabel()
        {
            int currentMinute = GetCurrentMinuteOfDay();
            int startMinute = (currentMinute / MinutesPerShift) * MinutesPerShift;
            int endMinute = (startMinute + MinutesPerShift) % 1440;
            return $"{FormatTime(startMinute)}-{FormatTime(endMinute)}";
        }

        internal static bool IsPatrolAssignment(ParoleOfficerAssignment assignment)
        {
            return assignment != ParoleOfficerAssignment.PoliceStationSupervisor;
        }

        private static int GetCurrentMinuteOfDay()
        {
            try
            {
                float gameMinutes = GameTimeManager.Instance?.GetCurrentGameTimeInMinutes() ?? 0f;
                int minute = (int)gameMinutes % 1440;
                return minute < 0 ? minute + 1440 : minute;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatTime(int minuteOfDay)
        {
            int hours = minuteOfDay / 60;
            int minutes = minuteOfDay % 60;
            return $"{hours:00}:{minutes:00}";
        }
    }
}
