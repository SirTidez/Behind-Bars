using System.Collections;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using UnityEngine;
using MelonLoader;

#if MONO
using FishNet;
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
using ScheduleOne.Levelling;
using ScheduleOne.Money;
#else
using Il2CppFishNet;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Money;
using Il2CppScheduleOne.Levelling;
#endif

namespace Behind_Bars.Systems
{
    public class BailSystem
    {
        private const float BAIL_MULTIPLIER = 2.5f; // Bail is typically 2.5x the fine
        private const float LEVEL_SCALING_FACTOR = 0.1f; // How much player level affects bail

        // Track bail amounts for each player
        private static System.Collections.Generic.Dictionary<string, float> playerBailAmounts =
            new System.Collections.Generic.Dictionary<string, float>();
        
        public class BailOffer
        {
            public float Amount { get; set; }
            public bool IsNegotiable { get; set; }
            public string Description { get; set; } = "";
            public float NegotiationRange { get; set; } = 0.2f; // 20% negotiation range
        }

        public BailOffer CalculateBailAmount(Player player, float baseFineAmount)
        {
            var bailOffer = new BailOffer();
            
            // Base bail amount is typically higher than the fine
            float baseBail = baseFineAmount * BAIL_MULTIPLIER;
            
            // Adjust based on player level/status
            float levelAdjustment = GetPlayerLevelAdjustment(player);
            bailOffer.Amount = baseBail * levelAdjustment;
            
            // Determine if bail is negotiable
            bailOffer.IsNegotiable = DetermineNegotiability(player, baseFineAmount);
            bailOffer.NegotiationRange = GetNegotiationRange(player);
            
            // Set description
            bailOffer.Description = $"Bail set at ${bailOffer.Amount:F0} for your charges. " +
                                  (bailOffer.IsNegotiable ? "This amount may be negotiable." : "This amount is non-negotiable.");
            
            ModLogger.Info($"Calculated bail amount: ${bailOffer.Amount:F0} for player {player.name}");
            
            return bailOffer;
        }

        private float GetPlayerLevelAdjustment(Player player)
        {
            // TODO: Implement actual level-based calculation
            // This should consider:
            // - Player level
            // - Reputation with law enforcement
            // - Previous criminal record
            // - Wealth status
            float playerLevel = LevelManager.Instance.Rank switch
            {
                ERank.Street_Rat => 1.0f,
                ERank.Hoodlum => 1.2f,
                ERank.Peddler => 1.5f,
                ERank.Hustler => 1.8f,
                ERank.Bagman => 2.0f,
                ERank.Enforcer => 2.5f,
                ERank.Shot_Caller => 3.0f,
                ERank.Block_Boss => 3.5f,
                ERank.Underlord => 4.0f,
                ERank.Baron => 4.5f,
                ERank.Kingpin => 5.0f,
                _ => 1.0f
            };

            return playerLevel;
        }

        private bool DetermineNegotiability(Player player, float fineAmount)
        {
            // TODO: Implement actual negotiability logic
            // This could depend on:
            // - Crime severity
            // - Player's lawyer skill
            // - Police officer's mood
            // - Time of day
            
            // For now, allow negotiation for moderate+ crimes
            return fineAmount >= 500f;
        }

        private float GetNegotiationRange(Player player)
        {
            // TODO: Implement actual negotiation range logic
            // This could depend on:
            // - Player's charisma/speech skill
            // - Available evidence
            // - Witness testimony
            
            // Base range is 20%, can be modified by player skills
            return 0.2f;
        }

        public bool CanPlayerAffordBail(Player player, float bailAmount)
        {
            // TODO: Implement actual money checking
            // This should check the player's actual money/currency
            bool canAfford = false;
            if (MoneyManager.Instance == null)
            {
                ModLogger.Error("MoneyManager is not initialized. Cannot check bail affordability.");
            }

            if (MoneyManager.Instance.onlineBalance > bailAmount || MoneyManager.Instance.cashBalance > bailAmount || MoneyManager.Instance.cashBalance + MoneyManager.Instance.onlineBalance > bailAmount)
            {
                canAfford = true;
            }
            
            return canAfford;
        }

        public bool CanFriendsPayBail(Player player, float bailAmount)
        {
            // Check if we're in multiplayer
            if (!IsMultiplayer())
                return false;
            
            // TODO: Implement friend bail payment logic
            // This should:
            // 1. Check if other players are online
            // 2. Check if they have enough money
            // 3. Check if they're willing to help
            
            return true; // Placeholder
        }

        private bool IsMultiplayer()
        {
            try
            {
                var nm = InstanceFinder.NetworkManager;
                return nm != null && (nm.IsServer || nm.IsClient);
            }
            catch
            {
                return false;
            }
        }

