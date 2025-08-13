using System.Collections;
using Behind_Bars.Helpers;
using UnityEngine;
using MelonLoader;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    public class CourtSystem
    {
        private const float NEGOTIATION_TIME_LIMIT = 60f; // 60 seconds to negotiate
        private const float MIN_NEGOTIATION_AMOUNT = 0.5f; // Can't negotiate below 50% of original
        
        public enum CourtPhase
        {
            InitialBail = 0,
            Negotiation = 1,
            FinalDecision = 2,
            Sentencing = 3
        }

        public class CourtSession
        {
            public Player Defendant { get; set; }
            public float OriginalBailAmount { get; set; }
            public float CurrentBailAmount { get; set; }
            public float NegotiatedAmount { get; set; }
            public CourtPhase CurrentPhase { get; set; }
            public float TimeRemaining { get; set; }
            public bool IsNegotiationSuccessful { get; set; }
            public string JudgeNotes { get; set; } = "";
        }

        public IEnumerator StartCourtSession(Player player, float bailAmount)
        {
            ModLogger.Info($"Starting court session for {player.name} with bail amount: ${bailAmount}");
            
            var session = new CourtSession
            {
                Defendant = player,
                OriginalBailAmount = bailAmount,
                CurrentBailAmount = bailAmount,
                NegotiatedAmount = bailAmount,
                CurrentPhase = CourtPhase.InitialBail,
                TimeRemaining = NEGOTIATION_TIME_LIMIT
            };
            
            // Start the court sequence
            yield return ConductCourtSession(session);
        }

        private IEnumerator ConductCourtSession(CourtSession session)
        {
            ModLogger.Info($"Court session beginning for {session.Defendant.name}");
            
            // Phase 1: Initial bail presentation
            yield return PresentInitialBail(session);
            
            // Phase 2: Negotiation (if applicable)
            if (session.CurrentBailAmount > 0)
            {
                yield return ConductNegotiation(session);
            }
            
            // Phase 3: Final decision
            yield return MakeFinalDecision(session);
            
            // Phase 4: Sentencing
            yield return ExecuteSentencing(session);
        }

        private IEnumerator PresentInitialBail(CourtSession session)
        {
            ModLogger.Info($"Presenting initial bail amount: ${session.OriginalBailAmount:F0}");
            
            // TODO: Implement court UI
            // This should show:
            // - Judge's opening statement
            // - Charges against the defendant
            // - Initial bail amount
            // - Options for the player
            
            session.CurrentPhase = CourtPhase.InitialBail;
            
            // Give player time to read
            yield return new WaitForSeconds(3f);
            
            ModLogger.Info("Initial bail presentation complete");
        }

        private IEnumerator ConductNegotiation(CourtSession session)
        {
            ModLogger.Info($"Beginning bail negotiation for {session.Defendant.name}");
            
            session.CurrentPhase = CourtPhase.Negotiation;
            session.TimeRemaining = NEGOTIATION_TIME_LIMIT;
            
            // TODO: Implement negotiation UI
            // This should include:
            // - Timer display
            // - Current bail amount
            // - Negotiation options
            // - Player's negotiation skills display
            
            // Simulate negotiation process
            while (session.TimeRemaining > 0)
            {
                session.TimeRemaining -= Time.deltaTime;
                
                // TODO: Check for player input during negotiation
                // This could include:
                // - Accept current amount
                // - Try to negotiate lower
                // - Present evidence
                // - Call character witnesses
                
                yield return null;
            }
            
            // Negotiation time expired
            ModLogger.Info("Negotiation time expired");
            session.IsNegotiationSuccessful = false;
        }

        private IEnumerator MakeFinalDecision(CourtSession session)
        {
            ModLogger.Info($"Making final decision for {session.Defendant.name}");
            
            session.CurrentPhase = CourtPhase.FinalDecision;
            
            // Determine final bail amount based on negotiation
            if (session.IsNegotiationSuccessful)
            {
                // TODO: Calculate final amount based on player's negotiation performance
                session.NegotiatedAmount = session.CurrentBailAmount * 0.8f; // 20% reduction as example
                session.JudgeNotes = "The court has considered your arguments and reduced the bail amount.";
            }
            else
            {
                session.NegotiatedAmount = session.OriginalBailAmount;
                session.JudgeNotes = "No successful negotiation was reached. Original bail amount stands.";
            }
            
            // Ensure the amount doesn't go below minimum
            session.NegotiatedAmount = Mathf.Max(session.NegotiatedAmount, 
                                               session.OriginalBailAmount * MIN_NEGOTIATION_AMOUNT);
            
            ModLogger.Info($"Final bail amount: ${session.NegotiatedAmount:F0}");
            
            // TODO: Show final decision UI
            yield return new WaitForSeconds(2f);
        }

        private IEnumerator ExecuteSentencing(CourtSession session)
        {
            ModLogger.Info($"Executing sentencing for {session.Defendant.name}");
            
            session.CurrentPhase = CourtPhase.Sentencing;
            
            // TODO: Implement sentencing UI
            // This should show:
            // - Judge's final ruling
            // - Final bail amount
            // - Payment options
            // - Consequences of non-payment
            
            ModLogger.Info($"Court session complete. Final bail: ${session.NegotiatedAmount:F0}");
            
            // Hand off to bail system for payment processing
            var bailSystem = Core.Instance?.GetBailSystem();
            if (bailSystem != null)
            {
                yield return bailSystem.ProcessBailPayment(session.Defendant, session.NegotiatedAmount);
            }
            
            yield return new WaitForSeconds(1f);
        }

        public float CalculateNegotiationSuccess(float playerSkill, float evidenceStrength, float witnessCredibility)
        {
            // Calculate negotiation success based on multiple factors
            float baseSuccess = playerSkill * 0.3f; // 30% from player skill
            float evidenceBonus = evidenceStrength * 0.4f; // 40% from evidence
            float witnessBonus = witnessCredibility * 0.3f; // 30% from witnesses
            
            float totalSuccess = baseSuccess + evidenceBonus + witnessBonus;
            
            // Clamp between 0 and 1
            return Mathf.Clamp01(totalSuccess);
        }

        public string GetJudgeResponse(float negotiationSuccess, float requestedReduction)
        {
            if (negotiationSuccess > 0.8f)
            {
                return "The court is impressed with your argument. We'll consider a significant reduction.";
            }
            else if (negotiationSuccess > 0.6f)
            {
                return "You make some valid points. A moderate reduction may be possible.";
            }
            else if (negotiationSuccess > 0.4f)
            {
                return "Your argument has some merit, but the evidence is not compelling enough for a large reduction.";
            }
            else
            {
                return "The court finds your arguments unconvincing. The original bail amount stands.";
            }
        }
    }
}
