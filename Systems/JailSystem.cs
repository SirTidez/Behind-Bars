using System.Collections;
using Behind_Bars.Helpers;
using UnityEngine;
using MelonLoader;




#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.AvatarFramework;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    public class JailSystem
    {
        private const float MIN_JAIL_TIME = 30f; // 30 seconds minimum
        private const float MAX_JAIL_TIME = 300f; // 5 minutes maximum

        public enum JailSeverity
        {
            Minor = 0,      // Traffic violations, small theft
            Moderate = 1,   // Assault, larger theft
            Major = 2,      // Drug dealing, major assault
            Severe = 3      // Murder, major drug operations
        }

        public class JailSentence
        {
            public JailSeverity Severity { get; set; }
            public float JailTime { get; set; }
            public float FineAmount { get; set; }
            public bool CanPayFine { get; set; }
            public string Description { get; set; } = "";
        }

        public IEnumerator HandlePlayerArrest(Player player)
        {
            ModLogger.Info($"Processing arrest for player: {player.name}");

            // Assess the crime severity
            var sentence = AssessCrimeSeverity(player);

            /// DEBUG LOGGING
            int i = 0;
            foreach (Accessory accessory in player.Avatar.appliedAccessories)
            {
                if (accessory == null)
                {
                    i++;
                    continue;
                }
                ModLogger.Info($"Player accessory {i}: {accessory.name}");
                i++;
            }

            // Present options to the player
            yield return PresentJailOptions(player, sentence);
        }

        private JailSentence AssessCrimeSeverity(Player player)
        {
            var sentence = new JailSentence();

            // Analyze player's crime data
            if (player.CrimeData != null)
            {
                // This would need to be expanded based on actual CrimeData structure
                // For now, using placeholder logic
                sentence.Severity = DetermineSeverityFromCrimeData(player.CrimeData);
            }
            else
            {
                // Default to moderate if no crime data
                sentence.Severity = JailSeverity.Moderate;
            }

            // Calculate jail time and fine based on severity
            CalculateSentence(sentence, player);

            ModLogger.Info($"Assessed crime severity: {sentence.Severity}, " +
                          $"Jail time: {sentence.JailTime}s, Fine: ${sentence.FineAmount}");

            return sentence;
        }

        private JailSeverity DetermineSeverityFromCrimeData(object crimeData)
        {
            // TODO: Implement actual crime data analysis
            // This is a placeholder that should be expanded based on the actual CrimeData structure
            return JailSeverity.Moderate;
        }

        private void CalculateSentence(JailSentence sentence, Player player)
        {
            // Base calculations
            float baseJailTime = 0f;
            float baseFine = 0f;

            switch (sentence.Severity)
            {
                case JailSeverity.Minor:
                    baseJailTime = 30f;
                    baseFine = 100f;
                    sentence.Description = "Minor offense - traffic violation or petty theft";
                    break;
                case JailSeverity.Moderate:
                    baseJailTime = 60f;
                    baseFine = 500f;
                    sentence.Description = "Moderate offense - assault or theft";
                    break;
                case JailSeverity.Major:
                    baseJailTime = 120f;
                    baseFine = 1000f;
                    sentence.Description = "Major offense - drug dealing or major assault";
                    break;
                case JailSeverity.Severe:
                    baseJailTime = 300f;
                    baseFine = 5000f;
                    sentence.Description = "Severe offense - murder or major drug operations";
                    break;
            }

            // Adjust based on player level/status
            float levelMultiplier = GetPlayerLevelMultiplier(player);
            sentence.JailTime = Mathf.Clamp(baseJailTime * levelMultiplier, MIN_JAIL_TIME, MAX_JAIL_TIME);
            sentence.FineAmount = baseFine * levelMultiplier;

            // Determine if player can pay fine
            sentence.CanPayFine = CanPlayerAffordFine(player, sentence.FineAmount);
        }

        private float GetPlayerLevelMultiplier(Player player)
        {
            // TODO: Implement actual level-based calculation
            // This should consider player level, reputation, etc.
            return 1.0f; // Default multiplier
        }

        private bool CanPlayerAffordFine(Player player, float fineAmount)
        {
            // TODO: Implement actual money checking
            // This should check the player's actual money/currency
            return true; // Placeholder
        }

        private IEnumerator PresentJailOptions(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Presenting jail options to player: {sentence.Description}");

            // TODO: Implement UI for presenting options
            // For now, just log the options

            if (sentence.CanPayFine)
            {
                ModLogger.Info($"Player can pay fine of ${sentence.FineAmount} or serve {sentence.JailTime}s in jail");
                // TODO: Show UI with "Pay Fine" or "Go to Jail" options
            }
            else
            {
                ModLogger.Info($"Player cannot afford fine, must serve {sentence.JailTime}s in jail");
                // TODO: Show UI with jail time information
            }

            // For now, automatically send to jail after a delay
            yield return new WaitForSeconds(0.1f);
            yield return SendPlayerToJail(player, sentence);
        }


        private Dictionary<string, Vector3> _lastKnownPlayerPosition = new();

        private IEnumerator SendPlayerToJail(Player player, JailSentence sentence)
        {
            ModLogger.Info($"Sending player {player.name} to jail for {sentence.JailTime}s");

            // Get jail system and find available holding cell
            var jailController = Core.ActiveJailController;
            if (jailController == null)
            {
                ModLogger.Error("No active jail controller found, using fallback jail method");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            if (_lastKnownPlayerPosition.ContainsKey(player.name))
                _lastKnownPlayerPosition[player.name] = player.transform.position;
            else
                _lastKnownPlayerPosition.Add(player.name, player.transform.position);

            // Find an available holding cell (for now, use first one)
            var holdingCell = jailController.GetAvailableHoldingCell();
            if (holdingCell == null)
            {
                ModLogger.Error("No holding cells available, using fallback jail method");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            // Get spawn point in the holding cell
            Transform spawnPoint = holdingCell.GetRandomSpawnPoint();
            if (spawnPoint == null)
            {
                ModLogger.Error($"No spawn point found in holding cell {holdingCell.cellName}, using fallback jail method");
                yield return FallbackJailMethod(player, sentence);
                yield break;
            }

            ModLogger.Info($"Teleporting player to holding cell: {holdingCell.cellName} at {spawnPoint.name}");

            // Teleport player to holding cell
            player.transform.position = spawnPoint.position;

            // Mark holding cell as occupied
            holdingCell.isOccupied = true;
            holdingCell.occupantName = player.name;

            // Lock the holding cell door
            holdingCell.LockCell(true);
            holdingCell.CloseCell();

            // Disable inventory but keep movement (let them move around in the cell)
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(false);

            ModLogger.Info($"Player {player.name} placed in {holdingCell.cellName} for {sentence.JailTime}s");

            // Wait for jail time
            yield return new WaitForSeconds(sentence.JailTime);

            ModLogger.Info($"Player {player.name} has served their jail time");

            // Release from holding cell
            holdingCell.isOccupied = false;
            holdingCell.occupantName = "";
            holdingCell.LockCell(false);
            holdingCell.OpenCell();

            ReleasePlayerFromJail(player);
        }

        /// <summary>
        /// Fallback method when holding cells are not available
        /// </summary>
        private IEnumerator FallbackJailMethod(Player player, JailSentence sentence)
        {
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(false);
            PlayerSingleton<PlayerMovement>.Instance.canMove = false;
            Singleton<BlackOverlay>.Instance.Open(2f);

            ModLogger.Info($"Player {player.name} using fallback jail method (screen blackout) for {sentence.JailTime}s");

            yield return new WaitForSeconds(sentence.JailTime);

            ModLogger.Info($"Player {player.name} has served their jail time (fallback method)");
            ReleasePlayerFromJail(player);
        }

        private void ReleasePlayerFromJail(Player player)
        {
            ModLogger.Info($"Releasing player {player.name} from jail");

            player.transform.position = _lastKnownPlayerPosition[player.name];

            // Restore player abilities
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
            PlayerSingleton<PlayerMovement>.Instance.canMove = true;

            // Close black overlay if it's open (for fallback method)
            try
            {
                Singleton<BlackOverlay>.Instance.Close(2f);
            }
            catch
            {
                // BlackOverlay might not have isOpen property, just try to close it
            }

            // TODO: Teleport player to jail exit location
            // For now, let them walk out of the holding cell

            // TODO: Notify other systems about release
            // var playerHandler = Core.Instance?.GetPlayerHandler(player);
            // if (playerHandler != null)
            // {
            //     playerHandler.OnReleasedFromJail(0f, 0f);
            // }

            ModLogger.Info($"Player {player.name} released from jail successfully");
        }
    }
}
