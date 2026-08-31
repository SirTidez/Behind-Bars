using Behind_Bars.Systems.CrimeTracking;
using System.Collections.Generic;
using UnityEngine;

namespace Behind_Bars.Systems.Parole.Conditions
{
    /// <summary>
    /// Restricts the player from entering concrete zones selected from crime history.
    /// Drug crimes currently restrict access to the known dealing areas; violent crimes can
    /// activate the condition but have no concrete zone list yet.
    /// Detection is officer-proximity only.
    /// </summary>
    /// <remarks>
    /// The current concrete zone table contains only two approximate drug/dealing areas.
    /// Violent crime history makes the condition applicable, but does not currently add a
    /// violent-zone entry, so a violent-only record has no restricted geometry to match.
    /// These helpers only evaluate applicability/geometry; the caller owns officer-proximity
    /// gating and violation recording.
    /// </remarks>
    public class RestrictedZoneCondition : IParoleCondition
    {
        /// <inheritdoc cref="IParoleCondition.ConditionId" />
        public string ConditionId => "restricted_zones";
        /// <inheritdoc cref="IParoleCondition.ConditionName" />
        public string ConditionName => "Restricted Zones";
        /// <inheritdoc cref="IParoleCondition.ConditionDescription" />
        public string ConditionDescription => "Stay away from designated restricted areas";
        /// <inheritdoc cref="IParoleCondition.ViolationType" />
        public ViolationType ViolationType => ViolationType.RestrictedAreaViolation;
        /// <inheritdoc cref="IParoleCondition.CompliancePenalty" />
        public float CompliancePenalty => 8f;

        /// <summary>
        /// Represents a restricted zone with a center point and radius
        /// </summary>
        public struct RestrictedZone
        {
            /// <summary>Player-facing zone name.</summary>
            public string Name;
            /// <summary>World-space center used by the distance check.</summary>
            public Vector3 Center;
            /// <summary>Inclusive world-space radius of the zone.</summary>
            public float Radius;

            /// <summary>Creates a restricted-zone geometry value.</summary>
            /// <param name="name">Player-facing zone name.</param>
            /// <param name="center">World-space center.</param>
            /// <param name="radius">Inclusive distance radius.</param>
            public RestrictedZone(string name, Vector3 center, float radius)
            {
                Name = name;
                Center = center;
                Radius = radius;
            }
        }

        // Known dealing areas - approximate positions in the game world. This is the only
        // concrete zone set currently returned by GetApplicableZones.
        private static readonly RestrictedZone[] DrugZones = new RestrictedZone[]
        {
            new RestrictedZone("Docks Alley", new Vector3(-120f, 0f, 50f), 40f),
            new RestrictedZone("Motel Area", new Vector3(80f, 0f, -30f), 35f),
        };

        /// <inheritdoc cref="IParoleCondition.IsApplicable" />
        /// <remarks>Activates for any drug-related or violent crime name in the RapSheet.</remarks>
        public bool IsApplicable(RapSheet rapSheet)
        {
            if (rapSheet == null) return false;

            // Restricted zones apply if player has drug or violent crimes
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
                if (crimeName.Contains("assault") || crimeName.Contains("violence") ||
                    crimeName.Contains("murder") || crimeName.Contains("battery"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get the restricted zones applicable for a given rap sheet
        /// </summary>
        /// <param name="rapSheet">RapSheet whose crimes select applicable zones.</param>
        /// <returns>A copy of the current concrete drug-zone list, or an empty list.</returns>
        /// <remarks>
        /// Despite applicability including violent crimes, this method currently adds zones
        /// only when a drug-related crime is present; violent-only records return no zones.
        /// </remarks>
        public static List<RestrictedZone> GetApplicableZones(RapSheet rapSheet)
        {
            var zones = new List<RestrictedZone>();
            if (rapSheet == null) return zones;

            var crimes = rapSheet.GetAllCrimes();
            if (crimes == null) return zones;

            bool hasDrugCrimes = false;

            foreach (var crime in crimes)
            {
                string crimeName = crime.GetCrimeName().ToLower();
                if (crimeName.Contains("drug") || crimeName.Contains("trafficking") ||
                    crimeName.Contains("possession") || crimeName.Contains("dealing"))
                {
                    hasDrugCrimes = true;
                }
            }

            if (hasDrugCrimes)
            {
                zones.AddRange(DrugZones);
            }

            return zones;
        }

        /// <summary>
        /// Check if a position is within any restricted zone
        /// </summary>
        /// <param name="position">World-space position to test.</param>
        /// <param name="rapSheet">RapSheet selecting the concrete zones.</param>
        /// <returns>A tuple containing the geometry result and matched zone name, or false/null.</returns>
        /// <remarks>This is a pure distance check; it does not inspect the supervising officer or record a violation.</remarks>
        public static (bool isRestricted, string zoneName) IsInRestrictedZone(Vector3 position, RapSheet rapSheet)
        {
            var zones = GetApplicableZones(rapSheet);

            foreach (var zone in zones)
            {
                float distance = Vector3.Distance(position, zone.Center);
                if (distance <= zone.Radius)
                {
                    return (true, zone.Name);
                }
            }

            return (false, null);
        }
    }
}
