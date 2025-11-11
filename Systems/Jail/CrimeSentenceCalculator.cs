using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Calculates jail sentences based on crime types
    /// Uses SentenceConfigManager for base sentences and applies multipliers
    /// Sentences are clamped to 2 game hours minimum and 5 game days maximum
    /// </summary>
    public class CrimeSentenceCalculator
    {
        private static CrimeSentenceCalculator? _instance;
        public static CrimeSentenceCalculator Instance => _instance ??= new CrimeSentenceCalculator();

        private const float MIN_SENTENCE_MINUTES = 120f; // Minimum sentence: 2 game hours (120 game minutes)
        private const float MAX_SENTENCE_MINUTES = 7200f; // Maximum sentence: 5 game days (7200 minutes)

        private CrimeSentenceCalculator() { }

        /// <summary>
        /// Calculate jail sentence for a player based on their current crimes
        /// </summary>
        public JailSentenceData CalculateSentence(Player player, RapSheet? rapSheet = null)
        {
            var sentenceData = new JailSentenceData();

            ModLogger.Info($"[SENTENCE CALC] Starting sentence calculation for {player?.name ?? "null player"}");
            
            // Try to get crimes from RapSheet first (most reliable source)
            List<CrimeInstance> crimesToProcess = new List<CrimeInstance>();
            
            if (rapSheet != null)
            {
                var rapSheetCrimes = rapSheet.GetAllCrimes();
                ModLogger.Info($"[SENTENCE CALC] Found {rapSheetCrimes.Count} crimes in RapSheet");
                crimesToProcess.AddRange(rapSheetCrimes);
            }
            
            // Also check player.CrimeData.Crimes as fallback (in case RapSheet doesn't have them yet)
            if (player?.CrimeData != null && player.CrimeData.Crimes != null && player.CrimeData.Crimes.Count > 0)
            {
                ModLogger.Info($"[SENTENCE CALC] Found {player.CrimeData.Crimes.Count} crime types in player.CrimeData.Crimes");
                
                // Convert CrimeData.Crimes to CrimeInstance list
                foreach (var crimeEntry in player.CrimeData.Crimes)
                {
                    var crime = crimeEntry.Key;
                    int count = crimeEntry.Value;
                    
                    // Check if we already have this crime from RapSheet
                    bool alreadyAdded = crimesToProcess.Any(c => c.Crime?.GetType() == crime.GetType());
                    
                    if (!alreadyAdded)
                    {
                        // Create CrimeInstance for each count
                        for (int i = 0; i < count; i++)
                        {
                            var crimeInstance = new CrimeInstance(
                                crime: crime,
                                location: player.transform.position,
                                severity: CalculateCrimeSeverity(crime)
                            );
                            crimesToProcess.Add(crimeInstance);
                        }
                    }
                }
            }
            
            ModLogger.Info($"[SENTENCE CALC] Total crimes to process: {crimesToProcess.Count}");

            if (crimesToProcess.Count == 0)
            {
                // No crimes - return minimum sentence
                ModLogger.Warn($"[SENTENCE CALC] No crimes found - returning minimum sentence: {MIN_SENTENCE_MINUTES} game minutes");
                sentenceData.TotalGameMinutes = MIN_SENTENCE_MINUTES;
                sentenceData.BaseSentenceMinutes = MIN_SENTENCE_MINUTES;
                return sentenceData;
            }

            var configManager = SentenceConfigManager.Instance;
            var crimeSentences = new List<CrimeSentence>();

            // Group crimes by type and count them
            var crimesByType = crimesToProcess
                .Where(c => c.Crime != null)
                .GroupBy(c => c.Crime.GetType())
                .ToList();

            ModLogger.Info($"[SENTENCE CALC] Processing {crimesByType.Count} unique crime types");
            
            // Process each crime type
            foreach (var crimeGroup in crimesByType)
            {
                var crimeType = crimeGroup.Key;
                var crimeInstances = crimeGroup.ToList();
                int count = crimeInstances.Count;
                var firstCrime = crimeInstances[0].Crime;
                string crimeClassName = firstCrime.GetType().Name;

                // Get base sentence for this crime
                float baseMinutes = GetBaseSentenceForCrime(firstCrime, configManager);
                ModLogger.Info($"[SENTENCE CALC] Crime: {crimeClassName} x{count}, Base: {baseMinutes} game minutes ({GameTimeManager.FormatGameTime(baseMinutes)})");

                // Create CrimeSentence for each instance
                for (int i = 0; i < count; i++)
                {
                    var crimeInstance = crimeInstances[i];
                    var crimeSentence = new CrimeSentence
                    {
                        CrimeClassName = crimeClassName,
                        BaseMinutes = baseMinutes,
                        Severity = crimeInstance.Severity > 0 ? crimeInstance.Severity : CalculateCrimeSeverity(firstCrime),
                        WasWitnessed = crimeInstance.WasWitnessed,
                        WitnessCount = crimeInstance.WitnessIds?.Count ?? 0
                    };

                    // Handle Murder crimes with victim type
                    if (firstCrime is Crimes.Murder murderCrime)
                    {
                        crimeSentence.VictimType = murderCrime.VictimType ?? "Civilian";
                        ModLogger.Info($"[SENTENCE CALC]   Murder victim type: {crimeSentence.VictimType}");
                    }

                    crimeSentences.Add(crimeSentence);
                }
            }

            // Calculate total base sentence with diminishing returns for multiple crimes
            float totalBaseMinutes = CalculateTotalWithDiminishingReturns(crimeSentences);
            ModLogger.Info($"[SENTENCE CALC] Total base (after diminishing returns): {totalBaseMinutes} game minutes ({GameTimeManager.FormatGameTime(totalBaseMinutes)})");

            // Apply multipliers
            float severityMultiplier = CalculateSeverityMultiplier(crimeSentences, configManager);
            float repeatOffenderMultiplier = CalculateRepeatOffenderMultiplier(rapSheet, configManager);
            float witnessMultiplier = CalculateWitnessMultiplier(crimeSentences, configManager);
            float globalMultiplier = configManager.GetGlobalMultiplier();
            
            ModLogger.Info($"[SENTENCE CALC] Multipliers - Severity: {severityMultiplier}, Repeat: {repeatOffenderMultiplier}, Witness: {witnessMultiplier}, Global: {globalMultiplier}");

            // Apply all multipliers
            float totalMinutes = totalBaseMinutes;
            ModLogger.Info($"[SENTENCE CALC] Before multipliers: {totalMinutes} game minutes");
            totalMinutes *= severityMultiplier;
            ModLogger.Info($"[SENTENCE CALC] After severity ({severityMultiplier}): {totalMinutes} game minutes");
            totalMinutes *= repeatOffenderMultiplier;
            ModLogger.Info($"[SENTENCE CALC] After repeat ({repeatOffenderMultiplier}): {totalMinutes} game minutes");
            totalMinutes *= witnessMultiplier;
            ModLogger.Info($"[SENTENCE CALC] After witness ({witnessMultiplier}): {totalMinutes} game minutes");
            totalMinutes *= globalMultiplier;
            ModLogger.Info($"[SENTENCE CALC] After global ({globalMultiplier}): {totalMinutes} game minutes");

            // Clamp to min/max
            float beforeClamp = totalMinutes;
            totalMinutes = Mathf.Clamp(totalMinutes, MIN_SENTENCE_MINUTES, MAX_SENTENCE_MINUTES);
            if (beforeClamp != totalMinutes)
            {
                ModLogger.Warn($"[SENTENCE CALC] Clamped from {beforeClamp} to {totalMinutes} game minutes (min: {MIN_SENTENCE_MINUTES}, max: {MAX_SENTENCE_MINUTES})");
            }

            // Populate sentence data
            sentenceData.BaseSentenceMinutes = totalBaseMinutes;
            sentenceData.SeverityMultiplier = severityMultiplier;
            sentenceData.RepeatOffenderMultiplier = repeatOffenderMultiplier;
            sentenceData.WitnessMultiplier = witnessMultiplier;
            sentenceData.GlobalMultiplier = globalMultiplier;
            sentenceData.TotalGameMinutes = totalMinutes;

            ModLogger.Info($"Calculated sentence: {sentenceData.GetBreakdown()}");
            return sentenceData;
        }

        /// <summary>
        /// Get base sentence for a crime using SentenceConfigManager
        /// </summary>
        private float GetBaseSentenceForCrime(Crime crime, SentenceConfigManager configManager)
        {
            string crimeClassName = crime.GetType().Name;

            // Handle Murder crimes with victim type
            if (crime is Crimes.Murder murderCrime)
            {
                string victimType = murderCrime.VictimType ?? "Civilian";
                float sentence = configManager.GetMurderSentenceLength(victimType);
                ModLogger.Debug($"[SENTENCE CALC] GetBaseSentenceForCrime: Murder ({victimType}) = {sentence} game minutes");
                return sentence;
            }

            float baseSentence = configManager.GetSentenceLength(crimeClassName);
            ModLogger.Debug($"[SENTENCE CALC] GetBaseSentenceForCrime: {crimeClassName} = {baseSentence} game minutes");
            return baseSentence;
        }

        /// <summary>
        /// Calculate crime severity (used for multiplier)
        /// </summary>
        private float CalculateCrimeSeverity(Crime crime)
        {
            // Default severity based on crime type
            // This can be enhanced to use CrimeInstance.Severity if available
            string crimeName = crime.GetType().Name;

            return crimeName switch
            {
                // Minor crimes
                "Speeding" or "Trespassing" or "DisturbingPeace" => 1.0f,
                "Vandalism" or "PublicIntoxication" or "DrugPossessionLow" => 1.0f,
                "RecklessDriving" or "DischargeFirearm" => 1.5f,

                // Moderate crimes
                "Theft" or "Assault" => 1.5f,
                "VehicleTheft" or "AssaultOnCivilian" => 2.0f,
                "HitAndRun" => 2.5f,

                // Major crimes
                "DeadlyAssault" or "Burglary" => 3.0f,
                "AssaultOnOfficer" or "WitnessIntimidation" => 3.5f,
                "DrugTraffickingCrime" => 4.0f,

                // Severe crimes
                "Manslaughter" => 4.0f,
                "Murder" => 4.0f,

                _ => 1.5f // Default moderate severity
            };
        }

        /// <summary>
        /// Calculate total sentence with diminishing returns for multiple crimes
        /// Formula: First crime = 100%, second = 75%, third = 50%, fourth+ = 25%
        /// </summary>
        private float CalculateTotalWithDiminishingReturns(List<CrimeSentence> crimeSentences)
        {
            if (crimeSentences.Count == 0)
            {
                return MIN_SENTENCE_MINUTES;
            }

            // Sort by base minutes (descending) to apply diminishing returns to most serious crimes first
            var sortedCrimes = crimeSentences.OrderByDescending(c => c.BaseMinutes).ToList();

            float total = 0f;
            for (int i = 0; i < sortedCrimes.Count; i++)
            {
                float multiplier = i switch
                {
                    0 => 1.0f,   // First crime: 100%
                    1 => 0.75f,  // Second crime: 75%
                    2 => 0.5f,   // Third crime: 50%
                    _ => 0.25f    // Fourth+ crimes: 25%
                };

                float contribution = sortedCrimes[i].BaseMinutes * multiplier;
                total += contribution;
                ModLogger.Debug($"[SENTENCE CALC] Diminishing returns [{i}]: {sortedCrimes[i].CrimeClassName} ({sortedCrimes[i].BaseMinutes} min) × {multiplier} = {contribution} min");
            }

            ModLogger.Info($"[SENTENCE CALC] Diminishing returns total: {total} game minutes from {sortedCrimes.Count} crimes");
            return total;
        }

        /// <summary>
        /// Calculate severity multiplier based on average severity of crimes
        /// </summary>
        private float CalculateSeverityMultiplier(List<CrimeSentence> crimeSentences, SentenceConfigManager configManager)
        {
            if (crimeSentences.Count == 0)
            {
                return 1.0f;
            }

            // Use average severity
            float avgSeverity = crimeSentences.Average(c => c.Severity);
            return configManager.GetSeverityMultiplier(avgSeverity);
        }

        /// <summary>
        /// Calculate repeat offender multiplier based on rap sheet
        /// </summary>
        private float CalculateRepeatOffenderMultiplier(RapSheet? rapSheet, SentenceConfigManager configManager)
        {
            if (rapSheet == null)
            {
                return 1.0f;
            }

            int offenseCount = rapSheet.GetCrimeCount();
            return configManager.GetRepeatOffenderMultiplier(offenseCount);
        }

        /// <summary>
        /// Calculate witness multiplier based on crimes
        /// </summary>
        private float CalculateWitnessMultiplier(List<CrimeSentence> crimeSentences, SentenceConfigManager configManager)
        {
            if (crimeSentences.Count == 0)
            {
                return 1.0f;
            }

            // Use average witness count and whether crimes were witnessed
            int totalWitnesses = crimeSentences.Sum(c => c.WitnessCount);
            int witnessedCrimes = crimeSentences.Count(c => c.WasWitnessed);
            bool anyWitnessed = witnessedCrimes > 0;

            // Average witness count per crime
            int avgWitnessCount = totalWitnesses / crimeSentences.Count;

            return configManager.GetWitnessMultiplier(avgWitnessCount, anyWitnessed);
        }
    }
}

