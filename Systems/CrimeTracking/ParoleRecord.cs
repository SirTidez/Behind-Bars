using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Represents a parole record for a player, tracking parole status, violations, and time periods.
    /// </summary>
    [Serializable]
    public class ParoleRecord
    {
        [JsonProperty("isOnParole")]
        private bool isOnParole;
        [JsonProperty("paroleStartTime")]
        private float paroleStartTime;
        [JsonProperty("paroleEndTime")]
        private float paroleEndTime;
        [JsonProperty("paroleTermLengthInSeconds")]
        private float paroleTermLengthInSeconds;
        [JsonProperty("paroleViolations")]
        private List<ViolationRecord> paroleViolations;
        [JsonProperty("isPaused")]
        private bool isPaused;
        [JsonProperty("pausedRemainingTime")]
        private float pausedRemainingTime;

        // Non-Serialized fields
        [NonSerialized]
        private Player player;

        /// <summary>
        /// Parameterless constructor for JSON deserialization
        /// </summary>
        [JsonConstructor]
        public ParoleRecord()
        {
            this.paroleViolations = new List<ViolationRecord>();
            // Don't load from file during JSON deserialization
        }

        /// <summary>
        /// Initializes a new instance of ParoleRecord for the specified player.
        /// </summary>
        /// <param name="player">The player this parole record belongs to.</param>
        public ParoleRecord(Player player)
        {
            this.player = player;
            this.paroleViolations = new List<ViolationRecord>();
            LoadRecordFromFile();
        }

        /// <summary>
        /// Set the player reference after deserialization
        /// </summary>
        public void SetPlayer(Player player)
        {
            this.player = player;
        }

        #region Parole Status Methods

        /// <summary>
        /// Checks if the player is currently on parole.
        /// </summary>
        /// <returns>True if the player is on parole, false otherwise.</returns>
        public bool IsOnParole()
        {
            return isOnParole;
        }

        /// <summary>
        /// Gets the current parole status and remaining time.
        /// </summary>
        /// <returns>A tuple containing whether they're on parole and remaining time in seconds (0 if not on parole).</returns>
        public (bool isParole, float remainingTime) GetParoleStatus()
        {
            if (!isOnParole)
            {
                return (false, 0f);
            }

            // If paused, return the preserved remaining time
            if (isPaused)
            {
                return (true, pausedRemainingTime);
            }

            // Otherwise calculate from current time
            float currentTime = UnityEngine.Time.time;
            float remainingTime = paroleEndTime - currentTime;
            return (true, Mathf.Max(0f, remainingTime));
        }

        /// <summary>
        /// Starts parole for the player with the specified term length.
        /// </summary>
        /// <param name="termLengthInSeconds">The length of the parole term in seconds.</param>
        /// <returns>True if parole was started successfully, false if already on parole.</returns>
        public bool StartParole(float termLengthInSeconds)
        {
            if (isOnParole)
            {
                ModLogger.Warn($"Player {player.name} is already on parole. Cannot start new parole term.");
                return false;
            }

            isOnParole = true;
            paroleStartTime = UnityEngine.Time.time;
            paroleTermLengthInSeconds = termLengthInSeconds;
            paroleEndTime = paroleStartTime + termLengthInSeconds;

            ModLogger.Info($"Started parole for {player.name}. Term: {termLengthInSeconds} seconds. Ends at: {paroleEndTime}");
            SaveRecordToFile();
            return true;
        }

        /// <summary>
        /// Ends the current parole period for the player.
        /// </summary>
        /// <returns>True if parole was ended successfully, false if not on parole.</returns>
        public bool EndParole()
        {
            if (!isOnParole)
            {
                ModLogger.Warn($"Player {player.name} is not on parole. Cannot end parole.");
                return false;
            }

            isOnParole = false;
            float actualEndTime = UnityEngine.Time.time;

            ModLogger.Info($"Ended parole for {player.name}. Was scheduled to end at: {paroleEndTime}, actually ended at: {actualEndTime}");
            SaveRecordToFile();
            return true;
        }

        /// <summary>
        /// Checks if the player's parole has expired based on the current time.
        /// </summary>
        /// <returns>True if parole has expired, false otherwise.</returns>
        public bool IsParoleExpired()
        {
            if (!isOnParole)
            {
                return false;
            }

            return UnityEngine.Time.time >= paroleEndTime;
        }

        /// <summary>
        /// Automatically checks and ends parole if it has expired.
        /// </summary>
        /// <returns>True if parole was expired and ended, false if still active or not on parole.</returns>
        public bool CheckAndEndExpiredParole()
        {
            if (isOnParole && IsParoleExpired())
            {
                ModLogger.Info($"Parole expired for {player.name}. Ending parole automatically.");
                EndParole();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pauses the current parole term, preserving remaining time for later resumption.
        /// Used when player is incarcerated while on parole.
        /// </summary>
        /// <returns>True if parole was paused successfully, false if not on parole or already paused.</returns>
        public bool PauseParole()
        {
            if (!isOnParole)
            {
                ModLogger.Warn($"Player {player.name} is not on parole. Cannot pause.");
                return false;
            }

            if (isPaused)
            {
                ModLogger.Warn($"Player {player.name}'s parole is already paused.");
                return false;
            }

            // Calculate and store remaining time
            float currentTime = UnityEngine.Time.time;
            pausedRemainingTime = Mathf.Max(0f, paroleEndTime - currentTime);
            isPaused = true;

            ModLogger.Info($"[PAROLE] Paused parole for {player.name}. Remaining time preserved: {pausedRemainingTime}s ({pausedRemainingTime / 60f:F1} minutes)");
            SaveRecordToFile();
            return true;
        }

        /// <summary>
        /// Resumes a paused parole term with the remaining time that was preserved.
        /// </summary>
        /// <returns>True if parole was resumed successfully, false if not paused.</returns>
        public bool ResumeParole()
        {
            if (!isOnParole || !isPaused)
            {
                ModLogger.Warn($"Player {player.name}'s parole is not paused. Cannot resume.");
                return false;
            }

            // Resume with preserved remaining time
            isPaused = false;
            paroleStartTime = UnityEngine.Time.time;
            paroleEndTime = paroleStartTime + pausedRemainingTime;
            paroleTermLengthInSeconds = pausedRemainingTime;

            ModLogger.Info($"[PAROLE] Resumed parole for {player.name}. Remaining time: {pausedRemainingTime}s ({pausedRemainingTime / 60f:F1} minutes)");
            SaveRecordToFile();
            return true;
        }

        /// <summary>
        /// Extends an existing paused parole term by adding additional time.
        /// Used when player accumulates new crimes or violations while paused.
        /// </summary>
        /// <param name="additionalSeconds">Additional time to add to the paused parole term.</param>
        /// <returns>True if parole was extended successfully, false if not paused.</returns>
        public bool ExtendPausedParole(float additionalSeconds)
        {
            if (!isOnParole || !isPaused)
            {
                ModLogger.Warn($"Player {player.name}'s parole is not paused. Cannot extend.");
                return false;
            }

            pausedRemainingTime += additionalSeconds;

            ModLogger.Info($"[PAROLE] Extended paused parole for {player.name} by {additionalSeconds}s. New remaining time: {pausedRemainingTime}s ({pausedRemainingTime / 60f:F1} minutes)");
            SaveRecordToFile();
            return true;
        }

        /// <summary>
        /// Checks if parole is currently paused.
        /// </summary>
        /// <returns>True if parole is paused, false otherwise.</returns>
        public bool IsPaused()
        {
            return isPaused;
        }

        /// <summary>
        /// Gets the remaining time that was preserved when parole was paused.
        /// </summary>
        /// <returns>The paused remaining time in seconds, or 0 if not paused.</returns>
        public float GetPausedRemainingTime()
        {
            return isPaused ? pausedRemainingTime : 0f;
        }

        #endregion

        #region Violation Methods

        /// <summary>
        /// Gets the list of parole violations for this record.
        /// </summary>
        /// <returns>A list of violation records. Returns empty list if none exist.</returns>
        public List<ViolationRecord> GetViolations()
        {
            return new List<ViolationRecord>(paroleViolations);
        }

        /// <summary>
        /// Gets the number of parole violations recorded.
        /// </summary>
        /// <returns>The count of violations.</returns>
        public int GetViolationCount()
        {
            return paroleViolations == null ? 0 : paroleViolations.Count;
        }

        /// <summary>
        /// Adds a violation to the parole record.
        /// </summary>
        /// <param name="violation">The violation record to add.</param>
        /// <returns>True if the violation was added successfully.</returns>
        public bool AddViolation(ViolationRecord violation)
        {
            if (violation == null)
            {
                ModLogger.Warn("Attempted to add null violation to parole record.");
                return false;
            }

            if (paroleViolations == null)
            {
                paroleViolations = new List<ViolationRecord>();
            }

            paroleViolations.Add(violation);
            ModLogger.Info($"Added violation to {player.name}'s parole record. Total violations: {paroleViolations.Count}");
            SaveRecordToFile();
            return true;
        }

        /// <summary>
        /// Clears all violations from the parole record.
        /// </summary>
        public void ClearViolations()
        {
            if (paroleViolations != null && paroleViolations.Count > 0)
            {
                int count = paroleViolations.Count;
                paroleViolations.Clear();
                ModLogger.Info($"Cleared {count} violations from {player.name}'s parole record.");
                SaveRecordToFile();
            }
        }

        #endregion

        #region Time Information Methods

        /// <summary>
        /// Gets the start time of the current parole term.
        /// </summary>
        /// <returns>The start time in Unity time, or 0 if not on parole.</returns>
        public float GetParoleStartTime()
        {
            return isOnParole ? paroleStartTime : 0f;
        }

        /// <summary>
        /// Gets the scheduled end time of the current parole term.
        /// </summary>
        /// <returns>The end time in Unity time, or 0 if not on parole.</returns>
        public float GetParoleEndTime()
        {
            return isOnParole ? paroleEndTime : 0f;
        }

        /// <summary>
        /// Gets the total length of the parole term in seconds.
        /// </summary>
        /// <returns>The term length in seconds.</returns>
        public float GetParoleTermLength()
        {
            return paroleTermLengthInSeconds;
        }

        /// <summary>
        /// Gets the remaining time on parole in a human-readable format.
        /// </summary>
        /// <returns>A formatted string showing remaining days, hours, and minutes, or "Not on parole".</returns>
        public string GetRemainingTimeFormatted()
        {
            if (!isOnParole)
            {
                return "Not on parole";
            }

            float remaining = GetParoleStatus().remainingTime;
            if (remaining <= 0)
            {
                return "Parole expired";
            }

            int days = Mathf.FloorToInt(remaining / 86400);
            int hours = Mathf.FloorToInt((remaining % 86400) / 3600);
            int minutes = Mathf.FloorToInt((remaining % 3600) / 60);

            return $"{days}d {hours}h {minutes}m";
        }

        #endregion

        #region File Operations

        /// <summary>
        /// Saves the parole record to file.
        /// </summary>
        /// <returns>True if the save was successful, false otherwise.</returns>
        public bool SaveRecordToFile()
        {
            if (FileUtilities.Instance == null)
            {
                ModLogger.Warn($"Failed to save parole record: FileUtilities instance is null for {player.name}!");
                return false;
            }

            var settings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.Indented
            };

            bool success = FileUtilities.AddOrUpdateFile($"{player.name}-parolerecord.json", JsonConvert.SerializeObject(this, settings));
            
            if (success)
            {
                ModLogger.Info($"Saved parole record for {player.name} to file!");
            }
            
            return success;
        }

        /// <summary>
        /// Loads the parole record from file.
        /// </summary>
        /// <returns>True if the load was successful, false otherwise.</returns>
        public bool LoadRecordFromFile()
        {
            if (player == null)
            {
                ModLogger.Warn("Cannot load parole record: Player reference is null");
                return false;
            }

            if (FileUtilities.Instance == null)
            {
                ModLogger.Warn($"Failed to load parole record: FileUtilities instance is null for {player.name}!");
                return false;
            }

            var allLoadedFiles = FileUtilities.AllLoadedFiles();
            if (allLoadedFiles == null)
            {
                ModLogger.Warn($"Failed to load parole record: AllLoadedFiles() returned null for {player.name}!");
                return false;
            }

            if (!allLoadedFiles.TryGetValue($"{player.name}-parolerecord.json", out string json))
            {
                ModLogger.Info($"No existing parole record found for {player.name}. Initializing new record.");

                // Initialize default values
                isOnParole = false;
                paroleStartTime = 0f;
                paroleEndTime = 0f;
                paroleTermLengthInSeconds = 0f;
                isPaused = false;
                pausedRemainingTime = 0f;

                if (paroleViolations == null)
                {
                    paroleViolations = new List<ViolationRecord>();
                }

                return false;
            }

            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    NullValueHandling = NullValueHandling.Ignore
                };

                JsonConvert.PopulateObject(json, this, settings);
                
                ModLogger.Info($"Parole record loaded for {player.name}");
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Error attempting to deserialize parole record for {player.name}: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
