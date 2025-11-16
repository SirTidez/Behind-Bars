using System.Collections.Generic;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Calculates fines independently from jail sentences
    /// Fines are based on crime type and can have multipliers applied
    /// </summary>
    public class FineCalculator
    {
        private static FineCalculator? _instance;
        public static FineCalculator Instance => _instance ??= new FineCalculator();

        // Base fine amounts (from plan - significantly increased)
        private readonly Dictionary<string, float> _baseFines = new()
        {
            // Minor crimes (keep current)
            { "Trespassing", 25f },
            { "Vandalism", 50f },
            { "PublicIntoxication", 50f },
            { "DisturbingPeace", 25f },
            { "Speeding", 15f },
            { "RecklessDriving", 75f },
            { "BrandishingWeapon", 50f },
            { "DischargeFirearm", 50f },
            { "DrugPossessionLow", 10f },
            { "PossessingLowSeverityDrug", 10f },
            { "PossessingControlledSubstances", 5f },
            { "WeaponPossession", 50f },
            { "IllegalWeaponPossession", 50f },

            // Moderate crimes (3-5x increase)
            { "Theft", 200f },
            { "VehicleTheft", 500f },
            { "Assault", 300f },
            { "AssaultOnCivilian", 400f },
            { "VehicularAssault", 400f },
            { "DrugPossessionModerate", 100f },
            { "PossessingModerateSeverityDrug", 100f },
            { "Evading", 300f },
            { "EvadingArrest", 300f },
            { "FailureToComply", 300f },
            { "HitAndRun", 600f },
            { "ViolatingCurfew", 200f },

            // Major crimes (5-10x increase)
            { "DeadlyAssault", 1000f },
            { "AssaultOnOfficer", 2500f },
            { "Burglary", 1500f },
            { "DrugPossessionHigh", 500f },
            { "PossessingHighSeverityDrug", 500f },
            { "DrugTraffickingCrime", 2000f },
            { "DrugTrafficking", 2000f },
            { "AttemptingToSell", 1500f },
            { "WitnessIntimidation", 1500f },

            // Severe crimes (10-20x increase)
            { "Manslaughter", 5000f },
            { "Murder", 15000f } // Default for civilian, will be overridden by victim type
        };

        private FineCalculator() { }

        /// <summary>
        /// Calculate total fine amount for a player based on their crimes
        /// Prioritizes RapSheet as the source of truth (crimes are moved there after arrest)
        /// Falls back to CrimeData if RapSheet doesn't have crimes yet
        /// </summary>
        public float CalculateTotalFine(Player player, RapSheet? rapSheet = null)
        {
            if (player == null)
            {
                ModLogger.Warn("[FINE CALC] Player is null - returning $0");
                return 0f;
            }

            // Get rap sheet if not provided
            if (rapSheet == null)
            {
                rapSheet = RapSheetManager.Instance.GetRapSheet(player);
            }

            float totalFine = 0f;
            var crimeCounts = new Dictionary<string, int>();
            bool usingRapSheet = false;

            // PRIORITY 1: Try to get crimes from RapSheet (primary source after arrest)
            if (rapSheet != null)
            {
                var rapSheetCrimes = rapSheet.GetAllCrimes();
                if (rapSheetCrimes != null && rapSheetCrimes.Count > 0)
                {
                    ModLogger.Info($"[FINE CALC] Using RapSheet as source - found {rapSheetCrimes.Count} crime instances");
                    usingRapSheet = true;

                    // Group crimes by type and count them
                    var crimesByType = new Dictionary<Type, List<CrimeInstance>>();
                    foreach (var crimeInstance in rapSheetCrimes)
                    {
                        if (crimeInstance?.Crime == null)
                        {
                            ModLogger.Warn("[FINE CALC] Found null crime instance in RapSheet - skipping");
                            continue;
                        }

                        // Use GetCrimeTypeName() for categorization (needs type name for fine lookup)
                        Type crimeType = crimeInstance.Crime != null ? crimeInstance.Crime.GetType() : null;
                        if (crimeType != null)
                        {
                            if (!crimesByType.ContainsKey(crimeType))
                            {
                                crimesByType[crimeType] = new List<CrimeInstance>();
                            }
                            crimesByType[crimeType].Add(crimeInstance);
                        }
                        else
                        {
                            // If no Crime object, use type name from Description
                            string typeName = crimeInstance.GetCrimeTypeName();
                            ModLogger.Debug($"CrimeInstance has no Crime object, using inferred type: {typeName}");
                            // Calculate fine directly using inferred type name
                            float fine = 25f; // Default fine
                            if (_baseFines.TryGetValue(typeName, out float baseFine))
                            {
                                fine = baseFine;
                            }
                            totalFine += fine * crimeInstance.Severity;
                            ModLogger.Info($"[FINE CALC] Processed crime without Crime object: {typeName} = ${fine * crimeInstance.Severity:F2}");
                        }
                    }

                    ModLogger.Info($"[FINE CALC] Processing {crimesByType.Count} unique crime types from RapSheet");

                    // Process each crime type
                    foreach (var crimeGroup in crimesByType)
                    {
                        var crimeType = crimeGroup.Key;
                        var crimeInstances = crimeGroup.Value;
                        int count = crimeInstances.Count;
                        
                        // Get first crime instance - Crime should not be null here since we filtered above
                        var firstInstance = crimeInstances[0];
                        if (firstInstance.Crime == null)
                        {
                            ModLogger.Warn("[FINE CALC] Found null Crime in grouped instances - skipping");
                            continue;
                        }
                        
                        var firstCrime = firstInstance.Crime;
                        // Use GetCrimeTypeName() to properly extract crime type from Description
                        // This handles cases where Crime object is base class but Description has actual type
                        string crimeName = firstInstance.GetCrimeTypeName();
                        
                        // If GetCrimeTypeName() returned "Crime" (base class), prefer Description
                        // Description contains the actual crime type name
                        if (crimeName == "Crime" && !string.IsNullOrEmpty(firstInstance.Description))
                        {
                            // Try to map Description to crime type name
                            crimeName = MapDescriptionToCrimeTypeName(firstInstance.Description);
                            ModLogger.Info($"[FINE CALC] Crime object is base class, using Description '{firstInstance.Description}' -> mapped to '{crimeName}'");
                        }

                        ModLogger.Info($"[FINE CALC] Processing crime: {crimeName} x{count}");

                        // Handle Murder crimes with victim type
                        if (crimeName == "Murder" && firstCrime is Crimes.Murder murderCrime)
                        {
                            string victimType = murderCrime.VictimType ?? "Civilian";
                            float fine = GetMurderFine(victimType);
                            float crimeFine = fine * count;
                            totalFine += crimeFine;
                            ModLogger.Info($"[FINE CALC]   Murder ({victimType}): ${fine} x {count} = ${crimeFine:F2}");
                            continue;
                        }

                        // Get base fine for this crime
                        if (_baseFines.TryGetValue(crimeName, out float baseFine))
                        {
                            float crimeFine = baseFine * count;
                            totalFine += crimeFine;
                            crimeCounts[crimeName] = (crimeCounts.ContainsKey(crimeName) ? crimeCounts[crimeName] : 0) + count;
                            ModLogger.Info($"[FINE CALC]   {crimeName}: ${baseFine} x {count} = ${crimeFine:F2}");
                        }
                        else
                        {
                            // Unknown crime - assign moderate fine
                            float crimeFine = 25f * count;
                            totalFine += crimeFine;
                            ModLogger.Warn($"[FINE CALC] Unknown crime type: {crimeName}, using default $25 x {count} = ${crimeFine:F2}");
                        }
                    }
                }
            }

            // PRIORITY 2: Fall back to CrimeData if RapSheet doesn't have crimes (pre-arrest state)
            if (!usingRapSheet && player.CrimeData != null)
            {
                ModLogger.Info("[FINE CALC] RapSheet empty or unavailable - falling back to CrimeData");
                
                // Check if Crimes dictionary exists and has entries
                if (player.CrimeData.Crimes != null && player.CrimeData.Crimes.Count > 0)
                {
                    ModLogger.Info($"[FINE CALC] Processing {player.CrimeData.Crimes.Count} crime types from CrimeData");

                    // Count crimes by type
                    foreach (var crimeEntry in player.CrimeData.Crimes)
                    {
                        if (crimeEntry.Key == null)
                        {
                            ModLogger.Warn("[FINE CALC] Found null crime entry - skipping");
                            continue;
                        }

                        var crime = crimeEntry.Key;
                        int count = crimeEntry.Value;
                        string crimeName = crime.GetType().Name;

                        ModLogger.Info($"[FINE CALC] Processing crime: {crimeName} x{count}");

                        // Handle Murder crimes with victim type
                        if (crimeName == "Murder" && crime is Crimes.Murder murderCrime)
                        {
                            string victimType = murderCrime.VictimType ?? "Civilian";
                            float fine = GetMurderFine(victimType);
                            float crimeFine = fine * count;
                            totalFine += crimeFine;
                            ModLogger.Info($"[FINE CALC]   Murder ({victimType}): ${fine} x {count} = ${crimeFine:F2}");
                            continue;
                        }

                        // Get base fine for this crime
                        if (_baseFines.TryGetValue(crimeName, out float baseFine))
                        {
                            float crimeFine = baseFine * count;
                            totalFine += crimeFine;
                            crimeCounts[crimeName] = (crimeCounts.ContainsKey(crimeName) ? crimeCounts[crimeName] : 0) + count;
                            ModLogger.Info($"[FINE CALC]   {crimeName}: ${baseFine} x {count} = ${crimeFine:F2}");
                        }
                        else
                        {
                            // Unknown crime - assign moderate fine
                            float crimeFine = 25f * count;
                            totalFine += crimeFine;
                            ModLogger.Warn($"[FINE CALC] Unknown crime type: {crimeName}, using default $25 x {count} = ${crimeFine:F2}");
                        }
                    }
                }
                else
                {
                    ModLogger.Info("[FINE CALC] No crimes found in CrimeData.Crimes dictionary");
                }

                // Check for evaded arrest (not stored in Crimes dict)
                if (player.CrimeData.EvadedArrest)
                {
                    totalFine += 300f; // Increased from $50
                    ModLogger.Info($"[FINE CALC] Added evaded arrest fine: $300. New total: ${totalFine:F2}");
                }
            }

            if (totalFine == 0f && !usingRapSheet && (player.CrimeData == null || player.CrimeData.Crimes == null || player.CrimeData.Crimes.Count == 0))
            {
                ModLogger.Warn("[FINE CALC] No crimes found in RapSheet or CrimeData - returning $0");
                return 0f;
            }

            ModLogger.Info($"[FINE CALC] Base fine total (before multipliers): ${totalFine:F2}");

            // Apply multipliers if rap sheet is available
            if (rapSheet != null)
            {
                int offenseCount = rapSheet.GetCrimeCount();
                float repeatMultiplier = GetRepeatOffenderMultiplier(offenseCount);
                float beforeMultiplier = totalFine;
                totalFine *= repeatMultiplier;
                ModLogger.Info($"[FINE CALC] Repeat offender multiplier: {offenseCount} offenses = {repeatMultiplier}x. ${beforeMultiplier:F2} -> ${totalFine:F2}");
            }
            else
            {
                ModLogger.Info("[FINE CALC] No rap sheet available - skipping repeat offender multiplier");
            }

            ModLogger.Info($"[FINE CALC] Final calculated total fine: ${totalFine:F2}");
            return totalFine;
        }

        /// <summary>
        /// Get fine amount for Murder based on victim type
        /// </summary>
        private float GetMurderFine(string victimType)
        {
            return victimType switch
            {
                "Police" => 25000f,
                "Employee" => 20000f,
                "Civilian" => 15000f,
                _ => 15000f
            };
        }

        /// <summary>
        /// Get repeat offender multiplier for fines
        /// </summary>
        private float GetRepeatOffenderMultiplier(int offenseCount)
        {
            return offenseCount switch
            {
                1 => 1.0f, // First offense - no penalty
                2 => 1.25f, // +25% for 2nd offense
                3 => 1.5f,  // +50% for 3rd offense
                _ => 2.0f   // +100% for 4+ offenses
            };
        }

        /// <summary>
        /// Get base fine for a crime type (without multipliers)
        /// </summary>
        public float GetBaseFine(string crimeClassName, string? victimType = null)
        {
            // Handle Murder crimes
            if (crimeClassName == "Murder" && !string.IsNullOrEmpty(victimType))
            {
                return GetMurderFine(victimType);
            }

            return _baseFines.TryGetValue(crimeClassName, out float fine) ? fine : 25f;
        }

        /// <summary>
        /// Map Description (user-friendly name) to crime type name (class name) for fine lookup
        /// </summary>
        private string MapDescriptionToCrimeTypeName(string description)
        {
            // Map common descriptions to type names that match _baseFines dictionary
            return description switch
            {
                "Murder" or "Murder of a Police Officer" or "Murder of an Employee" => "Murder",
                "Involuntary Manslaughter" => "Manslaughter",
                "Assault on Civilian" => "AssaultOnCivilian",
                "Assault" => "Assault",
                "Deadly Assault" => "DeadlyAssault",
                "Assault on Officer" => "AssaultOnOfficer",
                "Theft" => "Theft",
                "Vehicle Theft" => "VehicleTheft",
                "Burglary" => "Burglary",
                "Witness Intimidation" => "WitnessIntimidation",
                "Drug Possession (Low)" => "DrugPossessionLow",
                "Drug Possession (Moderate)" => "DrugPossessionModerate",
                "Drug Possession (High)" => "DrugPossessionHigh",
                "Drug Trafficking" => "DrugTraffickingCrime",
                "Illegal Weapon Possession" => "WeaponPossession",
                "Evading Arrest" => "EvadingArrest",
                "Evading" => "Evading",
                "Failure to Comply" => "FailureToComply",
                "Hit and Run" => "HitAndRun",
                "Trespassing" => "Trespassing",
                "Vandalism" => "Vandalism",
                "Public Intoxication" => "PublicIntoxication",
                "Disturbing Peace" => "DisturbingPeace",
                "Speeding" => "Speeding",
                "Reckless Driving" => "RecklessDriving",
                "Brandishing Weapon" => "BrandishingWeapon",
                "Discharge Firearm" => "DischargeFirearm",
                _ => description.Replace(" ", "").Replace("(", "").Replace(")", "") // Fallback: remove spaces and parentheses
            };
        }
    }
}

