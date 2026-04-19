using System.Collections.Generic;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
using UnityEngine;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Top-level justice NPC domain manager shell.
    /// Owns references to collaborating NPC orchestration services, but does not own
    /// spawning, behavior state machines, or runtime controller lifecycle.
    /// This type is intentionally limited to reference coordination so ownership boundaries
    /// stay explicit: the prison NPC manager, parole officer manager, and supervising officer
    /// coordinator remain collaborators rather than being subsumed into a behavior rewrite.
    /// </summary>
    public sealed class NpcManager
    {
        private readonly List<GuardBehavior> pendingGuardRegistrations = new();
        private readonly List<ParoleOfficerBehavior> pendingParoleOfficerRegistrations = new();
        private readonly List<ReleaseOfficerBehavior> pendingReleaseOfficerRegistrations = new();
        private readonly List<ReleaseOfficerBehavior> pendingReleaseOfficerUnregistrations = new();

        /// <summary>
        /// Gets the prison NPC manager collaborator, if attached.
        /// </summary>
        public PrisonNPCManager? PrisonNpcManager { get; private set; }

        /// <summary>
        /// Gets the dynamic parole officer manager collaborator, if attached.
        /// </summary>
        public DynamicParoleOfficerManager? DynamicParoleOfficerManager { get; private set; }

        /// <summary>
        /// Gets the supervising officer interaction coordinator collaborator, if attached.
        /// </summary>
        internal SupervisingOfficerInteractionCoordinator? SupervisingOfficerInteractionCoordinator { get; private set; }

        /// <summary>
        /// Refreshes scene-level prison and parole NPC manager bindings through the canonical resolver path.
        /// This keeps the manager responsible for owning its own scene references instead of relying on
        /// callers to push references in manually.
        /// </summary>
        public void RefreshSceneBindings()
        {
            AttachSceneManagers(ResolveScenePrisonNpcManager(), Core.ResolveDynamicParoleOfficerManager());
        }

        /// <summary>
        /// Ensure the dynamic parole officer manager is available through the NPC ownership seam.
        /// This preserves the current on-demand bootstrap behavior without forcing callers to resolve
        /// the dynamic manager directly.
        /// </summary>
        public void EnsureDynamicParoleOfficerManager()
        {
            if (DynamicParoleOfficerManager != null)
            {
                return;
            }

            RefreshSceneBindings();
            if (DynamicParoleOfficerManager != null)
            {
                return;
            }

            var existing = BBHelpers.FindObjectOfTypeSafe<DynamicParoleOfficerManager>();
            if (existing != null)
            {
                existing.Initialize();
                DynamicParoleOfficerManager = existing;
                ModLogger.Debug("NpcManager: Reused existing DynamicParoleOfficerManager");
                return;
            }

            var managerObj = new GameObject("DynamicParoleOfficerManager");
            var manager = BBHelpers.AddComponentSafe<DynamicParoleOfficerManager>(managerObj);
            if (manager != null)
            {
                manager.Initialize();
                DynamicParoleOfficerManager = manager;
                ModLogger.Info("NpcManager: Created DynamicParoleOfficerManager on demand");
            }
            else
            {
                ModLogger.Error("NpcManager: Failed to create DynamicParoleOfficerManager on demand");
            }
        }

        /// <summary>
        /// Attaches the scene-level prison and parole NPC managers for coordination only.
        /// </summary>
        public void AttachSceneManagers(
            PrisonNPCManager? prisonNpcManager,
            DynamicParoleOfficerManager? dynamicParoleOfficerManager)
        {
            PrisonNpcManager = prisonNpcManager;
            DynamicParoleOfficerManager = dynamicParoleOfficerManager;
            FlushPendingRegistrations();
        }

        /// <summary>
        /// Attaches the supervising officer interaction coordinator for shared ownership tracking.
        /// </summary>
        internal void AttachSupervisingOfficerCoordinator(
            SupervisingOfficerInteractionCoordinator? coordinator)
        {
            SupervisingOfficerInteractionCoordinator = coordinator;
        }

        /// <summary>
        /// Resolve the prison NPC manager from the current scene or the legacy singleton as a fallback.
        /// This keeps the NPC manager from routing scene binding back through <see cref="Core"/>.
        /// </summary>
        private static PrisonNPCManager? ResolveScenePrisonNpcManager()
        {
            var sceneManager = BBHelpers.FindObjectOfTypeSafe<PrisonNPCManager>();
            if (sceneManager != null)
            {
                return sceneManager;
            }

            return PrisonNPCManager.Instance;
        }

        /// <summary>
        /// Forward a parole-start transition through the NPC domain manager to the active
        /// dynamic parole officer orchestrator.
        /// </summary>
        public void HandleParoleStarted(Player player)
        {
            EnsureDynamicParoleOfficerManager();

            if (DynamicParoleOfficerManager == null)
            {
                return;
            }

            DynamicParoleOfficerManager.HandleParoleStarted(player);
            DynamicParoleOfficerManager.ForceUpdate();
        }

        /// <summary>
        /// Forward a parole-end transition through the NPC domain manager to the active
        /// dynamic parole officer orchestrator.
        /// </summary>
        public void HandleParoleEnded(Player player)
        {
            EnsureDynamicParoleOfficerManager();

            DynamicParoleOfficerManager?.HandleParoleEnded(player);
        }

        /// <summary>
        /// Resolve the current supervising officer through the NPC ownership seam.
        /// </summary>
        public ParoleOfficerBehavior? GetSupervisingOfficer()
        {
            var prisonNpcManager = GetPrisonNpcManager();
            return prisonNpcManager?.GetSupervisingOfficer();
        }

        /// <summary>
        /// Resolve the currently registered guards through the NPC ownership seam.
        /// </summary>
        public List<GuardBehavior> GetRegisteredGuards()
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                return new List<GuardBehavior>();
            }

            return new List<GuardBehavior>(prisonNpcManager.GetRegisteredGuards());
        }

        /// <summary>
        /// Resolve the current intake officer through the NPC ownership seam.
        /// </summary>
        public GuardBehavior? GetIntakeOfficer()
        {
            var prisonNpcManager = GetPrisonNpcManager();
            return prisonNpcManager?.GetIntakeOfficer();
        }

        /// <summary>
        /// Check whether the intake officer is currently available.
        /// </summary>
        public bool IsIntakeOfficerAvailable()
        {
            var prisonNpcManager = GetPrisonNpcManager();
            return prisonNpcManager != null && prisonNpcManager.IsIntakeOfficerAvailable();
        }

        /// <summary>
        /// Register a guard with the canonical NPC registry.
        /// </summary>
        public void RegisterGuard(GuardBehavior guard)
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                QueuePendingRegistration(pendingGuardRegistrations, guard, nameof(RegisterGuard));
                return;
            }

            prisonNpcManager.RegisterGuard(guard);
        }

        /// <summary>
        /// Register a parole officer with the canonical NPC registry.
        /// </summary>
        public void RegisterParoleOfficer(ParoleOfficerBehavior officer)
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                QueuePendingRegistration(pendingParoleOfficerRegistrations, officer, nameof(RegisterParoleOfficer));
                return;
            }

            prisonNpcManager.RegisterParoleOfficer(officer);
        }

        /// <summary>
        /// Spawn a parole officer through the canonical NPC ownership seam.
        /// </summary>
        public ParoleOfficer? SpawnParoleOfficer(
            Vector3 position,
            string firstName = "Officer",
            string badgeNumber = "",
            ParoleOfficerBehavior.ParoleOfficerAssignment assignment = ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor)
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                ModLogger.Warn($"NpcManager: SpawnParoleOfficer deferred because PrisonNPCManager is not yet bound for {firstName} ({badgeNumber})");
                return null;
            }

            return prisonNpcManager.SpawnParoleOfficer(position, firstName, badgeNumber, assignment);
        }

        /// <summary>
        /// Request a prisoner escort through the canonical NPC ownership seam.
        /// </summary>
        public bool RequestPrisonerEscort(GameObject prisoner)
        {
            var prisonNpcManager = GetPrisonNpcManager();
            return prisonNpcManager != null && prisonNpcManager.RequestPrisonerEscort(prisoner);
        }

        /// <summary>
        /// Register a release officer with the canonical NPC registry.
        /// </summary>
        public void RegisterReleaseOfficer(ReleaseOfficerBehavior officer)
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                QueuePendingRegistration(pendingReleaseOfficerRegistrations, officer, nameof(RegisterReleaseOfficer));
                return;
            }

            prisonNpcManager.RegisterReleaseOfficer(officer);
        }

        /// <summary>
        /// Unregister a release officer from the canonical NPC registry.
        /// </summary>
        public void UnregisterReleaseOfficer(ReleaseOfficerBehavior officer)
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                QueuePendingRegistration(pendingReleaseOfficerUnregistrations, officer, nameof(UnregisterReleaseOfficer));
                return;
            }

            prisonNpcManager.UnregisterReleaseOfficer(officer);
        }

        /// <summary>
        /// Get the currently registered release officers through the NPC ownership seam.
        /// </summary>
        public List<ReleaseOfficerBehavior> GetRegisteredReleaseOfficers()
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                return new List<ReleaseOfficerBehavior>();
            }

            return new List<ReleaseOfficerBehavior>(prisonNpcManager.GetRegisteredReleaseOfficers());
        }

        /// <summary>
        /// Get the currently registered parole officers through the NPC ownership seam.
        /// </summary>
        public List<ParoleOfficerBehavior> GetRegisteredParoleOfficers()
        {
            var prisonNpcManager = GetPrisonNpcManager();
            if (prisonNpcManager == null)
            {
                return new List<ParoleOfficerBehavior>();
            }

            return new List<ParoleOfficerBehavior>(prisonNpcManager.GetRegisteredParoleOfficers());
        }

        /// <summary>
        /// Resets all attached references.
        /// This is a safe no-op scaffold and does not attempt to destroy or reinitialize any collaborator.
        /// </summary>
        public void Shutdown()
        {
            pendingGuardRegistrations.Clear();
            pendingParoleOfficerRegistrations.Clear();
            pendingReleaseOfficerRegistrations.Clear();
            pendingReleaseOfficerUnregistrations.Clear();
            PrisonNpcManager = null;
            DynamicParoleOfficerManager = null;
            SupervisingOfficerInteractionCoordinator = null;
        }

        private PrisonNPCManager? GetPrisonNpcManager()
        {
            if (PrisonNpcManager == null)
            {
                RefreshSceneBindings();
            }

            if (PrisonNpcManager == null)
            {
                ModLogger.Warn("NpcManager: PrisonNPCManager is not yet bound; registration requests will remain queued until the scene manager is available.");
            }

            return PrisonNpcManager;
        }

        private void FlushPendingRegistrations()
        {
            if (PrisonNpcManager == null)
            {
                return;
            }

            if (pendingGuardRegistrations.Count > 0)
            {
                foreach (var guard in pendingGuardRegistrations)
                {
                    if (guard != null)
                    {
                        PrisonNpcManager.RegisterGuard(guard);
                    }
                }

                pendingGuardRegistrations.Clear();
            }

            if (pendingParoleOfficerRegistrations.Count > 0)
            {
                foreach (var officer in pendingParoleOfficerRegistrations)
                {
                    if (officer != null)
                    {
                        PrisonNpcManager.RegisterParoleOfficer(officer);
                    }
                }

                pendingParoleOfficerRegistrations.Clear();
            }

            if (pendingReleaseOfficerRegistrations.Count > 0)
            {
                foreach (var officer in pendingReleaseOfficerRegistrations)
                {
                    if (officer != null)
                    {
                        PrisonNpcManager.RegisterReleaseOfficer(officer);
                    }
                }

                pendingReleaseOfficerRegistrations.Clear();
            }

            if (pendingReleaseOfficerUnregistrations.Count > 0)
            {
                foreach (var officer in pendingReleaseOfficerUnregistrations)
                {
                    if (officer != null)
                    {
                        PrisonNpcManager.UnregisterReleaseOfficer(officer);
                    }
                }

                pendingReleaseOfficerUnregistrations.Clear();
            }
        }

        private static void QueuePendingRegistration<T>(List<T> queue, T item, string operation) where T : class
        {
            if (item == null)
            {
                return;
            }

            queue.Add(item);
            ModLogger.Warn($"NpcManager: {operation} queued because PrisonNPCManager is not yet bound.");
        }
    }
}
