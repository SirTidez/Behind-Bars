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
