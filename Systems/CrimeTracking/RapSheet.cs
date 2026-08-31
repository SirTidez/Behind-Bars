using Behind_Bars.Helpers;
using Behind_Bars.Systems.Parole;
using Behind_Bars.Utils;
using Behind_Bars.Utils.Saveable;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

#if !MONO
using Il2CppNewtonsoft.Json;
#else
using Newtonsoft.Json;
#endif


#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
#if MONO
    /// <summary>
    /// Custom JSON converter for Unity Vector3 to avoid circular reference issues
    /// Serializes Vector3 as a simple object with x, y, z properties
    /// </summary>
    public class Vector3JsonConverter : JsonConverter<Vector3>
    {
        /// <inheritdoc />
        public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("x");
            writer.WriteValue(value.x);
            writer.WritePropertyName("y");
            writer.WriteValue(value.y);
            writer.WritePropertyName("z");
            writer.WriteValue(value.z);
            writer.WriteEndObject();
        }

        /// <inheritdoc />
        public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            // Unspecified coordinates default to zero for compatibility with the original
            // lightweight converter used by older RapSheet saves.
            float x = 0, y = 0, z = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                    break;

                if (reader.TokenType == JsonToken.PropertyName)
                {
                    string propertyName = reader.Value.ToString();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "x":
                            x = Convert.ToSingle(reader.Value);
                            break;
                        case "y":
                            y = Convert.ToSingle(reader.Value);
                            break;
                        case "z":
                            z = Convert.ToSingle(reader.Value);
                            break;
                    }
                }
            }

            return new Vector3(x, y, z);
        }
    }