        public IEnumerator ProcessBailPayment(Player player, float bailAmount, bool isFriendPayment = false)
        {
            ModLogger.Info($"Processing bail payment of ${bailAmount} for player {player.name}" +
                          (isFriendPayment ? " (paid by friend)" : ""));
            
            if (isFriendPayment)
            {
                // TODO: Implement friend payment logic
                // This should:
                // 1. Deduct money from friend's account
                // 2. Show confirmation to both players
                // 3. Release the arrested player

                yield return new WaitForSeconds(1f);
                ModLogger.Info($"Bail paid by friend for {player.name}");
            }
            else
            {
                // TODO: Implement player payment logic
                // This should:
                // 1. Deduct money from player's account
                // 2. Show confirmation
                // 3. Release the player
                if (MoneyManager.Instance == null)
                {
                    ModLogger.Error("MoneyManager is not initialized. Cannot process bail payment.");
                    yield break;
                }
                if (MoneyManager.Instance.onlineBalance >= bailAmount)
                {
                    MoneyManager.Instance.onlineBalance -= bailAmount;
                }
                else if (MoneyManager.Instance.cashBalance >= bailAmount)
                {
                    MoneyManager.Instance.ChangeCashBalance(bailAmount);
                }
                else if (MoneyManager.Instance.cashBalance + MoneyManager.Instance.onlineBalance >= bailAmount)
                {
                    float remaining = bailAmount - MoneyManager.Instance.onlineBalance;
                    MoneyManager.Instance.onlineBalance = 0;
                    MoneyManager.Instance.ChangeCashBalance(remaining);
                }
                else
                {
                    ModLogger.Error($"Player {player.name} cannot afford bail of ${bailAmount:F0}");
                    yield break;
                }
                yield return new WaitForSeconds(1f);
                ModLogger.Info($"Bail paid by {player.name}");
            }

            // Store the bail amount for release processing
            StoreBailAmount(player, bailAmount);

            // Release player from custody
            yield return ReleasePlayerOnBail(player);
        }

        private IEnumerator ReleasePlayerOnBail(Player player)
        {
            ModLogger.Info($"Releasing {player.name} on bail");

            try
            {
                // Get the core jail system
                var jailSystem = Core.Instance?.JailSystem;
                if (jailSystem != null)
                {
                    // Use the enhanced release system for bail
                    float bailAmount = GetLastBailAmount(player); // We'll need to track this
                    jailSystem.InitiateEnhancedRelease(player, ReleaseManager.ReleaseType.BailPayment, bailAmount);

                    ModLogger.Info($"{player.name} has been released on bail through enhanced system");
                }
                else
                {
                    ModLogger.Error("JailSystem not found - cannot process bail release");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error processing bail release: {ex.Message}");
            }

            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Get the last bail amount paid for a player
        /// </summary>
        private float GetLastBailAmount(Player player)
        {
            if (player == null) return 0f;

            string playerKey = GetPlayerKey(player);
            if (playerBailAmounts.ContainsKey(playerKey))
            {
                float amount = playerBailAmounts[playerKey];
                playerBailAmounts.Remove(playerKey); // Remove after use
                return amount;
            }

            return 0f;
        }

        /// <summary>
        /// Store bail amount for a player when bail is paid
        /// </summary>
        private void StoreBailAmount(Player player, float amount)
        {
            if (player == null) return;

            string playerKey = GetPlayerKey(player);
            playerBailAmounts[playerKey] = amount;
            ModLogger.Info($"Stored bail amount ${amount:F0} for {player.name}");
        }

        /// <summary>
        /// Get unique key for player
        /// </summary>
        private string GetPlayerKey(Player player)
        {
            // Use player name for now, could be enhanced with unique ID
            return player.name;
        }

        public float NegotiateBailAmount(float originalAmount, float negotiationRange, float playerSkill)
        {
            // Calculate the minimum and maximum negotiation range
            float minAmount = originalAmount * (1f - negotiationRange);
            float maxAmount = originalAmount * (1f + negotiationRange);
            
            // Apply player skill to get better results
            float skillBonus = playerSkill * 0.1f; // 10% bonus per skill point
            float finalAmount = Mathf.Lerp(maxAmount, minAmount, skillBonus);
            
            // Ensure the amount stays within bounds
            finalAmount = Mathf.Clamp(finalAmount, minAmount, maxAmount);
            
            ModLogger.Info($"Negotiated bail from ${originalAmount:F0} to ${finalAmount:F0}");
            
            return finalAmount;
        }
    }
}
