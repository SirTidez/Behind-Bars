using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Utils.Saveable;
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
    /// Uses SaveableField attributes for automatic serialization by SaveableSerializer.
    /// </summary>
    [Serializable]
    public class ParoleRecord
    {
        [SaveableField("isOnParole")]
        private bool isOnParole;

        [SaveableField("paroleStartTime")]
        private float paroleStartTime; // Now stores game time (game minutes)

        [SaveableField("paroleEndTime")]
        private float paroleEndTime; // Now stores game time (game minutes)

        [SaveableField("paroleTermLengthInSeconds")]
        private float paroleTermLengthInSeconds; // Kept for JSON compatibility, but now stores game minutes

        [SaveableField("paroleViolations")]
        private List<ViolationRecord> paroleViolations;

        [SaveableField("isPaused")]
        private bool isPaused;

        [SaveableField("pausedRemainingTime")]
        private float pausedRemainingTime; // Now stores game time (game minutes)

        [SaveableField("lastCheckInGameTime")]
        private float lastCheckInGameTime; // Last check-in time (game minutes)

        [SaveableField("checkInCount")]
        private int checkInCount; // Total number of check-ins

        [SaveableField("missedCheckIns")]
        private int missedCheckIns; // Number of missed check-ins

        [SaveableField("complianceScore")]
        private float complianceScore; // Compliance score (0-100)

        [SaveableField("lastInteractionGameTime")]
        private float lastInteractionGameTime; // Last interaction with officer (game minutes)

        // Non-Serialized fields
        [NonSerialized]
        private Player player;

        /// <summary>
        /// Parameterless constructor for serialization
        /// </summary>
        public ParoleRecord()
        {
            this.paroleViolations = new List<ViolationRecord>();
            this.checkInCount = 0;
            this.missedCheckIns = 0;
            this.complianceScore = 100f; // Start with perfect compliance
            this.lastCheckInGameTime = 0f;
            this.lastInteractionGameTime = 0f;
        }

        /// <summary>
        /// Initializes a new instance of ParoleRecord for the specified player.
        /// </summary>
        /// <param name="player">The player this parole record belongs to.</param>
        public ParoleRecord(Player player)
        {
            this.player = player;
            this.paroleViolations = new List<ViolationRecord>();
            this.checkInCount = 0;
            this.missedCheckIns = 0;
            this.complianceScore = 100f; // Start with perfect compliance
            this.lastCheckInGameTime = 0f;
            this.lastInteractionGameTime = 0f;
            // Game's save system handles loading automatically - no manual file loading needed
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
        /// <returns>A tuple containing whether they're on parole and remaining time in game minutes (0 if not on parole).</returns>
        public (bool isParole, float remainingTime) GetParoleStatus()
        {
            if (!isOnParole)
            {
                return (false, 0f);
            }

            // If paused, return the preserved remaining time (in game minutes)
            if (isPaused)
            {
                return (true, pausedRemainingTime);
            }

            // Otherwise calculate from current game time
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float remainingTime = paroleEndTime - currentGameTime;
            return (true, Mathf.Max(0f, remainingTime));
        }

        /// <summary>
        /// Starts parole for the player with the specified term length.
        /// </summary>
        /// <param name="termLengthInGameMinutes">The length of the parole term in game minutes.</param>
        /// <returns>True if parole was started successfully, false if already on parole.</returns>
        public bool StartParole(float termLengthInGameMinutes)
        {
            if (isOnParole)
            {
                ModLogger.Warn($"Player {player.name} is already on parole. Cannot start new parole term.");
                return false;
            }

            isOnParole = true;
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            paroleStartTime = currentGameTime;
            paroleTermLengthInSeconds = termLengthInGameMinutes; // Store in game minutes (kept name for JSON compatibility)
            paroleEndTime = paroleStartTime + termLengthInGameMinutes;

            ModLogger.Info($"Started parole for {player.name}. Term: {termLengthInGameMinutes} game minutes ({GameTimeManager.FormatGameTime(termLengthInGameMinutes)}). Ends at game time: {paroleEndTime}");
            // Game's save system handles saving automatically - no manual file saving needed
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
            float actualEndGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();

            ModLogger.Info($"Ended parole for {player.name}. Was scheduled to end at game time: {paroleEndTime}, actually ended at game time: {actualEndGameTime}");
            // Game's save system handles saving automatically - no manual file saving needed
            return true;
        }

        /// <summary>
        /// Checks if the player's parole has expired based on the current game time.
        /// </summary>
        /// <returns>True if parole has expired, false otherwise.</returns>
        public bool IsParoleExpired()
        {
            if (!isOnParole)
            {
                return false;
            }

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            return currentGameTime >= paroleEndTime;
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

            // Calculate and store remaining time (in game minutes)
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            pausedRemainingTime = Mathf.Max(0f, paroleEndTime - currentGameTime);
            isPaused = true;

            ModLogger.Info($"[PAROLE] Paused parole for {player.name}. Remaining time preserved: {pausedRemainingTime} game minutes ({GameTimeManager.FormatGameTime(pausedRemainingTime)})");
            // Game's save system handles saving automatically - no manual file saving needed
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

            // Resume with preserved remaining time (in game minutes)
            isPaused = false;
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            paroleStartTime = currentGameTime;
            paroleEndTime = paroleStartTime + pausedRemainingTime;
            paroleTermLengthInSeconds = pausedRemainingTime; // Store in game minutes

            ModLogger.Info($"[PAROLE] Resumed parole for {player.name}. Remaining time: {pausedRemainingTime} game minutes ({GameTimeManager.FormatGameTime(pausedRemainingTime)})");
            // Game's save system handles saving automatically - no manual file saving needed
            return true;
        }

        /// <summary>
        /// Extends an existing paused parole term by adding additional time.
        /// Used when player accumulates new crimes or violations while paused.
        /// </summary>
        /// <param name="additionalGameMinutes">Additional time to add to the paused parole term (in game minutes).</param>
        /// <returns>True if parole was extended successfully, false if not paused.</returns>
        public bool ExtendPausedParole(float additionalGameMinutes)
        {
            if (!isOnParole || !isPaused)
            {
                ModLogger.Warn($"Player {player.name}'s parole is not paused. Cannot extend.");
                return false;
            }

            pausedRemainingTime += additionalGameMinutes;

            ModLogger.Info($"[PAROLE] Extended paused parole for {player.name} by {additionalGameMinutes} game minutes ({GameTimeManager.FormatGameTime(additionalGameMinutes)}). New remaining time: {pausedRemainingTime} game minutes ({GameTimeManager.FormatGameTime(pausedRemainingTime)})");
            // Game's save system handles saving automatically - no manual file saving needed
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
            // Game's save system handles saving automatically - no manual file saving needed
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
                // Game's save system handles saving automatically - no manual file saving needed
            }
        }

        #endregion

        #region Time Information Methods

        /// <summary>
        /// Gets the start time of the current parole term.
        /// </summary>
        /// <returns>The start time in game minutes, or 0 if not on parole.</returns>
        public float GetParoleStartTime()
        {
            return isOnParole ? paroleStartTime : 0f;
        }

        /// <summary>
        /// Gets the scheduled end time of the current parole term.
        /// </summary>
        /// <returns>The end time in game minutes, or 0 if not on parole.</returns>
        public float GetParoleEndTime()
        {
            return isOnParole ? paroleEndTime : 0f;
        }

        /// <summary>
        /// Gets the total length of the parole term in game minutes.
        /// </summary>
        /// <returns>The term length in game minutes.</returns>
        public float GetParoleTermLength()
        {
            return paroleTermLengthInSeconds; // Actually stores game minutes (kept name for JSON compatibility)
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

            float remaining = GetParoleStatus().remainingTime; // In game minutes
            if (remaining <= 0)
            {
                return "Parole expired";
            }

            // Format using GameTimeManager
            return GameTimeManager.FormatGameTime(remaining);
        }

        #endregion

        #region Check-In Methods

        /// <summary>
        /// Record a check-in for the parolee
        /// </summary>
        /// <returns>True if check-in was recorded successfully</returns>
        public bool RecordCheckIn()
        {
            if (!isOnParole)
            {
                ModLogger.Warn($"Player {player.name} is not on parole. Cannot record check-in.");
                return false;
            }

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            lastCheckInGameTime = currentGameTime;
            lastInteractionGameTime = currentGameTime;
            checkInCount++;

            // Update compliance score based on check-in (positive factor)
            complianceScore = Mathf.Min(100f, complianceScore + 2f);

            ModLogger.Info($"Recorded check-in for {player.name}. Total check-ins: {checkInCount}");
            // Game's save system handles saving automatically
            return true;
        }

        /// <summary>
        /// Get the last check-in time
        /// </summary>
        /// <returns>Last check-in time in game minutes, or 0 if never checked in</returns>
        public float GetLastCheckInGameTime()
        {
            return lastCheckInGameTime;
        }

        /// <summary>
        /// Get the total number of check-ins
        /// </summary>
        /// <returns>Check-in count</returns>
        public int GetCheckInCount()
        {
            return checkInCount;
        }

        /// <summary>
        /// Record a missed check-in
        /// </summary>
        public void RecordMissedCheckIn()
        {
            missedCheckIns++;
            // Decrease compliance score for missed check-in
            complianceScore = Mathf.Max(0f, complianceScore - 5f);
            ModLogger.Info($"Recorded missed check-in for {player.name}. Total missed: {missedCheckIns}");
            // Game's save system handles saving automatically
        }

        /// <summary>
        /// Get the number of missed check-ins
        /// </summary>
        /// <returns>Missed check-in count</returns>
        public int GetMissedCheckIns()
        {
            return missedCheckIns;
        }

        #endregion

        #region Compliance Methods

        /// <summary>
        /// Calculate compliance score based on various factors
        /// </summary>
        /// <returns>Compliance score (0-100)</returns>
        public float CalculateComplianceScore()
        {
            float score = 100f;

            // Deduct points for violations
            int violationCount = GetViolationCount();
            score -= violationCount * 10f; // -10 points per violation

            // Deduct points for missed check-ins
            score -= missedCheckIns * 5f; // -5 points per missed check-in

            // Ensure score stays in valid range
            complianceScore = Mathf.Clamp(score, 0f, 100f);
            return complianceScore;
        }

        /// <summary>
        /// Get the current compliance score
        /// </summary>
        /// <returns>Compliance score (0-100)</returns>
        public float GetComplianceScore()
        {
            // Recalculate if needed
            CalculateComplianceScore();
            return complianceScore;
        }

        /// <summary>
        /// Update compliance score directly
        /// </summary>
        /// <param name="newScore">New compliance score (will be clamped to 0-100)</param>
        public void UpdateComplianceScore(float newScore)
        {
            complianceScore = Mathf.Clamp(newScore, 0f, 100f);
            ModLogger.Debug($"Updated compliance score for {player.name} to {complianceScore}");
            // Game's save system handles saving automatically
        }

        /// <summary>
        /// Adjust compliance score by a delta amount
        /// </summary>
        /// <param name="delta">Amount to adjust (positive or negative)</param>
        public void AdjustComplianceScore(float delta)
        {
            complianceScore = Mathf.Clamp(complianceScore + delta, 0f, 100f);
            ModLogger.Debug($"Adjusted compliance score for {player.name} by {delta} to {complianceScore}");
            // Game's save system handles saving automatically
        }

        #endregion

        #region Interaction Methods

        /// <summary>
        /// Record an interaction with the supervising officer
        /// </summary>
        public void RecordInteraction()
        {
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            lastInteractionGameTime = currentGameTime;
            // Game's save system handles saving automatically
        }

        /// <summary>
        /// Get the last interaction time
        /// </summary>
        /// <returns>Last interaction time in game minutes, or 0 if never interacted</returns>
        public float GetLastInteractionGameTime()
        {
            return lastInteractionGameTime;
        }

        #endregion

        #region Conditions Methods

        /// <summary>
        /// Get a summary of parole conditions for display
        /// </summary>
        /// <returns>Formatted string with parole conditions</returns>
        public string GetConditionsSummary()
        {
            if (!isOnParole)
            {
                return "Not on parole";
            }

            var (isParole, remainingTime) = GetParoleStatus();
            if (!isParole)
            {
                return "Parole expired";
            }

            string summary = $"Parole Conditions:\n";
            summary += $"Duration: {GameTimeManager.FormatGameTime(paroleTermLengthInSeconds)}\n";
            summary += $"Remaining: {GetRemainingTimeFormatted()}\n";
            summary += $"Check-ins: {checkInCount} (Missed: {missedCheckIns})\n";
            summary += $"Violations: {GetViolationCount()}\n";
            summary += $"Compliance Score: {complianceScore:F1}/100";

            return summary;
        }

        #endregion

        // Serialization is handled automatically by SaveableSerializer via SaveableField attributes
    }
}
