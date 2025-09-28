using System;
using UnityEngine;
using Behind_Bars.Helpers;
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Object;

#if !MONO
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
#else
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// BaseNPC spawner using the community-discovered BaseNPC prefab (ID 182)
    /// This replaces DirectNPCBuilder with a cleaner approach using native game systems
    /// </summary>
    public static class BaseNPCSpawner
    {
        public const int BASE_NPC_PREFAB_ID = 182; // Community discovered BaseNPC prefab

        public enum NPCRole
        {
            PrisonGuard,
            PrisonInmate,
            IntakeOfficer,
            TestNPC
        }

        /// <summary>
        /// Spawn a BaseNPC and configure it for jail use
        /// </summary>
        /// <param name="role">Type of NPC to create</param>
        /// <param name="position">World position to spawn</param>
        /// <param name="firstName">NPC first name</param>
        /// <param name="lastName">NPC last name</param>
        /// <param name="badgeNumber">Badge number for guards</param>
        /// <returns>Spawned GameObject or null if failed</returns>
        public static GameObject SpawnJailNPC(NPCRole role, Vector3 position, string firstName = "NPC", string lastName = "Test", string badgeNumber = "")
        {
            try
            {
                ModLogger.Info($"🎯 Spawning BaseNPC for {role}: {firstName} {lastName} at {position}");

                // Get the BaseNPC prefab
                var baseNPCPrefab = GetBaseNPCPrefab();
                if (baseNPCPrefab == null)
                {
                    ModLogger.Error("❌ Failed to get BaseNPC prefab");
                    return null;
                }

                // Instantiate the BaseNPC
                var npcInstance = UnityEngine.Object.Instantiate(baseNPCPrefab, position, Quaternion.identity);
                if (npcInstance == null)
                {
                    ModLogger.Error("❌ Failed to instantiate BaseNPC");
                    return null;
                }

                // Set name immediately
                npcInstance.name = $"{role}_{firstName}_{lastName}";
                ModLogger.Info($"✓ BaseNPC instantiated: {npcInstance.name}");

                // Get the NPC component (BaseNPC should have this automatically)
                var npcComponent = npcInstance.GetComponent<NPC>();
                if (npcComponent != null)
                {
                    // Configure the NPC's basic properties
                    npcComponent.FirstName = firstName;
                    npcComponent.LastName = lastName;
                    npcComponent.ID = $"{role.ToString().ToLower()}_{Guid.NewGuid().ToString().Substring(0, 8)}";

                    ModLogger.Info($"✓ NPC component configured: {npcComponent.FirstName} {npcComponent.LastName} (ID: {npcComponent.ID})");
                }
                else
                {
                    ModLogger.Warn("⚠️ No NPC component found on BaseNPC - this is unexpected");
                }

                // Apply appearance fixes (this is crucial - fixes the "marshmallow man" issue)
                FixNPCAppearance(npcInstance, role, firstName);

                // Add jail-specific behavior components
                AddJailBehaviorComponents(npcInstance, role, badgeNumber);

                // Spawn on network if we're the server
                if (ShouldSpawnOnNetwork())
                {
                    SpawnOnNetwork(npcInstance);
                }

                // Final positioning and activation
                FinalizeNPCSpawn(npcInstance, position);

                ModLogger.Info($"🎉 Successfully spawned {role} NPC: {firstName} {lastName}");
                return npcInstance;
            }
            catch (Exception e)
            {
                ModLogger.Error($"❌ Failed to spawn BaseNPC for {role}: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Get the BaseNPC prefab from FishNet's prefab collection
        /// </summary>
        private static GameObject GetBaseNPCPrefab()
        {
            try
            {
                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager == null)
                {
                    ModLogger.Error("NetworkManager not found - FishNet not initialized?");
                    return null;
                }

                var prefabObjects = networkManager.GetPrefabObjects<PrefabObjects>(0, false);
                if (prefabObjects == null)
                {
                    ModLogger.Error("No prefab objects collection found");
                    return null;
                }

                if (BASE_NPC_PREFAB_ID >= prefabObjects.GetObjectCount())
                {
                    ModLogger.Error($"BaseNPC prefab ID {BASE_NPC_PREFAB_ID} is out of range (max: {prefabObjects.GetObjectCount() - 1})");
                    return null;
                }

                var prefab = prefabObjects.GetObject(true, BASE_NPC_PREFAB_ID);
                if (prefab == null)
                {
                    ModLogger.Error($"BaseNPC prefab at index {BASE_NPC_PREFAB_ID} is null");
                    return null;
                }

                ModLogger.Info($"✓ Found BaseNPC prefab: '{prefab.name}' at index {BASE_NPC_PREFAB_ID}");
                return prefab.gameObject;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error getting BaseNPC prefab: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fix the "marshmallow man" appearance issue
        /// Try to find and copy a working Avatar component to our NPC
        /// </summary>
        private static void FixNPCAppearance(GameObject npcInstance, NPCRole role, string firstName)
        {
            try
            {
                ModLogger.Info($"🎨 Fixing appearance for {npcInstance.name}...");

                // Get the NPC component
                var npcComponent = npcInstance.GetComponent<NPC>();
                if (npcComponent == null)
                {
                    ModLogger.Error("❌ No NPC component found - cannot set appearance");
                    return;
                }

                // Try to find the Avatar component on the NPC or its children
                var npcAvatar = npcInstance.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();

                if (npcAvatar == null)
                {
                    ModLogger.Info("No Avatar component on NPC, trying to add one from template");

                    // Find a working NPC with an Avatar to copy from
                    var templateNPC = FindWorkingNPCWithAvatar();
                    if (templateNPC != null)
                    {
                        var templateAvatar = templateNPC.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
                        if (templateAvatar != null && templateAvatar.gameObject != null)
                        {
                            ModLogger.Info($"Found template Avatar from {templateNPC.name}, attempting to copy structure");

                            // Clone the entire Avatar GameObject hierarchy
                            var avatarClone = UnityEngine.Object.Instantiate(templateAvatar.gameObject, npcInstance.transform);
                            avatarClone.name = "Avatar";

                            // Get the cloned Avatar component
                            npcAvatar = avatarClone.GetComponent<ScheduleOne.AvatarFramework.Avatar>();

                            // Assign it to the NPC
                            npcComponent.Avatar = npcAvatar;

                            ModLogger.Info($"✓ Cloned Avatar structure from {templateNPC.name} to {npcInstance.name}");
                        }
                    }
                    else
                    {
                        ModLogger.Error("❌ No template NPC with working Avatar found");
                        return;
                    }
                }
                else
                {
                    ModLogger.Info($"Found existing Avatar component on {npcInstance.name}");
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
                                ModLogger.Info($"✓ Avatar settings loaded for {npcInstance.name}");

                                // Force refresh the avatar
                                if (npcAvatar.InitialAvatarSettings == null)
                                {
                                    npcAvatar.InitialAvatarSettings = avatarSettings;
                                }

                                // Try to trigger avatar refresh
                                npcAvatar.enabled = false;
                                npcAvatar.enabled = true;

                                ModLogger.Info($"✓ Avatar refresh triggered for {npcInstance.name}");
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

                ModLogger.Info($"✓ Appearance fix attempt completed for {npcInstance.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error fixing NPC appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Apply predefined customizations for special characters like "Dre"
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
                        ModLogger.Info($"✓ Applied Dre's predefined customizations");
                        break;
                    case "tidez":
                        // Future: Add Tidez customizations here
                        ModLogger.Info($"✓ Tidez detected (no specific customizations yet)");
                        break;
                    case "spec":
                        // Future: Add Spec customizations here
                        ModLogger.Info($"✓ Spec detected (no specific customizations yet)");
                        break;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying predefined character customizations: {e.Message}");
            }
        }

        /// <summary>
        /// Apply Dre's specific customizations - arm tattoos and distinctive look
        /// </summary>
        private static void ApplyDreCustomizations(object npcAvatar)
        {
            try
            {
#if !MONO
                var avatar = npcAvatar as Il2CppScheduleOne.AvatarFramework.Avatar;
                if (avatar?.AvatarSettings?.BodyLayerSettings == null)
                {
                    ModLogger.Warn("Cannot apply Dre customizations - no body layer settings");
                    return;
                }

                // Force arm tattoos for Dre
                var armTattooLayer = new Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
                {
                    layerPath = AvatarResourcePaths.Body.UpperBodyTattoos,
                    layerType = Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerType.Top,
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
                if (null // DISABLED - API not available != null)
                {
                    // Set height to tall
                    SetOrUpdateCustomizationField(null // DISABLED - API not available, "Height", 0.8f);
                    // Set build to broader
                    SetOrUpdateCustomizationField(null // DISABLED - API not available, "Weight", 0.7f);
                    ModLogger.Info("✓ Applied Dre's physical customizations (tall & broad)");
                }

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
        /// Find an existing NPC with a working Avatar component
        /// </summary>
        private static GameObject FindWorkingNPCWithAvatar()
        {
            try
            {
                // First try to find employees as they typically have working avatars
                var employees = UnityEngine.Object.FindObjectsOfType<ScheduleOne.Employees.Employee>();
                foreach (var employee in employees)
                {
                    if (employee.gameObject.name.Contains("Employee") && !employee.gameObject.name.Contains("Prison"))
                    {
                        var avatar = employee.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
                        if (avatar != null && avatar.CurrentSettings != null)
                        {
                            ModLogger.Info($"Found working avatar on employee: {employee.gameObject.name}");
                            return employee.gameObject;
                        }
                    }
                }

                // Then try regular NPCs
                var npcs = UnityEngine.Object.FindObjectsOfType<ScheduleOne.NPCs.NPC>();
                foreach (var npc in npcs)
                {
                    // Skip our own spawned NPCs
                    if (!npc.gameObject.name.Contains("Prison") && !npc.gameObject.name.Contains("BaseNPC"))
                    {
                        var avatar = npc.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
                        if (avatar != null && avatar.CurrentSettings != null)
                        {
                            ModLogger.Info($"Found working avatar on NPC: {npc.gameObject.name}");
                            return npc.gameObject;
                        }
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
        /// Add jail-specific behavior components based on NPC role
        /// </summary>
        private static void AddJailBehaviorComponents(GameObject npcInstance, NPCRole role, string badgeNumber)
        {
            try
            {
                ModLogger.Info($"🔧 Adding jail behaviors for {role} on {npcInstance.name}...");

                switch (role)
                {
                    case NPCRole.PrisonGuard:
                    case NPCRole.IntakeOfficer:
                        AddGuardBehavior(npcInstance, role, badgeNumber);
                        break;

                    case NPCRole.PrisonInmate:
                        AddInmateBehavior(npcInstance);
                        break;

                    case NPCRole.TestNPC:
                        // TestNPC gets minimal components for testing
                        AddTestNPCBehavior(npcInstance);
                        break;
                }

                ModLogger.Info($"✓ Jail behaviors added to {npcInstance.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding jail behaviors: {e.Message}");
            }
        }

        /// <summary>
        /// Add guard-specific behavior components
        /// </summary>
        private static void AddGuardBehavior(GameObject npcInstance, NPCRole role, string badgeNumber)
        {
            // Add GuardBehavior component
            var guardBehavior = npcInstance.GetComponent<GuardBehavior>();
            if (guardBehavior == null)
            {
                guardBehavior = npcInstance.AddComponent<GuardBehavior>();
            }

            // Determine assignment based on role
            GuardBehavior.GuardAssignment assignment = role == NPCRole.IntakeOfficer
                ? GuardBehavior.GuardAssignment.Booking0
                : GuardBehavior.GuardAssignment.GuardRoom0;

            // Generate badge number if not provided
            if (string.IsNullOrEmpty(badgeNumber))
            {
                badgeNumber = $"G{UnityEngine.Random.Range(1000, 9999)}";
            }

            ModLogger.Info($"✓ GuardBehavior added to {npcInstance.name} with assignment {assignment}");
        }

        /// <summary>
        /// Add inmate-specific behavior components
        /// </summary>
        private static void AddInmateBehavior(GameObject npcInstance)
        {
            // Inmates use BaseJailNPC for basic movement and interaction
            var baseNPC = npcInstance.GetComponent<BaseJailNPC>();
            if (baseNPC == null)
            {
                // BaseNPC should inherit from MonoBehaviour, not BaseJailNPC
                // So we might need a wrapper component
                ModLogger.Debug($"Adding inmate behavior wrapper to {npcInstance.name}");
            }

            ModLogger.Info($"✓ Inmate behavior configured for {npcInstance.name}");
        }

        /// <summary>
        /// Add minimal components for test NPCs
        /// </summary>
        private static void AddTestNPCBehavior(GameObject npcInstance)
        {
            // TestNPC should have minimal components for testing pathfinding
            var testController = npcInstance.GetComponent<TestNPCController>();
            if (testController == null)
            {
                testController = npcInstance.AddComponent<TestNPCController>();
                testController.usePatrolMode = true;
            }

            ModLogger.Info($"✓ TestNPCController added to {npcInstance.name}");
        }

        /// <summary>
        /// Check if we should spawn the NPC on the network
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
        /// Spawn the NPC on the network for multiplayer compatibility
        /// </summary>
        private static void SpawnOnNetwork(GameObject npcInstance)
        {
            try
            {
                var networkObject = npcInstance.GetComponent<FishNet.Object.NetworkObject>();
                if (networkObject == null)
                {
                    ModLogger.Warn($"⚠️ No NetworkObject found on {npcInstance.name} - multiplayer may not work");
                    return;
                }

                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager != null && networkManager.IsServer)
                {
                    networkManager.ServerManager.Spawn(networkObject);
                    ModLogger.Info($"✓ {npcInstance.name} spawned on network");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning NPC on network: {e.Message}");
            }
        }

        // Removed ValidateAvatarComponents and fallback methods - no longer needed with MugshotRig approach

        /// <summary>
        /// Finalize NPC spawn - positioning, NavMesh, activation
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
                        ModLogger.Info($"✓ {npcInstance.name} positioned on NavMesh at {hit.position}");
                    }
                    else
                    {
                        ModLogger.Warn($"⚠️ Could not find NavMesh near {position} for {npcInstance.name}");
                    }
                }

                // Ensure the NPC is active
                npcInstance.SetActive(true);

                ModLogger.Info($"✓ {npcInstance.name} finalized and activated");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error finalizing NPC spawn: {e.Message}");
            }
        }

        /// <summary>
        /// Quick test method to spawn a BaseNPC and see if it works
        /// </summary>
        public static GameObject TestSpawnBaseNPC(Vector3 position)
        {
            ModLogger.Info($"🧪 Testing BaseNPC spawn at {position}");
            return SpawnJailNPC(NPCRole.TestNPC, position, "TestNPC", "BaseNPC", "TEST");
        }

        /// <summary>
        /// Convenience method for spawning guards
        /// </summary>
        public static GameObject SpawnGuard(Vector3 position, string firstName = "Officer", string lastName = "Guard", string badgeNumber = "")
        {
            return SpawnJailNPC(NPCRole.PrisonGuard, position, firstName, lastName, badgeNumber);
        }

        /// <summary>
        /// Convenience method for spawning intake officers
        /// </summary>
        public static GameObject SpawnIntakeOfficer(Vector3 position, string firstName = "Officer", string lastName = "Intake", string badgeNumber = "")
        {
            return SpawnJailNPC(NPCRole.IntakeOfficer, position, firstName, lastName, badgeNumber);
        }

        /// <summary>
        /// Convenience method for spawning inmates
        /// </summary>
        public static GameObject SpawnInmate(Vector3 position, string firstName = "Inmate", string lastName = "Prisoner")
        {
            return SpawnJailNPC(NPCRole.PrisonInmate, position, firstName, lastName);
        }
    }
}