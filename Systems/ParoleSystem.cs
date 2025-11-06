using System.Collections;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.UI;
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
    /// <summary>
    /// Manages active parole supervision for released players
    /// Handles parole monitoring, random searches, violations, and completion
    /// Integrates with RapSheet/LSI system for risk assessment
    /// </summary>
    public class ParoleSystem
    {
        private const float PAROLE_DURATION = 600f; // 10 minutes default
        private const float SEARCH_INTERVAL_MIN = 30f; // Minimum time between searches
        private const float SEARCH_INTERVAL_MAX = 120f; // Maximum time between searches
        private const float SEARCH_RADIUS = 50f; // How close PO needs to be to search

        /// <summary>
        /// Parole supervision status
        /// </summary>
        public enum ParoleStatus
        {
            None = 0,
            Active = 1,
            Violation = 2,
            Completed = 3,
            Revoked = 4
        }

        /// <summary>
        /// Runtime parole tracking record (in-memory)
        /// Separate from ParoleRecord in RapSheet which handles persistent storage
        /// </summary>
        public class ParoleRuntimeRecord
        {
            public Player Player { get; set; }
            public ParoleStatus Status { get; set; }
            public float StartTime { get; set; }
            public float Duration { get; set; }
            public float TimeRemaining { get; set; }
            public int ViolationCount { get; set; }
            public float LastSearchTime { get; set; }
            public float NextSearchTime { get; set; }
            public List<string> Violations { get; set; } = new();
        }

        private Dictionary<Player, ParoleRuntimeRecord> _paroleRecords = new();
        private GameObject? _paroleOfficerPrefab;
        private Transform? _paroleOfficerInstance;

        /// <summary>
        /// Start parole supervision for a player
        /// Creates runtime tracking and initializes RapSheet/LSI integration
        /// </summary>
        public void StartParole(Player player, float duration = PAROLE_DURATION)
        {
            ModLogger.Info($"Starting parole for {player.name} for {duration}s");

            var record = new ParoleRuntimeRecord
            {
                Player = player,
                Status = ParoleStatus.Active,
                StartTime = Time.time,
                Duration = duration,
                TimeRemaining = duration,
                ViolationCount = 0,
                LastSearchTime = 0f,
                NextSearchTime = Time.time + UnityEngine.Random.Range(SEARCH_INTERVAL_MIN, SEARCH_INTERVAL_MAX)
            };

            _paroleRecords[player] = record;

            // Initialize RapSheet ParoleRecord and perform LSI assessment
            InitializeParoleTracking(player, duration);

            // Start parole monitoring
            MelonCoroutines.Start(MonitorParole(record));

            // Spawn parole officer if not already present
            if (_paroleOfficerInstance == null)
            {
                SpawnParoleOfficer();
            }

            // Show parole status UI after a short delay to ensure RapSheet is initialized
            MelonCoroutines.Start(DelayedShowParoleUI(player));
        }

        /// <summary>
        /// Initialize parole tracking in RapSheet system with LSI assessment
        /// This integrates the ParoleSystem with the RapSheet/LSI tracking
        /// </summary>
        private void InitializeParoleTracking(Player player, float duration)
        {
            try
            {
                // Get cached rap sheet (loads from file only once)
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet == null)
                {
                    ModLogger.Warn($"[LSI] Failed to get rap sheet for {player.name}");
                    return;
                }

                // Start parole with initial LSI assessment
                bool success = rapSheet.StartParoleWithAssessment(duration);

                if (success)
                {
                    ModLogger.Info($"[LSI] Parole tracking initialized for {player.name} - LSI Level: {rapSheet.LSILevel}");

                    // Save the updated rap sheet and invalidate cache
                    RapSheetManager.Instance.SaveRapSheet(player, invalidateCache: true);
                }
                else
                {
                    ModLogger.Warn($"[LSI] Failed to start parole tracking for {player.name}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[LSI] Error initializing parole tracking for {player.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Delayed show parole UI to ensure RapSheet is initialized
        /// </summary>
        private IEnumerator DelayedShowParoleUI(Player player)
        {
            // Wait a frame to ensure RapSheet initialization is complete
            yield return new WaitForSeconds(0.5f);

            try
            {
                var uiManager = BehindBarsUIManager.Instance;
                if (uiManager != null)
                {
                    uiManager.ShowParoleStatus();
                    ModLogger.Info($"Parole status UI shown for {player.name}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to show parole status UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Monitor active parole and conduct periodic searches
        /// </summary>
        private IEnumerator MonitorParole(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Monitoring parole for {record.Player.name}");

            while (record.Status == ParoleStatus.Active && record.TimeRemaining > 0)
            {
                // Update time remaining
                record.TimeRemaining = Mathf.Max(0, record.Duration - (Time.time - record.StartTime));

                // Check if it's time for a random search
                if (Time.time >= record.NextSearchTime)
                {
                    yield return ConductRandomSearch(record);
                }

                // Check for parole violations
                //yield return CheckForViolations(record);

                yield return new WaitForSeconds(1f);
            }

            // Parole completed or violated
            if (record.Status == ParoleStatus.Active)
            {
                CompleteParole(record);
            }
        }

        /// <summary>
        /// Conduct a random search if parole officer is in range
        /// </summary>
        private IEnumerator ConductRandomSearch(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Conducting random search on {record.Player.name}");

            // Check if parole officer is close enough
            if (_paroleOfficerInstance != null && record.Player != null)
            {
                float distance = Vector3.Distance(_paroleOfficerInstance.position, record.Player.transform.position);

                if (distance <= SEARCH_RADIUS)
                {
                    // Conduct the search
                    yield return PerformBodySearch(record);
                }
                else
                {
                    ModLogger.Info($"Parole Officer too far from {record.Player.name} for search");
                }
            }

            // Schedule next search
            record.LastSearchTime = Time.time;
            record.NextSearchTime = Time.time + UnityEngine.Random.Range(SEARCH_INTERVAL_MIN, SEARCH_INTERVAL_MAX);
        }

        /// <summary>
        /// Perform body search on parolee
        /// </summary>
        private IEnumerator PerformBodySearch(ParoleRuntimeRecord record)
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
                yield return HandleParoleViolation(record);
            }
            else
            {
                ModLogger.Info($"Search completed for {record.Player.name} - no violations found");
            }
        }

        /// <summary>
        /// Check player inventory for contraband during search
        /// </summary>
        private bool CheckForSearchViolations(ParoleRuntimeRecord record)
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

        /// <summary>
        /// Handle parole violation consequences
        /// </summary>
        private IEnumerator HandleParoleViolation(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Handling parole violation for {record.Player.name}");

            // Determine violation severity
            if (record.ViolationCount >= 3)
            {
                // Major violation - revoke parole
                record.Status = ParoleStatus.Revoked;
                ModLogger.Info($"Parole revoked for {record.Player.name} due to multiple violations");

                // TODO: Implement parole revocation consequences
                // This could involve:
                // 1. Immediate arrest
                // 2. Extended jail time
                // 3. Increased fines
                // 4. Permanent record

                yield return HandleParoleRevocation(record);
            }
            else
            {
                // Minor violation - extend parole
                float extension = record.Duration * 0.2f; // 20% extension
                record.Duration += extension;
                record.TimeRemaining += extension;

                ModLogger.Info($"Parole extended for {record.Player.name} by {extension}s due to violation");

                // TODO: Show violation warning to player
                yield return new WaitForSeconds(1f);
            }
        }

        /// <summary>
        /// Handle parole revocation (send back to jail)
        /// </summary>
        private IEnumerator HandleParoleRevocation(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Handling parole revocation for {record.Player.name}");

            // End parole in RapSheet and archive it
            try
            {
                var rapSheet = RapSheetManager.Instance.GetRapSheet(record.Player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    rapSheet.CurrentParoleRecord.EndParole();
                    // Move current parole record to past records
                    rapSheet.ArchiveCurrentParoleRecord();
                    RapSheetManager.Instance.SaveRapSheet(record.Player, invalidateCache: true);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error ending parole in RapSheet: {ex.Message}");
            }

            // Hide parole status UI
            try
            {
                var uiManager = BehindBarsUIManager.Instance;
                if (uiManager != null)
                {
                    uiManager.HideParoleStatus();
                    ModLogger.Info($"Parole status UI hidden for {record.Player.name} (revoked)");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to hide parole status UI: {ex.Message}");
            }

            // Remove from active parole
            _paroleRecords.Remove(record.Player);

            // TODO: Implement revocation consequences
            // This could involve:
            // 1. Immediate arrest by parole officer
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

        /// <summary>
        /// Complete parole successfully
        /// </summary>
        private void CompleteParole(ParoleRuntimeRecord record)
        {
            ModLogger.Info($"Parole completed successfully for {record.Player.name}");

            record.Status = ParoleStatus.Completed;
            record.TimeRemaining = 0f;

            // End parole in RapSheet and archive it
            try
            {
                var rapSheet = RapSheetManager.Instance.GetRapSheet(record.Player);
                if (rapSheet?.CurrentParoleRecord != null)
                {
                    rapSheet.CurrentParoleRecord.EndParole();
                    // Move current parole record to past records
                    rapSheet.ArchiveCurrentParoleRecord();
                    RapSheetManager.Instance.SaveRapSheet(record.Player, invalidateCache: true);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error ending parole in RapSheet: {ex.Message}");
            }

            // Hide parole status UI
            try
            {
                var uiManager = BehindBarsUIManager.Instance;
                if (uiManager != null)
                {
                    uiManager.HideParoleStatus();
                    ModLogger.Info($"Parole status UI hidden for {record.Player.name}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"Failed to hide parole status UI: {ex.Message}");
            }

            // TODO: Implement parole completion rewards
            // This could involve:
            // 1. Clearing criminal record
            // 2. Restoring full rights
            // 3. Positive reputation boost
            // 4. Achievement unlock

            // Remove from active parole
            _paroleRecords.Remove(record.Player);

            // Check if we can despawn parole officer
            if (_paroleRecords.Count == 0)
            {
                DespawnParoleOfficer();
            }
        }

        /// <summary>
        /// Spawn parole officer NPC (placeholder)
        /// </summary>
        private void SpawnParoleOfficer()
        {
            ModLogger.Info("Parole Officer NPC spawning removed - feature not implemented");

            // NOTE: NPC spawning functionality has been removed from this mod
            // The parole system will continue to work without the physical NPC
            // Players will still be subject to parole rules and violations
        }

        /// <summary>
        /// Despawn parole officer NPC (placeholder)
        /// </summary>
        private void DespawnParoleOfficer()
        {
            ModLogger.Info("Parole Officer NPC despawning removed - feature not implemented");

            // NOTE: NPC despawning functionality has been removed from this mod
            // No cleanup needed as no NPCs are spawned
        }

        /// <summary>
        /// Get parole record for player
        /// </summary>
        public ParoleRuntimeRecord? GetParoleRecord(Player player)
        {
            _paroleRecords.TryGetValue(player, out var record);
            return record;
        }

        /// <summary>
        /// Check if player is currently on active parole
        /// </summary>
        public bool IsPlayerOnParole(Player player)
        {
            return _paroleRecords.ContainsKey(player) &&
                   _paroleRecords[player].Status == ParoleStatus.Active;
        }

        /// <summary>
        /// Extend parole duration for a player
        /// </summary>
        public void ExtendParole(Player player, float additionalTime)
        {
            if (_paroleRecords.TryGetValue(player, out var record))
            {
                record.Duration += additionalTime;
                record.TimeRemaining += additionalTime;
                ModLogger.Info($"Extended parole for {player.name} by {additionalTime}s");
            }
        }
    }
}
