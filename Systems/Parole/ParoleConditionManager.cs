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
    /// <remarks>
    /// The registry is process-local. A manager-owned instance can be torn down through the
    /// static lifecycle helpers, while a compatibility-created instance is deliberately left
    /// registered. Runtime active conditions are rebuilt on initialization/restore; the
    /// persisted parole record remains the source of condition IDs across saves.
    /// </remarks>
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
        /// <param name="instance">Instance to expose through the compatibility accessor.</param>
        /// <param name="managedBySystemManager">Whether the system manager owns its teardown.</param>
        /// <remarks>Registration replaces any existing reference without shutting the previous instance down.</remarks>
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
        /// <returns>The existing registered instance, or a newly registered manager-owned instance.</returns>
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
        /// <param name="instance">Current registered instance when the method returns true.</param>
        /// <returns><see langword="true"/> when a condition manager is registered.</returns>
        public static bool TryGetRegisteredInstance(out ParoleConditionManager instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// Tears down the manager-owned instance while leaving compatibility-created instances alone.
        /// </summary>
        /// <returns><see langword="true"/> when a manager-owned reference was cleared.</returns>
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

        // Definitions are rebuilt once by the constructor; active conditions are a per-term
        // runtime projection and are returned as copies to keep callers from mutating the list.
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
        /// <remarks>The current registry contains curfew, restricted zones, drug testing, and employment.</remarks>
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
        /// <param name="rapSheet">RapSheet containing the active parole record and LSI/crime history.</param>
        /// <remarks>
        /// The in-memory active list is cleared first, then applicable definitions are added.
        /// ParoleRecord.AddActiveCondition prevents duplicate persisted IDs on repeated calls,
        /// but this method does not remove previously persisted IDs that are no longer applicable.
        /// A missing RapSheet or parole record is a logged no-op.
        /// </remarks>
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
        /// <param name="conditionIds">Persisted condition identifiers to restore.</param>
        /// <remarks>
        /// Restoration only rebuilds the in-memory list from registered definitions; unknown
        /// IDs are ignored and the persisted list is not rewritten.
        /// </remarks>
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
        /// <returns>A new list containing the currently active condition implementations.</returns>
        public List<IParoleCondition> GetActiveConditions()
        {
            return new List<IParoleCondition>(_activeConditions);
        }

        /// <summary>
        /// Get condition descriptions for the release UI
        /// </summary>
        /// <returns>Static general requirements plus descriptions of the active special conditions.</returns>
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
        /// <param name="conditionId">Case-sensitive registered condition identifier.</param>
        /// <returns>The matching condition, or <see langword="null"/> when not registered.</returns>
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
        /// <param name="conditionId">Case-sensitive condition identifier to check.</param>
        /// <returns><see langword="true"/> when the condition is active for the current term.</returns>
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
