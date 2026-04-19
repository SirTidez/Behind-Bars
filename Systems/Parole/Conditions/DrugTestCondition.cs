using Behind_Bars.Systems.CrimeTracking;

namespace Behind_Bars.Systems.Parole.Conditions
{
    /// <summary>
    /// Drug testing condition applied during check-ins.
    /// Applies if player has drug-related crimes.
    /// Test probability varies by LSI level.
    /// </summary>
    public class DrugTestCondition : IParoleCondition
    {
        public string ConditionId => "drug_test";
        public string ConditionName => "Drug Testing";
        public string ConditionDescription => "Submit to random drug testing at check-ins";
        public ViolationType ViolationType => ViolationType.ContrabandPossession;
        public float CompliancePenalty => 15f;

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
