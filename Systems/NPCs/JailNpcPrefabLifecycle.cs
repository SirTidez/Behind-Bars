using System;
using System.Collections;
using System.Collections.Generic;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;

#if !MONO
using Il2CppFishNet;
using Il2CppFishNet.Managing.Object;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Schedules;
#else
using FishNet;
using FishNet.Managing.Object;
using FishNet.Object;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Schedules;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Owns Behind Bars' native NPC template lifecycle. Templates are built while inactive,
    /// registered with FishNet before use, and never sourced from a live NPC instance.
    /// </summary>
    internal static class JailNpcPrefabLifecycle
    {
        /// <summary>Persistent scene root that keeps the prepared native template out of live jail geometry.</summary>
        private const string TemplateRootName = "@BehindBars_NpcTemplates";
        /// <summary>Stable name used to identify the registered native NPC template.</summary>
        private const string TemplateName = "BehindBars_NativeNpcTemplate";
        /// <summary>Stable native schedule-action name provisioned on the prepared template.</summary>
        internal const string CourthouseHomeScheduleActionName = "BehindBars_CourthouseHome";

        /// <summary>Persistent root that owns the prepared template across scene changes.</summary>
        private static GameObject templateRoot;
        /// <summary>Inactive registered native NPC template reused for live instances.</summary>
        private static GameObject preparedTemplate;
        /// <summary>Prevents repeated donor-missing errors until the spawnable list changes.</summary>
        private static bool loggedDonorFailure;
        /// <summary>Live instances waiting for server readiness and delayed FishNet spawn.</summary>
        private static readonly List<PendingNetworkSpawn> pendingNetworkSpawns = new();
        /// <summary>Whether the global pending-spawn pump currently owns a coroutine.</summary>
        private static bool spawnPumpRunning;
        /// <summary>Opaque global handle for the pending FishNet spawn pump.</summary>
        private static Coroutine spawnPumpCoroutine;

        /// <summary>
        /// Cancels scene-owned FishNet spawn work. Native template assets deliberately survive
        /// scene changes, but queued live NPC instances must never be spawned into a menu or a
        /// subsequent save.
        /// </summary>
        internal static void CancelForSceneExit()
        {
            if (spawnPumpCoroutine != null)
            {
                MelonCoroutines.Stop(spawnPumpCoroutine);
                spawnPumpCoroutine = null;
            }

            spawnPumpRunning = false;
            foreach (var pending in pendingNetworkSpawns)
            {
                if (pending.NpcObject != null)
                {
                    UnityEngine.Object.Destroy(pending.NpcObject);
                }
            }

            pendingNetworkSpawns.Clear();
        }

        /// <summary>
        /// Attempts to prepare and register the shared native template for a bounded period. A timeout leaves
        /// jail NPC spawning blocked; this routine does not clone a live NPC as a fallback.
        /// </summary>
        internal static IEnumerator Prewarm()
        {
            const int attempts = 20;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (TryGetPreparedTemplate(out _))
                {
                    yield break;
                }

                yield return new WaitForSeconds(0.25f);
            }

            ModLogger.Error("[NPC Spawn] Native NPC template prewarm timed out; jail NPC spawning remains blocked until a donor prefab is available.");
        }

        /// <summary>
        /// Instantiates the inactive registered template, initializes fresh native data/identity, and normalizes
        /// navigation before handing the inactive object to the caller. No live NPC object is used as a donor.
        /// </summary>
        /// <param name="role">Role encoded into the generated object name and identity key.</param>
        /// <param name="firstName">Native identity first name.</param>
        /// <param name="lastName">Native identity last name.</param>
        /// <param name="npcObject">Receives an inactive prepared instance when successful.</param>
        /// <returns>True when a fresh native instance is ready for activation.</returns>
        internal static bool TryCreatePreparedInstance(
            BaseNPCSpawner.NPCRole role,
            string firstName,
            string lastName,
            out GameObject npcObject)
        {
            npcObject = null;
            if (!TryGetPreparedTemplate(out var template))
            {
                return false;
            }

            try
            {
                npcObject = UnityEngine.Object.Instantiate(template);
                npcObject.name = $"BehindBars_{role}_{firstName}_{Guid.NewGuid():N}";
                npcObject.SetActive(false);

                var npc = FindPlainNpc(npcObject);
                if (npc == null || !NPCCompatibility.TryInitializeFreshData(npc))
                {
                    ModLogger.Error($"[NPC Spawn] Failed to prepare fresh native data for {npcObject.name}");
                    UnityEngine.Object.Destroy(npcObject);
                    npcObject = null;
                    return false;
                }

                if (!NormalizeNativeNavigation(npcObject))
                {
                    UnityEngine.Object.Destroy(npcObject);
                    npcObject = null;
                    return false;
                }

                var id = $"behindbars:{role.ToString().ToLowerInvariant()}:{Guid.NewGuid():N}";
                if (!NPCCompatibility.ConfigureIdentity(npc, firstName, lastName, id))
                {
                    UnityEngine.Object.Destroy(npcObject);
                    npcObject = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NPC Spawn] Failed to create prepared {role} NPC: {ex.Message}");
                if (npcObject != null)
                {
                    UnityEngine.Object.Destroy(npcObject);
                    npcObject = null;
                }
                return false;
            }
        }

        /// <summary>
        /// Activates and validates a prepared instance, places it on the NavMesh, and queues its NetworkObject
        /// for server-side FishNet spawning. Returning true means queueing succeeded; it does not mean the
        /// asynchronous network spawn has completed.
        /// </summary>
        /// <param name="npcObject">Inactive prepared instance to activate.</param>
        /// <param name="position">Requested world-space spawn position.</param>
        /// <returns>True when validation passed and the instance was queued, not when FishNet has finished spawning.</returns>
        internal static bool TryActivateAndSpawn(GameObject npcObject, Vector3 position)
        {
            if (npcObject == null)
            {
                return false;
            }

            try
            {
                npcObject.transform.position = position;
                npcObject.SetActive(true);

                if (!NormalizeNativeNavigation(npcObject))
                {
                    UnityEngine.Object.Destroy(npcObject);
                    return false;
                }

                if (!TryValidateNativeGraph(npcObject, out var diagnostic))
                {
                    ModLogger.Error($"[NPC Spawn] Refusing to spawn '{npcObject.name}': {diagnostic}");
                    UnityEngine.Object.Destroy(npcObject);
                    return false;
                }

                if (!PositionOnNavMesh(npcObject, position))
                {
                    ModLogger.Error($"[NPC Spawn] Refusing to spawn '{npcObject.name}': no usable NavMesh position near requested point {position}");
                    UnityEngine.Object.Destroy(npcObject);
                    return false;
                }

                var networkObject = npcObject.GetComponent<NetworkObject>();
                if (networkObject == null)
                {
                    ModLogger.Error($"[NPC Spawn] Refusing to spawn '{npcObject.name}': native NetworkObject is missing");
                    UnityEngine.Object.Destroy(npcObject);
                    return false;
                }

                QueueNetworkSpawn(npcObject, networkObject, position);
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NPC Spawn] Failed to activate/spawn '{npcObject.name}': {ex.Message}");
                UnityEngine.Object.Destroy(npcObject);
                return false;
            }
        }

        /// <summary>Returns the registered inactive template, preparing it on demand when possible.</summary>
        /// <returns>The prepared template, or null when no donor/network prefab is available.</returns>
        internal static GameObject GetPreparedTemplateOrNull()
        {
            return TryGetPreparedTemplate(out var template) ? template : null;
        }

        /// <summary>
        /// Returns the cached template or builds one from FishNet spawnables. Donor selection deliberately
        /// prefers a plain native NPC and never inspects live scene NPCs.
        /// </summary>
        /// <param name="template">Receives the cached or newly registered template.</param>
        /// <returns>True when a registered inactive template is available.</returns>
        private static bool TryGetPreparedTemplate(out GameObject template)
        {
            template = preparedTemplate;
            if (template != null)
            {
                return true;
            }

            var networkManager = InstanceFinder.NetworkManager;
            if (networkManager == null)
            {
                return false;
            }

            var spawnables = networkManager.GetPrefabObjects<PrefabObjects>(0, false);
            if (spawnables == null)
            {
                return false;
            }

            var donor = FindDonorPrefab(spawnables);
            if (donor == null)
            {
                if (!loggedDonorFailure)
                {
                    loggedDonorFailure = true;
                    ModLogger.Error("[NPC Spawn] No native NPC donor exists in FishNet SpawnablePrefabs; live NPC cloning is intentionally disabled.");
                }
                return false;
            }

            return TryBuildTemplate(donor, spawnables, out template);
        }

        /// <summary>
        /// Selects a native FishNet donor, preferring a plain NPC and retaining generic/employee donors only
        /// when no plain donor exists. The returned object must be a registered prefab, not a live instance.
        /// </summary>
        /// <param name="spawnables">FishNet spawnable prefab collection.</param>
        /// <returns>Preferred donor NetworkObject, or null when no native NPC donor exists.</returns>
        private static NetworkObject FindDonorPrefab(PrefabObjects spawnables)
        {
            NetworkObject employeeDonor = null;
            NetworkObject genericNpcDonor = null;
            var count = spawnables.GetObjectCount();

            for (var index = 0; index < count; index++)
            {
                var candidate = spawnables.GetObject(true, index);
                if (candidate == null || candidate.gameObject == null ||
                    string.Equals(candidate.gameObject.name, TemplateName, StringComparison.Ordinal))
                {
                    continue;
                }

                var npc = candidate.gameObject.GetComponentInChildren<NPC>(true);
                if (npc == null)
                {
                    continue;
                }

                var employee = candidate.gameObject.GetComponentInChildren<Employee>(true);
                if (employee == null && npc.GetType() == typeof(NPC))
                {
                    return candidate;
                }

                if (employee == null)
                {
                    genericNpcDonor ??= candidate;
                }
                else
                {
                    employeeDonor ??= candidate;
                }
            }

            return genericNpcDonor ?? employeeDonor;
        }

        /// <summary>
        /// Clones a registered donor while inactive, converts employee donors to the plain native NPC surface,
        /// initializes navigation/schedule data, registers the template, and keeps it inactive for reuse.
        /// </summary>
        /// <param name="donor">Registered FishNet donor prefab.</param>
        /// <param name="spawnables">FishNet collection to update with the prepared template.</param>
        /// <param name="template">Receives the inactive registered template when successful.</param>
        /// <returns>True when the native template graph is prepared and registered.</returns>
        private static bool TryBuildTemplate(NetworkObject donor, PrefabObjects spawnables, out GameObject template)
        {
            template = null;
            var donorObject = donor.gameObject;
            var donorWasActive = donorObject.activeSelf;

            try
            {
                if (donorWasActive)
                {
                    donorObject.SetActive(false);
                }

                template = UnityEngine.Object.Instantiate(donorObject);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NPC Spawn] Failed to clone native donor '{donorObject.name}': {ex.Message}");
                return false;
            }
            finally
            {
                if (donorWasActive && donorObject != null)
                {
                    donorObject.SetActive(true);
                }
            }

            try
            {
                template.name = TemplateName;
                template.SetActive(false);

                if (template.GetComponentInChildren<Employee>(true) != null && !TryConvertEmployeeDonor(template))
                {
                    UnityEngine.Object.Destroy(template);
                    template = null;
                    return false;
                }

                var nativeNpc = FindPlainNpc(template);
                if (nativeNpc == null || !NPCCompatibility.TryInitializeFreshData(nativeNpc))
                {
                    ModLogger.Error("[NPC Spawn] The prepared template has no usable plain native NPC data surface");
                    UnityEngine.Object.Destroy(template);
                    template = null;
                    return false;
                }

                if (!NormalizeNativeNavigation(template))
                {
                    UnityEngine.Object.Destroy(template);
                    template = null;
                    return false;
                }

                var networkObject = template.GetComponent<NetworkObject>();
                if (networkObject == null)
                {
                    ModLogger.Error("[NPC Spawn] The prepared template has no NetworkObject");
                    UnityEngine.Object.Destroy(template);
                    template = null;
                    return false;
                }

                // The native building action is provisioned on the inactive network prefab.
                // It must exist before FishNet registration; adding it later would change the
                // networked component surface of a live NPC.
                if (!EnsureCourthouseHomeScheduleSurface(template, nativeNpc))
                {
                    UnityEngine.Object.Destroy(template);
                    template = null;
                    return false;
                }

                RegisterTemplate(spawnables, networkObject);
                OrganizeTemplate(template);
                preparedTemplate = template;
                loggedDonorFailure = false;
                ModLogger.Info($"[NPC Spawn] Prepared native NPC template from FishNet donor '{donorObject.name}'");
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NPC Spawn] Failed to prepare native NPC template: {ex.Message}");
                if (template != null)
                {
                    UnityEngine.Object.Destroy(template);
                    template = null;
                }
                return false;
            }
        }

        /// <summary>
        /// Pre-creates one native StayInBuilding action on the shared NPC template.  Only
        /// parole roster management enables it at runtime, but provisioning it here keeps
        /// its network component index stable for every spawned NPC.
        /// </summary>
        /// <param name="template">Inactive native template receiving the schedule surface.</param>
        /// <param name="nativeNpc">Plain native NPC assigned to the action.</param>
        /// <returns>True when the action is present and bound to the template NPC.</returns>
        private static bool EnsureCourthouseHomeScheduleSurface(GameObject template, NPC nativeNpc)
        {
            try
            {
                var scheduleManager = template.GetComponentInChildren<NPCScheduleManager>(true);
                if (scheduleManager == null)
                {
                    var scheduleObject = new GameObject("BehindBars_NpcSchedule");
                    scheduleObject.transform.SetParent(template.transform, false);
                    scheduleManager = scheduleObject.AddComponent<NPCScheduleManager>();
                }

                var actions = scheduleManager.GetComponentsInChildren<NPCEvent_StayInBuilding>(true);
                NPCEvent_StayInBuilding courthouseHomeAction = null;
                foreach (var action in actions)
                {
                    if (action != null && action.gameObject != null &&
                        string.Equals(action.gameObject.name, CourthouseHomeScheduleActionName, StringComparison.Ordinal))
                    {
                        courthouseHomeAction = action;
                        break;
                    }
                }

                if (courthouseHomeAction == null)
                {
                    var actionObject = new GameObject(CourthouseHomeScheduleActionName);
                    actionObject.transform.SetParent(scheduleManager.transform, false);
                    courthouseHomeAction = actionObject.AddComponent<NPCEvent_StayInBuilding>();
                    actionObject.SetActive(false);
                }

                ReflectionUtils.TrySetFieldOrProperty(courthouseHomeAction, "npc", nativeNpc);
                ReflectionUtils.TrySetFieldOrProperty(courthouseHomeAction, "schedule", scheduleManager);
                courthouseHomeAction.enabled = false;
                courthouseHomeAction.gameObject.SetActive(false);
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NPC Spawn] Failed to pre-create courthouse home schedule action: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Converts an employee-derived donor into a plain native NPC surface. This intentionally destroys all
        /// Employee components on the private template; it must never be applied to a live scene employee.
        /// </summary>
        /// <param name="template">Inactive template being normalized.</param>
        /// <returns>True when no Employee component remains and a plain NPC is available.</returns>
        private static bool TryConvertEmployeeDonor(GameObject template)
        {
            try
            {
                var plainNpc = FindPlainNpc(template);
                if (plainNpc == null)
                {
                    plainNpc = template.AddComponent<NPC>();
                }

                if (plainNpc == null)
                {
                    ModLogger.Error("[NPC Spawn] Employee donor conversion could not create a plain native NPC");
                    return false;
                }

                var employees = template.GetComponentsInChildren<Employee>(true);
                foreach (var employee in employees)
                {
                    if (employee != null)
                    {
                        UnityEngine.Object.DestroyImmediate(employee);
                    }
                }

                if (template.GetComponentInChildren<Employee>(true) != null)
                {
                    ModLogger.Error("[NPC Spawn] Employee donor conversion left an Employee component in the template");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NPC Spawn] Failed to normalize employee donor: {ex.Message}");
                return false;
            }
        }

        /// <summary>Finds an exact plain native NPC component in an inactive template graph.</summary>
        /// <param name="gameObject">Template root to search.</param>
        /// <returns>Plain native NPC, or null when only derived/missing NPC types exist.</returns>
        private static NPC FindPlainNpc(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            var npcs = gameObject.GetComponentsInChildren<NPC>(true);
            foreach (var npc in npcs)
            {
                if (npc != null && npc.GetType() == typeof(NPC))
                {
                    return npc;
                }
            }

            return null;
        }

        /// <summary>
        /// Converts the employee donor's navigation surface into the plain humanoid NPC surface.
        /// Employee prefabs can carry a worker-only agent type; its NavMeshAgent can sample the
        /// world mesh yet cannot bind to it, which leaves every cloned jail NPC off-mesh.
        /// </summary>
        /// <param name="npcObject">Prepared template or live instance whose native movement surface is normalized.</param>
        /// <returns>True when native movement and root agent bindings are valid.</returns>
        private static bool NormalizeNativeNavigation(GameObject npcObject)
        {
            var npc = FindPlainNpc(npcObject);
            var agent = npcObject.GetComponent<NavMeshAgent>();
            // NPC.Movement is populated by NPC.Awake. The reusable template is intentionally
            // inactive, so bind the existing native component directly before Awake and use the
            // cached property when it becomes available after activation.
            var movement = npc?.Movement ?? npcObject.GetComponent<NPCMovement>() ??
                           npcObject.GetComponentInChildren<NPCMovement>(true);
            if (npc == null || agent == null || movement == null)
            {
                ModLogger.Error($"[NPC Spawn] Cannot normalize navigation for '{npcObject?.name ?? "<null>"}': " +
                                $"NPC={npc != null}, Agent={agent != null}, Movement={movement != null}");
                return false;
            }

            try
            {
                movement.Agent = agent;
                movement.SetAgentType(NPCMovement.EAgentType.Humanoid);
                movement.DefaultObstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

                agent.areaMask = NavMesh.AllAreas;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
                agent.autoRepath = true;

                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NPC Spawn] Failed to normalize navigation for '{npcObject.name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Adds the prepared NetworkObject to FishNet spawnables once. Registration must precede live activation
        /// so the native network component index remains stable.
        /// </summary>
        /// <param name="spawnables">FishNet spawnable prefab collection.</param>
        /// <param name="templateNetworkObject">Prepared template network object.</param>
        private static void RegisterTemplate(PrefabObjects spawnables, NetworkObject templateNetworkObject)
        {
            var count = spawnables.GetObjectCount();
            for (var index = 0; index < count; index++)
            {
                var existing = spawnables.GetObject(true, index);
                if (existing != null && existing.gameObject != null &&
                    string.Equals(existing.gameObject.name, TemplateName, StringComparison.Ordinal))
                {
                    return;
                }
            }

            spawnables.AddObject(templateNetworkObject);
        }

        /// <summary>Parents and deactivates the prepared template under its persistent non-gameplay root.</summary>
        /// <param name="template">Prepared template to organize.</param>
        private static void OrganizeTemplate(GameObject template)
        {
            if (templateRoot == null)
            {
                templateRoot = GameObject.Find(TemplateRootName) ?? new GameObject(TemplateRootName);
                UnityEngine.Object.DontDestroyOnLoad(templateRoot);
            }

            template.transform.SetParent(templateRoot.transform, false);
            template.SetActive(false);
        }

        /// <summary>
        /// Verifies the native graph required before activation/network spawn: plain NPC, NPCData, Avatar,
        /// Movement, Inventory, Health, Awareness, Responses, Actions, Behaviour, and root NavMeshAgent.
        /// </summary>
        /// <param name="npcObject">Prepared or queued NPC object to validate.</param>
        /// <param name="diagnostic">Receives a comma-separated missing-surface report.</param>
        /// <returns>True only when every required native surface is present.</returns>
        private static bool TryValidateNativeGraph(GameObject npcObject, out string diagnostic)
        {
            var npc = FindPlainNpc(npcObject);
            if (npc == null)
            {
                diagnostic = "plain ScheduleOne.NPC is missing";
                return false;
            }

            var missing = new System.Collections.Generic.List<string>();
            if (npc.NPCData == null) missing.Add("NPCData");
            if (npc.Avatar == null) missing.Add("Avatar");
            if (npc.Movement == null) missing.Add("NPCMovement");
            if (npc.Inventory == null) missing.Add("NPCInventory");
            if (npc.Health == null) missing.Add("NPCHealth");
            if (npc.Awareness == null) missing.Add("NPCAwareness");
            if (npc.Responses == null) missing.Add("NPCResponses");
            if (npc.Actions == null) missing.Add("NPCActions");
            if (npc.Behaviour == null) missing.Add("NPCBehaviour");
            if (npcObject.GetComponent<NavMeshAgent>() == null) missing.Add("NavMeshAgent");

            diagnostic = missing.Count == 0 ? string.Empty : string.Join(", ", missing);
            return missing.Count == 0;
        }

        /// <summary>
        /// Samples within eight world units of the requested position and warps the root agent onto the mesh.
        /// FishNet may restore a prefab transform, so this is repeated after network spawn.
        /// </summary>
        /// <param name="npcObject">NPC object with a root NavMeshAgent.</param>
        /// <param name="position">Requested world-space location.</param>
        /// <returns>True when the root agent is enabled and on a NavMesh.</returns>
        private static bool PositionOnNavMesh(GameObject npcObject, Vector3 position)
        {
            var agent = npcObject.GetComponent<NavMeshAgent>();
            if (agent == null)
            {
                ModLogger.Error($"[NPC Spawn] '{npcObject.name}' has no root NavMeshAgent");
                return false;
            }

            if (NavMesh.SamplePosition(position, out var hit, 8f, NavMesh.AllAreas))
            {
                agent.enabled = true;
                if (!agent.Warp(hit.position) || !agent.isOnNavMesh)
                {
                    ModLogger.Error($"[NPC Spawn] Failed to place '{npcObject.name}' on NavMesh at {hit.position} (requested {position})");
                    return false;
                }

                return true;
            }

            ModLogger.Warn($"[NPC Spawn] No NavMesh position found within 8m of {position} for {npcObject.name}");
            return false;
        }

        /// <summary>
        /// Defers the FishNet spawn until native Awake has completed and the local server is ready.
        /// A newly activated native NPC cannot safely be spawned in the same call stack that creates
        /// its data object: native components hydrate their references during activation. On a client or
        /// before a NetworkManager exists, this method intentionally leaves the object unqueued; callers may
        /// still report activation success because the public contract is queue/validation success only.
        /// </summary>
        /// <param name="npcObject">Activated native NPC waiting for FishNet spawn.</param>
        /// <param name="networkObject">Registered NetworkObject on the NPC.</param>
        /// <param name="requestedPosition">Position to restore after FishNet spawn.</param>
        private static void QueueNetworkSpawn(GameObject npcObject, NetworkObject networkObject, Vector3 requestedPosition)
        {
            var networkManager = InstanceFinder.NetworkManager;
            if (networkManager != null && !networkManager.IsServer)
            {
                return;
            }

            for (var index = pendingNetworkSpawns.Count - 1; index >= 0; index--)
            {
                if (pendingNetworkSpawns[index].NetworkObject == null ||
                    pendingNetworkSpawns[index].NetworkObject == networkObject)
                {
                    pendingNetworkSpawns.RemoveAt(index);
                }
            }

            pendingNetworkSpawns.Add(new PendingNetworkSpawn(npcObject, networkObject, requestedPosition));
            if (!spawnPumpRunning)
            {
                spawnPumpRunning = true;
                spawnPumpCoroutine = MelonCoroutines.Start(ProcessPendingNetworkSpawns()) as Coroutine;
            }
        }

        /// <summary>
        /// Pumps queued native instances until the local FishNet server is ready, then validates and spawns each
        /// object. The realtime timeout is bounded; failures are destroyed rather than converted to non-networked
        /// NPCs, preserving canonical network behavior.
        /// </summary>
        private static IEnumerator ProcessPendingNetworkSpawns()
        {
            const float nativeActivationDelay = 0.10f;
            const float spawnTimeout = 12f;

            while (pendingNetworkSpawns.Count > 0)
            {
                var now = Time.realtimeSinceStartup;
                for (var index = pendingNetworkSpawns.Count - 1; index >= 0; index--)
                {
                    var pending = pendingNetworkSpawns[index];
                    if (pending.NpcObject == null || pending.NetworkObject == null)
                    {
                        pendingNetworkSpawns.RemoveAt(index);
                        continue;
                    }

                    if (pending.NetworkObject.IsSpawned)
                    {
                        pendingNetworkSpawns.RemoveAt(index);
                        continue;
                    }

                    if (now - pending.QueuedAt > spawnTimeout)
                    {
                        ModLogger.Error($"[NPC Spawn] Timed out waiting for FishNet server readiness for '{pending.NpcObject.name}'");
                        UnityEngine.Object.Destroy(pending.NpcObject);
                        pendingNetworkSpawns.RemoveAt(index);
                        continue;
                    }

                    if (now - pending.QueuedAt < nativeActivationDelay)
                    {
                        continue;
                    }

                    var networkManager = InstanceFinder.NetworkManager;
                    if (networkManager == null || !networkManager.IsServer ||
                        networkManager.ServerManager == null || !networkManager.ServerManager.Started)
                    {
                        continue;
                    }

                    if (!TryValidateNativeGraph(pending.NpcObject, out var diagnostic))
                    {
                        ModLogger.Error($"[NPC Spawn] Refusing queued spawn for '{pending.NpcObject.name}': {diagnostic}");
                        UnityEngine.Object.Destroy(pending.NpcObject);
                        pendingNetworkSpawns.RemoveAt(index);
                        continue;
                    }

                    try
                    {
                        networkManager.ServerManager.Spawn(pending.NetworkObject);
                        // FishNet can restore the registered prefab's transform while spawning. Reapply
                        // the requested location after the spawn so a native agent remains on NavMesh.
                        if (!PositionOnNavMesh(pending.NpcObject, pending.RequestedPosition))
                        {
                            ModLogger.Error($"[NPC Spawn] '{pending.NpcObject.name}' lost its NavMesh placement after FishNet spawn");
                        }
                        pendingNetworkSpawns.RemoveAt(index);
                    }
                    catch (Exception ex)
                    {
                        // FishNet may still be finishing its scene startup. Keep the entry queued
                        // until the bounded timeout instead of converting to a non-networked NPC.
                        if (!pending.SpawnFailureLogged)
                        {
                            pending.SpawnFailureLogged = true;
                            ModLogger.Warn($"[NPC Spawn] FishNet spawn is not ready for '{pending.NpcObject.name}': {ex.Message}");
                        }
                    }
                }

                yield return new WaitForSeconds(0.25f);
            }

            spawnPumpRunning = false;
            spawnPumpCoroutine = null;
        }

        /// <summary>State captured for one deferred server-side FishNet spawn.</summary>
        private sealed class PendingNetworkSpawn
        {
            /// <summary>Live prepared NPC object awaiting spawn.</summary>
            internal GameObject NpcObject { get; }
            /// <summary>FishNet NetworkObject that must be spawned by the local server.</summary>
            internal NetworkObject NetworkObject { get; }
            /// <summary>Requested position reapplied after FishNet spawn.</summary>
            internal Vector3 RequestedPosition { get; }
            /// <summary>Realtime enqueue timestamp used by the bounded timeout.</summary>
            internal float QueuedAt { get; }
            /// <summary>Prevents repeated warnings while FishNet startup is still incomplete.</summary>
            internal bool SpawnFailureLogged { get; set; }

            /// <summary>Captures a live prepared instance and its requested network-spawn position.</summary>
            /// <param name="npcObject">Prepared NPC object.</param>
            /// <param name="networkObject">NPC's FishNet network object.</param>
            /// <param name="requestedPosition">Requested world-space spawn position.</param>
            internal PendingNetworkSpawn(GameObject npcObject, NetworkObject networkObject, Vector3 requestedPosition)
            {
                NpcObject = npcObject;
                NetworkObject = networkObject;
                RequestedPosition = requestedPosition;
                QueuedAt = Time.realtimeSinceStartup;
            }
        }
    }
}
