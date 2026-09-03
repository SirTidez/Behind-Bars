using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using System.Collections.Generic;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Owns creation, expiration, and retrieval of persisted parole drug-use evidence.
    /// </summary>
    public static class DrugUseHistoryService
    {
        private const long WeedWindowMinutes = 24L * 60L;
        private const long ShroomsWindowMinutes = 24L * 60L;
        private const long CocaineWindowMinutes = 48L * 60L;
        private const long MethamphetamineWindowMinutes = 72L * 60L;

        /// <summary>Returns the current gameplay detection window for a supported category.</summary>
        public static long GetDetectionWindowMinutes(TrackedDrugType drugType)
        {
            switch (drugType)
            {
                case TrackedDrugType.Weed:
                    return WeedWindowMinutes;
                case TrackedDrugType.Shrooms:
                    return ShroomsWindowMinutes;
                case TrackedDrugType.Cocaine:
                    return CocaineWindowMinutes;
                case TrackedDrugType.Methamphetamine:
                    return MethamphetamineWindowMinutes;
                default:
                    return 0L;
            }
        }

        /// <summary>
        /// Records one supported consumption while the player has an active parole term.
        /// </summary>
        public static bool TryRecordUse(
            Player player,
            TrackedDrugType drugType,
            string productDefinitionId,
            out DrugUseRecord record)
        {
            record = null;
            if (player == null)
            {
                return false;
            }

            long detectionWindow = GetDetectionWindowMinutes(drugType);
            if (detectionWindow <= 0L)
            {
                return false;
            }

            RapSheet rapSheet = Core.GetRapSheet(player);
            if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
            {
                return false;
            }

            if (!ParoleCalendarClock.TryGetAbsoluteGameMinute(out long currentMinute))
            {
                ModLogger.Warn($"[DRUG HISTORY] Could not timestamp {drugType} use for {player.name}; native calendar is unavailable");
                return false;
            }

            rapSheet.PruneExpiredDrugUseRecords(currentMinute);
            record = new DrugUseRecord(
                drugType,
                productDefinitionId,
                currentMinute,
                currentMinute + detectionWindow);
            rapSheet.AddDrugUseRecord(record);
            Core.MarkRapSheetChanged(player);

            ModLogger.Info($"[DRUG HISTORY] Recorded {drugType} use for {player.name}; expires at native minute {record.ExpiresAtAbsoluteGameMinute}");
            return true;
        }

        /// <summary>Returns a snapshot of all non-expired records for UA evaluation.</summary>
        public static IReadOnlyList<DrugUseRecord> GetActiveRecords(RapSheet rapSheet)
        {
            if (rapSheet == null || !ParoleCalendarClock.TryGetAbsoluteGameMinute(out long currentMinute))
            {
                return new List<DrugUseRecord>();
            }

            rapSheet.PruneExpiredDrugUseRecords(currentMinute);
            return rapSheet.GetActiveDrugUseRecords(currentMinute);
        }
    }
}
