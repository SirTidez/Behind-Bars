using System;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using BBHelpers = Behind_Bars.Helpers.Helpers;


#if !MONO
using Il2CppFishNet;
using Il2CppFishNet.Managing;
using Il2CppFishNet.Managing.Object;
using Il2CppFishNet.Object;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne;
using Avatar = Il2CppScheduleOne.AvatarFramework.Avatar;
#else
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Object;
using ScheduleOne.Employees;
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
using ScheduleOne;
using Avatar = ScheduleOne.AvatarFramework.Avatar;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Native NPC prefab spawner. Prefab IDs and object names change between Schedule I builds,
    /// so the current prefab is selected from FishNet's registered native NPC prefabs.
    /// </summary>
    public static class BaseNPCSpawner
    {
        public enum NPCRole
        {
            /// <summary>Canonical guard role; receives <see cref="GuardBehavior"/>.</summary>
            PrisonGuard,
            /// <summary>Canonical inmate role; receives the current inmate behavior path.</summary>
            PrisonInmate,
            /// <summary>Role label routed through <see cref="GuardBehavior"/>; this helper does not add the intake state machine.</summary>
            IntakeOfficer,
            /// <summary>Canonical parole role; receives <see cref="ParoleOfficerBehavior"/>.</summary>
            ParoleOfficer,
            /// <summary>Minimal pathfinding/test role with no canonical jail workflow.</summary>
            TestNPC
        }

        /// <summary>
        /// Creates an inactive native NPC from the Behind Bars-owned FishNet template. Callers
        /// must attach and initialize role behavior before finalizing the network spawn.
        /// </summary>
        public static bool TryCreatePreparedNativeNPC(
            NPCRole role,
            string firstName,
            string lastName,
            out GameObject npcObject)
        {
            return JailNpcPrefabLifecycle.TryCreatePreparedInstance(role, firstName, lastName, out npcObject);
        }

        /// <summary>
        /// Activates a fully configured jail NPC, validates its native graph, positions it, and
        /// performs the server-side FishNet spawn when applicable.
        /// </summary>
        public static bool TryFinalizePreparedNativeNPC(GameObject npcObject, Vector3 position)
        {
            return JailNpcPrefabLifecycle.TryActivateAndSpawn(npcObject, position);
        }

        /// <summary>
        /// Warms the local FishNet registry with Behind Bars' persistent NPC template. This is
        /// safe on both host and client and never clones a live NPC.
        /// </summary>
        public static System.Collections.IEnumerator PrewarmNativeNpcTemplate()
        {
            return JailNpcPrefabLifecycle.Prewarm();
        }

        /// <summary>
        /// Creates a prepared native NPC, attaches the role-specific Behind Bars behavior,
        /// then finalizes it through <see cref="JailNpcPrefabLifecycle"/>. This is the current
        /// canonical spawner path; the older helper methods below remain compatibility paths
        /// and are not part of this flow.
        /// </summary>
        /// <param name="role">Type of NPC to create</param>
        /// <param name="position">World position to spawn</param>
        /// <param name="firstName">NPC first name</param>
        /// <param name="lastName">NPC last name</param>
        /// <param name="badgeNumber">Badge number for guards</param>
        /// <param name="guardAssignment">Guard post assigned to a guard or intake officer.</param>
        /// <param name="paroleAssignment">Parole assignment used for a parole officer.</param>
        /// <returns>Spawned GameObject or null if failed</returns>
        public static GameObject SpawnJailNPC(
            NPCRole role,
            Vector3 position,
            string firstName = "NPC",
            string lastName = "Test",
            string badgeNumber = "",
            GuardBehavior.GuardAssignment guardAssignment = GuardBehavior.GuardAssignment.GuardRoom0,
            ParoleOfficerBehavior.ParoleOfficerAssignment paroleAssignment = ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor)
        {
            try
            {
                if (!TryCreatePreparedNativeNPC(role, firstName, lastName, out var npcInstance))
                {
                    ModLogger.Error($"[NPC Spawn] Failed to prepare {role} NPC");
                    return null;
                }

                FixNPCAppearance(npcInstance, role, firstName);
                AddJailBehaviorComponents(npcInstance, role, badgeNumber, guardAssignment, paroleAssignment);

                if (TryFinalizePreparedNativeNPC(npcInstance, position))
                {
                    ModLogger.Debug($"[NPC Spawn] Spawned canonical {role} NPC: {firstName} {lastName}");
                    return npcInstance;
                }

                return null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"[NPC Spawn] Failed to spawn {role}: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Gets the current native NPC prefab from FishNet's registered spawnables.
        ///
        /// Earlier game versions exposed this as an object named BaseNPC. The current beta no
        /// longer does, so name lookup alone prevents all canonical guard/inmate behavior from spawning.
        /// The NPC component is a game-owned IL2CPP type, making this component-based lookup safe.
        /// </summary>
        public static GameObject GetBaseNPCPrefab()
        {
            return JailNpcPrefabLifecycle.GetPreparedTemplateOrNull();
        }

        /// <summary>
        /// Applies role appearance settings to the prepared NPC's own native Avatar. The
        /// canonical template must already contain an Avatar; live-avatar cloning is not
        /// attempted here. Missing appearance settings are logged and leave the native
        /// component in its existing state.
        /// </summary>
        private static void FixNPCAppearance(GameObject npcInstance, NPCRole role, string firstName)
        {
            try
            {
                ModLogger.Debug($"🎨 Fixing appearance for {npcInstance.name}...");

                // Get the NPC component
                var npcComponent = npcInstance.GetComponent<NPC>();
                if (npcComponent == null)
                {
                    npcComponent = npcInstance.GetComponentInChildren<NPC>();
                }
                
                if (npcComponent == null)
                {
                    ModLogger.Error("❌ No NPC component found - cannot set appearance");
                    return;
                }

                // Try to find the Avatar component on the NPC or its children
                var npcAvatar = npcInstance.GetComponent<Avatar>();
                if (npcAvatar == null)
                {
                    npcAvatar = npcInstance.GetComponentInChildren<Avatar>();
                }
                
                // Also check if NPC component has Avatar reference set
                if (npcAvatar == null && npcComponent.Avatar != null)
                {
                    npcAvatar = npcComponent.Avatar;
                    ModLogger.Debug($"✓ Found Avatar via NPC.Avatar reference on {npcInstance.name}");
                }

                if (npcAvatar == null)
                {
                    ModLogger.Error($"[NPC Spawn] {npcInstance.name} has no native Avatar. The prepared template is invalid; live-avatar cloning is disabled.");
                    return;
                }
                else
                {
                    ModLogger.Debug($"Found existing Avatar component on {npcInstance.name}");
                    npcComponent.Avatar = npcAvatar;
                }

                // Now apply appearance settings to the NPC's own Avatar
                if (npcAvatar != null)
                {
                    var appearanceSettings = NPCAppearanceManager.GetAppearanceForRole(role, firstName);
                    if (appearanceSettings != null)
                    {
#if !MONO
                        var avatarSettings = appearanceSettings as Il2CppScheduleOne.AvatarFramework.AvatarSettings;
#else
                        var avatarSettings = appearanceSettings as ScheduleOne.AvatarFramework.AvatarSettings;
#endif
                        if (avatarSettings != null)
                        {
                            try
                            {
                                // Ensure Avatar GameObject is active
                                npcAvatar.gameObject.SetActive(true);

                                // Apply the settings to the NPC's own Avatar
                                npcAvatar.LoadAvatarSettings(avatarSettings);
                                ModLogger.Debug($"✓ Avatar settings loaded for {npcInstance.name}");

                                // Try to trigger avatar refresh
                                npcAvatar.enabled = false;
                                npcAvatar.enabled = true;

                                ModLogger.Debug($"✓ Avatar refresh triggered for {npcInstance.name}");
                            }
                            catch (Exception e)
                            {
                                ModLogger.Error($"❌ Failed to load avatar settings: {e.Message}");
                            }
                        }
                    }
                    else
                    {
                        ModLogger.Warn($"⚠️ No appearance settings available for {role}");
                    }

                    // Apply predefined character customizations for special inmates
                    ApplyPredefinedCharacterCustomizations(npcAvatar, firstName, role);
                }

                ModLogger.Debug($"✓ Appearance fix attempt completed for {npcInstance.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error fixing NPC appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Applies optional name-based customization for special inmate identities. A name
        /// without a registered customization is intentionally a no-op; this helper does not
        /// create a replacement avatar or alter the role's behavior graph.
        /// </summary>
        private static void ApplyPredefinedCharacterCustomizations(object npcAvatar, string firstName, NPCRole role)
        {
            try
            {
                if (role != NPCRole.PrisonInmate || npcAvatar == null)
                {
                    return; // Only apply to inmates
                }

                string lowerFirstName = firstName?.ToLower();
                if (string.IsNullOrEmpty(lowerFirstName))
                {
                    return;
                }

                switch (lowerFirstName)
                {
                    case "dre":
                        ApplyDreCustomizations(npcAvatar);
                        ModLogger.Debug($"✓ Applied Dre's predefined customizations");
                        break;
                    case "tidez":
                        // Future: Add Tidez customizations here
                        ModLogger.Debug($"✓ Tidez detected (no specific customizations yet)");
                        break;
                    case "spec":
                        // Future: Add Spec customizations here
                        ModLogger.Debug($"✓ Spec detected (no specific customizations yet)");
                        break;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying predefined character customizations: {e.Message}");
            }
        }

        /// <summary>
        /// Applies the currently supported Dre tattoo customization to the native Avatar
        /// settings. Height/build customization remains disabled because the required API is
        /// unavailable in this branch.
        /// </summary>
        private static void ApplyDreCustomizations(object npcAvatar)
        {
            try
            {
#if !MONO
                var avatar = npcAvatar as Avatar;
                if (avatar?.CurrentSettings?.BodyLayerSettings == null)
                {
                    ModLogger.Warn("Cannot apply Dre customizations - no body layer settings");
                    return;
                }

                // Force arm tattoos for Dre
                var armTattooLayer = new Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
                {
                    layerPath = AvatarResourcePaths.Body.UpperBodyTattoos,
                    layerTint = new Color(0.15f, 0.1f, 0.1f, 1.0f) // Dark tattoo color
                };

                // Add to body layers if not already present
                bool hasArmTattoos = false;
                for (int i = 0; i < avatar.CurrentSettings.BodyLayerSettings.Count; i++)
                {
                    var layer = avatar.CurrentSettings.BodyLayerSettings[i];
                    if (layer.layerPath == AvatarResourcePaths.Body.UpperBodyTattoos)
                    {
                        hasArmTattoos = true;
                        break;
                    }
                }

                if (!hasArmTattoos)
                {
                    avatar.CurrentSettings.BodyLayerSettings.Add(armTattooLayer);
                    ModLogger.Info("✓ Added arm tattoos to Dre");
                }

                // Make him slightly more intimidating - taller and broader
                //if (null // DISABLED - API not available != null)
                //{
                //    // Set height to tall
                //    SetOrUpdateCustomizationField(null // DISABLED - API not available, "Height", 0.8f);
                //    // Set build to broader
                //    SetOrUpdateCustomizationField(null // DISABLED - API not available, "Weight", 0.7f);
                //    ModLogger.Info("✓ Applied Dre's physical customizations (tall & broad)");
                //}

#else
                var avatar = npcAvatar as ScheduleOne.AvatarFramework.Avatar;
                if (avatar?.CurrentSettings?.BodyLayerSettings == null)
                {
                    ModLogger.Warn("Cannot apply Dre customizations - no body layer settings");
                    return;
                }

                // Force arm tattoos for Dre
                var armTattooLayer = new ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
                {
                    layerPath = AvatarResourcePaths.Body.UpperBodyTattoos,
                    layerTint = new Color(0.15f, 0.1f, 0.1f, 1.0f) // Dark tattoo color
                };

                // Add to body layers if not already present
                bool hasArmTattoos = false;
                for (int i = 0; i < avatar.CurrentSettings.BodyLayerSettings.Count; i++)
                {
                    var layer = avatar.CurrentSettings.BodyLayerSettings[i];
                    if (layer.layerPath == AvatarResourcePaths.Body.UpperBodyTattoos)
                    {
                        hasArmTattoos = true;
                        break;
                    }
                }

                if (!hasArmTattoos)
                {
                    avatar.CurrentSettings.BodyLayerSettings.Add(armTattooLayer);
                    ModLogger.Info("✓ Added arm tattoos to Dre");
                }

#endif
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying Dre customizations: {e.Message}");
            }
        }

        /// <summary>
        /// Helper method to set or update a customization field
        /// </summary>
        private static void SetOrUpdateCustomizationField(System.Collections.Generic.List<object> fieldSettings, string fieldName, float value)
        {
            try
            {
                if (fieldSettings == null) return;

                // Try to find existing field and update it
                for (int i = 0; i < fieldSettings.Count; i++)
                {
                    var field = fieldSettings[i];
                    if (field == null) continue;

                    var fieldType = field.GetType();
                    var nameField = fieldType.GetField("fieldName");
                    if (nameField != null && nameField.GetValue(field)?.ToString() == fieldName)
                    {
                        var valueField = fieldType.GetField("fieldValue");
                        if (valueField != null)
                        {
                            valueField.SetValue(field, value);
                            ModLogger.Debug($"Updated {fieldName} to {value}");
                            return;
                        }
                    }
                }

                ModLogger.Debug($"Field {fieldName} not found in customization settings");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting customization field {fieldName}: {e.Message}");
            }
        }

        /// <summary>
        /// Legacy search helper for finding a live NPC with a usable Avatar. The canonical
        /// spawner does not use this method to clone a live NPC; it is retained only for old
        /// appearance experiments and diagnostics.
        /// </summary>
        private static GameObject FindWorkingNPCWithAvatar()
        {
            try
            {
                // First try to find employees as they typically have working avatars
                var employees = UnityEngine.Object.FindObjectsOfType<Employee>();
                foreach (var employee in employees)
                {
                    if (employee.gameObject.name.Contains("Employee") && !employee.gameObject.name.Contains("Prison"))
                    {
                        var avatar = employee.GetComponentInChildren<Avatar>();
                        if (avatar != null && avatar.CurrentSettings != null)
                        {
                            ModLogger.Info($"Found working avatar on employee: {employee.gameObject.name}");
                            return employee.gameObject;
                        }
                    }
                }

                // Then try regular NPCs - use NPCRegistry for O(1) access
                var npcs = NPCRegistryHelper.GetNPCsExcluding("Prison", "BaseNPC");
                foreach (var npc in npcs)
                {
                    var avatar = npc.GetComponentInChildren<Avatar>();
                    if (avatar != null && avatar.CurrentSettings != null)
                    {
                        ModLogger.Info($"Found working avatar on NPC: {npc.gameObject.name}");
                        return npc.gameObject;
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error finding working NPC: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Attaches the role-specific behavior to the prepared native object. Guards and
        /// intake officers receive <see cref="GuardBehavior"/>, parole officers receive
        /// <see cref="ParoleOfficerBehavior"/>, and test NPCs receive only their minimal
        /// test controller. This method does not replace the native NPC component.
        /// </summary>
        private static void AddJailBehaviorComponents(
            GameObject npcInstance,
            NPCRole role,
            string badgeNumber,
            GuardBehavior.GuardAssignment guardAssignment,
            ParoleOfficerBehavior.ParoleOfficerAssignment paroleAssignment)
        {
            try
            {
                ModLogger.Debug($"🔧 Adding jail behaviors for {role} on {npcInstance.name}...");

                switch (role)
                {
                    case NPCRole.PrisonGuard:
                    case NPCRole.IntakeOfficer:
                        AddGuardBehavior(npcInstance, guardAssignment, badgeNumber);
                        break;

                    case NPCRole.ParoleOfficer:
                        AddParoleOfficerBehavior(npcInstance, paroleAssignment, badgeNumber);
                        break;

                    case NPCRole.PrisonInmate:
                        AddInmateBehavior(npcInstance);
                        break;

                    case NPCRole.TestNPC:
                        // TestNPC gets minimal components for testing
                        AddTestNPCBehavior(npcInstance);
                        break;
                }

                ModLogger.Debug($"✓ Jail behaviors added to {npcInstance.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding jail behaviors: {e.Message}");
            }
        }

        /// <summary>
        /// Attaches the canonical <see cref="GuardBehavior"/> and immediately applies its
        /// assignment/badge configuration. The guard behavior owns subsequent movement and
        /// state transitions; this helper only performs initial attachment.
        /// </summary>
        private static void AddGuardBehavior(
            GameObject npcInstance,
            GuardBehavior.GuardAssignment assignment,
            string badgeNumber)
        {
            // Add GuardBehavior component
            var guardBehavior = BBHelpers.GetComponentSafe<GuardBehavior>(npcInstance);
            if (guardBehavior == null)
            {
                guardBehavior = BBHelpers.AddComponentSafe<GuardBehavior>(npcInstance);
            }

            // Generate badge number if not provided
            if (string.IsNullOrEmpty(badgeNumber))
            {
                badgeNumber = $"G{UnityEngine.Random.Range(1000, 9999)}";
            }

            guardBehavior.Initialize(assignment, badgeNumber);

            ModLogger.Debug($"✓ GuardBehavior added to {npcInstance.name} with assignment {assignment}");
        }

        /// <summary>
        /// Attaches and initializes the canonical parole-officer behavior. A failed component
        /// attachment is logged and does not silently downgrade the object to a static guard.
        /// </summary>
        private static void AddParoleOfficerBehavior(
            GameObject npcInstance,
            ParoleOfficerBehavior.ParoleOfficerAssignment assignment,
            string badgeNumber)
        {
            var behavior = BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(npcInstance)
                           ?? BBHelpers.AddComponentSafe<ParoleOfficerBehavior>(npcInstance);
            if (behavior == null)
            {
                ModLogger.Error($"[NPC Spawn] Failed to add canonical ParoleOfficerBehavior to {npcInstance.name}");
                return;
            }

            behavior.Initialize(assignment, badgeNumber);
        }

        /// <summary>
        /// Handles the inmate role's current attachment seam. In this branch the method only
        /// verifies/logs a <see cref="BaseJailNPC"/> component; it does not add an
        /// <see cref="InmateBehavior"/> component. The current runtime adds that component
        /// later through the prison-NPC manager, so this helper must not be read as a promise
        /// that a full inmate state machine was attached here.
        /// </summary>
        private static void AddInmateBehavior(GameObject npcInstance)
        {
            // Inmates use BaseJailNPC for basic movement and interaction
            var baseNPC = BBHelpers.GetComponentSafe<BaseJailNPC>(npcInstance);
            if (baseNPC == null)
            {
                // BaseNPC should inherit from MonoBehaviour, not BaseJailNPC
                // So we might need a wrapper component
                ModLogger.Debug($"Adding inmate behavior wrapper to {npcInstance.name}");
            }

            ModLogger.Debug($"✓ Inmate behavior configured for {npcInstance.name}");
        }

        /// <summary>
        /// Adds only the minimal test controller used by the explicit TestNPC role. This is a
        /// diagnostic path and is not a substitute for canonical guard, intake, parole, or
        /// inmate behavior.
        /// </summary>
        private static void AddTestNPCBehavior(GameObject npcInstance)
        {
            // TestNPC should have minimal components for testing pathfinding
            var testController = BBHelpers.GetComponentSafe<TestNPCController>(npcInstance);
            if (testController == null)
            {
                testController = BBHelpers.AddComponentSafe<TestNPCController>(npcInstance);
            }

            if (testController != null)
            {
                testController.usePatrolMode = true;
            }

                ModLogger.Debug($"✓ TestNPCController added to {npcInstance.name}");
        }

        /// <summary>
        /// Legacy server check retained for callers from the pre-template spawner. The
        /// current canonical path delegates network ownership to
        /// <see cref="JailNpcPrefabLifecycle"/> and does not call this helper.
        /// </summary>
        private static bool ShouldSpawnOnNetwork()
        {
            try
            {
                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager == null) return false;

                // Only spawn on network if we're the server
                return networkManager.IsServer;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Legacy direct FishNet spawn helper. It remains available for old callers but is
        /// not the canonical activation path; it does not perform the native graph checks or
        /// asynchronous spawn contract enforced by <see cref="JailNpcPrefabLifecycle"/>.
        /// </summary>
        private static void SpawnOnNetwork(GameObject npcInstance)
        {
            try
            {
                var networkObject = npcInstance.GetComponent<NetworkObject>();
                if (networkObject == null)
                {
                    ModLogger.Warn($"⚠️ No NetworkObject found on {npcInstance.name} - multiplayer may not work");
                    return;
                }

                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager != null && networkManager.IsServer)
                {
                    networkManager.ServerManager.Spawn(networkObject);
                    ModLogger.Debug($"✓ {npcInstance.name} spawned on network");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning NPC on network: {e.Message}");
            }
        }

        // Historical avatar-validation/fallback helpers were removed. The current template
        // path requires a native Avatar and intentionally does not restore live-object cloning.

        /// <summary>
        /// Legacy local finalization helper for the pre-template path. Current callers should
        /// use <see cref="TryFinalizePreparedNativeNPC"/> so native graph validation and the
        /// FishNet server/client contract are preserved.
        /// </summary>
        private static void FinalizeNPCSpawn(GameObject npcInstance, Vector3 position)
        {
            try
            {
                // Ensure correct positioning
                npcInstance.transform.position = position;

                // Make sure NavMeshAgent is properly positioned
                var navAgent = npcInstance.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (navAgent != null)
                {
                    // Try to warp to a valid NavMesh position
                    if (UnityEngine.AI.NavMesh.SamplePosition(position, out UnityEngine.AI.NavMeshHit hit, 5.0f, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        navAgent.Warp(hit.position);
                        navAgent.enabled = true;
                        ModLogger.Debug($"✓ {npcInstance.name} positioned on NavMesh at {hit.position}");
                    }
                    else
                    {
                        ModLogger.Warn($"⚠️ Could not find NavMesh near {position} for {npcInstance.name}");
                    }
                }

                // Ensure the NPC is active
                npcInstance.SetActive(true);

                ModLogger.Debug($"✓ {npcInstance.name} finalized and activated");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error finalizing NPC spawn: {e.Message}");
            }
        }

        /// <summary>
        /// Log the hierarchy of child GameObjects for debugging
        /// </summary>
        private static void LogChildHierarchy(GameObject obj, int depth)
        {
            if (obj == null) return;
            
            string indent = new string(' ', depth * 2);
            ModLogger.Debug($"{indent}{obj.name} (Components: {obj.GetComponents<Component>().Length})");
            
            foreach (Transform child in obj.transform)
            {
                LogChildHierarchy(child.gameObject, depth + 1);
            }
        }

        /// <summary>
        /// Diagnostic convenience wrapper that requests the minimal TestNPC role. It is not
        /// evidence that a canonical jail NPC behavior graph was spawned.
        /// </summary>
        public static GameObject TestSpawnBaseNPC(Vector3 position)
        {
            ModLogger.Debug($"🧪 Testing BaseNPC spawn at {position}");
            return SpawnJailNPC(NPCRole.TestNPC, position, "TestNPC", "BaseNPC", "TEST");
        }

        /// <summary>
        /// Convenience wrapper for the canonical guard role.
        /// </summary>
        public static GameObject SpawnGuard(Vector3 position, string firstName = "Officer", string lastName = "Guard", string badgeNumber = "")
        {
            return SpawnJailNPC(NPCRole.PrisonGuard, position, firstName, lastName, badgeNumber);
        }

        /// <summary>
        /// Convenience wrapper for the guard behavior configured as an intake officer.
        /// </summary>
        public static GameObject SpawnIntakeOfficer(Vector3 position, string firstName = "Officer", string lastName = "Intake", string badgeNumber = "")
        {
            return SpawnJailNPC(NPCRole.IntakeOfficer, position, firstName, lastName, badgeNumber);
        }

        /// <summary>
        /// Convenience wrapper for the current inmate role attachment path. Callers should
        /// verify the resulting object has the expected canonical inmate behavior.
        /// </summary>
        public static GameObject SpawnInmate(Vector3 position, string firstName = "Inmate", string lastName = "Prisoner")
        {
            return SpawnJailNPC(NPCRole.PrisonInmate, position, firstName, lastName);
        }
    }
}
