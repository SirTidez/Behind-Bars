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
    /// <remarks>
    /// Registrations received before the scene's <see cref="PrisonNPCManager"/> is available
    /// are queued and flushed when bindings are attached. The queues are simple FIFO lists
    /// without duplicate suppression; callers should avoid registering the same instance more
    /// than once. This shell never owns or destroys the collaborator components themselves.
    /// </remarks>
    public sealed class NpcManager
    {
        // Deferred registrations preserve scene-order arrivals until the canonical prison
        // manager is bound. Unregistration has a separate queue so a late binding cannot
        // accidentally re-add an officer that was already removed.
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
        /// <remarks>
        /// Refresh resolves the current scene managers and then flushes any deferred registry
        /// operations. It does not recreate behavior components or transfer their ownership.
        /// </remarks>
        public void RefreshSceneBindings()
        {
            AttachSceneManagers(ResolveScenePrisonNpcManager(), Core.ResolveDynamicParoleOfficerManager());
        }

        /// <summary>
        /// Ensure the dynamic parole officer manager is available through the NPC ownership seam.
        /// This preserves the current on-demand bootstrap behavior without forcing callers to resolve
        /// the dynamic manager directly.
        /// </summary>
        /// <remarks>
        /// The method first reuses an attached/scene instance, then creates a host GameObject and
        /// adds the component through the runtime-appropriate safe path. Failure is logged and
        /// leaves the reference null; no retry queue is kept for this manager creation itself.
        /// </remarks>
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
        /// <param name="prisonNpcManager">Scene prison manager to attach, or null.</param>
        /// <param name="dynamicParoleOfficerManager">Scene dynamic parole manager to attach, or null.</param>
        /// <remarks>Attaching a prison manager immediately flushes deferred registrations.</remarks>
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
        /// <param name="coordinator">Coordinator collaborator to attach, or null to detach.</param>
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
        /// <param name="player">Player whose parole start should be forwarded.</param>
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
        /// <param name="player">Player whose parole end should be forwarded.</param>
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
            // Parole supervision is dynamically owned. The prison manager's historical
            // field is only a fallback for scenes that still provide that legacy officer.
            var dynamicSupervisor = DynamicParoleOfficerManager?.GetActiveSupervisingOfficer();
            if (dynamicSupervisor != null)
            {
                return dynamicSupervisor;
            }

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
        /// <param name="guard">Guard instance to register.</param>
        /// <remarks>
        /// If the prison manager is unavailable, the instance is appended to the deferred
        /// queue and registered when the next scene binding is attached.
        /// </remarks>
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
        /// Removes a guard from both deferred and live canonical registries.
        /// </summary>
        /// <param name="guard">Guard instance to remove.</param>
        public void UnregisterGuard(GuardBehavior guard)
        {
            if (guard == null)
            {
                return;
            }

            pendingGuardRegistrations.Remove(guard);
            GetPrisonNpcManager()?.UnregisterGuard(guard);
        }

        /// <summary>
        /// Register a parole officer with the canonical NPC registry.
        /// </summary>
        /// <param name="officer">Parole officer instance to register.</param>
        /// <remarks>Unavailable scene managers defer registration in arrival order.</remarks>
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
        /// Removes a parole officer from both deferred and live canonical registries.
        /// </summary>
        /// <param name="officer">Parole officer instance to remove.</param>
        public void UnregisterParoleOfficer(ParoleOfficerBehavior officer)
        {
            if (officer == null)
            {
                return;
            }

            pendingParoleOfficerRegistrations.Remove(officer);
            GetPrisonNpcManager()?.UnregisterParoleOfficer(officer);
        }

        /// <summary>
        /// Spawn a parole officer through the canonical NPC ownership seam.
        /// </summary>
        /// <param name="position">World position for the spawned officer.</param>
        /// <param name="firstName">Officer first name.</param>
        /// <param name="badgeNumber">Officer badge identifier.</param>
        /// <param name="assignment">Officer assignment used by the canonical manager.</param>
        /// <returns>The created officer, or <see langword="null"/> when the prison manager is not bound.</returns>
        /// <remarks>Unlike registry registration, spawn requests are not queued when the scene manager is absent.</remarks>
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
        /// <param name="prisoner">Prisoner GameObject to pass to the scene manager.</param>
        /// <returns><see langword="true"/> only when a bound prison manager accepts the request.</returns>
        public bool RequestPrisonerEscort(GameObject prisoner)
        {
            var prisonNpcManager = GetPrisonNpcManager();
            return prisonNpcManager != null && prisonNpcManager.RequestPrisonerEscort(prisoner);
        }

        /// <summary>
        /// Register a release officer with the canonical NPC registry.
        /// </summary>
        /// <param name="officer">Release officer instance to register.</param>
        /// <remarks>Unavailable scene managers defer registration in arrival order.</remarks>
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
        /// <param name="officer">Release officer instance to unregister.</param>
        /// <remarks>When unbound, the removal is queued separately from deferred registrations.</remarks>
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
        /// <remarks>Pending registration and unregistration requests are discarded during shutdown.</remarks>
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

        /// <summary>
        /// Get the attached prison manager, refreshing scene bindings when it is absent.
        /// </summary>
        /// <returns>The bound scene manager, or <see langword="null"/> when unavailable.</returns>
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

        /// <summary>
        /// Replay deferred registry operations against the newly bound prison manager.
        /// </summary>
        /// <remarks>
        /// Registrations flush guard, parole, and release queues before release-officer
        /// unregistrations; null entries are skipped and each queue is cleared after replay.
        /// </remarks>
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

        /// <summary>
        /// Append a non-null registry operation to a deferred queue and log the deferral.
        /// </summary>
        /// <typeparam name="T">NPC behavior type stored by the queue.</typeparam>
        /// <param name="queue">Destination deferred-operation queue.</param>
        /// <param name="item">NPC behavior instance to queue.</param>
        /// <param name="operation">Operation name used in diagnostics.</param>
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
