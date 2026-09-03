using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Handles rewards granted upon successful parole completion.
    /// Reward tiers based on final compliance score.
    /// </summary>
    public static class ParoleCompletionRewards
    {
        /// <summary>
        /// Compliance tier names for display
        /// </summary>
        public enum ComplianceTier
        {
            /// <summary>Compliance score below 50.</summary>
            Poor,          // 0-49
            /// <summary>Compliance score from 50 through 74.</summary>
            Satisfactory,  // 50-74
            /// <summary>Compliance score from 75 through 89.</summary>
            Good,          // 75-89
            /// <summary>Compliance score from 90 through 100.</summary>
            Exemplary      // 90-100
        }

        /// <summary>
        /// Get the compliance tier for a given score
        /// </summary>
        /// <param name="complianceScore">Final compliance score used for tier thresholds.</param>
        /// <returns>The display/reward tier selected by the score.</returns>
        public static ComplianceTier GetComplianceTier(float complianceScore)
        {
            if (complianceScore >= 90f) return ComplianceTier.Exemplary;
            if (complianceScore >= 75f) return ComplianceTier.Good;
            if (complianceScore >= 50f) return ComplianceTier.Satisfactory;
            return ComplianceTier.Poor;
        }

        /// <summary>
        /// Get the cash reward for a given compliance tier
        /// </summary>
        /// <param name="tier">Compliance tier selecting the fixed cash reward.</param>
        /// <returns>Cash reward amount, or zero for the poor tier.</returns>
        public static float GetCashReward(ComplianceTier tier)
        {
            switch (tier)
            {
                case ComplianceTier.Exemplary: return 1000f;
                case ComplianceTier.Good: return 500f;
                case ComplianceTier.Satisfactory: return 200f;
                default: return 0f;
            }
        }

        /// <summary>
        /// Get the sentence reduction modifier for a given compliance tier.
        /// This is added to the RapSheet's sentenceReductionModifier.
        /// </summary>
        /// <param name="tier">Compliance tier selecting the fixed reduction.</param>
        /// <returns>Reduction fraction, from 0 to 0.25.</returns>
        public static float GetSentenceReduction(ComplianceTier tier)
        {
            switch (tier)
            {
                case ComplianceTier.Exemplary: return 0.25f;
                case ComplianceTier.Good: return 0.15f;
                case ComplianceTier.Satisfactory: return 0.05f;
                default: return 0f;
            }
        }

        /// <summary>
        /// Grant completion rewards based on final compliance score
        /// </summary>
        /// <param name="player">Player receiving cash, RapSheet changes, and officer text.</param>
        /// <param name="rapSheet">RapSheet containing the persisted reward counters.</param>
        /// <param name="paroleRecord">The completing term whose persisted claim marker owns idempotency.</param>
        /// <returns>True when this call claimed and processed the term; false when it was invalid or already claimed.</returns>
        /// <remarks>
        /// Cash is applied only when the runtime money service is available; RapSheet reduction
        /// and completed-count mutations continue independently. The term is claimed before
        /// side effects begin so re-entrant completion cannot duplicate cash or counters.
        /// </remarks>
        public static bool GrantCompletionRewards(Player player, RapSheet rapSheet, ParoleRecord paroleRecord)
        {
            if (player == null || rapSheet == null || paroleRecord == null)
            {
                return false;
            }

            if (!paroleRecord.TryClaimCompletionRewards())
            {
                ModLogger.Warn($"[REWARDS] Completion rewards already granted for {player.name}; duplicate claim skipped");
                return false;
            }

            float complianceScore = paroleRecord.GetComplianceScore();
            ComplianceTier tier = GetComplianceTier(complianceScore);

            // Cash reward
            float cashReward = GetCashReward(tier);
            if (cashReward > 0f)
            {
                try
                {
#if !MONO
                    var moneyManager = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance;
#else
                    var moneyManager = ScheduleOne.DevUtilities.NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance;
#endif
                    if (moneyManager != null)
                    {
                        moneyManager.ChangeCashBalance(cashReward, true);
                        ModLogger.Info($"[REWARDS] Granted ${cashReward:F0} cash reward to {player.name} (tier: {tier})");
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"[REWARDS] Error granting cash reward: {ex.Message}");
                }
            }

            // Sentence reduction modifier
            float sentenceReduction = GetSentenceReduction(tier);
            if (sentenceReduction > 0f)
            {
                rapSheet.SentenceReductionModifier = Mathf.Min(rapSheet.SentenceReductionModifier + sentenceReduction, 0.5f);
                ModLogger.Info($"[REWARDS] Applied {sentenceReduction:P0} sentence reduction for {player.name} (total: {rapSheet.SentenceReductionModifier:P0})");
            }

            // Track completed parole count
            rapSheet.CompletedParoleCount++;

            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

            // Send completion summary text
            string message = $"Parole completed with {tier} standing (Compliance: {complianceScore:F0}%). ";
            if (cashReward > 0f)
                message += $"Reward: ${cashReward:F0}. ";
            if (sentenceReduction > 0f)
                message += $"Future sentences reduced by {sentenceReduction:P0}. ";
            message += "Stay out of trouble.";

            Core.ResolveParoleManager()?.SendSupervisingOfficerText(player, message);

            ModLogger.Info($"[REWARDS] Parole completion rewards granted for {player.name}: Tier={tier}, Cash=${cashReward:F0}, SentenceReduction={sentenceReduction:P0}");
            return true;
        }
    }
}
