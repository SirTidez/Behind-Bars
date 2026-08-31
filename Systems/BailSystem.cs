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
    /// <summary>
    /// Calculates bail offers and coordinates the local cash mutation with custody release authorization.
    /// </summary>
    /// <remarks>
    /// Bail amounts and payment state are process-local. A player payment is deducted from
    /// <c>cashBalance</c>, then a pending bail release is recorded; if that authorization
    /// cannot be retained, the local deduction is reversed. Friend-funded bail currently has
    /// no authoritative payer or transaction path and is rejected.
    /// </remarks>
    public class BailSystem
    {
        private const float BAIL_MULTIPLIER = 2.5f; // Bail is typically 2.5x the fine
        // Retained for the planned level formula; the current implementation uses the rank
        // switch below and does not read this constant.
        private const float LEVEL_SCALING_FACTOR = 0.1f;

        // Process-local latest amounts keyed by the shared player identity. Entries remain
        // until overwritten or consumed by the private legacy getter; this is not persisted.
        private static System.Collections.Generic.Dictionary<string, float> playerBailAmounts =
            new System.Collections.Generic.Dictionary<string, float>();
        
        /// <summary>
        /// Describes the amount and current negotiation affordances presented to a player.
        /// </summary>
        public class BailOffer
        {
            /// <summary>Calculated bail amount in the game's currency units.</summary>
            public float Amount { get; set; }
            /// <summary>Whether the current simplified rules allow negotiation.</summary>
            public bool IsNegotiable { get; set; }
            /// <summary>Player-facing explanation of the calculated offer.</summary>
            public string Description { get; set; } = "";
            /// <summary>Negotiation range as a fraction of the original amount (0.2 means 20%).</summary>
            public float NegotiationRange { get; set; } = 0.2f; // 20% negotiation range
        }

        /// <summary>
        /// Calculate a bail offer from the base fine and the current global rank adjustment.
        /// </summary>
        /// <param name="player">Player receiving the offer; the current formula uses the global rank and the name for logging.</param>
        /// <param name="baseFineAmount">Fine amount used as the 2.5x bail baseline.</param>
        /// <returns>A newly-created bail offer.</returns>
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

        /// <summary>
        /// Resolve the current global rank multiplier used by the simplified bail formula.
        /// </summary>
        /// <param name="player">Currently unused; rank is read from the global <c>LevelManager</c>.</param>
        /// <returns>The rank multiplier, defaulting to 1.0 for an unknown rank.</returns>
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

        /// <summary>
        /// Apply the current fine-size threshold for negotiation.
        /// </summary>
        /// <param name="player">Currently unused by the threshold-only implementation.</param>
        /// <param name="fineAmount">Fine amount to compare with the 500-unit threshold.</param>
        /// <returns><see langword="true"/> when the fine is at least 500; otherwise <see langword="false"/>.</returns>
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

        /// <summary>
        /// Return the fixed negotiation range used until player-skill rules are implemented.
        /// </summary>
        /// <param name="player">Currently unused; retained for the future skill-aware calculation.</param>
        /// <returns>A 0.2 fraction, representing a 20 percent range.</returns>
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

        /// <summary>
        /// Check whether the player's current cash balance can cover bail.
        /// </summary>
        /// <param name="player">Currently unused; affordability reads the global cash balance.</param>
        /// <param name="bailAmount">Amount to compare against <c>MoneyManager.cashBalance</c>.</param>
        /// <returns><see langword="true"/> when the money service exists and cash is sufficient.</returns>
        public bool CanPlayerAffordBail(Player player, float bailAmount)
        {
            // Check cash balance only (not onlineBalance)
            if (MoneyManager.Instance == null)
            {
                ModLogger.Error("MoneyManager is not initialized. Cannot check bail affordability.");
                return false;
            }

            // Only check cashBalance - bail must be paid with cash
            return MoneyManager.Instance.cashBalance >= bailAmount;
        }

        /// <summary>
        /// Report whether a friend can fund bail through an authoritative transaction.
        /// </summary>
        /// <param name="player">Player for whom bail would be funded; currently unused.</param>
        /// <param name="bailAmount">Requested amount; currently unused.</param>
        /// <returns>Always <see langword="false"/> because no friend-payer deduction or confirmation path exists.</returns>
        public bool CanFriendsPayBail(Player player, float bailAmount)
        {
            // Friend-funded bail has no authoritative payer, deduction, or
            // network confirmation path yet. Do not expose a successful
            // result until that transaction can be completed safely.
            return false;
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

        /// <summary>
        /// Attempt a bail payment and stage the corresponding custody release authorization.
        /// </summary>
        /// <param name="player">Player whose custody release is being authorized.</param>
        /// <param name="bailAmount">Cash amount to deduct and record.</param>
        /// <param name="isFriendPayment">Requests the currently unsupported friend-payment path when <see langword="true"/>.</param>
        /// <remarks>
        /// Player payment checks and deducts cash before calling the jail manager. A failed
        /// authorization restores that local cash mutation and leaves no successful bail
        /// record. The waits are Unity-scaled <see cref="WaitForSeconds"/> intervals; they
        /// are pacing only and do not provide transaction confirmation. Friend payment exits
        /// before any deduction or authorization. The caller must still observe the custody
        /// release flow after the pending authorization is staged.
        /// </remarks>
        public IEnumerator ProcessBailPayment(Player player, float bailAmount, bool isFriendPayment = false)
        {
            ModLogger.Info($"Processing bail payment of ${bailAmount} for player {player.name}" +
                          (isFriendPayment ? " (paid by friend)" : ""));
            
            if (isFriendPayment)
            {
                ModLogger.Warn($"Friend-paid bail is unavailable; no bail was processed for {player.name}");
                yield break;
            }
            else
            {
                // Player payment - use cashBalance only
                if (MoneyManager.Instance == null)
                {
                    ModLogger.Error("MoneyManager is not initialized. Cannot process bail payment.");
                    yield break;
                }
                
                // Verify player has enough cash
                if (MoneyManager.Instance.cashBalance < bailAmount)
                {
                    ModLogger.Error($"Player {player.name} cannot afford bail of ${bailAmount:F0} (cash: ${MoneyManager.Instance.cashBalance:F0})");
                    yield break;
                }
                
                // Deduct from cashBalance only
                MoneyManager.Instance.ChangeCashBalance(-bailAmount);
                ModLogger.Info($"Deducted ${bailAmount:F0} from cash balance for {player.name} (remaining cash: ${MoneyManager.Instance.cashBalance:F0})");
                
                yield return new WaitForSeconds(0.5f);
                ModLogger.Info($"Bail paid by {player.name}");
            }

            // Only commit the payment once the custody system has accepted a
            // corresponding release authorization. If it cannot, reverse the
            // local cash mutation so a failed handoff cannot consume bail and
            // leave sentence tracking active.
            if (!TryRecordBailAuthorization(player))
            {
                if (!isFriendPayment && MoneyManager.Instance != null)
                {
                    MoneyManager.Instance.ChangeCashBalance(bailAmount);
                    ModLogger.Warn($"Returned ${bailAmount:F0} after bail authorization failed for {player.name}");
                }
                yield break;
            }

            StoreBailAmount(player, bailAmount);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Stage a pending bail release in the jail manager and verify that it was retained.
        /// </summary>
        /// <param name="player">Player whose release type should be marked.</param>
        /// <returns><see langword="true"/> only when the jail manager exists and reports the marker as present.</returns>
        /// <remarks>
        /// This method records authorization only; it does not release the player or clear
        /// custody state. The later jail/custody path owns those operations.
        /// </remarks>
        private bool TryRecordBailAuthorization(Player player)
        {
            ModLogger.Info($"Recording bail authorization for {player.name}");

            try
            {
                var jailManager = Core.Instance?.JailManager;
                if (jailManager == null)
                {
                    ModLogger.Error("JailManager not found - cannot record bail authorization");
                    return false;
                }

                jailManager.MarkPendingReleaseType(player, ReleaseManager.ReleaseType.BailPayment);
                if (!jailManager.HasPendingReleaseType(player))
                {
                    ModLogger.Error($"Bail authorization was not retained for {player.name}");
                    return false;
                }

                ModLogger.Info($"{player.name} bail payment recorded; awaiting custody cleanup before release");
                return true;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error recording bail authorization: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the stored bail amount for a player
        /// </summary>
        public float GetBailAmount(Player player)
        {
            if (player == null) return 0f;

            string playerKey = GetPlayerKey(player);
            if (playerBailAmounts.ContainsKey(playerKey))
            {
                return playerBailAmounts[playerKey];
            }

            return 0f;
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
        /// Store the latest bail amount for a player after a successful local payment handoff
        /// </summary>
        public void StoreBailAmount(Player player, float amount)
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
            return Core.ResolvePlayerKey(player);
        }

        /// <summary>
        /// Calculate a skill-adjusted amount inside the supplied negotiation range.
        /// </summary>
        /// <param name="originalAmount">Original offered amount.</param>
        /// <param name="negotiationRange">Fractional range around the original amount.</param>
        /// <param name="playerSkill">Skill value used as a 10 percent-per-point interpolation bonus.</param>
        /// <returns>The interpolated amount clamped to the calculated minimum and maximum.</returns>
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
