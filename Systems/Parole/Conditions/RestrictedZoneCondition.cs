using Behind_Bars.Systems.CrimeTracking;
using System.Collections.Generic;
using UnityEngine;

namespace Behind_Bars.Systems.Parole.Conditions
{
    /// <summary>
    /// Restricts the player from entering certain zones based on their crime history.
    /// Drug crimes restrict access to known dealing areas, violent crimes restrict
    /// access to certain neighborhoods.
    /// Detection is officer-proximity only.
    /// </summary>
    public class RestrictedZoneCondition : IParoleCondition
    {
        public string ConditionId => "restricted_zones";
        public string ConditionName => "Restricted Zones";
        public string ConditionDescription => "Stay away from designated restricted areas";
        public ViolationType ViolationType => ViolationType.RestrictedAreaViolation;
        public float CompliancePenalty => 8f;

        /// <summary>
        /// Represents a restricted zone with a center point and radius
        /// </summary>
        public struct RestrictedZone
        {
            public string Name;
            public Vector3 Center;
            public float Radius;

            public RestrictedZone(string name, Vector3 center, float radius)
            {
                Name = name;
                Center = center;
                Radius = radius;
            }
        }

        // Known dealing areas - approximate positions in the game world
        private static readonly RestrictedZone[] DrugZones = new RestrictedZone[]
        {
            new RestrictedZone("Docks Alley", new Vector3(-120f, 0f, 50f), 40f),
            new RestrictedZone("Motel Area", new Vector3(80f, 0f, -30f), 35f),
        };

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
