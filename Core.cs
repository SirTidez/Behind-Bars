using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using HarmonyLib;
using System;
using System.Runtime.CompilerServices;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.UI;
using Behind_Bars.Systems.CrimeDetection;
using Behind_Bars.Systems.Jail;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Behind_Bars.Systems.NPCs.PresetParoleOfficerRoutes;
using BBHelpers = Behind_Bars.Helpers.Helpers;

using Object = UnityEngine.Object;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif
using Behind_Bars.Players;
using Behind_Bars.Systems;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Data;
using Behind_Bars.Harmony;
using Behind_Bars.Utils;
using Behind_Bars.Systems.Parole;
using UnityEngine.AI;
#if !MONO
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

#if !MONO
using Il2CppScheduleOne.Law;
#else
using ScheduleOne.Law;
#endif

#if MONO
using FishNet;
using ScheduleOne.UI.Phone;
using ScheduleOne.DevUtilities;
using FishNet.Managing;
#else
using Il2CppFishNet;
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.UI.Phone;
using Il2CppScheduleOne.DevUtilities;
#endif

[assembly: MelonInfo(
    typeof(Behind_Bars.Core),
    Constants.MOD_NAME,
    Constants.MOD_VERSION,
    Constants.MOD_AUTHOR
)]
[assembly: MelonColor(0, 255, 0, 255)]
[assembly: MelonAdditionalCredits("Dreous - Jail Scripting and Unity work")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Behind_Bars
{
    /// <summary>
    /// Melon entry point and lifecycle owner for Behind Bars. The core service graph persists
    /// across scenes, while gameplay-session state, native loading holds, and HUD presentation
    /// are explicitly invalidated when the Main scene ends.
    /// </summary>
    public class Core : MelonMod
    {
        /// <summary>Gets the currently initialized Behind Bars mod instance, if startup completed.</summary>
        public static Core? Instance { get; private set; }

        /// <summary>
        /// True only while the loaded Main scene owns Behind Bars gameplay work.
        /// Long-running routines use this as their scene-transition cancellation boundary.
        /// </summary>
        public static bool IsGameplaySceneActive => Instance?._gameplaySceneActive == true;

        // Core systems. The session version invalidates stale coroutine continuations; the
        // active/ready flags are separate because a gameplay scene can exist before player
        // systems finish bootstrapping.
        private BehindBarsSystemManager? _systemManager;
        private Coroutine? _loadModCoroutine;
        private Coroutine? _playerInitializationCoroutine;
        private int _gameplaySessionVersion;
        private bool _gameplaySceneActive;
        private bool _playerSystemsReady;

        // Player management is keyed by native Player wrappers. Bootstrap overwrites an entry
        // when the same wrapper is seen again; the current scene teardown does not clear this map,
        // so callers should not treat it as a complete historical/session registry.
        private Dictionary<Player, PlayerHandler> _playerHandlers = new();

        // Jail management
        /// <summary>Gets the jail controller for the active gameplay scene, if it was created.</summary>
        public static JailController? JailController { get; private set; }

        /// <summary>
        /// Gets the cached jail asset bundle. The concrete type follows the Mono/IL2CPP runtime;
        /// callers should treat a null value as a load failure and use the idempotent loader.
        /// </summary>
        public static
#if !MONO
            Il2CppAssetBundle
#else
            AssetBundle
#endif
            ? CachedJailBundle { get; private set; }

        /// <summary>
        /// Gets the manager-owned root for the justice-system service graph. Temporary
        /// compatibility accessors below should migrate to this root over time.
        /// </summary>
        public BehindBarsSystemManager? SystemManager => _systemManager;

        /// <summary>Gets the manager-owned jail system, or null before manager initialization.</summary>
        public JailSystem? JailSystem => _systemManager?.JailSystem;
        /// <summary>Gets the manager-owned jail manager, or null before manager initialization.</summary>
        public JailManager? JailManager => _systemManager?.JailManager;
        /// <summary>Gets the manager-owned bail system, or null before manager initialization.</summary>
        public BailSystem? BailSystem => _systemManager?.BailSystem;
        /// <summary>Gets the manager-owned crime manager, or null before manager initialization.</summary>
        public CrimeManager? CrimeManager => _systemManager?.CrimeManager;
        /// <summary>Gets the manager-owned NPC manager, or null before manager initialization.</summary>
        public NpcManager? NpcManager => _systemManager?.NpcManager;
        /// <summary>Gets the manager-owned UI manager, or null before manager initialization.</summary>
        public JusticeUIManager? UIManager => _systemManager?.UIManager;
        /// <summary>Gets the manager-owned court system, or null before manager initialization.</summary>
        public CourtSystem? CourtSystem => _systemManager?.CourtSystem;
        /// <summary>Gets the manager-owned parole system, or null before manager initialization.</summary>
        public ParoleSystem? ParoleSystem => _systemManager?.ParoleSystem;
        /// <summary>Gets the manager-owned parole manager, or null before manager initialization.</summary>
        public ParoleManager? ParoleManager => _systemManager?.ParoleManager;
        /// <summary>Gets the manager-owned file service, with the compatibility singleton as fallback.</summary>
        public FileUtilities FileUtilities => _systemManager?.FileUtilitiesService ?? Utils.FileUtilities.Instance;

        /// <summary>
        /// Resolve the canonical runtime key for a player using the manager-owned key service.
        /// Falls back to the player's current name only if startup/bootstrap regressed and the service is unavailable.
        /// </summary>
        public static string ResolvePlayerKey(Player? player)
        {
            if (player == null)
            {
                return string.Empty;
            }

            return Instance?.GetPlayerKeyService()?.GetPlayerKey(player) ?? player.name;
        }

        /// <summary>
        /// Loads the jail bundle on demand once and caches the result for the current process.
        /// A null result is reported but not retried recursively, allowing scene/bootstrap
        /// callers to fail closed without creating duplicate bundle handles.
        /// </summary>
        /// <param name="reason">Diagnostic context for the on-demand load attempt.</param>
        /// <returns>The cached runtime-specific bundle, or null when loading fails.</returns>
        public static
#if !MONO
            Il2CppAssetBundle
#else
            AssetBundle
#endif
            ? EnsureJailBundleLoaded(string reason = "general use")
        {
            if (CachedJailBundle != null)
            {
                return CachedJailBundle;
            }

            ModLogger.Debug($"Loading jail asset bundle on demand for {reason}...");
            CachedJailBundle = Utils.AssetBundleUtils.LoadAssetBundle("Behind_Bars.behind_bars");

            if (CachedJailBundle == null)
            {
                ModLogger.Warn($"Failed to load behind-bars bundle on demand for {reason}");
            }

            return CachedJailBundle;
        }

        /// <summary>
        /// Resolve the canonical rap-sheet manager through the manager-owned crime domain.
        /// Falls back to the compatibility singleton only while call sites are still migrating.
        /// </summary>
        public static RapSheetManager ResolveRapSheetManager()
        {
            var managedRapSheetManager = Instance?._systemManager?.CrimeManager?.RapSheetManagerService;
            if (managedRapSheetManager != null)
            {
                return managedRapSheetManager;
            }

            return ResolveCompatibilityRapSheetManager();
        }

        /// <summary>
        /// Resolve the manager-owned UI root.
        /// Falls back to a thin compatibility wrapper while scene startup is still bootstrapping.
        /// </summary>
        public static JusticeUIManager ResolveUIManager()
        {
            var managedUiManager = Instance?._systemManager?.UIManager;
            if (managedUiManager != null)
            {
                return managedUiManager;
            }

            return ResolveCompatibilityUIManager();
        }

        /// <summary>
        /// Resolve the parole-condition support service through an explicit compatibility shim
        /// until the parole domain wires manager-owned construction.
        /// </summary>
        public static ParoleConditionManager ResolveParoleConditionManager()
        {
            if (ParoleConditionManager.TryGetRegisteredInstance(out var registeredInstance))
            {
                return registeredInstance;
            }

            return ResolveCompatibilityParoleConditionManager();
        }

        /// <summary>
        /// Resolve the parole-fee support service through an explicit compatibility shim
        /// until the parole domain wires manager-owned construction.
        /// </summary>
        public static ParoleFeeSystem ResolveParoleFeeSystem()
        {
            if (ParoleFeeSystem.TryGetRegisteredInstance(out var registeredInstance))
            {
                return registeredInstance;
            }

            return ResolveCompatibilityParoleFeeSystem();
        }

        /// <summary>
        /// Resolve the home-visit support service through an explicit compatibility shim
        /// until the parole domain wires manager-owned construction.
        /// </summary>
        public static HomeVisitSystem ResolveHomeVisitSystem()
        {
            if (HomeVisitSystem.TryGetRegisteredInstance(out var registeredInstance))
            {
                return registeredInstance;
            }

            return ResolveCompatibilityHomeVisitSystem();
        }

        /// <summary>
        /// Resolve the active jail cell-assignment manager through an explicit compatibility shim
        /// until jail ownership is fully moved behind the manager graph.
        /// </summary>
        public static CellAssignmentManager? ResolveCellAssignmentManager()
        {
            return ResolveCompatibilityCellAssignmentManager();
        }

        /// <summary>
        /// Resolve the active prison NPC registry/manager through an explicit compatibility shim
        /// until NPC ownership is fully moved behind the manager graph.
        /// </summary>
        public static PrisonNPCManager? ResolvePrisonNpcManager()
        {
            var managedNpcManager = Instance?._systemManager?.NpcManager?.PrisonNpcManager;
            if (managedNpcManager != null)
            {
                return managedNpcManager;
            }

            return ResolveCompatibilityPrisonNpcManager();
        }

        /// <summary>
        /// Resolve the manager-owned release service through an explicit compatibility shim
        /// until the jail domain fully owns release orchestration.
        /// </summary>
        public static ReleaseManager ResolveReleaseManager()
        {
            var managedReleaseManager = Instance?._systemManager?.ReleaseManagerService;
            if (managedReleaseManager != null)
            {
                return managedReleaseManager;
            }

            if (ReleaseManager.TryGetRegisteredInstance(out var registeredInstance))
            {
                return registeredInstance;
            }

            var compatibilityInstance = ResolveCompatibilityReleaseManager();
            if (compatibilityInstance != null)
            {
                return compatibilityInstance;
            }

            ModLogger.Warn("ResolveReleaseManager: no active release manager found; attempting late bootstrap");
            return ReleaseManager.BootstrapManagedInstance();
        }

        /// <summary>
        /// Resolve the active booking-process controller through an explicit compatibility shim
        /// until booking ownership is fully moved behind the jail manager graph.
        /// </summary>
        public static BookingProcess? ResolveBookingProcess()
        {
            return ResolveCompatibilityBookingProcess();
        }

        /// <summary>
        /// Resolve persistent-player justice state through an explicit compatibility shim
        /// until persistence ownership is fully moved behind the manager graph.
        /// </summary>
        public static PersistentPlayerData ResolvePersistentPlayerData()
        {
            return ResolveCompatibilityPersistentPlayerData();
        }

        /// <summary>
        /// Resolve jail-time tracking through an explicit compatibility shim
        /// until jail runtime ownership is fully moved behind the manager graph.
        /// </summary>
        public static JailTimeTracker ResolveJailTimeTracker()
        {
            return ResolveCompatibilityJailTimeTracker();
        }

        /// <summary>
        /// Resolve parole-time tracking through an explicit compatibility shim
        /// until parole runtime ownership is fully moved behind the manager graph.
        /// </summary>
        public static ParoleTimeTracker ResolveParoleTimeTracker()
        {
            var managedParoleTracker = Instance?._systemManager?.ParoleManager?.ParoleTimeTracker;
            if (managedParoleTracker != null)
            {
                return managedParoleTracker;
            }

            return ResolveCompatibilityParoleTimeTracker();
        }

        /// <summary>
        /// Resolve the manager-owned parole subsystem shell.
        /// Falls back to the compatibility wiring while the manager graph is still bootstrapping.
        /// </summary>
        public static ParoleManager ResolveParoleManager()
        {
            var managedParoleManager = Instance?._systemManager?.ParoleManager;
            if (managedParoleManager != null)
            {
                return managedParoleManager;
            }

            throw new InvalidOperationException("ParoleManager is not available.");
        }

        /// <summary>
        /// Resolve the active bail system through the manager-led runtime graph.
        /// Returns null while the runtime graph is still bootstrapping.
        /// </summary>
        public static BailSystem? ResolveBailSystem()
        {
            return Instance?._systemManager?.BailSystem;
        }

        /// <summary>
        /// Resolve the active dynamic parole-officer manager through an explicit compatibility shim
        /// until NPC orchestration is fully moved behind the manager graph.
        /// </summary>
        public static DynamicParoleOfficerManager? ResolveDynamicParoleOfficerManager()
        {
            return ResolveCompatibilityDynamicParoleOfficerManager();
        }

        /// <summary>
        /// Explicit compatibility shim for rap-sheet access before the manager graph is available.
        /// </summary>
        private static RapSheetManager ResolveCompatibilityRapSheetManager()
        {
            return RapSheetManager.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for UI access before the manager graph is available.
        /// </summary>
        private static JusticeUIManager ResolveCompatibilityUIManager()
        {
            return JusticeUIManager.CompatibilityInstance;
        }

        /// <summary>
        /// Explicit compatibility shim for parole-condition access before manager ownership is wired.
        /// </summary>
        private static ParoleConditionManager ResolveCompatibilityParoleConditionManager()
        {
            return ParoleConditionManager.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for parole-fee access before manager ownership is wired.
        /// </summary>
        private static ParoleFeeSystem ResolveCompatibilityParoleFeeSystem()
        {
            return ParoleFeeSystem.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for home-visit access before manager ownership is wired.
        /// </summary>
        private static HomeVisitSystem ResolveCompatibilityHomeVisitSystem()
        {
            return HomeVisitSystem.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for jail cell-assignment access before manager ownership is wired.
        /// </summary>
        private static CellAssignmentManager? ResolveCompatibilityCellAssignmentManager()
        {
            return CellAssignmentManager.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for prison NPC access before manager ownership is wired.
        /// </summary>
        private static PrisonNPCManager? ResolveCompatibilityPrisonNpcManager()
        {
            return PrisonNPCManager.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for release-manager access before manager ownership is fully wired.
        /// </summary>
        private static ReleaseManager? ResolveCompatibilityReleaseManager()
        {
            return ReleaseManager.TryGetRegisteredInstance(out var registered) ? registered : null;
        }

        /// <summary>
        /// Explicit compatibility shim for booking-process access before manager ownership is wired.
        /// </summary>
        private static BookingProcess? ResolveCompatibilityBookingProcess()
        {
            return JailController?.BookingProcessController ?? BookingProcess.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for persistent-player-data access before manager ownership is wired.
        /// </summary>
        private static PersistentPlayerData ResolveCompatibilityPersistentPlayerData()
        {
            return PersistentPlayerData.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for jail-time-tracker access before manager ownership is wired.
        /// </summary>
        private static JailTimeTracker ResolveCompatibilityJailTimeTracker()
        {
            return JailTimeTracker.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for parole-time-tracker access before manager ownership is wired.
        /// </summary>
        private static ParoleTimeTracker ResolveCompatibilityParoleTimeTracker()
        {
            return ParoleTimeTracker.Instance;
        }

        /// <summary>
        /// Explicit compatibility shim for dynamic parole-officer-manager access before manager ownership is wired.
        /// </summary>
        private static DynamicParoleOfficerManager? ResolveCompatibilityDynamicParoleOfficerManager()
        {
            return DynamicParoleOfficerManager.Instance;
        }

        /// <summary>
        /// Resolve a player's rap sheet through the manager-owned crime domain.
        /// </summary>
        public static RapSheet? GetRapSheet(Player? player)
        {
            return Instance?._systemManager?.CrimeManager?.GetRapSheet(player) ?? ResolveRapSheetManager().GetRapSheet(player);
        }

        /// <summary>
        /// Mark a player's rap sheet as changed through the manager-owned crime domain.
        /// </summary>
        public static void MarkRapSheetChanged(Player? player)
        {
            if (player == null)
            {
                return;
            }

            if (Instance?._systemManager?.CrimeManager != null)
            {
                Instance._systemManager.CrimeManager.MarkRapSheetChanged(player);
                return;
            }

            ResolveRapSheetManager().MarkRapSheetChanged(player);
        }

        /// <summary>
        /// Enumerate all known rap sheets through the manager-owned crime domain.
        /// </summary>
        public static IEnumerable<RapSheet> GetAllRapSheets()
        {
            return Instance?._systemManager?.CrimeManager?.GetAllRapSheets() ?? ResolveRapSheetManager().GetAllRapSheets();
        }

        /// <summary>
        /// Clear cached rap-sheet instances through the manager-owned crime domain.
        /// </summary>
        public static void ClearRapSheetCache()
        {
            if (Instance?._systemManager?.CrimeManager != null)
            {
                Instance._systemManager.CrimeManager.ClearRapSheetCache();
                return;
            }

            ResolveRapSheetManager().ClearCache();
        }

        // MelonPreferences
        private static MelonPreferences_Category? _prefsCategory;
        private static MelonPreferences_Entry<KeyCode>? _bailoutKeyPreference;

        /// <summary>Gets the configured bailout key, falling back to <see cref="KeyCode.B"/> before preferences load.</summary>
        public static KeyCode BailoutKey => _bailoutKeyPreference?.Value ?? KeyCode.B;
        
        // Update checking preferences
        private static MelonPreferences_Entry<long>? _lastUpdateCheckEntry;
        private static MelonPreferences_Entry<string>? _cachedLatestVersionEntry;
        private static MelonPreferences_Entry<bool>? _enableUpdateCheckingEntry;
        
        // Debug logging preference
        private static MelonPreferences_Entry<bool>? _enableDebugLoggingEntry;

        /// <summary>Gets whether verbose diagnostic logging was explicitly enabled in preferences.</summary>
        public static bool EnableDebugLogging => _enableDebugLoggingEntry?.Value ?? false;

        // Explicitly opt-in developer controls. These manipulate prison doors,
        // lighting, and booking state, so they must never be active in normal
        // play merely because the debug assembly is installed.
        private static MelonPreferences_Entry<bool>? _enableDeveloperShortcutsEntry;

        /// <summary>Gets whether developer shortcuts that mutate jail state are explicitly enabled.</summary>
        public static bool EnableDeveloperShortcuts => _enableDeveloperShortcutsEntry?.Value ?? false;

        // Retains native-event correlation metadata long enough for an arrest that occurs
        // well after the crime, while ensuring stale incidents cannot bleed into a later run.
        private static MelonPreferences_Entry<float>? _crimeIncidentRetentionSecondsEntry;

        /// <summary>Gets the real-time retention window used to correlate native crime incidents.</summary>
        public static float CrimeIncidentRetentionSeconds => _crimeIncidentRetentionSecondsEntry?.Value ?? 900f;

#if !MONO
        /// <summary>
        /// Registers all IL2CPP types with ClassInjector before any scene code can spawn them.
        /// Registration failures are accumulated and then abort initialization so a canonical NPC
        /// flow can never silently fall back to a partial/static implementation.
        /// </summary>
        private static void RegisterIl2CppTypes()
        {
            var registrationFailures = new List<string>();

            void TryRegister<T>(string name) where T : class
            {
                try
                {
                    ModLogger.Debug($"[IL2CPP] Registering {name}");
                    ClassInjector.RegisterTypeInIl2Cpp<T>();

                    try
                    {
                        var resolvedType = Il2CppInterop.Runtime.Il2CppType.Of<T>();
                        if (resolvedType == null)
                        {
                            const string message = "type pointer resolution returned null";
                            registrationFailures.Add($"{name}: {message}");
                            ModLogger.Error($"[IL2CPP] Registered {name} but {message}");
                        }
                        else
                        {
                            ModLogger.Debug($"[IL2CPP] Registered {name} (type pointer resolved)");
                        }
                    }
                    catch (Exception resolveEx)
                    {
                        registrationFailures.Add($"{name}: failed to resolve type pointer ({resolveEx.Message})");
                        ModLogger.Error($"[IL2CPP] Failed to resolve {name} after registration: {resolveEx}");
                    }
                }
                catch (Exception ex)
                {
                    registrationFailures.Add($"{name}: {ex.Message}");
                    ModLogger.Error($"[IL2CPP] Failed to register {name}: {ex}");
                }
            }

            // Jail System Components
            TryRegister<JailController>("JailController");
            TryRegister<SecurityCamera>("SecurityCamera");
            TryRegister<MonitorController>("MonitorController");
            TryRegister<JailMonitorController>("JailMonitorController");
            TryRegister<SecurityCameraCullingManager>("SecurityCameraCullingManager");
            TryRegister<JailLightingController>("JailLightingController");
            TryRegister<JailCellManager>("JailCellManager");
            TryRegister<JailAreaManager>("JailAreaManager");
            TryRegister<JailDoorController>("JailDoorController");
            TryRegister<JailPatrolManager>("JailPatrolManager");
            TryRegister<GuardAssaultLockdownManager>("GuardAssaultLockdownManager");
            TryRegister<JailLifecycleManager>("JailLifecycleManager");

            // Prison NPC System Components
            TryRegister<NPCUpdateManager>("NPCUpdateManager");
            TryRegister<PrisonNPCManager>("PrisonNPCManager");
            TryRegister<DynamicParoleOfficerManager>("DynamicParoleOfficerManager");
            TryRegister<PlayerLocationTracker>("PlayerLocationTracker");
            TryRegister<InmateBehavior>("InmateBehavior");
            TryRegister<PrisonGuard>("PrisonGuard");
            TryRegister<PrisonInmate>("PrisonInmate");

            // NPC support components (register before behavior classes)
            TryRegister<SecurityDoorBehavior>("SecurityDoorBehavior");
            TryRegister<JailNPCDialogueController>("JailNPCDialogueController");
            TryRegister<JailNPCAudioController>("JailNPCAudioController");
            TryRegister<StationaryBehavior>("StationaryBehavior");
            TryRegister<ParoleCheckInSystem>("ParoleCheckInSystem");

            // BaseJailNPC-derived behavior classes
            TryRegister<BaseJailNPC>("BaseJailNPC");
            TryRegister<IntakeOfficerStateMachine>("IntakeOfficerStateMachine");
            TryRegister<GuardBehavior>("GuardBehavior");
            TryRegister<ParoleIntakeStateMachine>("ParoleIntakeStateMachine");
            TryRegister<ParoleOfficerBehavior>("ParoleOfficerBehavior");
            TryRegister<OfficerCoordinator>("OfficerCoordinator");
            TryRegister<DoorTriggerHandler>("DoorTriggerHandler");

            // Test Components
            TryRegister<TestNPCController>("TestNPCController");
            TryRegister<MoveableTargetController>("MoveableTargetController");

            // UI Components
            TryRegister<BehindBarsUIWrapper>("BehindBarsUIWrapper");
            TryRegister<WantedLevelUI>("WantedLevelUI");
            TryRegister<OfficerCommandUI>("OfficerCommandUI");
            TryRegister<TierStatusUI>("TierStatusUI");
            TryRegister<UpdateNotificationUI>("UpdateNotificationUI");
            TryRegister<UI.ParoleStatusUI>("ParoleStatusUI");
            TryRegister<UI.ParoleConditionsUI>("ParoleConditionsUI");
            TryRegister<UI.BailUI>("BailUI");

            // Booking System Components
            TryRegister<BookingProcess>("BookingProcess");
            TryRegister<MugshotStation>("MugshotStation");
            TryRegister<ScannerStation>("ScannerStation");
            TryRegister<InventoryDropOff>("InventoryDropOff");          // extends InteractableObject (game class)
            TryRegister<InventoryDropOffStation>("InventoryDropOffStation");
            TryRegister<JailBed>("JailBed");
            TryRegister<PrisonBedInteractable>("PrisonBedInteractable");
            TryRegister<PrisonItemEquippable>("PrisonItemEquippable");  // extends Equippable_Viewmodel (game class)

            // Cell Management Components
            TryRegister<CellAssignmentManager>("CellAssignmentManager");

            // Jail Inventory System
            TryRegister<JailInventoryPickupStation>("JailInventoryPickupStation");
            TryRegister<InventoryPickupStation>("InventoryPickupStation");
            TryRegister<ExitScannerStation>("ExitScannerStation");
            TryRegister<ExitReleaseTriggerRelay>("ExitReleaseTriggerRelay");
            TryRegister<SimpleExitDoor>("SimpleExitDoor");
            // StorageEntity derives from FishNet.NetworkBehaviour. The current IL2CPP bridge cannot
            // inject a managed subclass because its inherited RPC surface includes NetworkConnection.
            // InventoryPickupStation uses its managed direct-transfer path on IL2CPP instead.
            ModLogger.Warn("[IL2CPP] PrisonStorageEntity is unavailable on this runtime; release inventory uses direct transfer");

            // Release System
            TryRegister<ReleaseManager>("ReleaseManager");
            TryRegister<ReleaseOfficerBehavior>("ReleaseOfficerBehavior");
            TryRegister<ParoleOfficer>("ParoleOfficer");

            // Testing
            TryRegister<Systems.Testing.SaveableTestSystem>("SaveableTestSystem");

            if (registrationFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[IL2CPP] Behind Bars startup blocked because type registration failed: {string.Join(" | ", registrationFailures)}");
            }
        }
#endif

        /// <summary>
        /// Initializes preferences, runtime registrations, Harmony, and the persistent manager
        /// graph in dependency order. IL2CPP type registration completes before any injected
        /// component or canonical NPC can be created.
        /// </summary>
        public override void OnInitializeMelon()
        {
            Instance = this;
            Utils.MelonLoaderVersionChecker.CheckMelonLoaderVersion();
#if !MONO
            RegisterIl2CppTypes();
#endif
            // Initialize MelonPreferences
            _prefsCategory = MelonPreferences.CreateCategory(Constants.PREF_CATEGORY);
            _bailoutKeyPreference = _prefsCategory.CreateEntry<KeyCode>(
                "BailoutKey",
                KeyCode.B,
                "Key to press for bailout payment",
                "The key binding used to pay bail and get released early from jail"
            );
            ModLogger.Debug($"Bailout key preference initialized: {BailoutKey}");
            
            // Initialize update checking preferences
            _lastUpdateCheckEntry = _prefsCategory.CreateEntry<long>(
                "LastUpdateCheck",
                0,
                "Last update check timestamp",
                "Unix timestamp of last update check"
            );
            _cachedLatestVersionEntry = _prefsCategory.CreateEntry<string>(
                "CachedLatestVersion",
                "",
                "Cached latest version",
                "Cached version from last check"
            );
            _enableUpdateCheckingEntry = _prefsCategory.CreateEntry<bool>(
                "EnableUpdateChecking",
                Constants.ENABLE_UPDATE_CHECKING,
                "Enable update checking",
                "Check for mod updates on menu load"
            );
            
            // Initialize debug logging preference (default: false)
            _enableDebugLoggingEntry = _prefsCategory.CreateEntry<bool>(
                "EnableDebugLogging",
                false,
                "Enable debug logging",
                "Show detailed debug logs. Enable this if you're experiencing issues and need to report bugs. Warning: This will produce a lot of log output."
            );

            _enableDeveloperShortcutsEntry = _prefsCategory.CreateEntry<bool>(
                "EnableDeveloperShortcuts",
                false,
                "Enable developer jail shortcuts",
                "Enables destructive Alt-key jail and door test shortcuts. Keep disabled during normal gameplay."
            );

            _crimeIncidentRetentionSecondsEntry = _prefsCategory.CreateEntry<float>(
                "CrimeIncidentRetentionSeconds",
                900f,
                "Crime incident retention (real seconds)",
                "How long Behind Bars retains native crime-to-enhancement correlations before arrest. Increase this if police take longer to arrest the player."
            );
            
            // Initialize UpdateChecker with preferences
            Utils.UpdateChecker.InitializePreferences(
                _lastUpdateCheckEntry,
                _cachedLatestVersionEntry,
                _enableUpdateCheckingEntry
            );
            ModLogger.Debug("Update checking preferences initialized");
            ModLogger.Info($"Debug logging: {(EnableDebugLogging ? "ENABLED" : "DISABLED")} (default: disabled)");
            ModLogger.Info($"Developer jail shortcuts: {(EnableDeveloperShortcuts ? "ENABLED" : "DISABLED")} (default: disabled)");
            ModLogger.Info($"Crime incident retention: {CrimeIncidentRetentionSeconds:F0} real seconds");

            // Initialize core systems
            HarmonyPatches.Initialize(this);
            
            // Initialize NavMesh optimization patches (manual patch for CanGetTo method)
            InitializeNavMeshOptimizationPatches();
            
            // Initialize GameTimeManager first (needed by other systems)
            GameTimeManager.Instance.Initialize();
            ModLogger.Debug("GameTimeManager initialized");

            _systemManager = new BehindBarsSystemManager();
            _systemManager.Initialize();

            // Note: BehindBarsUIManager (including WantedLevelUI) initialization moved to OnSceneWasLoaded to avoid initializing in menu

            if (EnableDeveloperShortcuts)
            {
                // Initialize SaveableTestSystem for testing (Alt + letter keybinds).
                // The singleton uses the IL2CPP-safe component helper for injected types.
                var saveableTestSystem = Systems.Testing.SaveableTestSystem.Instance;
                if (saveableTestSystem != null)
                {
                    saveableTestSystem.enabled = true;
                    ModLogger.Debug("SaveableTestSystem initialized - Use Alt+S/L/R/P/D/C for testing");
                }
            }

            // Initialize preset parole officer routes
            PresetParoleOfficerRoutes.InitializePatrolPoints();
            ModLogger.Debug("Preset parole officer routes initialized");

            //AssetManager = new AssetManager();
            //AssetManager.Init();

            // Add scene change detection for cleanup
#if !MONO
            SceneManager.activeSceneChanged += new System.Action<Scene, Scene>(OnSceneChanged);
#else
            SceneManager.activeSceneChanged += OnSceneChanged;
#endif

            ModLogger.Debug("Behind Bars initialized with all systems");
        }

        /// <summary>
        /// Begins a new Main-scene gameplay session and holds the native loading surface while
        /// scene-owned systems bootstrap. Non-gameplay scenes do not start the jail load coroutine.
        /// </summary>
        /// <param name="buildIndex">Unity build index reported for the initialized scene.</param>
        /// <param name="sceneName">Unity scene name used to select gameplay initialization.</param>
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            ModLogger.Debug($"Scene initialized: {sceneName} (Build Index: {buildIndex})");

            // Spawn furniture when the scene is initialized
            try
            {
                if (sceneName == "Main")
                {
                    BeginGameplaySceneSession();
                    // Retain the game's loading surface until the scene-owned Behind Bars
                    // systems have completed their real startup work. Do not create a second
                    // overlay after the player has already loaded into the world.
                    HarmonyPatches.BeginNativeLoadingScreenHold(_gameplaySessionVersion);
                    _loadModCoroutine = MelonCoroutines.Start(LoadModWithProgress()) as Coroutine;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning furniture on scene initialization: {e.Message}");
            }
        }

        /// <summary>
        /// Master loading coroutine that coordinates the scene-owned startup work while the
        /// game's native loading screen displays the final Behind Bars preparation step.
        /// </summary>
        private IEnumerator LoadModWithProgress()
        {
            var uiManager = ResolveUIManager();
            HarmonyPatches.SetNativeLoadingScreenStatus(_gameplaySessionVersion, "Preparing Behind Bars...");

            // Wait for essential systems to be ready
#if !MONO
            while (true)
            {
                if (!IsGameplaySceneActive)
                {
                    yield break;
                }

                try
                {
                    var instance = PlayerSingleton<AppsCanvas>.Instance;
                    if (instance != null && instance.Pointer != System.IntPtr.Zero)
                        break;
                }
                catch
                {
                    // Instance is null or not ready
                }
                yield return null;
            }
#else
            while (PlayerSingleton<AppsCanvas>.Instance == null)
            {
                if (!IsGameplaySceneActive)
                {
                    yield break;
                }

                yield return null;
            }
#endif

            // Load asset bundle BEFORE initializing UI manager (UI prefab is in the bundle)
            HarmonyPatches.SetNativeLoadingScreenStatus(_gameplaySessionVersion, "Loading Behind Bars assets...");
            // Load the behind-bars bundle and cache it
            if (CachedJailBundle == null)
            {
                ModLogger.Debug("Loading jail asset bundle for UI prefab...");
                CachedJailBundle = Utils.AssetBundleUtils.LoadAssetBundle("Behind_Bars.behind_bars");
                if (CachedJailBundle == null)
                {
                    ModLogger.Error("Failed to load behind-bars bundle - UI prefab will not be available");
                }
                else
                {
                    ModLogger.Debug("✓ Jail asset bundle loaded successfully");
                }
            }

            try
            {
                uiManager.InitializeSceneUI();
                ModLogger.Debug("✓ Behind Bars UI system initialized successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing UI system: {e.Message}");
            }

            if (!IsGameplaySceneActive)
            {
                yield break;
            }

            // Jail setup owns the canonical prison asset and NPC bootstrap sequence.
            HarmonyPatches.SetNativeLoadingScreenStatus(_gameplaySessionVersion, "Setting up the jail...");
            var setupJailCoroutine = SetupJail();
            while (setupJailCoroutine.MoveNext())
            {
                if (!IsGameplaySceneActive)
                {
                    yield break;
                }

                yield return setupJailCoroutine.Current;
            }

            // Do not use fixed, simulated progress to close the native loading screen.
            // Keep the native loading screen through the complete canonical NPC pass,
            // including guards, parole setup, and inmates.
            HarmonyPatches.SetNativeLoadingScreenStatus(_gameplaySessionVersion, "Initializing jail NPCs...");
            const float npcManagerTimeoutSeconds = 15f;
            float npcManagerWaitElapsed = 0f;
            PrisonNPCManager? npcManager = ResolvePrisonNpcManager();
            while (npcManager == null && npcManagerWaitElapsed < npcManagerTimeoutSeconds)
            {
                if (!IsGameplaySceneActive)
                {
                    yield break;
                }

                yield return null;
                npcManagerWaitElapsed += Time.unscaledDeltaTime;
                npcManager = ResolvePrisonNpcManager();
            }

            if (npcManager == null)
            {
                ModLogger.Warn("PrisonNPCManager was unavailable after jail setup; continuing after native loading fail-safe");
            }
            else
            {
                HarmonyPatches.SetNativeLoadingScreenStatus(_gameplaySessionVersion, "Spawning guards and inmates...");
                const float npcSpawnTimeoutSeconds = 75f;
                float npcSpawnWaitElapsed = 0f;
                while (!npcManager.IsSpawningComplete && npcSpawnWaitElapsed < npcSpawnTimeoutSeconds)
                {
                    if (!IsGameplaySceneActive)
                    {
                        yield break;
                    }

                    yield return null;
                    npcSpawnWaitElapsed += Time.unscaledDeltaTime;
                }

                if (npcManager.IsSpawningComplete)
                {
                    ModLogger.Debug("✓ Canonical jail NPC spawning completed");
                }
                else
                {
                    ModLogger.Warn($"NPC spawning did not complete within {npcSpawnTimeoutSeconds:F0}s; releasing native loading screen with startup diagnostics intact");
                }
            }

            HarmonyPatches.SetNativeLoadingScreenStatus(_gameplaySessionVersion, "Finalizing player systems...");
            // OnSceneWasLoaded also starts this routine. Only start it here if the
            // other lifecycle callback has not already established the player state.
            if (!_playerSystemsReady && _playerInitializationCoroutine == null)
            {
                _playerInitializationCoroutine = MelonCoroutines.Start(InitializePlayerSystems()) as Coroutine;
            }

            const float playerSystemsTimeoutSeconds = 30f;
            float playerSystemsWaitElapsed = 0f;
            while (!_playerSystemsReady && playerSystemsWaitElapsed < playerSystemsTimeoutSeconds)
            {
                if (!IsGameplaySceneActive)
                {
                    yield break;
                }

                yield return null;
                playerSystemsWaitElapsed += Time.unscaledDeltaTime;
            }

            if (!_playerSystemsReady)
            {
                ModLogger.Warn($"Player systems did not complete within {playerSystemsTimeoutSeconds:F0}s; releasing native loading screen with startup diagnostics intact");
            }

            HarmonyPatches.CompleteNativeLoadingScreenHold(_gameplaySessionVersion);
            ModLogger.Debug("✓ Behind Bars scene startup complete");
        }

        /// <summary>
        /// Handles the reliable post-load boundary for scene-owned UI and player bootstrap.
        /// Menu loads force cleanup because they may not emit the outgoing active-scene event.
        /// </summary>
        /// <param name="buildIndex">Unity build index reported for the loaded scene.</param>
        /// <param name="sceneName">Unity scene name used to select cleanup or bootstrap work.</param>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            ModLogger.Debug($"Scene loaded: {sceneName}");

            // The game can load Menu without emitting activeSceneChanged for the outgoing
            // gameplay scene. This callback is the reliable boundary for clearing all
            // Main-scene UI that has opted into DontDestroyOnLoad.
            if (sceneName == "Menu")
            {
                ShutdownGameplayScene("Menu scene loaded", forceUiCleanup: true);
            }
            
            // Check for updates when entering Menu scene (always check on first load, ignore cache)
            if (sceneName == "Menu" && _enableUpdateCheckingEntry?.Value == true)
            {
                ModLogger.Info("Menu scene loaded - checking for updates (first load check)");
                MelonCoroutines.Start(Utils.UpdateChecker.CheckForUpdatesAsync(forceCheck: false));
            }
            
            if (sceneName == "Main")
            {
                ModLogger.Debug("Main scene loaded, initializing player systems");
                if (!_gameplaySceneActive)
                {
                    BeginGameplaySceneSession();
                }
                // Build and register the inactive native jail-NPC template on every local
                // process before managers request a network spawn. This is deliberately
                // independent from S1API and never uses a live NPC as a donor.
                MelonCoroutines.Start(BaseNPCSpawner.PrewarmNativeNpcTemplate());
                _playerInitializationCoroutine = MelonCoroutines.Start(InitializePlayerSystems()) as Coroutine;
            }
            else if (sceneName != "Menu" && sceneName != "Loading")
            {
                // Initialize the manager-owned UI forwarding layer for gameplay scenes.
                ModLogger.Debug($"Initializing JusticeUIManager for scene: {sceneName}");
                ResolveUIManager().InitializeSceneUI();
            }
        }

        /// <summary>
        /// Waits for the runtime HUD/player prerequisites, then creates the local PlayerHandler
        /// and restores persisted parole state. The Mono/IL2CPP readiness checks are deliberately
        /// separate because IL2CPP wrappers require a valid native pointer before use.
        /// </summary>
        private IEnumerator InitializePlayerSystems()
        {
            ModLogger.Debug("Waiting for player to be ready...");
#if !MONO
            // IL2CPP - More robust null checking
            while (true)
            {
                if (!IsGameplaySceneActive)
                {
                    yield break;
                }
                try
                {
                    var instance = PlayerSingleton<AppsCanvas>.Instance;
                    if (instance != null && instance.Pointer != System.IntPtr.Zero)
                        break;
                }
                catch
                {
                    // Instance is null or not ready
                }
                yield return null;
            }
#else
            // Mono - Standard Unity null check
            while (PlayerSingleton<AppsCanvas>.Instance == null)
            {
                if (!IsGameplaySceneActive)
                {
                    yield break;
                }
                yield return null;
            }
#endif

            // Initialize player handler for local player
            if (Player.Local != null)
            {
                var playerHandler = new PlayerHandler(Player.Local);
                _playerHandlers[Player.Local] = playerHandler;
                // Arrest handling is centralized in HarmonyPatches; no direct listener needed here

                ModLogger.Debug("Player systems initialized successfully");
                _playerSystemsReady = true;
                
                // Restore parole tracking if player is on parole
                MelonCoroutines.Start(RestoreParoleIfActive(Player.Local));
            }
            else
            {
                ModLogger.Warn("Player.Local is null, retrying in 2 seconds...");
                yield return new WaitForSeconds(2f);
                if (IsGameplaySceneActive)
                {
                    _playerInitializationCoroutine = MelonCoroutines.Start(InitializePlayerSystems()) as Coroutine;
                }
            }
        }

        /// <summary>
        /// Restores tracking and the runtime parole record if the player is actively on parole
        /// when the scene loads. The persisted rap-sheet state is authoritative; the private
        /// runtime dictionary/monitor are repopulated through reflection because no public restore
        /// API is available.
        /// </summary>
        /// <param name="player">Local player whose persisted parole state is being restored.</param>
        private IEnumerator RestoreParoleIfActive(Player player)
        {
            // Wait a moment for systems to be ready
            yield return new WaitForSeconds(1f);
            
            try
            {
                var paroleSystem = ParoleSystem;
                if (paroleSystem == null)
                {
                    ModLogger.Warn("ParoleSystem is null, cannot restore parole");
                    yield break;
                }
                
                // Get the player's rap sheet
                var rapSheet = ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet == null)
                {
                    ModLogger.Debug($"No rap sheet found for {player.name}, skipping parole restoration");
                    yield break;
                }
                
                // Check if player is on parole
                var paroleRecord = rapSheet.CurrentParoleRecord;
                if (paroleRecord == null || !paroleRecord.IsOnParole())
                {
                    ModLogger.Debug($"Player {player.name} is not on parole, skipping restoration");
                    yield break;
                }
                
                // Get remaining parole time
                var (isParole, remainingTime) = paroleRecord.GetParoleStatus();
                if (!isParole || remainingTime <= 0)
                {
                    ModLogger.Info($"Player {player.name} has expired parole, completing it");
                    // Parole expired while away - complete it
                    if (paroleSystem != null)
                    {
                        paroleSystem.CompleteParoleForPlayer(player);
                    }
                    yield break;
                }
                
                // Check if tracking is already active
                var paroleTimeTracker = ResolveParoleTimeTracker();
                if (paroleTimeTracker.IsTracking(player))
                {
                    ModLogger.Debug($"Parole tracking already active for {player.name}");
                    yield break;
                }
                
                ModLogger.Debug($"Restoring parole tracking for {player.name}: {remainingTime} game minutes remaining ({GameTimeManager.FormatGameTime(remainingTime)})");
                
                // Restore parole tracking
                paroleTimeTracker.StartTracking(player, remainingTime, (p) =>
                {
                    ModLogger.Debug($"Restored parole completed for {p.name}");
                    var currentParoleSystem = ParoleSystem;
                    if (currentParoleSystem != null)
                    {
                        currentParoleSystem.CompleteParoleForPlayer(p);
                    }
                });
                
                // Restore parole runtime record in ParoleSystem
                float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
                float termLength = paroleRecord.GetParoleTermLength(); // Get term length in game minutes
                var runtimeRecord = new ParoleSystem.ParoleRuntimeRecord
                {
                    Player = player,
                    Status = ParoleSystem.ParoleStatus.Active,
                    StartGameTimeMinutes = currentGameTime - (termLength - remainingTime),
                    DurationGameMinutes = termLength,
                    TimeRemainingGameMinutes = remainingTime,
                    ViolationCount = paroleRecord.GetViolationCount()
                };
                
                // Restore the runtime record through the private field because ParoleSystem does
                // not expose a public bootstrap API. If that shape is unavailable, persisted
                // parole remains intact but runtime monitoring cannot be restarted here.
                var paroleSystemType = typeof(ParoleSystem);
                var paroleRecordsField = paroleSystemType.GetField("_paroleRecords", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (paroleRecordsField != null)
                {
                    var paroleRecords = paroleRecordsField.GetValue(paroleSystem) as System.Collections.Generic.Dictionary<Player, ParoleSystem.ParoleRuntimeRecord>;
                    if (paroleRecords != null)
                    {
                        paroleRecords[player] = runtimeRecord;
                        ModLogger.Debug($"Restored parole runtime record for {player.name}");
                        
                        // Restart the private monitor only after the runtime record is installed;
                        // otherwise the monitor would observe an incomplete or duplicate record.
                        var monitorMethod = paroleSystemType.GetMethod("MonitorParole", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (monitorMethod != null)
                        {
                            var coroutine = monitorMethod.Invoke(paroleSystem, new object[] { runtimeRecord });
                            if (coroutine != null)
                            {
                                MelonCoroutines.Start(coroutine as IEnumerator);
                                ModLogger.Debug($"Restarted parole monitoring for {player.name}");
                            }
                        }
                    }
                }
                
                // Show parole UI
                MelonCoroutines.Start(DelayedShowParoleUI(player));
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error restoring parole for {player.name}: {ex.Message}");
                ModLogger.Error($"Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Show parole UI after a delay to ensure systems are ready
        /// </summary>
        private IEnumerator DelayedShowParoleUI(Player player)
        {
            yield return new WaitForSeconds(2f);
            
            var paroleSystem = ParoleSystem;
            var uiManager = ResolveUIManager();
            if (paroleSystem != null)
            {
                var rapSheet = ResolveRapSheetManager().GetRapSheet(player);
                if (rapSheet != null && rapSheet.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    var (isParole, remainingTime) = rapSheet.CurrentParoleRecord.GetParoleStatus();
                    if (isParole && remainingTime > 0)
                    {
                        uiManager.ShowParoleStatus();
                        ModLogger.Debug($"Showed parole UI for {player.name} after scene load");
                    }
                }
            }
        }

        private static IEnumerator InitializeUISystem()
        {
            ModLogger.Debug("Initializing Behind Bars UI system...");
            
            // Wait for essential systems to be ready
#if !MONO
            while (true)
            {
                try
                {
                    var instance = PlayerSingleton<AppsCanvas>.Instance;
                    if (instance != null && instance.Pointer != System.IntPtr.Zero)
                        break;
                }
                catch
                {
                    // Instance is null or not ready
                }
                yield return null;
            }
#else
            while (PlayerSingleton<AppsCanvas>.Instance == null)
                yield return null;
#endif
            
            // Wait for asset manager to be ready
            yield return new WaitForSeconds(1f);
            
            // Initialize the UI manager
            try
            {
                ResolveUIManager().InitializeSceneUI();
                ModLogger.Debug("✓ Behind Bars UI system initialized successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing UI system: {e.Message}");
                yield break;
            }
            
            // Test the UI system (optional - can be removed in production)
            if (EnableDebugLogging)
            {
                yield return new WaitForSeconds(2f);
                TestUISystem();
            }
        }
        
        private static void TestUISystem()
        {
            ModLogger.Debug("Testing Behind Bars UI system...");
            
            try
            {
                var uiManager = ResolveUIManager();
                // Show test jail info UI
                uiManager.ShowJailInfoUI(
                    crime: "Major Possession, Assaulting Officer, Resisting Arrest", 
                    timeInfo: "2 days", 
                    bailInfo: "$500"
                );
                
                ModLogger.Debug("✓ Test UI displayed successfully - check your screen!");
                
                // Auto-hide after 10 seconds for testing
                MelonCoroutines.Start(AutoHideTestUI());
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error testing UI system: {e.Message}");
            }
        }
        
        private static IEnumerator AutoHideTestUI()
        {
            yield return new WaitForSeconds(10f);
            ResolveUIManager().HideJailInfoUI();
            ModLogger.Debug("Test UI auto-hidden after 10 seconds");
        }

        private static IEnumerator SetupJail()
        {
            ModLogger.Debug("Setting up jail from asset bundle...");

            // Load the behind-bars bundle specifically and cache it
            if (CachedJailBundle == null)
            {
                CachedJailBundle = Utils.AssetBundleUtils.LoadAssetBundle("Behind_Bars.behind_bars");
                
            }

            var jailBundle = CachedJailBundle;
            if (jailBundle == null)
            {
                ModLogger.Error("Failed to load behind-bars bundle");
                yield break;
            }

            // Safety: Retry loading UI prefab now that bundle is confirmed loaded
            ResolveUIManager().RetryLoadUIPrefab();

            // Debug: List all assets in the bundle
            var allAssets = jailBundle.GetAllAssetNames();
            ModLogger.Debug($"Assets in bundle ({allAssets.Length} total):");
            foreach (var asset in allAssets)
            {
                ModLogger.Debug($"  - {asset}");
            }

            // Also list all GameObjects
#if MONO
            var gameObjects = jailBundle.LoadAllAssets<GameObject>();
#else
            var gameObjects = jailBundle.LoadAllAssets(Il2CppInterop.Runtime.Il2CppType.Of<GameObject>());
#endif
            ModLogger.Debug($"GameObjects in bundle ({gameObjects.Length} total):");
            for (int i = 0; i < gameObjects.Length; i++)
            {
#if MONO
                var obj = gameObjects[i];
                ModLogger.Debug($"  - {obj?.name ?? "<null>"}");
#else
                var obj = gameObjects[i].TryCast<GameObject>();
                ModLogger.Debug($"  - {obj?.name ?? "<null>"}");
#endif
            }

            // Wait for player to be ready (using our IL2CPP-safe check)
#if !MONO
            while (true)
            {
                try
                {
                    var instance = PlayerSingleton<AppsCanvas>.Instance;
                    if (instance != null && instance.Pointer != System.IntPtr.Zero)
                        break;
                }
                catch
                {
                    // Instance is null or not ready
                }
                yield return null;
            }
#else
            while (PlayerSingleton<AppsCanvas>.Instance == null)
                yield return null;
#endif

            var jailPrefab = jailBundle.LoadAsset<GameObject>("Jail");
            if (jailPrefab == null)
            {
                ModLogger.Error("Jail_2 prefab not found in asset bundle!");
                yield break;
            }

            // Spawn the jail
            var jail = Object.Instantiate(jailPrefab, new Vector3(66.5362f, 8.5001f, -220.6056f), Quaternion.identity);
            jail.name = "[Prefab] JailHouseBlues";

            // The clean authoring project deliberately does not embed the
            // full Schedule I shader library. Rebind the material references
            // to the game's loaded URP shaders immediately after instantiation
            // so editor-local shader variants can never render the jail magenta.
            Utils.JailMaterialCompatibility.RepairForScheduleOne(jail);

            ModLogger.Debug($"Jail spawned successfully at {jail.transform.position}");

            // Attach NavMesh data from asset bundle (asset bundles don't preserve NavMesh data)
            yield return new WaitForSeconds(0.5f); // Let components settle first
            JailNavMeshSetup.AttachJailNavMesh(jail.transform);

            // Initialize JailController system
            yield return new WaitForSeconds(1f); // Give the NavMesh time to build
            InitializeJailController(jail);
        }

        private static void InitializeJailController(GameObject jail)
        {
            try
            {
                ModLogger.Debug("Initializing JailController system...");

                // Check if the jail already has a JailController
                var existingController = BBHelpers.GetComponentSafe<JailController>(jail);
                if (existingController != null)
                {
                    ModLogger.Debug("Found existing JailController on jail prefab");
                    JailController = existingController;
                }
                else
                {
                    ModLogger.Debug("Adding JailController component to jail");
                    JailController = BBHelpers.AddComponentSafe<JailController>(jail);
                }

                // Load and assign prefabs from bundle, then trigger door setup
                if (JailController != null)
                {
                    LoadAndAssignJailPrefabs(JailController);
                    ModLogger.Debug("✓ JailController prefabs loaded");

                    // Manually call SetupDoors after prefabs are loaded
                    if (JailController.jailDoorPrefab != null || JailController.steelDoorPrefab != null)
                    {
                        JailController.SetupDoors();
                        ModLogger.Debug("✓ Door setup completed after prefab loading");
                    }
                    else
                    {
                        ModLogger.Error("Skipping door setup - no door prefabs were resolved from bundle");
                    }

                    // Setup exit door specifically
                    SetupExitDoor(JailController);
                    ModLogger.Debug("✓ Exit door setup completed");

                    // Log status after a frame to let everything complete
                    MelonCoroutines.Start(LogStatusAfterFrame());

                    // Create jail NPCs after JailController is fully initialized
                    MelonCoroutines.Start(CreateJailNPCs());
                }
                else
                {
                    ModLogger.Error("Failed to get JailController component");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing JailController: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }
        
        private static IEnumerator LogStatusAfterFrame()
        {
            // Wait a frame to let Unity's Start() method complete
            yield return null;
            yield return new WaitForSeconds(0.5f); // Extra time for initialization
            
            ModLogger.Debug("Logging jail status after initialization...");
            LogJailControllerStatus();
        }

        private static IEnumerator CreateJailNPCs()
        {
            // Wait for everything to be fully initialized before creating NPCs
            yield return new WaitForSeconds(2f);

            ModLogger.Debug("Creating jail NPCs with custom appearances...");

            // Create PrisonNPCManager to handle all NPC spawning and management
            if (JailController != null)
            {
                var npcManager = BBHelpers.AddComponentSafe<PrisonNPCManager>(JailController.gameObject);
                ModLogger.Debug("✓ PrisonNPCManager added to JailController");
                
                // Add CellAssignmentManager for cell tracking
                var cellManager = BBHelpers.AddComponentSafe<CellAssignmentManager>(JailController.gameObject);
                ModLogger.Debug("✓ CellAssignmentManager added to JailController");

                var lifecycleManager = BBHelpers.AddComponentSafe<JailLifecycleManager>(JailController.gameObject);
                ModLogger.Debug("✓ JailLifecycleManager added to JailController");
            }
            else
            {
                ModLogger.Error("ActiveJailController is null - cannot add managers");
            }

            ModLogger.Debug("✓ Jail NPCs created successfully with custom appearances");
            
            // Door interaction system temporarily disabled to reduce log spam
            // NPCDoorInteraction.InitializeDoorDatabase();
            // ModLogger.Info("✓ Door interaction system initialized");
            
            // Validate NavMesh before finishing
            yield return new WaitForSeconds(1f);
            var jail = Core.JailController;
            if (jail != null)
            {
                if (JailNavMeshSetup.HasValidNavMesh(jail.transform))
                {
                    ModLogger.Debug("✓ NavMesh validation passed");
                }
                else
                {
                    ModLogger.Warn("NavMesh validation failed - NavMesh may not be properly attached");
                }
            }
            
            yield return new WaitForSeconds(1f);
            
            // Initialize booking system
            InitializeBookingSystem();
            
            ModLogger.Debug("✓ NPC initialization completed");
        }
        
        /// <summary>
        /// Initialize the booking process system
        /// </summary>
        private static void InitializeBookingSystem()
        {
            try
            {
                ModLogger.Debug("Initializing booking system...");
                
                if (JailController == null)
                {
                    ModLogger.Error("Cannot initialize booking system - no active jail controller");
                    return;
                }
                
                GameObject jailGameObject = JailController.gameObject;

                // Add BookingProcess component if it doesn't exist
                JailController.BookingProcessController = BBHelpers.GetComponentSafe<Behind_Bars.Systems.Jail.BookingProcess>(jailGameObject);
                if (JailController.BookingProcessController == null)
                {
                    JailController.BookingProcessController = BBHelpers.AddComponentSafe<Behind_Bars.Systems.Jail.BookingProcess>(jailGameObject);
                    ModLogger.Debug("✓ BookingProcess component added to jail");
                }
                else
                {
                    ModLogger.Debug("✓ BookingProcess component already exists");
                }

                // A single scene-local owner prevents duplicate guard-damage callbacks from
                // creating competing lockdowns during the same custody incident.
                if (BBHelpers.GetComponentSafe<GuardAssaultLockdownManager>(jailGameObject) == null)
                {
                    BBHelpers.AddComponentSafe<GuardAssaultLockdownManager>(jailGameObject);
                    ModLogger.Debug("✓ GuardAssaultLockdownManager added to jail");
                }
                
                // Find and set up booking stations
                SetupBookingStations(jailGameObject.transform);
                
                ModLogger.Debug("✓ Booking system initialized successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error initializing booking system: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set up booking stations (mugshot and scanner)
        /// </summary>
        private static void SetupBookingStations(Transform jailTransform)
        {
            try
            {
                // Find booking area
                Transform bookingArea = jailTransform.Find("Booking");
                if (bookingArea == null)
                {
                    ModLogger.Error("Booking area not found in jail hierarchy");
                    return;
                }
                
                // Set up Mugshot Station - SINGLE COMPONENT ONLY (like ScannerStation)
                Transform mugshotStation = bookingArea.Find("MugshotStation");
                if (mugshotStation != null)
                {
                    var mugshotComponent = BBHelpers.GetComponentSafe<Behind_Bars.Systems.Jail.MugshotStation>(mugshotStation.gameObject);
                    if (mugshotComponent == null)
                    {
                        mugshotComponent = BBHelpers.AddComponentSafe<Behind_Bars.Systems.Jail.MugshotStation>(mugshotStation.gameObject);
                        ModLogger.Debug("✓ MugshotStation component added to main GameObject");
                    }
                    
                    // DO NOT add manual collider - let InteractableObject handle collision detection
                    ModLogger.Debug("MugshotStation setup complete - single component approach");
                }
                else
                {
                    ModLogger.Warn("MugshotStation not found in booking area");
                }
                
                // Set up Scanner Station - SINGLE COMPONENT ONLY
                Transform scannerStation = bookingArea.Find("ScannerStation");
                if (scannerStation != null)
                {
                    var scannerComponent = BBHelpers.GetComponentSafe<Behind_Bars.Systems.Jail.ScannerStation>(scannerStation.gameObject);
                    if (scannerComponent == null)
                    {
                        scannerComponent = BBHelpers.AddComponentSafe<Behind_Bars.Systems.Jail.ScannerStation>(scannerStation.gameObject);
                        ModLogger.Debug("✓ ScannerStation component added to main GameObject");
                    }
                    
                    // DO NOT add ScannerStation to Interaction child - this causes duplicates!
                    ModLogger.Debug("ScannerStation setup complete - single component approach");
                }
                else
                {
                    ModLogger.Warn("ScannerStation not found in booking area");
                }

                // Set up Exit Scanner Station - SINGLE COMPONENT ONLY
                ModLogger.Debug("Searching for ExitScannerStation...");
                Transform hallway = jailTransform.Find("Hallway");
                Transform exitScannerStation = null;

                if (hallway != null)
                {
                    ModLogger.Debug($"Found Hallway at {hallway.name}");
                    exitScannerStation = hallway.Find("ExitScannerStation");
                    if (exitScannerStation != null)
                    {
                        ModLogger.Debug($"Found ExitScannerStation in Hallway: {exitScannerStation.name}");
                    }
                    else
                    {
                        ModLogger.Warn("ExitScannerStation not found in Hallway");
                    }
                }
                else
                {
                    ModLogger.Warn("Hallway not found in jail");
                }

                if (exitScannerStation == null)
                {
                    exitScannerStation = jailTransform.Find("ExitScannerStation");
                    if (exitScannerStation != null)
                    {
                        ModLogger.Debug($"Found ExitScannerStation directly in jail: {exitScannerStation.name}");
                    }
                }

                if (exitScannerStation != null)
                {
                    var exitScannerComponent = BBHelpers.GetComponentSafe<Behind_Bars.Systems.Jail.ExitScannerStation>(exitScannerStation.gameObject);
                    if (exitScannerComponent == null)
                    {
                        exitScannerComponent = BBHelpers.AddComponentSafe<Behind_Bars.Systems.Jail.ExitScannerStation>(exitScannerStation.gameObject);
                        ModLogger.Debug("✓ ExitScannerStation component added to GameObject at " + exitScannerStation.name);
                    }
                    else
                    {
                        ModLogger.Debug("ExitScannerStation component already exists");
                    }

                    ModLogger.Debug("ExitScannerStation setup complete - found at " + exitScannerStation.name);
                }
                else
                {
                    ModLogger.Warn("ExitScannerStation not found in jail area or Hallway - searching all children");

                    // Debug: List all children of jailTransform
                    for (int i = 0; i < jailTransform.childCount; i++)
                    {
                        var child = jailTransform.GetChild(i);
                        ModLogger.Debug($"Jail child {i}: {child.name}");

                        if (child.name == "Hallway")
                        {
                            ModLogger.Debug($"Found Hallway, checking its children:");
                            for (int j = 0; j < child.childCount; j++)
                            {
                                var grandchild = child.GetChild(j);
                                ModLogger.Debug($"  Hallway child {j}: {grandchild.name}");
                            }
                        }
                    }
                }

                // Set up Inventory Drop-off Station
                // Based on Unity hierarchy, look for Storage/InventoryDropOff
                Transform storageArea = jailTransform.Find("Storage");
                Transform inventoryDropOff = null;
                
                if (storageArea != null)
                {
                    inventoryDropOff = storageArea.Find("InventoryDropOff");
                }
                
                if (inventoryDropOff != null)
                {
                    var inventoryComponent = BBHelpers.GetComponentSafe<Behind_Bars.Systems.Jail.InventoryDropOffStation>(inventoryDropOff.gameObject);
                    if (inventoryComponent == null)
                    {
                        inventoryComponent = BBHelpers.AddComponentSafe<Behind_Bars.Systems.Jail.InventoryDropOffStation>(inventoryDropOff.gameObject);
                        ModLogger.Debug("✓ InventoryDropOffStation component added to InventoryDropOff GameObject");
                    }
                    
                    ModLogger.Debug("InventoryDropOffStation setup complete");
                }
                else
                {
                    ModLogger.Warn("Storage/InventoryDropOff not found in jail hierarchy");
                }
                
                // Set up Jail Inventory Pickup Station (for prison items)
                Transform jailInventoryPickup = null;
                if (storageArea != null)
                {
                    jailInventoryPickup = storageArea.Find("JailInventoryPickup");
                }

                if (jailInventoryPickup != null)
                {
                    var jailPickupComponent = BBHelpers.GetComponentSafe<Behind_Bars.Systems.Jail.JailInventoryPickupStation>(jailInventoryPickup.gameObject);
                    if (jailPickupComponent == null)
                    {
                        jailPickupComponent = BBHelpers.AddComponentSafe<Behind_Bars.Systems.Jail.JailInventoryPickupStation>(jailInventoryPickup.gameObject);
                        ModLogger.Debug("✓ JailInventoryPickupStation component added to JailInventoryPickup GameObject");
                    }

                    ModLogger.Debug("JailInventoryPickupStation setup complete");
                }
                else
                {
                    ModLogger.Warn("Storage/JailInventoryPickup not found in jail hierarchy");
                }

                // Set up Inventory Pickup Station (for personal belongings return)
                Transform inventoryPickup = null;
                if (storageArea != null)
                {
                    inventoryPickup = storageArea.Find("InventoryPickup");
                }

                if (inventoryPickup != null)
                {
                    var pickupComponent = BBHelpers.GetComponentSafe<Behind_Bars.Systems.Jail.InventoryPickupStation>(inventoryPickup.gameObject);
                    if (pickupComponent == null)
                    {
                        pickupComponent = BBHelpers.AddComponentSafe<Behind_Bars.Systems.Jail.InventoryPickupStation>(inventoryPickup.gameObject);
                        ModLogger.Debug("✓ InventoryPickupStation component added to InventoryPickup GameObject");
                    }

                    ModLogger.Debug("InventoryPickupStation setup complete");
                }
                else
                {
                    ModLogger.Warn("Storage/InventoryPickup not found in jail hierarchy");
                }
                
                ModLogger.Debug("✓ Booking stations setup completed");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error setting up booking stations: {ex.Message}");
            }
        }

        private static string NormalizeAssetLookup(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static bool AssetNameMatchesCandidate(string assetName, string candidate)
        {
            if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(candidate))
                return false;

            string normalizedAsset = NormalizeAssetLookup(assetName);
            string normalizedFileName = NormalizeAssetLookup(System.IO.Path.GetFileNameWithoutExtension(assetName));
            string normalizedCandidate = NormalizeAssetLookup(candidate);
            string normalizedCandidateFile = NormalizeAssetLookup(System.IO.Path.GetFileNameWithoutExtension(candidate));

            return normalizedAsset == normalizedCandidate ||
                   normalizedFileName == normalizedCandidate ||
                   normalizedAsset == normalizedCandidateFile ||
                   normalizedFileName == normalizedCandidateFile;
        }

        private static void LoadAndAssignJailPrefabs(JailController controller)
        {
            try
            {
                ModLogger.Debug("Loading jail prefabs from asset bundle...");

                // Use the cached behind-bars bundle
                var jailBundle = CachedJailBundle;
                if (jailBundle == null)
                {
                    ModLogger.Error("Failed to load behind-bars bundle for prefabs - bundle not cached");
                    return;
                }

#if MONO
                var allAssetNames = jailBundle.GetAllAssetNames() ?? Array.Empty<string>();
                int allAssetNameCount = allAssetNames.Length;
#else
                var allAssetNames = new List<string>();
                var il2CppAssetNames = jailBundle.GetAllAssetNames();
                if (il2CppAssetNames != null)
                {
                    for (int i = 0; i < il2CppAssetNames.Length; i++)
                    {
                        if (il2CppAssetNames[i] != null)
                            allAssetNames.Add(il2CppAssetNames[i].ToString());
                    }
                }
                int allAssetNameCount = allAssetNames.Count;
#endif

                GameObject TryLoadPrefabByName(string assetName)
                {
#if MONO
                    return jailBundle.LoadAsset<GameObject>(assetName);
#else
                    return jailBundle.LoadAsset(assetName, Il2CppInterop.Runtime.Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                }

                GameObject LoadPrefabWithFallback(string label, params string[] candidates)
                {
                    foreach (var candidate in candidates)
                    {
                        var prefab = TryLoadPrefabByName(candidate);
                        if (prefab != null)
                            return prefab;
                    }

                    foreach (var assetName in allAssetNames)
                    {
                        foreach (var candidate in candidates)
                        {
                            if (!AssetNameMatchesCandidate(assetName, candidate))
                                continue;

                            var matchedPrefab = TryLoadPrefabByName(assetName);
                            if (matchedPrefab != null)
                            {
                                ModLogger.Warn($"{label} matched by asset name fallback: {assetName}");
                                return matchedPrefab;
                            }
                        }
                    }

                    return null;
                }

                // Load JailDoor prefab - try multiple naming variations
                var jailDoorPrefab = LoadPrefabWithFallback(
                    "JailDoor prefab",
                    "JailDoor",
                    "jaildoor",
                    "CellDoor",
                    "celldoor",
                    "assets/behindbars/jaildoor.prefab",
                    "assets/behindbars/celldoor.prefab"
                );
                if (jailDoorPrefab != null)
                {
                    controller.jailDoorPrefab = jailDoorPrefab;
                    ModLogger.Debug($"✓ Loaded JailDoor prefab: {jailDoorPrefab.name}");
                }
                else
                {
                    ModLogger.Warn("JailDoor prefab not found in bundle - no cell doors will be instantiated!");
                }

                // Load GuardDoors prefab - try multiple naming variations
                var steelDoorPrefab = LoadPrefabWithFallback(
                    "Steel door prefab",
                    "GuardDoors",
                    "guarddoors",
                    "GuardDoor",
                    "guarddoor",
                    "SteelDoor",
                    "steeldoor",
                    "assets/behindbars/guarddoors.prefab",
                    "assets/behindbars/guarddoor.prefab",
                    "assets/behindbars/steeldoor.prefab"
                );
                if (steelDoorPrefab != null)
                {
                    controller.steelDoorPrefab = steelDoorPrefab;
                    ModLogger.Debug($"✓ Loaded SteelDoor prefab: {steelDoorPrefab.name}");
                }
                else
                {
                    ModLogger.Warn("SteelDoor prefab not found in bundle - no steel doors will be instantiated!");
                }

                // Load SecurityCamera prefab (if available)
                var cameraPrefab = LoadPrefabWithFallback(
                    "SecurityCamera prefab",
                    "SecurityCameraPlaceHolder",
                    "SecurityCameraPlaceholder",
                    "securitycameraplaceholder",
                    "assets/behindbars/securitycameraplaceholder.prefab"
                );
                if (cameraPrefab != null)
                {
                    controller.securityCameraPrefab = cameraPrefab;
                    ModLogger.Debug("✓ Loaded SecurityCamera prefab");
                }
                else
                {
                    ModLogger.Warn("SecurityCamera prefab not found in bundle (optional)");
                }

                if (controller.jailDoorPrefab == null && controller.steelDoorPrefab == null)
                {
                    ModLogger.Error($"No door prefabs resolved from bundle. Asset count scanned: {allAssetNameCount}");
                }

                ModLogger.Debug("Jail prefab loading completed");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error loading jail prefabs: {e.Message}");
            }
        }

        private static void LogJailControllerStatus()
        {
            if (JailController == null)
            {
                ModLogger.Warn("ActiveJailController is null");
                return;
            }

            try
            {
                ModLogger.Debug("=== JAIL CONTROLLER STATUS ===");
                ModLogger.Debug($"Cells discovered: {JailController.cells?.Count ?? 0}");
                ModLogger.Debug($"Holding cells discovered: {JailController.holdingCells?.Count ?? 0}");
                ModLogger.Debug($"Security cameras: {JailController.securityCameras?.Count ?? 0}");
                ModLogger.Debug($"Area lights: {JailController.areaLights?.Count ?? 0}");
                ModLogger.Debug($"Door prefabs loaded: JailDoor={JailController.jailDoorPrefab != null}, SteelDoor={JailController.steelDoorPrefab != null}");

                // Check area initialization
                var areas = new[]
                {
                    ("Kitchen", JailController.kitchen?.isInitialized ?? false),
                    ("Laundry", JailController.laundry?.isInitialized ?? false),
                    ("Phone Area", JailController.phoneArea?.isInitialized ?? false),
                    ("Booking", JailController.booking?.isInitialized ?? false),
                    ("Guard Room", JailController.guardRoom?.isInitialized ?? false),
                    ("Main Rec", JailController.mainRec?.isInitialized ?? false),
                    ("Showers", JailController.showers?.isInitialized ?? false)
                };

                ModLogger.Debug("Area status:");
                foreach (var (name, initialized) in areas)
                {
                    ModLogger.Debug($"  {name}: {(initialized ? "✓ Initialized" : "✗ Not initialized")}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error logging jail controller status: {e.Message}");
            }
        }

        /// <summary>
        /// Temporary compatibility accessor for the manager-owned player key service.
        /// New code should prefer <see cref="SystemManager"/> ownership directly.
        /// </summary>
        public IPlayerKeyService? GetPlayerKeyService() => _systemManager?.PlayerKeyService;

        // Jail Controller convenience methods
        public static bool IsJailControllerReady() => JailController != null;

        public static void TriggerEmergencyLockdown()
        {
            if (JailController != null)
            {
                JailController.EmergencyLockdown();
                ModLogger.Info("Emergency lockdown triggered via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot trigger emergency lockdown - JailController not available");
            }
        }

        public static void UnlockAllDoors()
        {
            if (JailController != null)
            {
                JailController.UnlockAll();
                ModLogger.Info("All doors unlocked via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot unlock doors - JailController not available");
            }
        }

        public static void SetJailLighting(JailLightingController.LightingState state)
        {
            if (JailController != null)
            {
                JailController.SetJailLighting(state);
                ModLogger.Info($"Jail lighting set to {state} via mod system");
            }
            else
            {
                ModLogger.Warn("Cannot set lighting - JailController not available");
            }
        }

        public static string GetPlayerCurrentArea()
        {
            if (JailController != null && Player.Local != null)
            {
                return JailController.GetPlayerCurrentArea(Player.Local.transform.position);
            }
            return "Unknown - JailController not available";
        }
        
        /// <summary>
        /// Get the PlayerHandler for a given player
        /// </summary>
        public static PlayerHandler? GetPlayerHandler(Player player)
        {
            if (Instance != null && player != null && Instance._playerHandlers.ContainsKey(player))
            {
                return Instance._playerHandlers[player];
            }
            return null;
        }

        /// <summary>
        /// Public API: Show jail information UI
        /// </summary>
        public static void ShowJailInfoUI(string crime, string timeInfo, string bailInfo)
        {
            try
            {
                ResolveUIManager().ShowJailInfoUI(crime, timeInfo, bailInfo);
                ModLogger.Info($"Jail info UI shown: Crime={crime}, Time={timeInfo}, Bail={bailInfo}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error showing jail info UI: {e.Message}");
            }
        }

        /// <summary>
        /// Public API: Hide jail information UI
        /// </summary>
        public static void HideJailInfoUI()
        {
            try
            {
                ResolveUIManager().HideJailInfoUI();
                ModLogger.Info("Jail info UI hidden");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error hiding jail info UI: {e.Message}");
            }
        }

        /// <summary>
        /// Setup door triggers for the specific jail door triggers
        /// Call this in-game to add DoorTriggerHandler components to your jail triggers
        /// </summary>
        public static void SetupDoorTriggers()
        {
            try
            {
                ModLogger.Debug("Starting jail door trigger setup...");
                
                // Setup only the specific jail door triggers under PatrolPoints
                Behind_Bars.Utils.ManualDoorTriggerSetup.SetupJailDoorTriggers();
                
                ModLogger.Debug("Jail door trigger setup completed!");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting up door triggers: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Manual setup for a specific door trigger by name
        /// Example: Core.SetupSpecificDoorTrigger("BookingDoorTrigger", "Booking Inner Door")
        /// </summary>
        public static void SetupSpecificDoorTrigger(string triggerName, string doorName = null)
        {
            try
            {
                bool success = Behind_Bars.Utils.ManualDoorTriggerSetup.SetupDoorTriggerByName(triggerName, doorName);
                if (success)
                {
                    ModLogger.Debug($"Successfully setup door trigger: {triggerName}");
                }
                else
                {
                    ModLogger.Error($"Failed to setup door trigger: {triggerName}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting up specific door trigger {triggerName}: {e.Message}");
            }
        }

        /// <summary>
        /// Handle hotkeys for testing and debugging
        /// </summary>
        public override void OnUpdate()
        {
            try
            {
                // The property locker is a native uGUI modal.  Keep the game's first-person
                // camera from reclaiming the mouse while it owns the interaction.
                if (PropertyLockerUI.IsPresentationOpen)
                {
                    PropertyLockerUI.MaintainOpenPresentation();
                }

                // Home key - Teleport to jail for testing
                if (Input.GetKeyDown(KeyCode.Home))
                {
                    TeleportToJail();
                }

                // End key - Teleport to Taco Ticklers
                if (Input.GetKeyDown(KeyCode.End))
                {
                    TeleportToTacoTicklers();
                }

                // F9 key - Show crime details (debug)
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    ResolveUIManager().ShowCrimeDetails();
                }

                // F6 key - Quick 10-second jail sentence for release testing
                if (Input.GetKeyDown(KeyCode.F6))
                {
                    QuickJailForReleaseTesting();
                }

                // F8 key - Trigger instant arrest for testing
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    TriggerTestArrest();
                }

                // F12 key - Test spawn NPC with new avatar system
                if (Input.GetKeyDown(KeyCode.F12))
                {
                    TestSpawnNPCWithAvatar();
                }

                // Alt+0 key - Show/hide instructions screen (Message of the Day)
                if (Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.Alpha0))
                {
                    var uiManager = ResolveUIManager();
                    if (uiManager != null)
                    {
                        // Toggle instructions screen
                        if (uiManager.IsLoadingScreenVisible())
                        {
                            uiManager.HideLoadingScreen();
                        }
                        else
                        {
                            uiManager.ShowInstructions();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Silently ignore input errors to avoid spam
            }
        }

        /// <summary>
        /// Test spawn an NPC with the new avatar system
        /// </summary>
        private void TestSpawnNPCWithAvatar()
        {
            try
            {
#if !MONO
                var player = Object.FindObjectOfType<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var player = Object.FindObjectOfType<ScheduleOne.PlayerScripts.Player>();
#endif
                if (player != null)
                {
                    ModLogger.Debug("F12 pressed - Testing NPC spawn with working avatar system");

                    // Spawn in front of player
                    var spawnPos = player.transform.position + (player.transform.forward * 3f);

                    // Randomly choose between guard and inmate
                    GameObject testNPC = null;
                    bool spawnGuard = UnityEngine.Random.Range(0f, 1f) > 0.5f;

                    if (spawnGuard)
                    {
                        ModLogger.Debug("Spawning test GUARD with proper uniform...");
                        testNPC = BaseNPCSpawner.SpawnGuard(spawnPos, "Officer", "Test", $"G{UnityEngine.Random.Range(1000, 9999)}");
                    }
                    else
                    {
                        ModLogger.Debug("Spawning test INMATE with orange jumpsuit...");
                        testNPC = BaseNPCSpawner.SpawnInmate(spawnPos, "Inmate", $"Test{UnityEngine.Random.Range(100, 999)}");
                    }

                    if (testNPC != null)
                    {
                        ModLogger.Debug($"✅ Test {(spawnGuard ? "GUARD" : "INMATE")} spawned successfully: {testNPC.name}");
                        ModLogger.Debug($"Appearance: {(spawnGuard ? "Blue uniform with police cap and combat boots" : "Orange jumpsuit with sandals")}");
                    }
                    else
                    {
                        ModLogger.Error("❌ Failed to spawn test NPC");
                    }
                }
                else
                {
                    ModLogger.Warn("Player not found for NPC spawn location");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error testing NPC spawn: {e.Message}");
            }
        }

        /// <summary>
        /// Teleport player to inside the jail for testing
        /// </summary>
        private void TeleportToJail()
        {
            try
            {
#if !MONO
                var player = Object.FindObjectOfType<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var player = Object.FindObjectOfType<ScheduleOne.PlayerScripts.Player>();
#endif
                if (player != null)
                {
                    Vector3 jailPosition = new Vector3(44.324f, 10.2846f, -218.7174f);
                    player.transform.position = jailPosition;
                    ModLogger.Debug($"✓ Teleported player to jail at {jailPosition}");
                }
                else
                {
                    ModLogger.Warn("Player not found for teleportation");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error teleporting to jail: {e.Message}");
            }
        }
        
        /// <summary>
        /// Teleport player to Taco Ticklers for testing
        /// </summary>
        private void TeleportToTacoTicklers()
        {
            try
            {
#if !MONO
                var player = Object.FindObjectOfType<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var player = Object.FindObjectOfType<ScheduleOne.PlayerScripts.Player>();
#endif
                if (player != null)
                {
                    Vector3 tacoTicklersPosition = new Vector3(-30.4759f, 2.0734f, 61.9903f);
                    player.transform.position = tacoTicklersPosition;
                    ModLogger.Debug($"✓ Teleported player to Taco Ticklers at {tacoTicklersPosition}");
                }
                else
                {
                    ModLogger.Warn("Player not found for teleportation");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error teleporting to Taco Ticklers: {e.Message}");
            }
        }

        /// <summary>
        /// Quick 10-second jail sentence for release testing - skips full booking process
        /// </summary>
        private void QuickJailForReleaseTesting()
        {
            try
            {
#if !MONO
                var player = Object.FindObjectOfType<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var player = Object.FindObjectOfType<ScheduleOne.PlayerScripts.Player>();
#endif
                if (player != null)
                {
                    ModLogger.Debug("F6 pressed - Quick 10-second jail for release testing!");

                    if (JailSystem != null && JailController != null)
                    {
                        // Create a minimal 10-second sentence
                        var testSentence = new JailSystem.JailSentence
                        {
                            JailTime = 10f, // 10 seconds
                            FineAmount = 100f,
                            Severity = JailSystem.JailSeverity.Minor,
                            Description = "Quick Test Sentence",
                            CanPayFine = true
                        };

                        // Skip booking stations - go straight to cell assignment
                        ModLogger.Debug("Skipping booking process for quick test...");

                        // Assign player to a cell
                        var cellManager = ResolveCellAssignmentManager();
                        if (cellManager != null)
                        {
                            int cellNumber = cellManager.AssignPlayerToCell(player);
                            if (cellNumber >= 0)
                            {
                                ModLogger.Debug($"✓ Player assigned to cell {cellNumber}");

                                // Teleport player to the cell
                                var cell = JailController.GetCellByIndex(cellNumber);
                                if (cell?.cellTransform != null)
                                {
                                    player.transform.position = cell.cellTransform.position + Vector3.up * 1f;
                                    ModLogger.Debug($"✓ Player teleported to cell {cellNumber}");

                                    // Close and lock the cell door
                                    if (JailController.doorController != null)
                                    {
                                        JailController.doorController.CloseJailCellDoor(cellNumber);
                                        ModLogger.Debug($"✓ Cell {cellNumber} door closed and locked");
                                    }

                                    // Start UI timer
                                    var uiWrapper = ResolveUIManager().GetUIWrapper();
                                    if (uiWrapper != null)
                                    {
                                        float bailAmount = JailSystem.CalculateBailAmount(testSentence.FineAmount, testSentence.Severity);
                                        uiWrapper.StartDynamicUpdates(testSentence.JailTime, bailAmount);
                                        ModLogger.Debug($"✓ UI timer started: 10s jail time, ${bailAmount} bail");
                                    }

                                    ModLogger.Debug("✓ Quick jail test complete - player in cell with 10-second sentence!");
                                    ModLogger.Debug("   Timer will trigger automatic release when complete");
                                }
                                else
                                {
                                    ModLogger.Error("Could not find cell transform for teleport");
                                }
                            }
                            else
                            {
                                ModLogger.Error("Failed to assign cell");
                            }
                        }
                        else
                        {
                            ModLogger.Error("CellAssignmentManager not available");
                        }
                    }
                    else
                    {
                        ModLogger.Error("JailSystem or JailController not available");
                    }
                }
                else
                {
                    ModLogger.Warn("No player found for quick jail test");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error in quick jail test: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Trigger an instant arrest for testing purposes
        /// </summary>
        private void TriggerTestArrest()
        {
            try
            {
#if !MONO
                var player = Object.FindObjectOfType<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var player = Object.FindObjectOfType<ScheduleOne.PlayerScripts.Player>();
#endif
                if (player != null)
                {
                    ModLogger.Debug("F8 pressed - Triggering test arrest!");

                    // Start the arrest process through JailSystem
                    if (JailSystem != null)
                    {
                        // Trigger immediate arrest using the existing system
                        MelonCoroutines.Start(JailSystem.HandleImmediateArrest(player));
                        ModLogger.Debug("✓ Test arrest triggered - player will be processed through booking");
                    }
                    else
                    {
                        ModLogger.Error("JailSystem not available for arrest trigger");
                    }
                }
                else
                {
                    ModLogger.Warn("No player found to arrest");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error triggering test arrest: {e.Message}");
            }
        }

        /// <summary>
        /// Handles active-scene transitions, cancelling a Main gameplay session before Unity
        /// destroys scene-owned objects. Menu loading also has an explicit post-load cleanup path
        /// because this event is not guaranteed for every outgoing scene.
        /// </summary>
        private void OnSceneChanged(Scene oldScene, Scene newScene)
        {
            try
            {
                ModLogger.Debug($"Scene changed from '{oldScene.name}' to '{newScene.name}'");

                // A Main -> Menu/Loading transition destroys scene objects but does not deinitialize
                // the Melon mod. Cancel the active gameplay session before Unity begins invoking
                // callbacks against those destroyed objects.
                if (oldScene.name == "Main" && newScene.name != "Main")
                {
                    ShutdownGameplayScene($"scene change {oldScene.name} -> {newScene.name}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error handling scene change: {e.Message}");
            }
        }

        /// <summary>
        /// Schedule I reclaims first-person input after normal update work. Reassert the
        /// locker modal after that pass so its cursor is the final UI owner for the frame.
        /// The panel's native exit listener consumes the first menu/back action.
        /// </summary>
        public override void OnLateUpdate()
        {
            if (PropertyLockerUI.IsPresentationOpen)
            {
                PropertyLockerUI.MaintainOpenPresentation();
            }
        }

        /// <summary>
        /// Opens a new gameplay session, invalidating continuations from any earlier Main scene
        /// and resetting player-bootstrap readiness before scene-owned trackers are started.
        /// </summary>
        private void BeginGameplaySceneSession()
        {
            _gameplaySessionVersion++;
            _gameplaySceneActive = true;
            _playerSystemsReady = false;
            try { ResolveJailTimeTracker().BeginGameplaySession(); }
            catch (Exception ex) { ModLogger.Warn($"Jail sentence tracker startup reported an issue: {ex.Message}"); }
            ModLogger.Debug($"Behind Bars gameplay session {_gameplaySessionVersion} started");
        }

        /// <summary>
        /// Cancels scene-bound gameplay owners without tearing down the persistent mod service graph.
        /// The session is invalidated before coroutines and systems are stopped, then UI fades,
        /// listeners, and scene hosts are released. This is intentionally separate from
        /// OnDeinitializeMelon so loading another save can bootstrap cleanly.
        /// </summary>
        /// <param name="reason">Diagnostic context for the transition or teardown.</param>
        /// <param name="forceUiCleanup">Whether UI cleanup runs even without an active session.</param>
        private void ShutdownGameplayScene(string reason, bool forceUiCleanup = false)
        {
            var hasActiveGameplaySession = _gameplaySceneActive || _loadModCoroutine != null || _playerInitializationCoroutine != null;
            if (!hasActiveGameplaySession && !forceUiCleanup)
            {
                return;
            }

            if (hasActiveGameplaySession)
            {
                ModLogger.Info($"Behind Bars gameplay session {_gameplaySessionVersion} ending ({reason})");
                _gameplaySceneActive = false;
                _playerSystemsReady = false;
                _gameplaySessionVersion++;

                StopSceneCoroutine(ref _loadModCoroutine);
                StopSceneCoroutine(ref _playerInitializationCoroutine);

                try { ResolveBookingProcess()?.CancelForSceneExit(); }
                catch (Exception ex) { ModLogger.Warn($"Booking shutdown reported an issue: {ex.Message}"); }

                try { ResolveJailTimeTracker().EndGameplaySession(); }
                catch (Exception ex) { ModLogger.Warn($"Jail sentence tracker shutdown reported an issue: {ex.Message}"); }

                try { Core.JailController?.BookingProcessController?.scannerStation?.CancelForSceneExit(); }
                catch (Exception ex) { ModLogger.Warn($"Scanner shutdown reported an issue: {ex.Message}"); }

                try { Core.JailController?.BookingProcessController?.mugshotStation?.CancelForSceneExit(); }
                catch (Exception ex) { ModLogger.Warn($"Mugshot shutdown reported an issue: {ex.Message}"); }

                try { ResolvePrisonNpcManager()?.CancelForSceneExit(); }
                catch (Exception ex) { ModLogger.Warn($"NPC manager shutdown reported an issue: {ex.Message}"); }

                try { JailNpcPrefabLifecycle.CancelForSceneExit(); }
                catch (Exception ex) { ModLogger.Warn($"NPC spawn lifecycle shutdown reported an issue: {ex.Message}"); }

                try
                {
                    if (ReleaseManager.TryGetRegisteredInstance(out var releaseManager))
                    {
                        releaseManager.CancelForSceneExit();
                    }
                }
                catch (Exception ex) { ModLogger.Warn($"Release shutdown reported an issue: {ex.Message}"); }

                try { HarmonyPatches.ResetSceneTransientState(); }
                catch (Exception ex) { ModLogger.Warn($"Harmony transient-state reset reported an issue: {ex.Message}"); }

                try { ClearRapSheetCache(); }
                catch (Exception ex) { ModLogger.Warn($"Rap-sheet cache reset reported an issue: {ex.Message}"); }
            }

            try
            {
                // ShutdownSceneUI directly owns the jail overlay. Do not call the public
                // HideJailInfoUI facade here: it lazily initializes scene UI when absent,
                // which is the opposite of what a Menu transition needs.
                ResolveUIManager()?.ShutdownSceneUI();
            }
            catch (Exception ex) { ModLogger.Warn($"UI shutdown reported an issue: {ex.Message}"); }
        }

        /// <summary>Stops one scene-owned coroutine and clears its handle idempotently.</summary>
        /// <param name="coroutine">Reference to the coroutine handle owned by the current session.</param>
        private static void StopSceneCoroutine(ref Coroutine? coroutine)
        {
            if (coroutine == null)
            {
                return;
            }

            MelonCoroutines.Stop(coroutine);
            coroutine = null;
        }

        /// <summary>
        /// Performs final mod teardown after scene-owned cleanup has completed. Static scene
        /// listeners are removed, the persistent UI is released, and the manager service graph
        /// is shut down; this path is not a substitute for the per-scene session boundary.
        /// </summary>
        public override void OnDeinitializeMelon()
        {
            try
            {
                ModLogger.Debug("Behind Bars shutting down - cleaning up...");
                ShutdownGameplayScene("mod deinitialization");

                // Unsubscribe from scene events
#if !MONO
                SceneManager.activeSceneChanged -= new System.Action<Scene, Scene>(OnSceneChanged);
#else
                SceneManager.activeSceneChanged -= OnSceneChanged;
#endif

                // Clean up UI
                // The public facade is retained here for legacy callers; scene shutdown above
                // is the authoritative non-creating cleanup path. If the facade initializes a
                // missing manager, DestroyJailInfoUI immediately removes that final artifact.
                HideJailInfoUI();
                var uiManager = ResolveUIManager();
                if (uiManager != null)
                {
                    uiManager.DestroyJailInfoUI();
                }

                _systemManager?.Shutdown();
                _systemManager = null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error during Behind Bars cleanup: {e.Message}");
            }
        }

        /// <summary>
        /// Resolves the authored exit-scanner hierarchy, assigns the available steel/jail door
        /// prefab, and instantiates the door once. Missing hierarchy or prefab data fails closed
        /// with diagnostics so repeated setup calls do not create duplicate doors.
        /// </summary>
        private static void SetupExitDoor(JailController jailController)
        {
            try
            {
                ModLogger.Debug("Setting up exit door...");

                if (jailController == null)
                {
                    return;
                }

                var exitScannerArea = jailController.areaManager?.GetExitScanner();
                if ((exitScannerArea == null || exitScannerArea.exitDoor == null) && jailController.transform != null)
                {
                    var exitScannerRoot = jailController.transform.Find("Hallway/ExitScannerStation") ??
                                          jailController.transform.Find("ExitScannerStation");
                    if (exitScannerRoot != null)
                    {
                        if (exitScannerArea == null)
                        {
                            exitScannerArea = new BehindBars.Areas.ExitScannerArea();
                            if (jailController.areaManager != null)
                            {
                                jailController.areaManager.exitScanner = exitScannerArea;
                            }
                        }

                        if (exitScannerArea.exitDoor == null || exitScannerArea.areaRoot == null)
                        {
                            exitScannerArea.Initialize(exitScannerRoot);
                            ModLogger.Debug($"SetupExitDoor: Initialized exit scanner area directly from hierarchy at {exitScannerRoot.name}");
                        }
                    }
                }

                if (exitScannerArea?.exitDoor != null)
                {
                    var exitDoor = exitScannerArea.exitDoor;
                    ModLogger.Debug($"Found exitDoor in ExitScannerArea: {exitDoor.doorName}");

                    if ((jailController.steelDoorPrefab == null && jailController.jailDoorPrefab == null) && CachedJailBundle != null)
                    {
                        LoadAndAssignJailPrefabs(jailController);
                    }

                    if (jailController.doorController != null)
                    {
                        jailController.doorController.steelDoorPrefab ??= jailController.steelDoorPrefab;
                        jailController.doorController.jailDoorPrefab ??= jailController.jailDoorPrefab;
                    }

                    var exitDoorPrefab = jailController.steelDoorPrefab ??
                                         jailController.doorController?.steelDoorPrefab ??
                                         jailController.jailDoorPrefab ??
                                         jailController.doorController?.jailDoorPrefab;

                    // Instantiate using the guard-door prefab when available, otherwise the jail-door fallback.
                    if (exitDoorPrefab != null && exitDoor.doorHolder != null)
                    {
                        if (!exitDoor.IsInstantiated())
                        {
                            exitDoor.doorInstance = UnityEngine.Object.Instantiate(exitDoorPrefab, exitDoor.doorHolder);
                            ModLogger.Debug($"✓ Exit door instantiated using {(exitDoorPrefab == jailController.steelDoorPrefab || exitDoorPrefab == jailController.doorController?.steelDoorPrefab ? "steelDoorPrefab" : "jailDoorPrefab fallback")}");

                            // Enable SecuritySlots for visual difference
                            var hingePoint = exitDoor.doorInstance.transform.Find("HingePoint");
                            if (hingePoint != null)
                            {
                                var securitySlots = hingePoint.Find("SecuritySlots");
                                if (securitySlots != null)
                                {
                                    securitySlots.gameObject.SetActive(true);
                                    ModLogger.Debug("✓ SecuritySlots enabled on exit door");
                                }
                            }

                            // Lock the door initially
                            exitDoor.LockDoor();
                            ModLogger.Debug("✓ Exit door locked initially");
                        }
                        else
                        {
                            ModLogger.Debug("Exit door already instantiated");
                        }
                    }
                    else
                    {
                        ModLogger.Warn($"Cannot instantiate exit door - controllerSteelDoorPrefab: {jailController.steelDoorPrefab != null}, doorControllerSteelDoorPrefab: {jailController.doorController?.steelDoorPrefab != null}, controllerJailDoorPrefab: {jailController.jailDoorPrefab != null}, doorControllerJailDoorPrefab: {jailController.doorController?.jailDoorPrefab != null}, doorHolder: {exitDoor.doorHolder != null}");
                    }
                }
                else
                {
                    var hasHallwayScanner = jailController.transform?.Find("Hallway/ExitScannerStation") != null ||
                                            jailController.transform?.Find("ExitScannerStation") != null;
                    var hasExitDoorObject = jailController.transform?.Find("Hallway/ExitDoor") != null ||
                                            jailController.transform?.Find("ExitDoor") != null;

                    if (hasHallwayScanner || hasExitDoorObject)
                    {
                        ModLogger.Warn($"Exit scanner hierarchy exists but exit door binding is incomplete - ExitScannerStation: {hasHallwayScanner}, ExitDoor: {hasExitDoorObject}");
                    }
                    else
                    {
                        ModLogger.Warn("No ExitScannerStation or ExitDoor GameObjects found in jail hierarchy for setup");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error setting up exit door: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Initializes the NavMesh optimization patch manually because the target method's
        /// <c>ref NavMeshPath</c> parameter is not reliably discovered by the normal patch scan.
        /// </summary>
        private void InitializeNavMeshOptimizationPatches()
        {
            ModLogger.Info("Initializing NavMesh optimization patches...");
            
            try
            {
#if !MONO
                var npcMovementType = typeof(Il2CppScheduleOne.NPCs.NPCMovement);
#else
                var npcMovementType = typeof(ScheduleOne.NPCs.NPCMovement);
#endif
                var canGetToMethod = npcMovementType.GetMethod("CanGetTo",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(Vector3), typeof(float), typeof(NavMeshPath).MakeByRefType() },
                    null);

                if (canGetToMethod != null)
                {
                    var prefixMethod = typeof(Harmony.NavMeshOptimizationPatches.NPCMovementCanGetToPatch).GetMethod("Prefix", 
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (prefixMethod != null)
                    {
                        HarmonyInstance.Patch(canGetToMethod, new HarmonyLib.HarmonyMethod(prefixMethod));
                        ModLogger.Info("✓ NavMesh optimization: NPCMovement.CanGetTo patch applied");
                    }
                    else
                    {
                        ModLogger.Error("Could not find NPCMovementCanGetToPatch.Prefix method");
                    }
                }
                else
                {
                    ModLogger.Error("Could not find NPCMovement.CanGetTo method with ref NavMeshPath parameter");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error while manually patching NPCMovement.CanGetTo: {ex.Message}");
            }
            
            ModLogger.Info("NavMesh optimization patches initialized");
        }
    }
}
