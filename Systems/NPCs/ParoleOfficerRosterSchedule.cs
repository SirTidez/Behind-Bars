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
        // Schedule blocks are game-clock minutes, not Unity seconds. Six four-hour
        // blocks cover one 24-hour in-game day.
        private const int MinutesPerShift = 240;

        // Each entry lists the two route assignments active during that block;
        // the supervising officer is intentionally absent because it is stationary.
        private static readonly ParoleOfficerAssignment[][] PatrolShifts =
        {
            new[] { ParoleOfficerAssignment.DocksPatrol, ParoleOfficerAssignment.NorthtownPatrol },
            new[] { ParoleOfficerAssignment.WestsidePatrol, ParoleOfficerAssignment.PoliceStationPatrol },
            new[] { ParoleOfficerAssignment.UptownPatrol, ParoleOfficerAssignment.NorthtownPatrol },
            new[] { ParoleOfficerAssignment.PoliceStationPatrol, ParoleOfficerAssignment.WestsidePatrol },
            new[] { ParoleOfficerAssignment.UptownPatrol, ParoleOfficerAssignment.DocksPatrol },
            new[] { ParoleOfficerAssignment.NorthtownPatrol, ParoleOfficerAssignment.PoliceStationPatrol }
        };

        /// <summary>
        /// Checks whether a route assignment is active in the current in-game
        /// four-hour block. The schedule wraps the game clock to a 24-hour day.
        /// </summary>
        /// <param name="assignment">Route assignment to test.</param>
        /// <returns>True when the assignment is one of the two active patrols.</returns>
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

        /// <summary>Returns the current game-clock block as a 24-hour label.</summary>
        /// <returns>A <c>HH:mm-HH:mm</c> label for the active four-hour block.</returns>
        internal static string GetCurrentShiftLabel()
        {
            int currentMinute = GetCurrentMinuteOfDay();
            int startMinute = (currentMinute / MinutesPerShift) * MinutesPerShift;
            int endMinute = (startMinute + MinutesPerShift) % 1440;
            return $"{FormatTime(startMinute)}-{FormatTime(endMinute)}";
        }

        /// <summary>Checks whether an assignment is route-based rather than stationary.</summary>
        /// <param name="assignment">Assignment to classify.</param>
        /// <returns>False only for the supervising officer assignment.</returns>
        internal static bool IsPatrolAssignment(ParoleOfficerAssignment assignment)
        {
            return assignment != ParoleOfficerAssignment.PoliceStationSupervisor;
        }

        /// <summary>
        /// Reads and wraps the current game-clock minute. A missing/failed clock
        /// lookup falls back to minute zero, which selects the first block.
        /// </summary>
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

        /// <summary>Formats a wrapped day minute as a zero-padded 24-hour time.</summary>
        private static string FormatTime(int minuteOfDay)
        {
            int hours = minuteOfDay / 60;
            int minutes = minuteOfDay % 60;
            return $"{hours:00}:{minutes:00}";
        }
    }
}
