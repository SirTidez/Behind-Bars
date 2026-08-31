using Behind_Bars.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Behind_Bars.Systems;
using UnityEngine;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Players
{
    /// <summary>
    /// Tracks local-player custody, parole, criminal-history, and confiscated-property state.
    /// Arrest processing itself is centralized in HarmonyPatches; this handler receives release
    /// and parole transitions and stores the resulting player-facing history.
    /// </summary>
    public class PlayerHandler
    {
        /// <summary>Gets the player associated with this handler.</summary>
        public Player? Player { get; private set; }
        /// <summary>Gets the last native crime-data object captured for this handler, when available.</summary>
        public object? LastCrimeData { get; private set; } = null;
        /// <summary>Gets the wall-clock timestamp of the last handled arrest.</summary>
        public DateTime LastArrestTime { get; private set; }
        /// <summary>Gets the number of arrests recorded by this handler.</summary>
        public int ArrestCount { get; private set; } = 0;
        /// <summary>Gets whether the player is currently in an arrested/custody state.</summary>
        public bool IsCurrentlyArrested { get; private set; } = false;
        /// <summary>Gets whether this handler currently considers the player to be on parole.</summary>
        public bool IsOnParole { get; private set; } = false;
        
        // Criminal record tracking. Arrest/release timestamps are wall-clock values; jail time
        // is accumulated in seconds and monetary fields are currency amounts.
        /// <summary>Gets the mutable criminal-record history owned by this handler.</summary>
        public List<CriminalRecord> CriminalHistory { get; private set; } = new();
        /// <summary>Gets the cumulative fines and bail paid by the player.</summary>
        public float TotalFinesPaid { get; private set; } = 0f;
        /// <summary>Gets cumulative jail time served in seconds.</summary>
        public float TotalJailTimeServed { get; private set; } = 0f;
        
        // Confiscated items are persisted item identifiers. Callers receive a defensive copy
        // through GetConfiscatedItems and must use the explicit mutation methods below.
        /// <summary>Gets the handler-owned list of persisted confiscated-item identifiers.</summary>
        public List<string> ConfiscatedItems { get; private set; } = new();

        /// <summary>
        /// Creates a player handler and subscribes to the runtime-specific arrest event. The
        /// current arrest callback is intentionally inert because canonical arrest bookkeeping
        /// is centralized in HarmonyPatches; the commented body below is retained as historical
        /// reference and is not executed. There is currently no public disposal method for the
        /// subscribed arrest listener, so the owning lifecycle must account for handler lifetime.
        /// </summary>
        /// <param name="player">Non-null player wrapper to track.</param>
        public PlayerHandler(Player player)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player), "Player cannot be null");
            }
            
            this.Player = player;
            this.LastArrestTime = DateTime.MinValue;

#if !MONO
            // Subscribe to arrest events
            player.add_onArrested(new Action(OnArrested));
#else
            player.onArrested += OnArrested;
