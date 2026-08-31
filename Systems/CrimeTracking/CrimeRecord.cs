using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
#if !MONO
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.Law;
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.CrimeTracking
{
    /// <summary>
    /// Maintains a persistent record of all crimes committed by the player
    /// </summary>
    public class CrimeRecord
    {
        // The secondary index only contains instances whose native Crime object was
        // available when they were recorded. Loaded or degraded instances can still
        // live in _allCrimes and therefore remain part of totals and wanted level.
        private Dictionary<Type, List<CrimeInstance>> _crimesByType = new Dictionary<Type, List<CrimeInstance>>();
        // Canonical insertion order for persistence, expiration, totals, and UI
        // summaries. This list is authoritative when the secondary index is absent.
        private List<CrimeInstance> _allCrimes = new List<CrimeInstance>();
        
        /// <summary>
        /// Gets the number of crime instances currently retained.
        /// </summary>
        /// <remarks>Expiration is cleaned opportunistically by aggregate/query methods, not by this getter.</remarks>
        public int TotalCrimeCount => _allCrimes.Count;

        /// <summary>
        /// Gets the clamped wanted-level contribution of the retained crime instances.
        /// </summary>
        public float CurrentWantedLevel { get; private set; }
        
        /// <summary>
        /// Adds a crime instance to the canonical list and, when possible, its type index.
        /// </summary>
        /// <param name="crimeInstance">Instance to retain.</param>
        public void AddCrime(CrimeInstance crimeInstance)
        {
            // Keep both indexes in sync when possible. A null native object is an
            // intentional persistence/compatibility fallback: the instance still
            // enters _allCrimes, but cannot be addressed by native Type.
            // Handle null Crime object gracefully
            if (crimeInstance.Crime == null)
            {
                ModLogger.Warn($"CrimeInstance has null Crime object - Description: {crimeInstance.Description}");
                // Still add it, but use Description for categorization
                _allCrimes.Add(crimeInstance);
                UpdateWantedLevel();
                ModLogger.Info($"Added crime (no Crime object): {crimeInstance.GetCrimeName()} at {crimeInstance.Location}. " +
                              $"Witnesses: {crimeInstance.WitnessIds.Count}. New wanted level: {CurrentWantedLevel:F2}");
                return;
            }
            
            Type crimeType = crimeInstance.Crime.GetType();
            
            if (!_crimesByType.ContainsKey(crimeType))
            {
                _crimesByType[crimeType] = new List<CrimeInstance>();
            }
            
            _crimesByType[crimeType].Add(crimeInstance);
            _allCrimes.Add(crimeInstance);
            
            UpdateWantedLevel();
            
            ModLogger.Info($"Added crime: {crimeInstance.GetCrimeName()} at {crimeInstance.Location}. " +
                          $"Witnesses: {crimeInstance.WitnessIds.Count}. New wanted level: {CurrentWantedLevel:F2}");
        }
        
        /// <summary>
        /// Creates and adds a crime instance at the supplied location.
        /// </summary>
        /// <param name="crime">Native crime object to associate.</param>
        /// <param name="location">World-space incident location.</param>
        /// <param name="severity">Severity multiplier used for wanted/fine calculations.</param>
        public void AddCrime(Crime crime, Vector3 location, float severity = 1.0f)
        {
            var instance = new CrimeInstance(crime, location, severity);
            AddCrime(instance);
        }
        
        /// <summary>
        /// Gets a detached list of crimes indexed by the requested native type.
        /// </summary>
        /// <typeparam name="T">Native crime type to query.</typeparam>
        /// <returns>A new list, or an empty list when no type-indexed crimes exist.</returns>
        public List<CrimeInstance> GetCrimesByType<T>() where T : Crime
        {
            Type crimeType = typeof(T);
            if (_crimesByType.ContainsKey(crimeType))
            {
                return new List<CrimeInstance>(_crimesByType[crimeType]);
            }
            return new List<CrimeInstance>();
        }
        
        /// <summary>
        /// Gets a detached list of retained crimes after opportunistic expiration cleanup.
        /// </summary>
        /// <returns>A new list of currently retained crime instances.</returns>
        public List<CrimeInstance> GetActiveCrimes()
        {
            CleanupExpiredCrimes();
            return new List<CrimeInstance>(_allCrimes);
        }
        
        /// <summary>
        /// Calculates total fines using the current PenaltyHandler-compatible table.
        /// </summary>
        /// <returns>The severity-weighted fine for all retained crime instances.</returns>
        public float CalculateTotalFines()
        {
            CleanupExpiredCrimes();
            
            float totalFine = 0f;
            
            foreach (var crime in _allCrimes)
            {
                // Use GetCrimeTypeName() for fine calculation (needs type name, not display name)
                string crimeName = crime.GetCrimeTypeName();
                int count = 1; // Each instance counts as 1
                
                // Use same fine calculation as PenaltyHandler
                totalFine += GetCrimeFine(crimeName) * count * crime.Severity;
            }
            
            return totalFine;
        }
        
        /// <summary>
        /// Get the fine amount for a specific crime type
        /// </summary>
        private float GetCrimeFine(string crimeName)
        {
            // These values mirror the mod's current PenaltyHandler compatibility
            // table; they are not read from the native game's live crime metadata.
            // Unknown names intentionally use the conservative default below.
            return crimeName switch
            {
                // Original crimes from PenaltyHandler
                "PossessingControlledSubstances" => 5f,
                "PossessingLowSeverityDrug" => 10f,
                "PossessingModerateSeverityDrug" => 20f,
                "PossessingHighSeverityDrug" => 30f,
                "Evading" => 50f,
                "FailureToComply" => 50f,
                "ViolatingCurfew" => 100f,
                "AttemptingToSell" => 150f,
                "Assault" => 75f,
                "DeadlyAssault" => 150f,
                "Vandalism" => 50f,
                "Theft" => 50f,
                "BrandishingWeapon" => 50f,
                "DischargeFirearm" => 50f,
                "VehicularAssault" => 100f,
                "DrugTrafficking" => 200f,
                
                // New crimes
                "Murder" => 1000f,
                "Manslaughter" => 300f,
                "AssaultOnCivilian" => 100f,
                "AssaultOnOfficer" => 1000f,
                "WitnessIntimidation" => 150f,
                
                // Contraband crimes
                "DrugPossessionLow" => 150f,
                "DrugPossessionModerate" => 500f,
                "DrugPossessionHigh" => 1500f,
                "DrugTraffickingCrime" => 5000f,
                "WeaponPossession" => 800f,
                
                // Default
                _ => 25f
            };
        }
        
        /// <summary>
        /// Update the current wanted level based on all crimes
        /// </summary>
        private void UpdateWantedLevel()
        {
            CleanupExpiredCrimes();
            
            float wantedLevel = 0f;
            foreach (var crime in _allCrimes)
            {
                wantedLevel += crime.GetWantedContribution();
            }
            
            CurrentWantedLevel = Mathf.Clamp(wantedLevel, 0f, 10f); // Cap at 10
        }

        /// <summary>
        /// Resets the cached wanted aggregate without removing retained crime instances.
        /// </summary>
        public void ClearWantedLevel()
        {
            // This clears only the cached aggregate. Crime instances remain available
            // for history and are not removed from either crime index.
            CurrentWantedLevel = 0f;
        }
        
        /// <summary>
        /// Remove expired crimes from the record
        /// </summary>
        private void CleanupExpiredCrimes()
        {
            // Expiration is opportunistic: callers that read aggregate data trigger
            // cleanup, rather than a background timer. Remove each expired instance
            // from the canonical list and, when present, its type index as well.
            var expiredCrimes = _allCrimes.Where(c => c.ShouldExpire()).ToList();
            
            foreach (var expiredCrime in expiredCrimes)
            {
                _allCrimes.Remove(expiredCrime);
                
                // Only remove from _crimesByType if Crime object exists
                if (expiredCrime.Crime != null)
                {
                    Type crimeType = expiredCrime.Crime.GetType();
                    if (_crimesByType.ContainsKey(crimeType))
                    {
                        _crimesByType[crimeType].Remove(expiredCrime);
                        
                        // Clean up empty lists
                        if (_crimesByType[crimeType].Count == 0)
                        {
                            _crimesByType.Remove(crimeType);
                        }
                    }
                }
            }
            
            if (expiredCrimes.Count > 0)
            {
                ModLogger.Info($"Cleaned up {expiredCrimes.Count} expired crimes");
            }
        }
        
        /// <summary>
        /// Clears both crime indexes and resets the cached wanted level.
        /// </summary>
        /// <remarks>Use for full resolution flows such as jail time or paid fines.</remarks>
        public void ClearAllCrimes()
        {
            int crimeCount = _allCrimes.Count;
            _crimesByType.Clear();
            _allCrimes.Clear();
            CurrentWantedLevel = 0f;
            
            ModLogger.Info($"Cleared all {crimeCount} crimes from record");
        }
        
        /// <summary>
        /// Gets a display-name count summary of retained crimes.
        /// </summary>
        /// <returns>A new dictionary keyed by each crime's display name.</returns>
        public Dictionary<string, int> GetCrimeSummary()
        {
            CleanupExpiredCrimes();
            
            var summary = new Dictionary<string, int>();
            
            foreach (var crime in _allCrimes)
            {
                // Use GetCrimeName() for display (user-friendly name)
                string crimeName = crime.GetCrimeName();
                if (summary.ContainsKey(crimeName))
                {
                    summary[crimeName]++;
                }
                else
                {
                    summary[crimeName] = 1;
                }
            }
            
            return summary;
        }
        
        /// <summary>
        /// Converts type-indexed entries to Schedule I's native crime/count shape.
        /// </summary>
        /// <returns>A native-object keyed count map; degraded instances are omitted.</returns>
        public Dictionary<Crime, int> ToNativeCrimeFormat()
        {
            // Only instances with a live native Crime object can be represented in
            // this compatibility shape. Degraded instances are intentionally omitted
            // here even though they remain in _allCrimes and affect mod totals.
            CleanupExpiredCrimes();
            
            var nativeCrimes = new Dictionary<Crime, int>();
            
            foreach (var crimeGroup in _crimesByType)
            {
                if (crimeGroup.Value.Count > 0)
                {
                    // The native format groups by object identity. The first instance
                    // supplies the key while the secondary index supplies the count.
                    var firstInstance = crimeGroup.Value[0];
                    if (firstInstance.Crime != null)
                    {
                        Crime crimeKey = firstInstance.Crime;
                        nativeCrimes[crimeKey] = crimeGroup.Value.Count;
                    }
                }
            }
            
            return nativeCrimes;
        }
    }
}
