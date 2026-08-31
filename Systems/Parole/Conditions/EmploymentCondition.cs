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
    /// <remarks>
    /// The condition exposes the ownership predicate only; check-in warning/escalation state
    /// is applied by callers. The current predicate treats any owned Property component as
    /// employment and does not inspect a player-specific income stream.
    /// </remarks>
    public class EmploymentCondition : IParoleCondition
    {
        /// <inheritdoc cref="IParoleCondition.ConditionId" />
        public string ConditionId => "employment";
        /// <inheritdoc cref="IParoleCondition.ConditionName" />
        public string ConditionName => "Employment Verification";
        /// <inheritdoc cref="IParoleCondition.ConditionDescription" />
        public string ConditionDescription => "Maintain employment or income-generating activity";
        /// <inheritdoc cref="IParoleCondition.ViolationType" />
        public ViolationType ViolationType => ViolationType.Other;
        /// <inheritdoc cref="IParoleCondition.CompliancePenalty" />
        public float CompliancePenalty => 5f;

        /// <summary>
        /// Number of consecutive check-in failures before a formal violation is recorded
        /// </summary>
        public const int WARNINGS_BEFORE_VIOLATION = 3;

        /// <inheritdoc cref="IParoleCondition.IsApplicable" />
        /// <remarks>Returns true for Medium, High, or Severe LSI and false for null/lower levels.</remarks>
        public bool IsApplicable(RapSheet rapSheet)
        {
            if (rapSheet == null) return false;
            // Employment condition applies for Medium, High, and Severe LSI
            return rapSheet.LSILevel >= LSILevel.Medium;
        }

        /// <summary>
        /// Check if the player is currently employed (owns a business or income-generating property)
        /// </summary>
        /// <returns><see langword="true"/> when any scene <c>Property</c> is owned; false when none or lookup fails.</returns>
        /// <remarks>
        /// The current implementation searches all scene properties, so the result is global
        /// rather than explicitly filtered by the player parameter (there is no parameter here).
        /// </remarks>
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
