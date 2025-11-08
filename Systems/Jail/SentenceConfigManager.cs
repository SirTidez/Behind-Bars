using MelonLoader;
using Behind_Bars.Helpers;
using System.Collections.Generic;

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Manages sentence configuration via MelonPreferences
    /// Provides centralized access to all sentence lengths and multipliers
    /// </summary>
    public class SentenceConfigManager
    {
        private static SentenceConfigManager? _instance;
        public static SentenceConfigManager Instance => _instance ??= new SentenceConfigManager();

        private MelonPreferences_Category? _category;

        // Minor crime preferences (in game minutes)
        private MelonPreferences_Entry<float>? _trespassing;
        private MelonPreferences_Entry<float>? _vandalism;
        private MelonPreferences_Entry<float>? _publicIntoxication;
        private MelonPreferences_Entry<float>? _disturbingPeace;
        private MelonPreferences_Entry<float>? _speeding;
        private MelonPreferences_Entry<float>? _recklessDriving;
        private MelonPreferences_Entry<float>? _brandishingWeapon;
        private MelonPreferences_Entry<float>? _dischargeFirearm;
        private MelonPreferences_Entry<float>? _drugPossessionLow;
        private MelonPreferences_Entry<float>? _illegalWeaponPossession;

        // Moderate crime preferences (in game minutes)
        private MelonPreferences_Entry<float>? _theft;
        private MelonPreferences_Entry<float>? _vehicleTheft;
        private MelonPreferences_Entry<float>? _assault;
        private MelonPreferences_Entry<float>? _assaultOnCivilian;
        private MelonPreferences_Entry<float>? _vehicularAssault;
        private MelonPreferences_Entry<float>? _drugPossessionModerate;
        private MelonPreferences_Entry<float>? _evadingArrest;
        private MelonPreferences_Entry<float>? _failureToComply;
        private MelonPreferences_Entry<float>? _hitAndRun;
        private MelonPreferences_Entry<float>? _violatingCurfew;

        // Major crime preferences (in game minutes)
        private MelonPreferences_Entry<float>? _deadlyAssault;
        private MelonPreferences_Entry<float>? _assaultOnOfficer;
        private MelonPreferences_Entry<float>? _burglary;
        private MelonPreferences_Entry<float>? _drugPossessionHigh;
        private MelonPreferences_Entry<float>? _drugTrafficking;
        private MelonPreferences_Entry<float>? _attemptingToSell;
        private MelonPreferences_Entry<float>? _witnessIntimidation;

        // Severe crime preferences (in game minutes)
        private MelonPreferences_Entry<float>? _manslaughter;
        private MelonPreferences_Entry<float>? _murderCivilian;
        private MelonPreferences_Entry<float>? _murderEmployee;
        private MelonPreferences_Entry<float>? _murderPolice;

        // Multiplier preferences
        private MelonPreferences_Entry<float>? _severity1_0;
        private MelonPreferences_Entry<float>? _severity1_5;
        private MelonPreferences_Entry<float>? _severity2_0;
        private MelonPreferences_Entry<float>? _severity2_5;
        private MelonPreferences_Entry<float>? _severity3_0;
        private MelonPreferences_Entry<float>? _severity4_0;
        private MelonPreferences_Entry<float>? _repeatOffender2;
        private MelonPreferences_Entry<float>? _repeatOffender3;
        private MelonPreferences_Entry<float>? _repeatOffender4Plus;
        private MelonPreferences_Entry<float>? _unwitnessed;
        private MelonPreferences_Entry<float>? _witnessBonus;

        // Global preferences
        private MelonPreferences_Entry<float>? _globalMultiplier;
        private MelonPreferences_Entry<bool>? _enableRepeatOffenderPenalties;
        private MelonPreferences_Entry<bool>? _enableWitnessMultipliers;
        private MelonPreferences_Entry<bool>? _enableSeverityMultipliers;

        // Cache for crime name to preference mapping
        private Dictionary<string, MelonPreferences_Entry<float>?> _crimePreferenceCache = new();

        private SentenceConfigManager()
        {
            InitializePreferences();
        }

        private void InitializePreferences()
        {
            _category = MelonPreferences.CreateCategory(Constants.PREF_CATEGORY);

            // Minor crimes (defaults in game minutes)
            _trespassing = _category.CreateEntry<float>("Sentence_Trespassing", 15f, "Trespassing sentence (game minutes)");
            _vandalism = _category.CreateEntry<float>("Sentence_Vandalism", 30f, "Vandalism sentence (game minutes)");
            _publicIntoxication = _category.CreateEntry<float>("Sentence_PublicIntoxication", 30f, "Public Intoxication sentence (game minutes)");
            _disturbingPeace = _category.CreateEntry<float>("Sentence_DisturbingPeace", 15f, "Disturbing the Peace sentence (game minutes)");
            _speeding = _category.CreateEntry<float>("Sentence_Speeding", 3f, "Speeding sentence (game minutes) - Minimum");
            _recklessDriving = _category.CreateEntry<float>("Sentence_RecklessDriving", 45f, "Reckless Driving sentence (game minutes)");
            _brandishingWeapon = _category.CreateEntry<float>("Sentence_BrandishingWeapon", 30f, "Brandishing Weapon sentence (game minutes)");
            _dischargeFirearm = _category.CreateEntry<float>("Sentence_DischargeFirearm", 45f, "Discharge Firearm sentence (game minutes)");
            _drugPossessionLow = _category.CreateEntry<float>("Sentence_DrugPossessionLow", 30f, "Drug Possession (Low) sentence (game minutes)");
            _illegalWeaponPossession = _category.CreateEntry<float>("Sentence_IllegalWeaponPossession", 30f, "Illegal Weapon Possession sentence (game minutes)");

            // Moderate crimes
            _theft = _category.CreateEntry<float>("Sentence_Theft", 120f, "Theft sentence (game minutes)");
            _vehicleTheft = _category.CreateEntry<float>("Sentence_VehicleTheft", 240f, "Vehicle Theft sentence (game minutes)");
            _assault = _category.CreateEntry<float>("Sentence_Assault", 180f, "Assault sentence (game minutes)");
            _assaultOnCivilian = _category.CreateEntry<float>("Sentence_AssaultOnCivilian", 360f, "Assault on Civilian sentence (game minutes)");
            _vehicularAssault = _category.CreateEntry<float>("Sentence_VehicularAssault", 360f, "Vehicular Assault sentence (game minutes)");
            _drugPossessionModerate = _category.CreateEntry<float>("Sentence_DrugPossessionModerate", 180f, "Drug Possession (Moderate) sentence (game minutes)");
            _evadingArrest = _category.CreateEntry<float>("Sentence_EvadingArrest", 360f, "Evading Arrest sentence (game minutes)");
            _failureToComply = _category.CreateEntry<float>("Sentence_FailureToComply", 180f, "Failure to Comply sentence (game minutes)");
            _hitAndRun = _category.CreateEntry<float>("Sentence_HitAndRun", 480f, "Hit and Run sentence (game minutes)");
            _violatingCurfew = _category.CreateEntry<float>("Sentence_ViolatingCurfew", 120f, "Violating Curfew sentence (game minutes)");

            // Major crimes
            _deadlyAssault = _category.CreateEntry<float>("Sentence_DeadlyAssault", 1440f, "Deadly Assault sentence (game minutes) - 1 day");
            _assaultOnOfficer = _category.CreateEntry<float>("Sentence_AssaultOnOfficer", 2880f, "Assault on Officer sentence (game minutes) - 2 days");
            _burglary = _category.CreateEntry<float>("Sentence_Burglary", 2160f, "Burglary sentence (game minutes) - 1.5 days");
            _drugPossessionHigh = _category.CreateEntry<float>("Sentence_DrugPossessionHigh", 1440f, "Drug Possession (High) sentence (game minutes) - 1 day");
            _drugTrafficking = _category.CreateEntry<float>("Sentence_DrugTrafficking", 3600f, "Drug Trafficking sentence (game minutes) - 2.5 days");
            _attemptingToSell = _category.CreateEntry<float>("Sentence_AttemptingToSell", 2160f, "Attempting to Sell sentence (game minutes) - 1.5 days");
            _witnessIntimidation = _category.CreateEntry<float>("Sentence_WitnessIntimidation", 2880f, "Witness Intimidation sentence (game minutes) - 2 days");

            // Severe crimes
            _manslaughter = _category.CreateEntry<float>("Sentence_Manslaughter", 4320f, "Manslaughter sentence (game minutes) - 3 days");
            _murderCivilian = _category.CreateEntry<float>("Sentence_MurderCivilian", 5760f, "Murder (Civilian) sentence (game minutes) - 4 days");
            _murderEmployee = _category.CreateEntry<float>("Sentence_MurderEmployee", 6480f, "Murder (Employee) sentence (game minutes) - 4.5 days");
            _murderPolice = _category.CreateEntry<float>("Sentence_MurderPolice", 7200f, "Murder (Police) sentence (game minutes) - 5 days (Maximum)");

            // Multipliers
            _severity1_0 = _category.CreateEntry<float>("Multiplier_Severity1_0", 1.0f, "Severity 1.0 multiplier (no change)");
            _severity1_5 = _category.CreateEntry<float>("Multiplier_Severity1_5", 1.5f, "Severity 1.5 multiplier (+50%)");
            _severity2_0 = _category.CreateEntry<float>("Multiplier_Severity2_0", 2.0f, "Severity 2.0 multiplier (+100%)");
            _severity2_5 = _category.CreateEntry<float>("Multiplier_Severity2_5", 2.5f, "Severity 2.5 multiplier (+150%)");
            _severity3_0 = _category.CreateEntry<float>("Multiplier_Severity3_0", 3.0f, "Severity 3.0 multiplier (+200%)");
            _severity4_0 = _category.CreateEntry<float>("Multiplier_Severity4_0", 4.0f, "Severity 4.0 multiplier (+300%)");
            _repeatOffender2 = _category.CreateEntry<float>("Multiplier_RepeatOffender2", 1.25f, "Repeat Offender (2nd offense) multiplier (+25%)");
            _repeatOffender3 = _category.CreateEntry<float>("Multiplier_RepeatOffender3", 1.5f, "Repeat Offender (3rd offense) multiplier (+50%)");
            _repeatOffender4Plus = _category.CreateEntry<float>("Multiplier_RepeatOffender4Plus", 2.0f, "Repeat Offender (4+ offenses) multiplier (+100%)");
            _unwitnessed = _category.CreateEntry<float>("Multiplier_Unwitnessed", 0.8f, "Unwitnessed crime multiplier (-20%)");
            _witnessBonus = _category.CreateEntry<float>("Multiplier_WitnessBonus", 0.1f, "Witness bonus per additional witness (+10% per witness, max +30%)");

            // Global preferences
            _globalMultiplier = _category.CreateEntry<float>("Global_SentenceMultiplier", 1.0f, "Global multiplier for all sentences (1.0 = normal, 2.0 = double)");
            _enableRepeatOffenderPenalties = _category.CreateEntry<bool>("Enable_RepeatOffenderPenalties", true, "Enable repeat offender penalties");
            _enableWitnessMultipliers = _category.CreateEntry<bool>("Enable_WitnessMultipliers", true, "Enable witness multipliers");
            _enableSeverityMultipliers = _category.CreateEntry<bool>("Enable_SeverityMultipliers", true, "Enable severity multipliers");

            // Build crime name to preference mapping
            BuildCrimePreferenceCache();

            MelonPreferences.Save();
            ModLogger.Info("SentenceConfigManager initialized with all preferences");
        }

        /// <summary>
        /// Build cache mapping crime class names to preference entries
        /// </summary>
        private void BuildCrimePreferenceCache()
        {
            // Minor crimes
            _crimePreferenceCache["Trespassing"] = _trespassing;
            _crimePreferenceCache["Vandalism"] = _vandalism;
            _crimePreferenceCache["PublicIntoxication"] = _publicIntoxication;
            _crimePreferenceCache["DisturbingPeace"] = _disturbingPeace;
            _crimePreferenceCache["Speeding"] = _speeding;
            _crimePreferenceCache["RecklessDriving"] = _recklessDriving;
            _crimePreferenceCache["BrandishingWeapon"] = _brandishingWeapon;
            _crimePreferenceCache["DischargeFirearm"] = _dischargeFirearm;
            _crimePreferenceCache["DrugPossessionLow"] = _drugPossessionLow;
            _crimePreferenceCache["WeaponPossession"] = _illegalWeaponPossession;

            // Moderate crimes
            _crimePreferenceCache["Theft"] = _theft;
            _crimePreferenceCache["VehicleTheft"] = _vehicleTheft;
            _crimePreferenceCache["Assault"] = _assault;
            _crimePreferenceCache["AssaultOnCivilian"] = _assaultOnCivilian;
            _crimePreferenceCache["VehicularAssault"] = _vehicularAssault;
            _crimePreferenceCache["DrugPossessionModerate"] = _drugPossessionModerate;
            _crimePreferenceCache["Evading"] = _evadingArrest;
            _crimePreferenceCache["FailureToComply"] = _failureToComply;
            _crimePreferenceCache["HitAndRun"] = _hitAndRun;
            _crimePreferenceCache["ViolatingCurfew"] = _violatingCurfew;

            // Major crimes
            _crimePreferenceCache["DeadlyAssault"] = _deadlyAssault;
            _crimePreferenceCache["AssaultOnOfficer"] = _assaultOnOfficer;
            _crimePreferenceCache["Burglary"] = _burglary;
            _crimePreferenceCache["DrugPossessionHigh"] = _drugPossessionHigh;
            _crimePreferenceCache["DrugTraffickingCrime"] = _drugTrafficking;
            _crimePreferenceCache["AttemptingToSell"] = _attemptingToSell;
            _crimePreferenceCache["WitnessIntimidation"] = _witnessIntimidation;

            // Severe crimes - Murder needs special handling for victim types
            _crimePreferenceCache["Manslaughter"] = _manslaughter;
            _crimePreferenceCache["Murder"] = _murderCivilian; // Default to civilian
        }

        /// <summary>
        /// Get sentence length for a crime by its class name
        /// </summary>
        public float GetSentenceLength(string crimeClassName)
        {
            if (_crimePreferenceCache.TryGetValue(crimeClassName, out var entry) && entry != null)
            {
                return entry.Value;
            }

            // Fallback to default based on crime category
            ModLogger.Warn($"No preference found for crime: {crimeClassName}, using default");
            return GetDefaultSentenceLength(crimeClassName);
        }

        /// <summary>
        /// Get sentence length for Murder based on victim type
        /// </summary>
        public float GetMurderSentenceLength(string victimType)
        {
            return victimType switch
            {
                "Police" => _murderPolice?.Value ?? 7200f,
                "Employee" => _murderEmployee?.Value ?? 6480f,
                "Civilian" => _murderCivilian?.Value ?? 5760f,
                _ => _murderCivilian?.Value ?? 5760f
            };
        }

        /// <summary>
        /// Get default sentence length if preference doesn't exist
        /// </summary>
        private float GetDefaultSentenceLength(string crimeClassName)
        {
            // Return minimum sentence (120 minutes = 2 game hours) as safe default
            ModLogger.Warn($"[SENTENCE CONFIG] No preference found for {crimeClassName}, using minimum sentence: 120 game minutes");
            return 120f;
        }

        /// <summary>
        /// Get severity multiplier based on severity value
        /// </summary>
        public float GetSeverityMultiplier(float severity)
        {
            if (!(_enableSeverityMultipliers?.Value ?? true))
            {
                return 1.0f;
            }

            return severity switch
            {
                <= 1.0f => _severity1_0?.Value ?? 1.0f,
                <= 1.5f => _severity1_5?.Value ?? 1.5f,
                <= 2.0f => _severity2_0?.Value ?? 2.0f,
                <= 2.5f => _severity2_5?.Value ?? 2.5f,
                <= 3.0f => _severity3_0?.Value ?? 3.0f,
                _ => _severity4_0?.Value ?? 4.0f
            };
        }

        /// <summary>
        /// Get repeat offender multiplier based on offense count
        /// </summary>
        public float GetRepeatOffenderMultiplier(int offenseCount)
        {
            if (!(_enableRepeatOffenderPenalties?.Value ?? true))
            {
                return 1.0f;
            }

            return offenseCount switch
            {
                1 => 1.0f, // First offense - no penalty
                2 => _repeatOffender2?.Value ?? 1.25f,
                3 => _repeatOffender3?.Value ?? 1.5f,
                _ => _repeatOffender4Plus?.Value ?? 2.0f // 4+ offenses
            };
        }

        /// <summary>
        /// Get witness multiplier based on witness count and whether crime was witnessed
        /// </summary>
        public float GetWitnessMultiplier(int witnessCount, bool wasWitnessed)
        {
            if (!(_enableWitnessMultipliers?.Value ?? true))
            {
                return 1.0f;
            }

            if (!wasWitnessed || witnessCount == 0)
            {
                return _unwitnessed?.Value ?? 0.8f; // -20% for unwitnessed
            }

            // Base witnessed crime: 1.0 (no change)
            // Additional witnesses: +10% each, max +30%
            float bonus = Math.Min((witnessCount - 1) * (_witnessBonus?.Value ?? 0.1f), 0.3f);
            return 1.0f + bonus;
        }

        /// <summary>
        /// Get global sentence multiplier
        /// </summary>
        public float GetGlobalMultiplier()
        {
            return _globalMultiplier?.Value ?? 1.0f;
        }
    }
}

