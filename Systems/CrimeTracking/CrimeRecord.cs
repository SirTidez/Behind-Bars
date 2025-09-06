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
        private Dictionary<Type, List<CrimeInstance>> _crimesByType = new Dictionary<Type, List<CrimeInstance>>();
        private List<CrimeInstance> _allCrimes = new List<CrimeInstance>();
        
        public int TotalCrimeCount => _allCrimes.Count;
        public float CurrentWantedLevel { get; private set; }
        
        /// <summary>
        /// Add a new crime to the record
        /// </summary>
        public void AddCrime(CrimeInstance crimeInstance)
        {
            Type crimeType = crimeInstance.Crime.GetType();
            
            if (!_crimesByType.ContainsKey(crimeType))
            {
                _crimesByType[crimeType] = new List<CrimeInstance>();
            }
            
            _crimesByType[crimeType].Add(crimeInstance);
            _allCrimes.Add(crimeInstance);
            
            UpdateWantedLevel();
            
            ModLogger.Info($"Added crime: {crimeInstance.Crime.CrimeName} at {crimeInstance.Location}. " +
                          $"Witnesses: {crimeInstance.WitnessIds.Count}. New wanted level: {CurrentWantedLevel:F2}");
        }
        
        /// <summary>
        /// Add a crime with automatic instance creation
        /// </summary>
        public void AddCrime(Crime crime, Vector3 location, float severity = 1.0f)
        {
            var instance = new CrimeInstance(crime, location, severity);
            AddCrime(instance);
        }
        
        /// <summary>
        /// Get all crimes of a specific type
        /// </summary>
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
        /// Get all active (non-expired) crimes
        /// </summary>
        public List<CrimeInstance> GetActiveCrimes()
        {
            CleanupExpiredCrimes();
            return new List<CrimeInstance>(_allCrimes);
        }
        
        /// <summary>
        /// Calculate total fine amount using the same logic as PenaltyHandler
        /// </summary>
        public float CalculateTotalFines()
        {
            CleanupExpiredCrimes();
            
            float totalFine = 0f;
            
            foreach (var crime in _allCrimes)
            {
                string crimeName = crime.Crime.GetType().Name;
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
        /// Remove expired crimes from the record
        /// </summary>
        private void CleanupExpiredCrimes()
        {
            var expiredCrimes = _allCrimes.Where(c => c.ShouldExpire()).ToList();
            
            foreach (var expiredCrime in expiredCrimes)
            {
                _allCrimes.Remove(expiredCrime);
                
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
            
            if (expiredCrimes.Count > 0)
            {
                ModLogger.Info($"Cleaned up {expiredCrimes.Count} expired crimes");
            }
        }
        
        /// <summary>
        /// Clear all crimes (called when serving jail time, paying fines, etc.)
        /// </summary>
        public void ClearAllCrimes()
        {
            int crimeCount = _allCrimes.Count;
            _crimesByType.Clear();
            _allCrimes.Clear();
            CurrentWantedLevel = 0f;
            
            ModLogger.Info($"Cleared all {crimeCount} crimes from record");
        }
        
        /// <summary>
        /// Get a summary of current crimes for UI display
        /// </summary>
        public Dictionary<string, int> GetCrimeSummary()
        {
            CleanupExpiredCrimes();
            
            var summary = new Dictionary<string, int>();
            
            foreach (var crime in _allCrimes)
            {
                string crimeName = crime.Crime.CrimeName;
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
        /// Convert to Schedule I's native crime format for compatibility
        /// </summary>
        public Dictionary<Crime, int> ToNativeCrimeFormat()
        {
            CleanupExpiredCrimes();
            
            var nativeCrimes = new Dictionary<Crime, int>();
            
            foreach (var crimeGroup in _crimesByType)
            {
                if (crimeGroup.Value.Count > 0)
                {
                    // Use the first instance's crime object as the key
                    Crime crimeKey = crimeGroup.Value[0].Crime;
                    nativeCrimes[crimeKey] = crimeGroup.Value.Count;
                }
            }
            
            return nativeCrimes;
        }
    }
}