using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;


#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Law;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Custom JSON converter for Unity Vector3 to avoid circular reference issues
    /// Serializes Vector3 as a simple object with x, y, z properties
    /// </summary>
    public class Vector3JsonConverter : JsonConverter<Vector3>
    {
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

        public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
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
        /// Search chance: 10% per patrol encounter
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

    [Serializable]
    public class RapSheet
    {
        [JsonProperty("inmateID")]
        public string InmateID;
        [JsonProperty("fullName")]
        public string FullName;
        [JsonProperty("crimesCommited")]
        public List<CrimeInstance> CrimesCommited;
        [JsonProperty("currentParoleRecord")]
        public ParoleRecord CurrentParoleRecord;
        [JsonProperty("pastParoleRecords")]
        public List<ParoleRecord> PastParoleRecords;

        /// <summary>
        /// LSI risk assessment level - determines supervision intensity and search frequency
        /// </summary>
        [JsonProperty("lsiLevel")]
        public LSILevel LSILevel = LSILevel.None;

        /// <summary>
        /// Last LSI assessment date - tracks when risk level was last calculated
        /// </summary>
        [JsonProperty("lastLSIAssessment")]
        public DateTime LastLSIAssessment = DateTime.MinValue;

        // Non-Serialized fields
        [NonSerialized]
        public Player Player;


        public RapSheet(Player player)
        {
            this.Player = player;
            this.FullName = player.name;
            
            // Initialize collections
            if (CrimesCommited == null)
                CrimesCommited = new List<CrimeInstance>();
            if (PastParoleRecords == null)
                PastParoleRecords = new List<ParoleRecord>();
            if (string.IsNullOrEmpty(InmateID))
                InmateID = GenerateInmateID();
        }

        /// <summary>
        /// Generates a unique inmate ID for the player.
        /// </summary>
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

            if (CrimesCommited == null)
            {
                CrimesCommited = new List<CrimeInstance>();
            }

            CrimesCommited.Add(crimeInstance);
            ModLogger.Info($"Added crime to rap sheet: {crimeInstance.Description} (Severity: {crimeInstance.Severity})");

            // Only update LSI if player is currently on parole
            // Initial assessment is performed when parole starts via StartParoleWithAssessment()
            if (CurrentParoleRecord != null && CurrentParoleRecord.IsOnParole())
            {
                ModLogger.Debug($"[LSI] Updating LSI for {FullName} after crime added (on parole)");
                UpdateLSILevel();
            }

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
            if (CrimesCommited == null || CrimesCommited.Count == 0)
            {
                return LSILevel.Minimum;
            }

            int score = 0;

            // Factor 1: Number of crimes (0-20 points)
            // More crimes = higher risk
            score += Math.Min(CrimesCommited.Count * 2, 20);

            // Factor 2: Crime severity (0-30 points)
            // Average severity of all crimes
            float avgSeverity = 0f;
            foreach (var crime in CrimesCommited)
            {
                avgSeverity += crime.Severity;
            }
            avgSeverity /= CrimesCommited.Count;
            score += (int)(avgSeverity * 10);

            // Factor 3: Parole violations (0-30 points)
            // Current parole violations are a strong indicator of risk
            if (CurrentParoleRecord != null)
            {
                int violationCount = CurrentParoleRecord.GetViolationCount();
                score += Math.Min(violationCount * 5, 30);
            }

            // Factor 4: Past parole failures (0-20 points)
            // History of parole failures indicates recidivism risk
            if (PastParoleRecords != null)
            {
                score += Math.Min(PastParoleRecords.Count * 10, 20);
            }

            // Determine LSI level based on score
            // Total possible: 100 points
            if (score < 20) return LSILevel.Minimum;      // 0-19 points
            if (score < 40) return LSILevel.Medium;       // 20-39 points
            if (score < 70) return LSILevel.High;         // 40-69 points
            return LSILevel.Severe;                       // 70+ points
        }

        /// <summary>
        /// Update LSI level based on current criminal history
        /// Automatically calculates and saves the new risk assessment
        /// </summary>
        public void UpdateLSILevel()
        {
            LSILevel oldLevel = LSILevel;
            LSILevel = CalculateLSILevel();
            LastLSIAssessment = DateTime.Now;

            if (oldLevel != LSILevel)
            {
                ModLogger.Info($"[LSI] Updated LSI level for {FullName}: {oldLevel} -> {LSILevel}");
            }
            else
            {
                ModLogger.Debug($"[LSI] Reassessed LSI level for {FullName}: {LSILevel} (no change)");
            }

            SaveRapSheet();
        }

        /// <summary>
        /// Get search probability based on LSI level
        /// Used by parole officers to determine random search frequency
        /// </summary>
        /// <returns>Probability as a float between 0.0 and 1.0</returns>
        public float GetSearchProbability()
        {
            switch (LSILevel)
            {
                case LSILevel.None:
                    return 0.0f;        // 0% - Not on parole or no assessment
                case LSILevel.Minimum:
                    return 0.10f;       // 10% - Low risk
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
        /// <param name="termLengthInSeconds">Length of parole term in seconds</param>
        /// <returns>True if parole was started successfully</returns>
        public bool StartParoleWithAssessment(float termLengthInSeconds)
        {
            // Create parole record if it doesn't exist
            if (CurrentParoleRecord == null)
            {
                CurrentParoleRecord = new ParoleRecord(Player);
            }

            // Start parole
            bool success = CurrentParoleRecord.StartParole(termLengthInSeconds);

            if (success)
            {
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
                // Update LSI level after violation
                ModLogger.Info($"[LSI] Updating LSI after parole violation for {FullName}");
                UpdateLSILevel();
            }

            return success;
        }

        #endregion

        #region File Operations
        
        /// <summary>
        /// Loads the rap sheet from file.
        /// </summary>
        /// <returns>True if loaded successfully, false otherwise.</returns>
        public bool LoadRapSheet()
        {
            if (FileUtilities.Instance == null)
            {
                ModLogger.Warn("Failed to load rap sheet: FileUtilities instance is null!");
                return false;
            }

            var settings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                MaxDepth = 10,
                Converters = new List<JsonConverter> { new Vector3JsonConverter() }
            };

            // Try to load from disk first if not in cache
            string fileName = $"{Player.name}-rapsheet.json";
            if (!FileUtilities.IsFileLoaded(fileName))
            {
                FileUtilities.LoadFileFromDisk(fileName);
            }

            if (!FileUtilities.AllLoadedFiles().TryGetValue(fileName, out string json))
            {
                ModLogger.Info($"No existing rap sheet found for player {Player.name}.");
                return false;
            }

            try
            {
                // Deserialize the JSON into the RapSheet object
                JsonConvert.PopulateObject(json, this, settings);

                // Ensure collections are initialized
                if (CrimesCommited == null)
                    CrimesCommited = new List<CrimeInstance>();
                if (PastParoleRecords == null)
                    PastParoleRecords = new List<ParoleRecord>();

                ModLogger.Info($"Rap sheet loaded for {Player.name}: {GetCrimeCount()} crimes, {PastParoleRecords.Count} past parole records");
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error deserializing rap sheet: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Saves the rap sheet to file.
        /// </summary>
        /// <returns>True if saved successfully, false otherwise.</returns>
        public bool SaveRapSheet()
        {
            if (FileUtilities.Instance == null)
            {
                ModLogger.Warn("Failed to save rap sheet: FileUtilities instance is null!");
                return false;
            }

            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented,
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    MaxDepth = 10,
                    Converters = new List<JsonConverter> { new Vector3JsonConverter() }
                };

                string fileName = $"{Player.name}-rapsheet.json";
                string jsonData = JsonConvert.SerializeObject(this, settings);

                bool success = FileUtilities.AddOrUpdateFile(fileName, jsonData);

                if (success)
                {
                    ModLogger.Info($"Rap sheet saved for {Player.name}: {GetCrimeCount()} crimes, {PastParoleRecords?.Count ?? 0} past parole records");
                }

                return success;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error serializing rap sheet: {ex.Message}");
                return false;
            }
        }
        
        #endregion
    }
}