#endif
            ModLogger.Debug($"PlayerHandler initialized for {player.name}");
        }

        /// <summary>
        /// Receives the player arrest event but currently performs no local mutation. Arrest
        /// bookkeeping is centralized in HarmonyPatches; the preserved commented implementation
        /// below is intentionally disabled and should not be mistaken for active behavior.
        /// </summary>
        private void OnArrested()
        {
            /*if (Player == null) return;
            
                ModLogger.Info($"Player {Player.name} arrested - processing arrest sequence");
                
                // Update arrest tracking
                LastArrestTime = DateTime.Now;
                ArrestCount++;
                IsCurrentlyArrested = true;
                
                // Store crime data for processing
                LastCrimeData = Player.CrimeData;
                
                // Create criminal record entry
                var record = new CriminalRecord
                {
                    ArrestTime = LastArrestTime,
                    CrimeData = LastCrimeData,
                    ArrestNumber = ArrestCount,
                    Location = Player.transform.position
                };
                
                CriminalHistory.Add(record);
                
                // Log arrest details
                ModLogger.Info($"Arrest #{ArrestCount} for {Player.name} at {LastArrestTime}");
                */
                
        }

        /// <summary>
        /// Marks the player released from custody, accumulates jail time/fines, and updates the
        /// latest record using the current wall-clock release timestamp.
        /// </summary>
        /// <param name="jailTimeServed">Sentence duration served, in seconds.</param>
        /// <param name="finePaid">Fine paid during release, in currency units.</param>
        public void OnReleasedFromJail(float jailTimeServed, float finePaid = 0f)
        {
            if (Player == null) return;
            
            ModLogger.Info($"Player {Player.name} released from jail after {jailTimeServed}s");
            
            // Update tracking
            IsCurrentlyArrested = false;
            TotalJailTimeServed += jailTimeServed;
            
            if (finePaid > 0)
            {
                TotalFinesPaid += finePaid;
                ModLogger.Info($"Total fines paid by {Player.name}: ${TotalFinesPaid:F0}");
            }

            // Check if parole should be started
            if (ShouldStartParole())
            {
                StartParole();
            }

            // Update the most recent criminal record
            if (CriminalHistory.Count > 0)
            {
                var latestRecord = CriminalHistory[CriminalHistory.Count - 1];
                latestRecord.JailTimeServed = jailTimeServed;
                latestRecord.FinePaid = finePaid;
                latestRecord.ReleaseTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Marks the player released on bail, records the bail payment, and flags the latest
        /// criminal record as bail release before evaluating parole eligibility.
        /// </summary>
        /// <param name="bailAmount">Bail paid, in currency units.</param>
        public void OnReleasedOnBail(float bailAmount)
        {
            if (Player == null) return;
            
            ModLogger.Info($"Player {Player.name} released on bail: ${bailAmount:F0}");
            
            // Update tracking
            IsCurrentlyArrested = false;
            TotalFinesPaid += bailAmount;
            
            // Update the most recent criminal record
            if (CriminalHistory.Count > 0)
            {
                var latestRecord = CriminalHistory[CriminalHistory.Count - 1];
                latestRecord.BailAmount = bailAmount;
                latestRecord.ReleaseTime = DateTime.Now;
                latestRecord.ReleasedOnBail = true;
            }
            
            // Check if parole should be started
            if (ShouldStartParole())
            {
                StartParole();
            }
        }

        /// <summary>
        /// Evaluates the current parole threshold: the player must not already be on parole,
        /// must have at least two arrests, and must have at least two arrests in the last thirty
        /// wall-clock days. The current implementation does not filter those arrests by severity.
        /// </summary>
        /// <returns><c>true</c> when a new parole term should be started.</returns>
        private bool ShouldStartParole()
        {
            // Start parole if:
            // 1. Player has been arrested multiple times
            // 2. Player has multiple recent arrests (severity is not currently checked)
            // 3. Player is not already on parole

            if (IsOnParole) return false;
            if (ArrestCount < 2) return false;
            
            // Check recent criminal history; the current rule counts all recent arrests.
            var recentArrests = CriminalHistory
                .Where(r => r.ArrestTime > DateTime.Now.AddDays(-30)) // Last 30 days
                .ToList();
            
            if (recentArrests.Count >= 2)
            {
                ModLogger.Info($"Player {Player?.name} qualifies for parole due to {recentArrests.Count} recent arrests");
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Sets the local parole flag and asks the manager-owned parole system to start a term.
        /// The flag is set before the manager lookup; if that service is unavailable, no local
        /// rollback currently occurs and the state remains marked on parole.
        /// </summary>
        private void StartParole()
        {
            if (Player == null || IsOnParole) return;

            ModLogger.Info($"Starting parole for {Player.name}");

            IsOnParole = true;

            // Get parole system and start parole
        var paroleSystem = Core.ResolveParoleManager()?.ParoleSystem;
            if (paroleSystem != null)
            {
                // Calculate parole duration based on criminal history
                float paroleDuration = CalculateParoleDuration();
                paroleSystem.StartParole(Player, paroleDuration);

                ModLogger.Info($"Parole started for {Player.name} - duration: {paroleDuration}s");
            }
        }

        /// <summary>
        /// Calculates a capped parole duration from arrest count and recent history. The current
        /// implementation counts all arrests in the last seven wall-clock days despite the
        /// historical variable name suggesting a serious-crime filter; the result is in seconds.
        /// </summary>
        /// <returns>A parole duration between the configured base and thirty-minute cap, in seconds.</returns>
        private float CalculateParoleDuration()
        {
            // Base parole duration
            float baseDuration = 300f; // 5 minutes
            
            // Increase based on arrest count
            float arrestMultiplier = 1f + (ArrestCount - 1) * 0.2f; // 20% increase per arrest
            
            // Increase based on recent arrests; severity is not currently filtered.
            var recentSeriousCrimes = CriminalHistory
                .Where(r => r.ArrestTime > DateTime.Now.AddDays(-7)) // Last week
                .Count();
            
            float crimeMultiplier = 1f + recentSeriousCrimes * 0.3f; // 30% increase per recent arrest
            
            float finalDuration = baseDuration * arrestMultiplier * crimeMultiplier;
            
            // Cap at reasonable maximum
            return Mathf.Min(finalDuration, 1800f); // Max 30 minutes
        }

        /// <summary>Clears the local parole flag after the manager-owned parole term completes.</summary>
        public void OnParoleCompleted()
        {
            ModLogger.Info($"Parole completed for {Player?.name}");
            IsOnParole = false;
        }

        /// <summary>
        /// Records a parole-violation notification. Consequence handling remains delegated to the
        /// parole system; this handler currently logs the event without additional local mutation.
        /// </summary>
        public void OnParoleViolation()
        {
            ModLogger.Info($"Parole violation for {Player?.name}");
            // Parole system will handle the consequences
        }

        /// <summary>Gets the most recent criminal record, or null when no history exists.</summary>
        public CriminalRecord? GetLatestCriminalRecord()
        {
            return CriminalHistory.Count > 0 ? CriminalHistory[CriminalHistory.Count - 1] : null;
        }

        /// <summary>
        /// Calculates the current heuristic record score from arrest count, served jail minutes,
        /// paid-fine amounts, and wall-clock recency bonuses.
        /// </summary>
        /// <returns>The aggregate criminal-record score.</returns>
        public float GetCriminalRecordScore()
        {
            // Calculate a "criminal record score" based on history
            float score = 0f;
            
            foreach (var record in CriminalHistory)
            {
                // Base points for arrest
                score += 10f;
                
                // Additional points for jail time
                score += record.JailTimeServed / 60f; // 1 point per minute
                
                // Additional points for fines
                score += record.FinePaid / 100f; // 1 point per $100
                
                // Recent arrests count more
                var daysSinceArrest = (DateTime.Now - record.ArrestTime).TotalDays;
                if (daysSinceArrest <= 7) score += 5f; // Recent arrest bonus
                else if (daysSinceArrest <= 30) score += 2f; // Recent arrest bonus
            }
            
            return score;
        }
        
        // Confiscated-item mutations operate on persisted identifiers; they do not directly move
        // inventory objects. The jail/release systems remain responsible for transfer authority.
        /// <summary>Adds persisted confiscated-item identifiers to the handler's record.</summary>
        /// <param name="items">Identifiers to append; null or empty input is ignored.</param>
        public void AddConfiscatedItems(List<string> items)
        {
            if (items != null && items.Count > 0)
            {
                ConfiscatedItems.AddRange(items);
                ModLogger.Info($"Added {items.Count} confiscated items for {Player?.name}");
            }
        }
        
        /// <summary>Gets a defensive copy of the persisted confiscated-item identifiers.</summary>
        /// <returns>A new mutable list that cannot mutate handler state directly.</returns>
        public List<string> GetConfiscatedItems()
        {
            return new List<string>(ConfiscatedItems);
        }

        /// <summary>
        /// Removes one persisted confiscated-item marker. Callers that need to
        /// mutate the jail state must use this rather than altering the
        /// defensive copy returned by <see cref="GetConfiscatedItems"/>.
        /// </summary>
        public bool RemoveConfiscatedItem(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            bool removed = ConfiscatedItems.Remove(itemId);
            if (removed)
            {
                ModLogger.Info($"Removed confiscated item marker '{itemId}' for {Player?.name}");
            }

            return removed;
        }
        
        /// <summary>Removes all persisted confiscated-item identifiers from this handler.</summary>
        public void ClearConfiscatedItems()
        {
            int count = ConfiscatedItems.Count;
            ConfiscatedItems.Clear();
            ModLogger.Info($"Cleared {count} confiscated items for {Player?.name}");
        }
        
        /// <summary>Returns whether any persisted confiscated-item identifiers remain.</summary>
        public bool HasConfiscatedItems()
        {
            return ConfiscatedItems.Count > 0;
        }
    }

    /// <summary>
    /// Mutable history entry for one arrest. Date fields use wall-clock timestamps, while jail
    /// duration is stored in seconds and monetary fields use currency units.
    /// </summary>
    public class CriminalRecord
    {
        /// <summary>Gets or sets the wall-clock time at which the arrest occurred.</summary>
        public DateTime ArrestTime { get; set; }
        /// <summary>Gets or sets the wall-clock release time, when the record has been completed.</summary>
        public DateTime? ReleaseTime { get; set; }
        /// <summary>Gets or sets the opaque native crime-data object associated with the arrest.</summary>
        public object? CrimeData { get; set; }
        /// <summary>Gets or sets the one-based arrest number represented by this record.</summary>
        public int ArrestNumber { get; set; }
        /// <summary>Gets or sets the world position associated with the arrest.</summary>
        public Vector3 Location { get; set; }
        /// <summary>Gets or sets jail time served for this record, in seconds.</summary>
        public float JailTimeServed { get; set; } = 0f;
        /// <summary>Gets or sets the fine paid for this record, in currency units.</summary>
        public float FinePaid { get; set; } = 0f;
        /// <summary>Gets or sets bail paid for this record, in currency units.</summary>
        public float? BailAmount { get; set; }
        /// <summary>Gets or sets whether release occurred through bail rather than sentence completion.</summary>
        public bool ReleasedOnBail { get; set; } = false;
        /// <summary>Gets or sets optional free-form notes for the record.</summary>
        public string? Notes { get; set; }
    }
}
