using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using System.Collections.Generic;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Manages the registry and activation of parole conditions.
    /// Determines which conditions apply for a given parole term and provides
    /// methods for officer-proximity and check-in condition checks.
    /// </summary>
    public class ParoleConditionManager
    {
        private static ParoleConditionManager _instance;
        private static bool _isManagedBySystemManager;

        /// <summary>
        /// Compatibility accessor. Prefers a manager-registered instance when available.
        /// </summary>
        public static ParoleConditionManager Instance
        {
            get
            {
                if (TryGetRegisteredInstance(out var existing))
                {
                    return existing;
                }

                return RegisterInstance(new ParoleConditionManager(), false);
            }
        }

        /// <summary>
        /// Returns true when a condition manager is already registered.
        /// </summary>
        public static bool HasRegisteredInstance => _instance != null;

        /// <summary>
        /// Register the active condition manager instance.
        /// </summary>
        public static ParoleConditionManager RegisterInstance(ParoleConditionManager instance, bool managedBySystemManager = false)
        {
            if (instance == null)
            {
                return null;
            }

            _instance = instance;
            _isManagedBySystemManager = managedBySystemManager;
            return _instance;
        }

        /// <summary>
        /// Create the manager-owned instance when none is registered yet.
        /// </summary>
        public static ParoleConditionManager BootstrapManagedInstance()
        {
            if (TryGetRegisteredInstance(out var existing))
            {
                return existing;
            }

            return RegisterInstance(new ParoleConditionManager(), true);
        }

        /// <summary>
        /// Returns the currently registered instance when present.
        /// </summary>
        public static bool TryGetRegisteredInstance(out ParoleConditionManager instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// Tears down the manager-owned instance while leaving compatibility-created instances alone.
        /// </summary>
        public static bool ShutdownManagedInstance()
        {
            if (_instance == null || !_isManagedBySystemManager)
            {
                return false;
            }

            _instance = null;
            _isManagedBySystemManager = false;
            return true;
        }

        private readonly List<IParoleCondition> _allConditions = new List<IParoleCondition>();
        private readonly List<IParoleCondition> _activeConditions = new List<IParoleCondition>();

        /// <summary>
        /// Create a parole-condition manager instance suitable for explicit construction/injection.
        /// </summary>
        public ParoleConditionManager()
        {
            RegisterAllConditions();
        }

        /// <summary>
        /// Register all available condition implementations
        /// </summary>
        private void RegisterAllConditions()
        {
            _allConditions.Clear();
            _allConditions.Add(new Conditions.CurfewCondition());
            _allConditions.Add(new Conditions.RestrictedZoneCondition());
            _allConditions.Add(new Conditions.DrugTestCondition());
            _allConditions.Add(new Conditions.EmploymentCondition());
        }

        /// <summary>
        /// Initialize conditions for a parole term based on the player's rap sheet.
        /// Determines which conditions should be active and stores their IDs on the parole record.
        /// </summary>
        public void InitializeConditions(RapSheet rapSheet)
        {
            _activeConditions.Clear();

            if (rapSheet == null || rapSheet.CurrentParoleRecord == null)
            {
                ModLogger.Warn("[CONDITIONS] Cannot initialize conditions - no active parole record");
                return;
            }

            var paroleRecord = rapSheet.CurrentParoleRecord;

            foreach (var condition in _allConditions)
            {
                if (condition.IsApplicable(rapSheet))
                {
                    _activeConditions.Add(condition);
                    paroleRecord.AddActiveCondition(condition.ConditionId);
                    ModLogger.Info($"[CONDITIONS] Activated condition: {condition.ConditionName} ({condition.ConditionId})");
                }
            }

            ModLogger.Info($"[CONDITIONS] Initialized {_activeConditions.Count} conditions for {rapSheet.FullName}");
        }

        /// <summary>
        /// Restore active conditions from saved condition IDs (after load)
        /// </summary>
        public void RestoreConditionsFromIds(List<string> conditionIds)
        {
            _activeConditions.Clear();

            if (conditionIds == null) return;

            foreach (var condition in _allConditions)
            {
                if (conditionIds.Contains(condition.ConditionId))
                {
                    _activeConditions.Add(condition);
                }
            }

            ModLogger.Debug($"[CONDITIONS] Restored {_activeConditions.Count} conditions from saved IDs");
        }

        /// <summary>
        /// Get all currently active conditions
        /// </summary>
        public List<IParoleCondition> GetActiveConditions()
        {
            return new List<IParoleCondition>(_activeConditions);
        }

        /// <summary>
        /// Get condition descriptions for the release UI
        /// </summary>
        public (List<string> generalConditions, List<string> specialConditions) GetConditionDescriptions()
        {
            var generalConditions = new List<string>
            {
                "Report to parole officer as required",
                "No possession of illegal items",
                "Comply with all search requests",
                "Remain within designated areas"
            };

            var specialConditions = new List<string>();

            foreach (var condition in _activeConditions)
            {
                specialConditions.Add(condition.ConditionDescription);
            }

            return (generalConditions, specialConditions);
        }

        /// <summary>
        /// Get a specific condition by ID
        /// </summary>
        public IParoleCondition GetCondition(string conditionId)
        {
            foreach (var condition in _allConditions)
            {
                if (condition.ConditionId == conditionId)
                    return condition;
            }
            return null;
        }

        /// <summary>
        /// Check if a specific condition type is active
        /// </summary>
        public bool IsConditionActive(string conditionId)
        {
            foreach (var condition in _activeConditions)
            {
                if (condition.ConditionId == conditionId)
                    return true;
            }
            return false;
        }
    }
}
