using System.Collections;
using Behind_Bars.Helpers;
using UnityEngine;
using MelonLoader;
using System.Collections.Generic;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    public class ProbationSystem
    {
        private const float PROBATION_DURATION = 600f; // 10 minutes default
        private const float SEARCH_INTERVAL_MIN = 30f; // Minimum time between searches
        private const float SEARCH_INTERVAL_MAX = 120f; // Maximum time between searches
        private const float SEARCH_RADIUS = 50f; // How close PO needs to be to search
        
        public enum ProbationStatus
        {
            None = 0,
            Active = 1,
            Violation = 2,
            Completed = 3,
            Revoked = 4
        }

        public class ProbationRecord
        {
            public Player Player { get; set; }
            public ProbationStatus Status { get; set; }
            public float StartTime { get; set; }
            public float Duration { get; set; }
            public float TimeRemaining { get; set; }
            public int ViolationCount { get; set; }
            public float LastSearchTime { get; set; }
            public float NextSearchTime { get; set; }
            public List<string> Violations { get; set; } = new();
        }

        private Dictionary<Player, ProbationRecord> _probationRecords = new();
        private GameObject? _probationOfficerPrefab;
        private Transform? _probationOfficerInstance;

        public void StartProbation(Player player, float duration = PROBATION_DURATION)
        {
            ModLogger.Info($"Starting probation for {player.name} for {duration}s");
            
            var record = new ProbationRecord
            {
                Player = player,
                Status = ProbationStatus.Active,
                StartTime = Time.time,
                Duration = duration,
                TimeRemaining = duration,
                ViolationCount = 0,
                LastSearchTime = 0f,
                NextSearchTime = Time.time + UnityEngine.Random.Range(SEARCH_INTERVAL_MIN, SEARCH_INTERVAL_MAX)
            };
            
            _probationRecords[player] = record;
            
            // Start probation monitoring
            MelonCoroutines.Start(MonitorProbation(record));
            
            // Spawn probation officer if not already present
            if (_probationOfficerInstance == null)
            {
                SpawnProbationOfficer();
            }
        }

        private IEnumerator MonitorProbation(ProbationRecord record)
        {
            ModLogger.Info($"Monitoring probation for {record.Player.name}");
            
            while (record.Status == ProbationStatus.Active && record.TimeRemaining > 0)
            {
                // Update time remaining
                record.TimeRemaining = Mathf.Max(0, record.Duration - (Time.time - record.StartTime));
                
                // Check if it's time for a random search
                if (Time.time >= record.NextSearchTime)
                {
                    yield return ConductRandomSearch(record);
                }
                
                // Check for probation violations
                //yield return CheckForViolations(record);
                
                yield return new WaitForSeconds(1f);
            }
            
            // Probation completed or violated
            if (record.Status == ProbationStatus.Active)
            {
                CompleteProbation(record);
            }
        }

        private IEnumerator ConductRandomSearch(ProbationRecord record)
        {
            ModLogger.Info($"Conducting random search on {record.Player.name}");
            
            // Check if probation officer is close enough
            if (_probationOfficerInstance != null && record.Player != null)
            {
                float distance = Vector3.Distance(_probationOfficerInstance.position, record.Player.transform.position);
                
                if (distance <= SEARCH_RADIUS)
                {
                    // Conduct the search
                    yield return PerformBodySearch(record);
                }
                else
                {
                    ModLogger.Info($"Probation Officer too far from {record.Player.name} for search");
                }
            }
            
            // Schedule next search
            record.LastSearchTime = Time.time;
            record.NextSearchTime = Time.time + UnityEngine.Random.Range(SEARCH_INTERVAL_MIN, SEARCH_INTERVAL_MAX);
        }

        private IEnumerator PerformBodySearch(ProbationRecord record)
        {
            ModLogger.Info($"Performing body search on {record.Player.name}");
            
            // TODO: Implement actual search mechanics
            // This could involve:
            // 1. Showing search UI
            // 2. Checking player inventory for contraband
            // 3. Applying consequences for violations
            // 4. Recording search results
            
            // Simulate search time
            yield return new WaitForSeconds(2f);
            
            // Check for violations during search
            bool hasViolations = CheckForSearchViolations(record);
            
            if (hasViolations)
            {
                record.ViolationCount++;
                record.Violations.Add($"Contraband found during search at {Time.time}");
                ModLogger.Info($"Search violation found for {record.Player.name}. Total violations: {record.ViolationCount}");
                
                // Apply violation consequences
                yield return HandleProbationViolation(record);
            }
            else
            {
                ModLogger.Info($"Search completed for {record.Player.name} - no violations found");
            }
        }

        private bool CheckForSearchViolations(ProbationRecord record)
        {
            bool foundViolations = false;
            var violationDetails = new System.Text.StringBuilder();
            
            if (record.Player?.Inventory == null)
                return false;
                
            ModLogger.Info($"Checking inventory for contraband items for {record.Player.name}");
            
            try
            {
                // Get all inventory slots from PlayerInventory instance
                var playerInventory = record.Player.Inventory;
                if (playerInventory == null)
                {
                    ModLogger.Info($"Player {record.Player.name} has no inventory instance");
                    return false;
                }
                
                // Inventory checking disabled - use fallback random detection for now
                return UnityEngine.Random.Range(0f, 1f) < 0.2f; // 20% chance
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error during contraband search: {ex.Message}");
                // Fall back to random chance if inventory check fails
                return UnityEngine.Random.Range(0f, 1f) < 0.2f;
            }
        }
        
        /// <summary>
        /// Check if an item name indicates it's a drug-related item
        /// </summary>
        private bool IsDrugItem(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return false;
                
            var name = itemName.ToLower();
            
            return name.Contains("weed") ||
                   name.Contains("cannabis") ||
                   name.Contains("marijuana") ||
                   name.Contains("cocaine") ||
                   name.Contains("coke") ||
                   name.Contains("meth") ||
                   name.Contains("crystal") ||
                   name.Contains("heroin") ||
                   name.Contains("opium") ||
                   name.Contains("pill") ||
                   name.Contains("drug") ||
                   name.Contains("narcotic") ||
                   name.Contains("substance");
        }
        
        /// <summary>
        /// Check if an item name indicates it's a weapon
        /// </summary>
        private bool IsWeaponItem(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return false;
                
            var name = itemName.ToLower();
            
            return name.Contains("gun") ||
                   name.Contains("pistol") ||
                   name.Contains("rifle") ||
                   name.Contains("shotgun") ||
                   name.Contains("knife") ||
                   name.Contains("blade") ||
                   name.Contains("weapon") ||
                   name.Contains("taser") ||
                   name.Contains("baton") ||
                   name.Contains("sword") ||
                   name.Contains("axe") ||
                   name.Contains("hammer") && name.Contains("war"); // war hammer, etc.
        }

        private IEnumerator HandleProbationViolation(ProbationRecord record)
        {
            ModLogger.Info($"Handling probation violation for {record.Player.name}");
            
            // Determine violation severity
            if (record.ViolationCount >= 3)
            {
                // Major violation - revoke probation
                record.Status = ProbationStatus.Revoked;
                ModLogger.Info($"Probation revoked for {record.Player.name} due to multiple violations");
                
                // TODO: Implement probation revocation consequences
                // This could involve:
                // 1. Immediate arrest
                // 2. Extended jail time
                // 3. Increased fines
                // 4. Permanent record
                
                yield return HandleProbationRevocation(record);
            }
            else
            {
                // Minor violation - extend probation
                float extension = record.Duration * 0.2f; // 20% extension
                record.Duration += extension;
                record.TimeRemaining += extension;
                
                ModLogger.Info($"Probation extended for {record.Player.name} by {extension}s due to violation");
                
                // TODO: Show violation warning to player
                yield return new WaitForSeconds(1f);
            }
        }

        private IEnumerator HandleProbationRevocation(ProbationRecord record)
        {
            ModLogger.Info($"Handling probation revocation for {record.Player.name}");
            
            // TODO: Implement revocation consequences
            // This could involve:
            // 1. Immediate arrest by probation officer
            // 2. Transfer to jail system
            // 3. Harsher sentencing
            
            yield return new WaitForSeconds(1f);
            
            // Hand off to jail system
            var jailSystem = Core.Instance?.GetJailSystem();
            if (jailSystem != null && record.Player != null)
            {
                yield return jailSystem.HandlePlayerArrest(record.Player);
            }
        }

        private void CompleteProbation(ProbationRecord record)
        {
            ModLogger.Info($"Probation completed successfully for {record.Player.name}");
            
            record.Status = ProbationStatus.Completed;
            record.TimeRemaining = 0f;
            
            // TODO: Implement probation completion rewards
            // This could involve:
            // 1. Clearing criminal record
            // 2. Restoring full rights
            // 3. Positive reputation boost
            // 4. Achievement unlock
            
            // Remove from active probation
            _probationRecords.Remove(record.Player);
            
            // Check if we can despawn probation officer
            if (_probationRecords.Count == 0)
            {
                DespawnProbationOfficer();
            }
        }

        private void SpawnProbationOfficer()
        {
            ModLogger.Info("Probation Officer NPC spawning removed - feature not implemented");
            
            // NOTE: NPC spawning functionality has been removed from this mod
            // The probation system will continue to work without the physical NPC
            // Players will still be subject to probation rules and violations
        }

        private void DespawnProbationOfficer()
        {
            ModLogger.Info("Probation Officer NPC despawning removed - feature not implemented");
            
            // NOTE: NPC despawning functionality has been removed from this mod
            // No cleanup needed as no NPCs are spawned
        }

        public ProbationRecord? GetProbationRecord(Player player)
        {
            _probationRecords.TryGetValue(player, out var record);
            return record;
        }

        public bool IsPlayerOnProbation(Player player)
        {
            return _probationRecords.ContainsKey(player) && 
                   _probationRecords[player].Status == ProbationStatus.Active;
        }

        public void ExtendProbation(Player player, float additionalTime)
        {
            if (_probationRecords.TryGetValue(player, out var record))
            {
                record.Duration += additionalTime;
                record.TimeRemaining += additionalTime;
                ModLogger.Info($"Extended probation for {player.name} by {additionalTime}s");
            }
        }
    }
}
