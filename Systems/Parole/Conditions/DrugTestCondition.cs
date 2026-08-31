using Behind_Bars.Systems.CrimeTracking;

namespace Behind_Bars.Systems.Parole.Conditions
{
    /// <summary>
    /// Drug testing condition applied during check-ins.
    /// Applies if player has drug-related crimes.
    /// Test probability varies by LSI level.
    /// </summary>
    /// <remarks>
    /// This implementation exposes applicability and a probability table only; the actual
    /// check-in test/roll and any violation side effect are owned by callers.
    /// </remarks>
    public class DrugTestCondition : IParoleCondition
    {
        /// <inheritdoc cref="IParoleCondition.ConditionId" />
        public string ConditionId => "drug_test";
        /// <inheritdoc cref="IParoleCondition.ConditionName" />
        public string ConditionName => "Drug Testing";
        /// <inheritdoc cref="IParoleCondition.ConditionDescription" />
        public string ConditionDescription => "Submit to random drug testing at check-ins";
        /// <inheritdoc cref="IParoleCondition.ViolationType" />
        public ViolationType ViolationType => ViolationType.ContrabandPossession;
        /// <inheritdoc cref="IParoleCondition.CompliancePenalty" />
        public float CompliancePenalty => 15f;

        /// <inheritdoc cref="IParoleCondition.IsApplicable" />
        /// <remarks>Matches lower-cased crime names containing drug, trafficking, possession, or dealing.</remarks>
        public bool IsApplicable(RapSheet rapSheet)
        {
            if (rapSheet == null) return false;

            var crimes = rapSheet.GetAllCrimes();
            if (crimes == null || crimes.Count == 0) return false;

            foreach (var crime in crimes)
            {
                string crimeName = crime.GetCrimeName().ToLower();
                if (crimeName.Contains("drug") || crimeName.Contains("trafficking") ||
                    crimeName.Contains("possession") || crimeName.Contains("dealing"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get the probability of a drug test at check-in based on LSI level
        /// </summary>
        /// <param name="lsiLevel">LSI level selecting the fixed probability.</param>
        /// <returns>Probability from 0.15 to 0.60, or zero for no/unknown LSI.</returns>
        public static float GetTestProbability(LSILevel lsiLevel)
        {
            switch (lsiLevel)
            {
                case LSILevel.Severe: return 0.60f;
                case LSILevel.High: return 0.60f;
                case LSILevel.Medium: return 0.30f;
                case LSILevel.Minimum: return 0.15f;
                default: return 0f;
            }
        }
    }
}
