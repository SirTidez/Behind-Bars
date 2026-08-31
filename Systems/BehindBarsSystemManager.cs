using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems.Parole;
using Behind_Bars.Utils;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Global runtime owner for the justice-system subsystem graph.
    /// This manager owns construction, access, and shutdown for the core gameplay services
    /// that previously lived directly on <see cref="Core"/>.
    /// </summary>
    /// <remarks>
    /// Initialization is intentionally ordered from jail/release through parole support and
    /// NPC/UI bindings so cross-domain resolvers can use the manager graph. The initialized
    /// flag is set only after the full sequence returns; a mid-sequence exception can therefore
    /// leave a partially populated graph for diagnostics, and shutdown is best effort across
    /// each service boundary.
    /// </remarks>
    public class BehindBarsSystemManager : ISubsystemLifecycle
    {
        /// <summary>
        /// Jail-domain manager scaffold.
        /// </summary>
        public JailManager? JailManager { get; private set; }

        /// <summary>
        /// Jail flow ownership.
        /// </summary>
        public JailSystem? JailSystem { get; private set; }

        /// <summary>
        /// NPC-domain manager scaffold.
        /// </summary>
        public NpcManager? NpcManager { get; private set; }

        /// <summary>
        /// Bail flow ownership.
        /// </summary>
        public BailSystem? BailSystem { get; private set; }

        /// <summary>
        /// Crime-state ownership.
        /// </summary>
        public CrimeManager? CrimeManager { get; private set; }

        /// <summary>
        /// UI-domain ownership.
        /// </summary>
        public JusticeUIManager? UIManager { get; private set; }

        /// <summary>
        /// Court flow ownership.
        /// </summary>
        public CourtSystem? CourtSystem { get; private set; }

        /// <summary>
        /// Parole-domain manager scaffold.
        /// </summary>
        public ParoleManager? ParoleManager { get; private set; }

        /// <summary>
        /// Parole flow ownership.
        /// </summary>
        public ParoleSystem? ParoleSystem { get; private set; }

        /// <summary>
        /// File helper ownership for justice-system data paths and caches.
        /// </summary>
        public FileUtilities? FileUtilitiesService { get; private set; }

        /// <summary>
        /// Release flow ownership.
        /// </summary>
        public ReleaseManager? ReleaseManagerService { get; private set; }

        /// <summary>
        /// Stable player identity ownership for cross-subsystem runtime keys.
        /// </summary>
        public IPlayerKeyService? PlayerKeyService { get; private set; }

        /// <summary>
        /// Parole condition-registry ownership.
        /// </summary>
        public ParoleConditionManager? ParoleConditionManagerService { get; private set; }

        /// <summary>
        /// Parole fee-scheduling ownership.
        /// </summary>
        public ParoleFeeSystem? ParoleFeeSystemService { get; private set; }

        /// <summary>
        /// Parole home-visit ownership.
        /// </summary>
        public HomeVisitSystem? HomeVisitSystemService { get; private set; }

        // Set only after Initialize completes. This guard prevents duplicate graph creation,
        // but does not itself roll back a partially completed initialization.
        private bool _isInitialized;
        // Named event handlers are retained so the manager can remove the exact subscriptions
        // it added when the owned parole system is still available.
        private bool _isSubscribedToParoleLifecycle;

        /// <inheritdoc />
        /// <remarks>
        /// Constructs owned shells, bootstraps managed support services, attaches collaborators,
        /// subscribes to managed parole lifecycle events, and refreshes scene bindings. Repeated
        /// calls after successful initialization are ignored.
        /// </remarks>
        public void Initialize()
        {
            if (_isInitialized)
            {
                ModLogger.Debug("BehindBarsSystemManager already initialized");
                return;
            }

            ModLogger.Debug("Initializing BehindBarsSystemManager...");

            JailSystem = new JailSystem();
            JailSystem.Initialize();
            JailManager = new JailManager(JailSystem);

            // Preserve the existing release-manager bootstrap inside the new ownership boundary.
            MelonLogger.Msg("[BehindBarsSystemManager] Initializing ReleaseManager");
            ReleaseManagerService = ReleaseManager.BootstrapManagedInstance();
            if (ReleaseManagerService != null)
            {
                ModLogger.Debug("ReleaseManager initialized under BehindBarsSystemManager");
                JailManager.AttachReleaseManager(ReleaseManagerService);
            }
            else
            {
                ModLogger.Warn("ReleaseManager bootstrap returned null under BehindBarsSystemManager");
            }

            BailSystem = new BailSystem();
            CrimeManager = new CrimeManager();
            CrimeManager.Initialize();
            UIManager = new JusticeUIManager();
            UIManager.Initialize();
            CourtSystem = new CourtSystem();
            ParoleConditionManagerService = ParoleConditionManager.BootstrapManagedInstance();
            ParoleFeeSystemService = ParoleFeeSystem.BootstrapManagedInstance();
            HomeVisitSystemService = HomeVisitSystem.BootstrapManagedInstance();
            ParoleSystem = new ParoleSystem();
            ParoleManager = new ParoleManager(ParoleSystem);
            ParoleManager.AttachSupportServices(ParoleConditionManagerService, ParoleFeeSystemService, HomeVisitSystemService);
            PlayerKeyService = new PlayerKeyService();
            NpcManager = new NpcManager();

            Behind_Bars.Utils.FileUtilities.Initialize();
            FileUtilitiesService = Utils.FileUtilities.Instance;
            SubscribeToParoleLifecycle();
            RefreshSceneManagerBindings();

            _isInitialized = true;
            ModLogger.Debug("BehindBarsSystemManager initialized successfully");
        }

        /// <summary>
        /// Initialize the scene-bound UI layer through the manager-owned UI service.
        /// </summary>
        /// <remarks>
        /// This is separate from graph initialization because the UI requires a gameplay HUD;
        /// it also refreshes scene jail/NPC bindings after the UI handoff.
        /// </remarks>
        public void InitializeSceneUI()
        {
            UIManager?.InitializeSceneUI();
            RefreshSceneManagerBindings();
        }

        /// <summary>
        /// Refresh scene-bound collaborator attachments for scaffold managers without changing live flow ownership.
        /// </summary>
        /// <remarks>
        /// Booking and NPC references are resolved from the current scene and attached to their
        /// owning shells. This method does not create a second jail/release/parole service graph.
        /// </remarks>
        public void RefreshSceneManagerBindings()
        {
            JailManager?.AttachBookingProcess(Core.ResolveBookingProcess());
            NpcManager?.RefreshSceneBindings();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Unsubscribes manager-owned parole handlers first, then requests best-effort shutdown
        /// of jail/parole/NPC/crime/support/UI/release services before clearing references. A
        /// service exception is logged and does not prevent later cleanup groups from running;
        /// individual service shells may retain their own incomplete transient state.
        /// </remarks>
        public void Shutdown()
        {
            if (!_isInitialized)
            {
                return;
            }

            UnsubscribeFromParoleLifecycle();

            try
            {
                JailManager?.Shutdown();
                ParoleManager?.Shutdown();
                NpcManager?.Shutdown();
                ParoleSystem?.Shutdown();
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"BehindBarsSystemManager: parole shutdown reported an issue: {ex.Message}");
            }

            try
            {
                CrimeManager?.Shutdown();
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"BehindBarsSystemManager: crime shutdown reported an issue: {ex.Message}");
            }

            try
            {
                ParoleConditionManager.ShutdownManagedInstance();
                ParoleFeeSystem.ShutdownManagedInstance();
                HomeVisitSystem.ShutdownManagedInstance();
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"BehindBarsSystemManager: parole support-service shutdown reported an issue: {ex.Message}");
            }

            try
            {
                UIManager?.Shutdown();
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"BehindBarsSystemManager: UI shutdown reported an issue: {ex.Message}");
            }

            try
            {
                if (ReleaseManagerService != null)
                {
                    ReleaseManager.ShutdownManagedInstance();
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"BehindBarsSystemManager: release shutdown reported an issue: {ex.Message}");
            }

            JailSystem = null;
            JailManager = null;
            BailSystem = null;
            CrimeManager = null;
            NpcManager = null;
            UIManager = null;
            CourtSystem = null;
            ParoleSystem = null;
            ParoleManager = null;
            ReleaseManagerService = null;
            FileUtilitiesService = null;
            PlayerKeyService = null;
            ParoleConditionManagerService = null;
            ParoleFeeSystemService = null;
            HomeVisitSystemService = null;
            _isInitialized = false;

            ModLogger.Debug("BehindBarsSystemManager shut down");
        }

        /// <summary>
        /// Attach the manager's stable handlers to the owned parole lifecycle events once.
        /// </summary>
        /// <remarks>The guard prevents duplicate forwarding into the scene NPC orchestrator.</remarks>
        private void SubscribeToParoleLifecycle()
        {
            if (_isSubscribedToParoleLifecycle || ParoleSystem == null)
            {
                return;
            }

            ParoleSystem.ParoleStarted += HandleParoleStarted;
            ParoleSystem.ParoleEnded += HandleParoleEnded;
            _isSubscribedToParoleLifecycle = true;
            ModLogger.Debug("BehindBarsSystemManager subscribed to manager-owned ParoleSystem lifecycle events");
        }

        /// <summary>
        /// Remove the manager's parole lifecycle handlers when the owned event source is present.
        /// </summary>
        /// <remarks>
        /// Unsubscription is idempotent; if the source has already been cleared, the guard
        /// returns without attempting to recreate or resolve a replacement source.
        /// </remarks>
        private void UnsubscribeFromParoleLifecycle()
        {
            if (!_isSubscribedToParoleLifecycle || ParoleSystem == null)
            {
                return;
            }

            try
            {
                ParoleSystem.ParoleStarted -= HandleParoleStarted;
                ParoleSystem.ParoleEnded -= HandleParoleEnded;
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"BehindBarsSystemManager: parole lifecycle unsubscribe reported an issue: {ex.Message}");
            }

            _isSubscribedToParoleLifecycle = false;
            ModLogger.Debug("BehindBarsSystemManager unsubscribed from manager-owned ParoleSystem lifecycle events");
        }

        /// <summary>
        /// Forward parole-start ownership transitions through the manager graph to the
        /// scene-bound parole NPC orchestrator when it is available.
        /// </summary>
        private void HandleParoleStarted(Player player)
        {
            NpcManager.HandleParoleStarted(player);
        }

        /// <summary>
        /// Forward parole-end ownership transitions through the manager graph to the
        /// scene-bound parole NPC orchestrator when it is available.
        /// </summary>
        private void HandleParoleEnded(Player player)
        {
            NpcManager.HandleParoleEnded(player);
        }
    }
}
