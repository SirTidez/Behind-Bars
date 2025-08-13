using System.Collections;
using Behind_Bars.Helpers;
using UnityEngine;
using MelonLoader;

#if MONO
using FishNet;
using ScheduleOne.PlayerScripts;
#else
using Il2CppFishNet;
using Il2CppScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    public class BailSystem
    {
        private const float BAIL_MULTIPLIER = 2.5f; // Bail is typically 2.5x the fine
        private const float LEVEL_SCALING_FACTOR = 0.1f; // How much player level affects bail
        
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
            
            // Placeholder implementation
            return 1.0f;
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
            
            // Placeholder implementation
            return true;
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
                
                yield return new WaitForSeconds(1f);
                ModLogger.Info($"Bail paid by {player.name}");
            }
            
            // Release player from custody
            yield return ReleasePlayerOnBail(player);
        }

        private IEnumerator ReleasePlayerOnBail(Player player)
        {
            ModLogger.Info($"Releasing {player.name} on bail");
            
            // TODO: Implement bail release mechanics
            // This could involve:
            // 1. Restoring player movement
            // 2. Teleporting player to courthouse/police station
            // 3. Setting bail conditions
            // 4. Starting probation period if applicable
            
            yield return new WaitForSeconds(1f);
            ModLogger.Info($"{player.name} has been released on bail");
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
