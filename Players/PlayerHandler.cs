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
    public class PlayerHandler
    {
        public Player? Player { get; private set; }
        public object? LastCrimeData { get; private set; } = null;
        public DateTime LastArrestTime { get; private set; }
        public int ArrestCount { get; private set; } = 0;
        public bool IsCurrentlyArrested { get; private set; } = false;
        public bool IsOnProbation { get; private set; } = false;
        
        // Criminal record tracking
        public List<CriminalRecord> CriminalHistory { get; private set; } = new();
        public float TotalFinesPaid { get; private set; } = 0f;
        public float TotalJailTimeServed { get; private set; } = 0f;
        
        // Confiscated items tracking
        public List<string> ConfiscatedItems { get; private set; } = new();

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
            player.onArrested.AddListener(new Action(OnArrested));
#else
            player.onArrested.AddListener(OnArrested);
#endif
            ModLogger.Info($"PlayerHandler initialized for {player.name}");
        }

        private void OnArrested()
        {
            if (Player == null) return;
            
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
            
            // The actual arrest processing is now handled by the Core class
            // which will coordinate with the JailSystem
        }

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
            
            // Check if probation should be started
            if (ShouldStartProbation())
            {
                StartProbation();
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
            
            // Check if probation should be started
            if (ShouldStartProbation())
            {
                StartProbation();
            }
        }

        private bool ShouldStartProbation()
        {
            // Start probation if:
            // 1. Player has been arrested multiple times
            // 2. Recent arrests were for serious crimes
            // 3. Player is not already on probation
            
            if (IsOnProbation) return false;
            if (ArrestCount < 2) return false;
            
            // Check recent criminal history for serious offenses
            var recentArrests = CriminalHistory
                .Where(r => r.ArrestTime > DateTime.Now.AddDays(-30)) // Last 30 days
                .ToList();
            
            if (recentArrests.Count >= 2)
            {
                ModLogger.Info($"Player {Player?.name} qualifies for probation due to {recentArrests.Count} recent arrests");
                return true;
            }
            
            return false;
        }

        private void StartProbation()
        {
            if (Player == null || IsOnProbation) return;
            
            ModLogger.Info($"Starting probation for {Player.name}");
            
            IsOnProbation = true;
            
            // Get probation system and start probation
            var probationSystem = Core.Instance?.GetProbationSystem();
            if (probationSystem != null)
            {
                // Calculate probation duration based on criminal history
                float probationDuration = CalculateProbationDuration();
                probationSystem.StartProbation(Player, probationDuration);
                
                ModLogger.Info($"Probation started for {Player.name} - duration: {probationDuration}s");
            }
        }

        private float CalculateProbationDuration()
        {
            // Base probation duration
            float baseDuration = 300f; // 5 minutes
            
            // Increase based on arrest count
            float arrestMultiplier = 1f + (ArrestCount - 1) * 0.2f; // 20% increase per arrest
            
            // Increase based on recent serious crimes
            var recentSeriousCrimes = CriminalHistory
                .Where(r => r.ArrestTime > DateTime.Now.AddDays(-7)) // Last week
                .Count();
            
            float crimeMultiplier = 1f + recentSeriousCrimes * 0.3f; // 30% increase per serious crime
            
            float finalDuration = baseDuration * arrestMultiplier * crimeMultiplier;
            
            // Cap at reasonable maximum
            return Mathf.Min(finalDuration, 1800f); // Max 30 minutes
        }

        public void OnProbationCompleted()
        {
            ModLogger.Info($"Probation completed for {Player?.name}");
            IsOnProbation = false;
        }

        public void OnProbationViolation()
        {
            ModLogger.Info($"Probation violation for {Player?.name}");
            // Probation system will handle the consequences
        }

        public CriminalRecord? GetLatestCriminalRecord()
        {
            return CriminalHistory.Count > 0 ? CriminalHistory[CriminalHistory.Count - 1] : null;
        }

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
        
        // Confiscated items methods
        public void AddConfiscatedItems(List<string> items)
        {
            if (items != null && items.Count > 0)
            {
                ConfiscatedItems.AddRange(items);
                ModLogger.Info($"Added {items.Count} confiscated items for {Player?.name}");
            }
        }
        
        public List<string> GetConfiscatedItems()
        {
            return new List<string>(ConfiscatedItems);
        }
        
        public void ClearConfiscatedItems()
        {
            int count = ConfiscatedItems.Count;
            ConfiscatedItems.Clear();
            ModLogger.Info($"Cleared {count} confiscated items for {Player?.name}");
        }
        
        public bool HasConfiscatedItems()
        {
            return ConfiscatedItems.Count > 0;
        }
    }

    public class CriminalRecord
    {
        public DateTime ArrestTime { get; set; }
        public DateTime? ReleaseTime { get; set; }
        public object? CrimeData { get; set; }
        public int ArrestNumber { get; set; }
        public Vector3 Location { get; set; }
        public float JailTimeServed { get; set; } = 0f;
        public float FinePaid { get; set; } = 0f;
        public float? BailAmount { get; set; }
        public bool ReleasedOnBail { get; set; } = false;
        public string? Notes { get; set; }
    }
}
