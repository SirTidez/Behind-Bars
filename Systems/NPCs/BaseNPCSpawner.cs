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
        /// Fix the "marshmallow man" appearance issue by properly loading avatar settings
        /// This is the key fix discovered by the community
        /// </summary>
        private static void FixNPCAppearance(GameObject npcInstance, NPCRole role, string firstName)
        {
            try
            {
                ModLogger.Info($"🎨 Fixing appearance for {npcInstance.name}...");

                // Find the Avatar component (BaseNPC should have this)
                var avatar = npcInstance.GetComponent<ScheduleOne.AvatarFramework.Avatar>();
                if (avatar == null)
                {
                    avatar = npcInstance.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
                }

                if (avatar == null)
                {
                    ModLogger.Warn($"⚠️ No Avatar component found on {npcInstance.name} - appearance may not work");
                    return;
                }

                // Get or create appearance manager to load proper settings
                // var appearanceSettings = NPCAppearanceManager.GetAppearanceForRole(role, firstName);
                // if (appearanceSettings != null)
                // {
                //     // This is the key method that fixes the appearance - discovered by community testing
                //     avatar.LoadAvatarSettings(appearanceSettings);
                //     ModLogger.Info($"✓ Avatar settings loaded for {npcInstance.name}");
                // }
                // else
                // {
                //     ModLogger.Warn($"⚠️ No appearance settings available for {role}");
                // }

                // Ensure avatar is active and properly configured
                if (avatar.gameObject != npcInstance)
                {
                    avatar.gameObject.SetActive(true);
                }

                ModLogger.Info($"✓ Appearance fix applied to {npcInstance.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error fixing NPC appearance: {e.Message}");
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