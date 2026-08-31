using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Helpers;
using System.Collections.Generic;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Crime-domain runtime owner for criminal-record access and persistence services.
    /// Legacy singleton access remains as a temporary compatibility bridge while callers migrate.
    /// </summary>
    public sealed class CrimeManager : ISubsystemLifecycle
    {
        /// <summary>
        /// Manager-owned criminal-record cache and persistence access.
        /// </summary>
        public RapSheetManager? RapSheetManagerService { get; private set; }

        private bool _isInitialized;

        /// <inheritdoc />
        public void Initialize()
        {
            if (_isInitialized)
            {
                return;
            }

            RapSheetManagerService = RapSheetManager.BootstrapManagedInstance();
            _isInitialized = true;
            ModLogger.Debug("CrimeManager initialized");
        }

        /// <inheritdoc />
        public void Shutdown()
        {
            if (!_isInitialized)
            {
                return;
            }

            RapSheetManager.ShutdownManagedInstance();
            RapSheetManagerService = null;
            _isInitialized = false;
            ModLogger.Debug("CrimeManager shut down");
        }

        /// <summary>
        /// Return the criminal record for a player through the manager-owned rap-sheet service.
        /// </summary>
        public RapSheet? GetRapSheet(Player? player)
        {
            if (player == null)
            {
                return null;
            }

            return ResolveRapSheetService().GetRapSheet(player);
        }

        /// <summary>
        /// Mark a player's rap sheet as changed through the manager-owned service.
        /// </summary>
        public void MarkRapSheetChanged(Player? player)
        {
            if (player == null)
            {
                return;
            }

            ResolveRapSheetService().MarkRapSheetChanged(player);
        }

        /// <summary>
        /// Enumerate all known rap sheets through the manager-owned service.
        /// </summary>
        public IEnumerable<RapSheet> GetAllRapSheets()
        {
            return ResolveRapSheetService().GetAllRapSheets();
        }

        /// <summary>
        /// Clear cached rap-sheet instances through the manager-owned service.
        /// </summary>
        public void ClearRapSheetCache()
        {
            ResolveRapSheetService().ClearCache();
        }

        /// <summary>
        /// Resolve the effective rap-sheet service for this domain.
        /// Uses the manager-owned service when initialized, otherwise defers to Core's explicit compatibility shim.
        /// </summary>
        private RapSheetManager ResolveRapSheetService()
        {
            return RapSheetManagerService ?? Core.ResolveRapSheetManager();
        }
    }
}
