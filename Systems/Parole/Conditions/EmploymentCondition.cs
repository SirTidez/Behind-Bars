using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.Property;
#else
using ScheduleOne.Property;
#endif

namespace Behind_Bars.Systems.Parole.Conditions
{
    /// <summary>
    /// Employment verification condition checked at check-ins.
    /// Applies if LSI >= Medium.
    /// Checks if player owns any business or income-generating property.
    /// Graduated consequences: warnings before formal violations.
    /// </summary>
    public class EmploymentCondition : IParoleCondition
    {
        public string ConditionId => "employment";
        public string ConditionName => "Employment Verification";
        public string ConditionDescription => "Maintain employment or income-generating activity";
        public ViolationType ViolationType => ViolationType.Other;
        public float CompliancePenalty => 5f;

        /// <summary>
        /// Number of consecutive check-in failures before a formal violation is recorded
        /// </summary>
        public const int WARNINGS_BEFORE_VIOLATION = 3;

        public bool IsApplicable(RapSheet rapSheet)
        {
            if (rapSheet == null) return false;
            // Employment condition applies for Medium, High, and Severe LSI
            return rapSheet.LSILevel >= LSILevel.Medium;
        }

        /// <summary>
        /// Check if the player is currently employed (owns a business or income-generating property)
        /// </summary>
        public static bool IsPlayerEmployed()
        {
            try
            {
                var properties = UnityEngine.Object.FindObjectsOfType<Property>();
                if (properties == null || properties.Length == 0) return false;

                foreach (var property in properties)
                {
                    if (property == null) continue;

                    try
                    {
                        if (property.IsOwned)
                        {
                            // Owning any property counts as having income/employment
                            return true;
                        }
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }
                }

                return false;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[EMPLOYMENT] Error checking employment status: {ex.Message}");
                return false;
            }
        }
    }
}