#endif

    /// <summary>
    /// Level of Service Inventory (LSI) - Risk assessment level for parolees
    /// Determines supervision intensity and random search frequency
    /// </summary>
    [Serializable]
    public enum LSILevel : int
    {
        /// <summary>
        /// No LSI assessment recorded (default state)
        /// Search chance: 0% (not on parole or no assessment)
        /// </summary>
        None = 0,

        /// <summary>
        /// Minimum risk - Low supervision requirements
        /// Legacy design note: search chance was 10% per patrol encounter;
        /// GetSearchProbability currently returns 15%.
        /// Check-in frequency: Once per week
        /// </summary>
        Minimum = 1,

        /// <summary>
        /// Medium risk - Moderate supervision requirements
        /// Search chance: 30% per patrol encounter
        /// Check-in frequency: Twice per week
        /// </summary>
        Medium = 2,

        /// <summary>
        /// High risk - Intensive supervision requirements
        /// Search chance: 60% per patrol encounter
        /// Check-in frequency: Every other day
        /// </summary>
        High = 3,

        /// <summary>
        /// Severe risk - Maximum supervision requirements
        /// Search chance: 90% per patrol encounter
        /// Check-in frequency: Daily
        /// Electronic monitoring recommended
        /// </summary>
        Severe = 4
    }

    /// <summary>
    /// RapSheet - Criminal record for a player.
    /// Inherits from Saveable to automatically handle persistence via the game's save system.
    /// </summary>
    public class RapSheet : Saveable
    {
        // Persisted once and reused as the inmate-facing identifier. It is generated
        // locally when absent; it is not regenerated during hydration.
        [SaveableField("inmateID")]
        private string _inmateID;

        // Display metadata. FullName is refreshed from Player during OnLoaded when a
        // live player reference is available.
        [SaveableField("fullName")]
        private string _fullName;

        // Stable save identity supplied by Core when possible. Save paths fall back to
        // the persisted/display name only when this key is unavailable.
        [SaveableField("playerKey")]
        private string _playerKey;

        // The serialized key retains the historical "Commited" spelling for save
        // compatibility; the list itself is the rap sheet's canonical crime history.
        [SaveableField("crimesCommited")]
        private List<CrimeInstance> _crimesCommited;

        [SaveableField("currentParoleRecord")]
        private ParoleRecord _currentParoleRecord;

        [SaveableField("pastParoleRecords")]
        private List<ParoleRecord> _pastParoleRecords;

        /// <summary>
        /// LSI risk assessment level - determines supervision intensity and search frequency
        /// </summary>
        [SaveableField("lsiLevel")]
        private LSILevel _lsiLevel = LSILevel.None;

        /// <summary>
        /// Last LSI assessment date - tracks when risk level was last calculated
        /// </summary>
        [SaveableField("lastLSIAssessment")]
        private DateTime _lastLSIAssessment = DateTime.MinValue;

        /// <summary>
        /// Compliance-based LSI reduction. Accumulated through sustained good behavior.
        /// Subtracted from LSI score before tier determination.
        /// </summary>
        [SaveableField("complianceLSIReduction")]
        private int _complianceLSIReduction = 0;

        /// <summary>
        /// Carry-over rapport score from previous parole term
        /// </summary>
        [SaveableField("carryOverRapport")]
        private float _carryOverRapport = -1f; // -1 means no carry-over

        /// <summary>
        /// Sentence reduction modifier earned from good parole performance (0.0-0.5)
        /// </summary>
        [SaveableField("sentenceReductionModifier")]
        private float _sentenceReductionModifier = 0f;

        /// <summary>
        /// Number of successfully completed parole terms
        /// </summary>
        [SaveableField("completedParoleCount")]
        private int _completedParoleCount = 0;

        // Properties for safe access. Setters mark the Saveable dirty so callers that
        // mutate persisted state do not have to know the save framework details.
        /// <summary>
        /// Gets or sets the persisted inmate-facing identifier.
        /// </summary>
        public string InmateID
        {
            get => _inmateID;
            set
            {
                _inmateID = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the display name stored with the rap sheet.
        /// </summary>
        public string FullName
        {
            get => _fullName;
            set
            {
                _fullName = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the committed-crime history; a missing list is materialized on read.
        /// </summary>
        public List<CrimeInstance> CrimesCommited
        {
            get => _crimesCommited ??= new List<CrimeInstance>();
            set
            {
                _crimesCommited = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the stable player key used to select the save identity.
        /// </summary>
        public string PlayerKey
        {
            get => _playerKey;
            set
            {
                _playerKey = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the active parole record, if the player is currently on parole.
        /// </summary>
        public ParoleRecord CurrentParoleRecord
        {
            get => _currentParoleRecord;
            set
            {
                _currentParoleRecord = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets archived parole records retained for risk assessment history.
        /// </summary>
        public List<ParoleRecord> PastParoleRecords
        {
            get => _pastParoleRecords ??= new List<ParoleRecord>();
            set
            {
                _pastParoleRecords = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the currently persisted LSI risk tier.
        /// </summary>
        public LSILevel LSILevel
        {
            get => _lsiLevel;
            set
            {
                _lsiLevel = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the local timestamp of the last LSI calculation.
        /// </summary>
        public DateTime LastLSIAssessment
        {
            get => _lastLSIAssessment;
            set
            {
                _lastLSIAssessment = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the compliance reduction applied to the calculated LSI score.
        /// Values are clamped to the current 0-40 reduction cap.
        /// </summary>
        public int ComplianceLSIReduction
        {
            get => _complianceLSIReduction;
            set
            {
                _complianceLSIReduction = Math.Max(0, Math.Min(value, 40)); // Cap at 40 (2 tier drops max)
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the rapport score carried from the previous parole term.
        /// A negative value means no carry-over is pending.
        /// </summary>
        public float CarryOverRapport
        {
            get => _carryOverRapport;
            set
            {
                _carryOverRapport = value;
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the sentence reduction modifier earned through parole performance.
        /// Values are clamped to the current 0.0-0.5 range.
        /// </summary>
        public float SentenceReductionModifier
        {
            get => _sentenceReductionModifier;
            set
            {
                _sentenceReductionModifier = Mathf.Clamp(value, 0f, 0.5f);
                MarkChanged();
            }
        }

        /// <summary>
        /// Gets or sets the number of parole terms completed successfully.
        /// </summary>
        public int CompletedParoleCount
        {
            get => _completedParoleCount;
            set
            {
                _completedParoleCount = value;
                MarkChanged();
            }
        }

        // Non-Serialized fields
        /// <summary>
        /// Runtime player reference. It is deliberately omitted from saves and restored by SetPlayer/OnLoaded.
        /// </summary>
        [NonSerialized]
        public Player Player;

        // Saveable implementation
        // Both names derive from the same sanitized stable identity so a load does not
        // silently switch files when a display name contains path-invalid characters.
        /// <summary>
        /// Gets the save folder derived from the stable player identity.
        /// </summary>
        protected override string SaveFolderName => $"BehindBars/{GetPersistenceIdentity()}";

        /// <summary>
        /// Gets the save file name derived from the stable player identity.
        /// </summary>
        protected override string SaveFileName => $"RapSheet_{GetPersistenceIdentity()}";

        /// <summary>
        /// Parameterless constructor for deserialization.
        /// Used by SaveableAutoRegistry and the game's save system.
        /// Player reference should be set via SetPlayer() after deserialization.
        /// </summary>
        public RapSheet() : base()
        {
            // Initialize collections
            _crimesCommited = new List<CrimeInstance>();
            _pastParoleRecords = new List<ParoleRecord>();
            
            // Don't initialize SaveManager registration here - will be done by RapSheetManager
            // Don't call OnLoaded here - will be called by the save system after loading
        }

        /// <summary>
        /// Constructor with player parameter for normal creation.
        /// </summary>
        /// <param name="skipOnLoaded">If true, skips calling OnLoaded() - should be used when LoadInternal() will be called afterwards</param>
        public RapSheet(Player player, bool skipOnLoaded = false) : this()
        {
            SetPlayer(player);
            
            if (string.IsNullOrEmpty(_inmateID))
                _inmateID = GenerateInmateID();
            
            // Initialize and register with SaveManager
            InitializeSaveable();
            
            // OnLoaded will be called by LoadInternal() after loading data
            // Only call OnLoaded() here if we're NOT going to load data (new save)
            if (!skipOnLoaded)
            {
                OnLoaded();
            }
        }

        /// <summary>
        /// Set the player reference after deserialization.
        /// Should be called after loading from save data.
        /// </summary>
        /// <param name="player">The live player instance to reconnect, or null during teardown.</param>
        public void SetPlayer(Player player)
        {
            // Hydration is intentionally two-phase: Saveable restores fields first,
            // then this method reconnects the non-serialized Player and nested records.
            this.Player = player;
            if (player != null)
            {
                string resolvedPlayerKey = ResolvePlayerKey(player);
                if (!string.IsNullOrWhiteSpace(resolvedPlayerKey))
                {
                    _playerKey = resolvedPlayerKey;
                }

                if (string.IsNullOrEmpty(_fullName))
                {
                    _fullName = player.name;
                }
            }
            
            // Set player reference on ParoleRecord if it exists
            if (_currentParoleRecord != null)
            {
                _currentParoleRecord.SetPlayer(player);
            }
        }

        /// <summary>
        /// Resolves the save identity that should be used for a specific player.
        /// </summary>
        /// <param name="player">Player whose stable key or name should be examined.</param>
        /// <returns>A path-safe stable key, or <c>Unknown</c> when no identity is available.</returns>
        internal static string GetPersistenceIdentityForPlayer(Player player)
        {
            if (player == null)
            {
                return "Unknown";
            }

            return GetPersistenceIdentity(ResolvePlayerKey(player), player.name);
        }

        private string GetPersistenceIdentity()
        {
            return GetPersistenceIdentity(_playerKey, _fullName ?? Player?.name);
        }

        private static string GetPersistenceIdentity(string playerKey, string fallbackName)
        {
            // Prefer a stable player key; the name fallback is only for older/malformed
            // records. Sanitization keeps identity selection deterministic and filesystem-safe.
            string rawIdentity = !string.IsNullOrWhiteSpace(playerKey) ? playerKey : fallbackName;
            if (string.IsNullOrWhiteSpace(rawIdentity))
            {
                rawIdentity = "Unknown";
            }

            return SanitizePathSegment(rawIdentity);
        }

        private static string ResolvePlayerKey(Player player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Behind_Bars.Core.ResolvePlayerKey(player);
        }

        private static string SanitizePathSegment(string value)
        {
            // Saveable paths are derived from user/game data. Replace separators and
            // invalid filename characters rather than allowing them to create folders.
            if (string.IsNullOrWhiteSpace(value))
            {
                return "Unknown";
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar || Array.IndexOf(invalidChars, c) >= 0)
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(c);
                }
            }

            string sanitized = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
        }

        /// <summary>
        /// Generates a six-character pseudo-unique inmate ID for the player.
        /// </summary>
        /// <remarks>
        /// The current implementation truncates a new GUID, so the value is intended as
        /// a compact display identifier rather than a collision-proof primary key. Once
        /// persisted, it remains stable through save/load.
        /// </remarks>
        public string GenerateInmateID()
        {
            string inmateID = Guid.NewGuid().ToString();
            inmateID = inmateID.Substring(0, Math.Min(inmateID.Length, 6));
            return inmateID;
        }
        
        /// <summary>
        /// Adds a crime instance to the rap sheet.
        /// Automatically updates LSI level if player is currently on parole.
        /// </summary>
        /// <param name="crimeInstance">The crime instance to add.</param>
        /// <returns>True if the crime was added successfully.</returns>
        public bool AddCrime(CrimeInstance crimeInstance)
        {
            if (crimeInstance == null)
            {
                ModLogger.Warn("Attempted to add null crime instance to rap sheet.");
                return false;
            }

            if (_crimesCommited == null)
            {
                _crimesCommited = new List<CrimeInstance>();
            }

            _crimesCommited.Add(crimeInstance);
            MarkChanged();
            ModLogger.Info($"Added crime to rap sheet: {crimeInstance.GetCrimeName()} (Severity: {crimeInstance.Severity})");

            // Always calculate and update LSI when crimes are added
            // This ensures LSI is current and ready for parole assessment
            bool isOnParole = _currentParoleRecord != null && _currentParoleRecord.IsOnParole();
            ModLogger.Info($"[LSI] Crime added - calculating LSI for {FullName} (OnParole: {isOnParole}, Total Crimes: {_crimesCommited.Count})");
            UpdateLSILevel();

            return true;
        }
        
        /// <summary>
        /// Gets the total number of crimes committed.
        /// </summary>
        /// <returns>The count of crimes.</returns>
        public int GetCrimeCount()
        {
            return CrimesCommited == null ? 0 : CrimesCommited.Count;
        }
        
        /// <summary>
        /// Gets all crimes committed.
        /// </summary>
        /// <returns>A list of all crime instances.</returns>
        public List<CrimeInstance> GetAllCrimes()
        {
            return CrimesCommited == null ? new List<CrimeInstance>() : new List<CrimeInstance>(CrimesCommited);
        }

        #region LSI Risk Assessment

        /// <summary>
        /// Gets the component scores used by the current LSI calculation.
        /// </summary>
        /// <returns>
        /// A tuple containing adjusted total score, crime-count score, average-severity
        /// score, current-violation score, past-record score, and resulting tier. With
        /// no crimes, the current implementation returns zero components and Minimum.
        /// </returns>
        public (int totalScore, int crimeCountScore, int severityScore, int violationScore, int pastParoleScore, LSILevel resultingLevel) GetLSIBreakdown()
        {
            if (CrimesCommited == null || CrimesCommited.Count == 0)
            {
                return (0, 0, 0, 0, 0, LSILevel.Minimum);
            }

            // The breakdown intentionally mirrors CalculateLSILevel rather than reading
            // the cached tier. The past-record factor counts every archived record as a
            // failure, even when the record's outcome is not otherwise inspected.
            int crimeCountScore = Math.Min(CrimesCommited.Count * 2, 20);
            
            float avgSeverity = 0f;
            foreach (var crime in CrimesCommited)
            {
                avgSeverity += crime.Severity;
            }
            avgSeverity /= CrimesCommited.Count;
            int severityScore = (int)(avgSeverity * 10);
            
            int violationScore = 0;
            if (CurrentParoleRecord != null)
            {
                int violationCount = CurrentParoleRecord.GetViolationCount();
                violationScore = Math.Min(violationCount * 5, 30);
            }
            
            int pastParoleScore = 0;
            if (PastParoleRecords != null && PastParoleRecords.Count > 0)
            {
                pastParoleScore = Math.Min(PastParoleRecords.Count * 10, 20);
            }
            
            int totalScore = crimeCountScore + severityScore + violationScore + pastParoleScore;
            int adjustedScore = Math.Max(0, totalScore - _complianceLSIReduction);
            
            LSILevel resultingLevel;
            if (adjustedScore < 20) resultingLevel = LSILevel.Minimum;
            else if (adjustedScore < 40) resultingLevel = LSILevel.Medium;
            else if (adjustedScore < 70) resultingLevel = LSILevel.High;
            else resultingLevel = LSILevel.Severe;
            
            return (adjustedScore, crimeCountScore, severityScore, violationScore, pastParoleScore, resultingLevel);
        }

        /// <summary>
        /// Calculate LSI level based on rap sheet data
        /// Uses a 100-point scoring system based on:
        /// - Number of crimes (0-20 points)
        /// - Average crime severity (0-30 points)
        /// - Parole violations (0-30 points)
        /// - Past parole failures (0-20 points)
        /// </summary>
        /// <returns>Calculated LSI level</returns>
        public LSILevel CalculateLSILevel()
        {
            // This is the current reduced model: it uses all crimes, current violations,
            // and the number of archived parole records, then subtracts the persisted
            // compliance reduction. It does not inspect crime type or archived outcomes.
            ModLogger.Debug($"[LSI] Starting LSI calculation for {FullName}");

            if (CrimesCommited == null || CrimesCommited.Count == 0)
            {
                ModLogger.Debug($"[LSI] No crimes recorded - defaulting to Minimum");
                return LSILevel.Minimum;
            }

            int score = 0;

            // Factor 1: Number of crimes (0-20 points)
            // More crimes = higher risk
            int crimeCountScore = Math.Min(CrimesCommited.Count * 2, 20);
            score += crimeCountScore;
            ModLogger.Debug($"[LSI]   Factor 1 - Crime Count: {CrimesCommited.Count} crimes = {crimeCountScore} points");

            // Factor 2: Crime severity (0-30 points)
            // Average severity of all crimes
            float avgSeverity = 0f;
            foreach (var crime in CrimesCommited)
            {
                avgSeverity += crime.Severity;
            }
            avgSeverity /= CrimesCommited.Count;
            int severityScore = (int)(avgSeverity * 10);
            score += severityScore;
            ModLogger.Debug($"[LSI]   Factor 2 - Crime Severity: Avg {avgSeverity:F2} = {severityScore} points");

            // Factor 3: Parole violations (0-30 points)
            // Current parole violations are a strong indicator of risk
            int violationScore = 0;
            if (CurrentParoleRecord != null)
            {
                int violationCount = CurrentParoleRecord.GetViolationCount();
                violationScore = Math.Min(violationCount * 5, 30);
                score += violationScore;
                ModLogger.Debug($"[LSI]   Factor 3 - Parole Violations: {violationCount} violations = {violationScore} points");
            }
            else
            {
                ModLogger.Debug($"[LSI]   Factor 3 - Parole Violations: No current parole record = 0 points");
            }

            // Factor 4: Past parole failures (0-20 points)
            // History of parole failures indicates recidivism risk
            int pastParoleScore = 0;
            if (PastParoleRecords != null && PastParoleRecords.Count > 0)
            {
                pastParoleScore = Math.Min(PastParoleRecords.Count * 10, 20);
                score += pastParoleScore;
                ModLogger.Debug($"[LSI]   Factor 4 - Past Parole Failures: {PastParoleRecords.Count} failures = {pastParoleScore} points");
            }
            else
            {
                ModLogger.Debug($"[LSI]   Factor 4 - Past Parole Failures: No past records = 0 points");
            }

            // Apply compliance-based LSI reduction (good behavior step-down)
            int rawScore = score;
            if (_complianceLSIReduction > 0)
            {
                score = Math.Max(0, score - _complianceLSIReduction);
                ModLogger.Debug($"[LSI]   Compliance Reduction: -{_complianceLSIReduction} (raw {rawScore} → adjusted {score})");
            }

            // Determine LSI level based on score
            // Total possible: 100 points
            LSILevel calculatedLevel;
            if (score < 20) calculatedLevel = LSILevel.Minimum;      // 0-19 points
            else if (score < 40) calculatedLevel = LSILevel.Medium;  // 20-39 points
            else if (score < 70) calculatedLevel = LSILevel.High;    // 40-69 points
            else calculatedLevel = LSILevel.Severe;                   // 70+ points

            ModLogger.Debug($"[LSI] Total Score: {score}/100 (raw: {rawScore}) → LSI Level: {calculatedLevel}");

            return calculatedLevel;
        }

        /// <summary>
        /// Update LSI level based on current criminal history
        /// Automatically calculates and saves the new risk assessment
        /// </summary>
        public void UpdateLSILevel()
        {
            LSILevel oldLevel = _lsiLevel;
            ModLogger.Info($"[LSI] === Starting LSI Update for {FullName} ===");
            ModLogger.Info($"[LSI] Previous Level: {oldLevel}");

            _lsiLevel = CalculateLSILevel();
            _lastLSIAssessment = DateTime.Now;
            MarkChanged(); // Mark as changed for saving

            if (oldLevel != _lsiLevel)
            {
                ModLogger.Info($"[LSI] ✓ LSI Level Changed: {oldLevel} -> {_lsiLevel}");
            }
            else
            {
                ModLogger.Info($"[LSI] ✓ LSI Level Unchanged: {_lsiLevel}");
            }

            // Game's save system handles saving automatically - no manual file saving needed
            ModLogger.Info($"[LSI] LSI updated - game will save automatically");

            ModLogger.Info($"[LSI] === LSI Update Complete ===");
        }

        /// <summary>
        /// Get search probability based on LSI level
        /// Used by parole officers to determine random search frequency
        /// </summary>
        /// <returns>Probability as a float between 0.0 and 1.0</returns>
        public float GetSearchProbability()
        {
            // Keep this explicit mapping in sync with runtime behavior. The enum's
            // Minimum XML text still says 10%, but the current implementation returns
            // 15% for Minimum; this comment records the intentional-as-is mismatch.
            switch (LSILevel)
            {
                case LSILevel.None:
                    return 0.0f;        // 0% - Not on parole or no assessment
                case LSILevel.Minimum:
                    return 0.15f;       // 15% - Low risk (increased from 10%)
                case LSILevel.Medium:
                    return 0.30f;       // 30% - Moderate risk
                case LSILevel.High:
                    return 0.60f;       // 60% - High risk
                case LSILevel.Severe:
                    return 0.90f;       // 90% - Maximum risk
                default:
                    return 0.0f;
            }
        }

        /// <summary>
        /// Get human-readable description of current LSI level
        /// </summary>
        /// <returns>Description string</returns>
        public string GetLSIDescription()
        {
            switch (LSILevel)
            {
                case LSILevel.None:
                    return "No assessment";
                case LSILevel.Minimum:
                    return "Minimum risk - Low supervision";
                case LSILevel.Medium:
                    return "Medium risk - Moderate supervision";
                case LSILevel.High:
                    return "High risk - Intensive supervision";
                case LSILevel.Severe:
                    return "Severe risk - Maximum supervision";
                default:
                    return "Unknown";
            }
        }

        /// <summary>
        /// Start parole for this player with initial LSI assessment
        /// This is a convenience method that combines parole start and LSI calculation
        /// </summary>
        /// <param name="termLengthInGameMinutes">Length of parole term in game minutes</param>
        /// <returns>True if parole was started successfully</returns>
        public bool StartParoleWithAssessment(float termLengthInGameMinutes)
        {
            // Create parole record if it doesn't exist
            if (CurrentParoleRecord == null)
            {
                CurrentParoleRecord = new ParoleRecord(Player);
            }

            // Initialize rapport from carry-over if available
            if (_carryOverRapport >= 0f)
            {
                // Blend carry-over with default: 70% carry-over, 30% default (50)
                float blendedRapport = (_carryOverRapport * 0.7f) + (50f * 0.3f);
                CurrentParoleRecord.InitializeRapportFromCarryOver(blendedRapport);
                ModLogger.Info($"[RAPPORT] Initialized rapport from carry-over: {_carryOverRapport:F1} → blended {blendedRapport:F1}");
                _carryOverRapport = -1f; // Clear carry-over after use
            }

            // Start parole (now uses game minutes)
            bool success = CurrentParoleRecord.StartParole(termLengthInGameMinutes);

            if (success)
            {
                // Mark RapSheet as changed since ParoleRecord was modified
                MarkChanged();
                
                // Perform initial LSI assessment when parole starts
                ModLogger.Info($"[LSI] Performing initial LSI assessment for {FullName} at parole start");
                UpdateLSILevel();
            }

            return success;
        }

        /// <summary>
        /// Add a parole violation and update LSI level
        /// This is a convenience method that combines violation recording and LSI recalculation
        /// </summary>
        /// <param name="violation">The violation record to add</param>
        /// <returns>True if violation was added successfully</returns>
        public bool AddParoleViolation(ViolationRecord violation)
        {
            if (CurrentParoleRecord == null)
            {
                ModLogger.Warn($"Cannot add parole violation for {FullName} - not on parole");
                return false;
            }

            bool success = CurrentParoleRecord.AddViolation(violation);

            if (success)
            {
                // Mark RapSheet as changed since ParoleRecord was modified
                MarkChanged();
                
                // Update LSI level after violation
                ModLogger.Info($"[LSI] Updating LSI after parole violation for {FullName}");
                UpdateLSILevel();
            }

            return success;
        }

        /// <summary>
        /// Apply one LSI step-down (reduces effective LSI score by 20 points, max 40).
        /// Called when sustained good compliance is achieved.
        /// </summary>
        public void ApplyLSIStepDown()
        {
            int oldReduction = _complianceLSIReduction;
            _complianceLSIReduction = Math.Min(_complianceLSIReduction + 20, 40);
            MarkChanged();
            ModLogger.Info($"[LSI] Applied step-down for {FullName}: reduction {oldReduction} → {_complianceLSIReduction}");
        }

        /// <summary>
        /// Archive the current parole record to past records
        /// Moves CurrentParoleRecord to PastParoleRecords and clears CurrentParoleRecord.
        /// Carries over rapport score for next parole term.
        /// </summary>
        /// <returns>True if archived successfully, false if no current parole record exists</returns>
        public bool ArchiveCurrentParoleRecord()
        {
            if (_currentParoleRecord == null)
            {
                ModLogger.Debug($"No current parole record to archive for {FullName}");
                return false;
            }

            // Carry over rapport score for next parole term
            _carryOverRapport = _currentParoleRecord.GetRapportScore();
            ModLogger.Info($"[RAPPORT] Carrying over rapport score {_carryOverRapport:F1} for {FullName}");

            // Ensure PastParoleRecords list exists
            if (_pastParoleRecords == null)
            {
                _pastParoleRecords = new List<ParoleRecord>();
            }

            // Add current record to past records
            _pastParoleRecords.Add(_currentParoleRecord);
            ModLogger.Info($"Archived parole record for {FullName} - Total past records: {_pastParoleRecords.Count}");

            // Clear current parole record
            _currentParoleRecord = null;
            MarkChanged(); // Mark as changed for saving

            return true;
        }

        #region ParoleRecord Helper Methods

        /// <summary>
        /// Helper method to start parole on the current parole record.
        /// Automatically marks the RapSheet as changed.
        /// </summary>
        public bool StartParole(float termLengthInGameMinutes)
        {
            if (CurrentParoleRecord == null)
            {
                CurrentParoleRecord = new ParoleRecord(Player);
            }

            bool success = CurrentParoleRecord.StartParole(termLengthInGameMinutes);
            if (success)
            {
                MarkChanged();
            }
            return success;
        }

        /// <summary>
        /// Helper method to end parole on the current parole record.
        /// Automatically marks the RapSheet as changed.
        /// </summary>
        public bool EndParole()
        {
            if (CurrentParoleRecord == null)
            {
                return false;
            }

            bool success = CurrentParoleRecord.EndParole();
            if (success)
            {
                MarkChanged();
            }
            return success;
        }

        /// <summary>
        /// Helper method to pause parole on the current parole record.
        /// Automatically marks the RapSheet as changed.
        /// </summary>
        public bool PauseParole()
        {
            if (CurrentParoleRecord == null)
            {
                return false;
            }

            bool success = CurrentParoleRecord.PauseParole();
            if (success)
            {
                MarkChanged();
            }
            return success;
        }

        /// <summary>
        /// Helper method to resume parole on the current parole record.
        /// Automatically marks the RapSheet as changed.
        /// </summary>
        public bool ResumeParole()
        {
            if (CurrentParoleRecord == null)
            {
                return false;
            }

            bool success = CurrentParoleRecord.ResumeParole();
            if (success)
            {
                MarkChanged();
            }
            return success;
        }

        /// <summary>
        /// Helper method to extend paused parole on the current parole record.
        /// Automatically marks the RapSheet as changed.
        /// </summary>
        public bool ExtendPausedParole(float additionalGameMinutes)
        {
            if (CurrentParoleRecord == null)
            {
                return false;
            }

            bool success = CurrentParoleRecord.ExtendPausedParole(additionalGameMinutes);
            if (success)
            {
                MarkChanged();
            }
            return success;
        }

        #endregion

        /// <summary>
        /// Called after data is loaded from JSON.
        /// Initializes collections and validates loaded data.
        /// </summary>
        protected override void OnLoaded()
        {
            base.OnLoaded();

            // Saveable can legitimately leave newer collections null when loading an
            // older schema. Recreate them before any identity or parole hydration work.
            // Initialize collections if null
            if (_crimesCommited == null)
                _crimesCommited = new List<CrimeInstance>();
            
            if (_pastParoleRecords == null)
                _pastParoleRecords = new List<ParoleRecord>();

            // Generate InmateID if missing
            if (string.IsNullOrEmpty(_inmateID))
                _inmateID = GenerateInmateID();

            // Update FullName from player if available
            if (Player != null && !string.IsNullOrEmpty(Player.name))
            {
                _fullName = Player.name;
                string resolvedPlayerKey = ResolvePlayerKey(Player);
                if (!string.IsNullOrWhiteSpace(resolvedPlayerKey))
                {
                    _playerKey = resolvedPlayerKey;
                }
            }

            // ParoleRecord.Player is non-serialized, so restore it for the active record
            // and every archived record before callers use officer/parole behavior.
            if (_currentParoleRecord != null && Player != null)
                _currentParoleRecord.SetPlayer(Player);

            if (_pastParoleRecords != null && Player != null)
            {
                foreach (var pastRecord in _pastParoleRecords)
                {
                    if (pastRecord != null)
                        pastRecord.SetPlayer(Player);
                }
            }

            // Conditions persist as IDs, while the runtime manager owns live condition
            // objects. Restore is best-effort; malformed/missing managers do not discard
            // the saved RapSheet and are reported through the existing warning path.
            if (_currentParoleRecord != null && _currentParoleRecord.IsOnParole())
            {
                try
                {
                    var savedConditionIds = _currentParoleRecord.GetActiveConditionIds();
                    if (savedConditionIds != null && savedConditionIds.Count > 0)
                    {
                        var conditionManager = Behind_Bars.Core.ResolveParoleConditionManager();
                        if (conditionManager != null)
                        {
                            conditionManager.RestoreConditionsFromIds(savedConditionIds);
                            ModLogger.Debug($"[SAVEABLE] Restored {savedConditionIds.Count} parole conditions from save");
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Warn($"[SAVEABLE] Failed to restore parole conditions: {ex.Message}");
                }
            }

            ModLogger.Debug($"[SAVEABLE] RapSheet loaded for {_fullName} - Crimes: {_crimesCommited.Count}, LSI: {_lsiLevel}");
        }

        /// <summary>
        /// Called before data is saved to JSON.
        /// Performs any cleanup or finalization needed before saving.
        /// </summary>
        protected override void OnSaved()
        {
            base.OnSaved();
            // No normalization is performed here. SaveableField values are already the
            // persisted contract, and changing them at this hook could alter old saves.
        }

        #endregion
    }
}
