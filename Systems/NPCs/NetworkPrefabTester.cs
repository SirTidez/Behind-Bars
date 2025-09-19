using System;
using UnityEngine;
using Behind_Bars.Helpers;
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Object;

#if !MONO
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Test class to verify what NetworkObject prefabs are available in the game
    /// This will help us determine if there's a "BaseNPC" or other usable prefabs
    /// </summary>
    public static class NetworkPrefabTester
    {
        /// <summary>
        /// Test to find and list all NetworkObject prefabs registered in FishNet
        /// Call this to see what we have available to work with
        /// </summary>
        public static void TestFindNetworkPrefabs()
        {
            try
            {
                ModLogger.Info("=== TESTING NETWORK PREFABS ===");

                // Try to access FishNet's NetworkManager
                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager == null)
                {
                    ModLogger.Error("❌ NetworkManager not found! FishNet might not be initialized yet.");
                    return;
                }

                ModLogger.Info("✓ NetworkManager found");

                // Try to get the default prefab objects collection (collection ID 0)
                var prefabObjects = networkManager.GetPrefabObjects<PrefabObjects>(0, false);
                if (prefabObjects == null)
                {
                    ModLogger.Error("❌ No prefab objects collection found for collection ID 0!");

                    // Try to find other collections
                    ModLogger.Info("Attempting to find other collections...");
                    for (ushort i = 0; i < 10; i++)
                    {
                        var collection = networkManager.GetPrefabObjects<PrefabObjects>(i, false);
                        if (collection != null)
                        {
                            ModLogger.Info($"Found collection {i} with {collection.GetObjectCount()} objects");
                        }
                    }
                    return;
                }

                int objectCount = prefabObjects.GetObjectCount();
                ModLogger.Info($"✓ Found prefab collection with {objectCount} registered prefabs");

                if (objectCount == 0)
                {
                    ModLogger.Warn("Collection exists but has no prefabs registered");
                    return;
                }

                // List all available prefabs
                ModLogger.Info("=== LISTING ALL PREFABS ===");
                int npcCount = 0;

                for (int i = 0; i < objectCount; i++)
                {
                    try
                    {
                        var prefab = prefabObjects.GetObject(true, i); // Get server prefab
                        if (prefab == null)
                        {
                            ModLogger.Debug($"Prefab {i}: null");
                            continue;
                        }

                        string prefabName = prefab.name;
                        ModLogger.Info($"Prefab {i}: '{prefabName}'");

                        // Check if it has NPC component
                        var npcComponent = prefab.GetComponent<NPC>();
                        if (npcComponent != null)
                        {
                            npcCount++;
                            ModLogger.Info($"  🎯 HAS NPC COMPONENT!");
                            ModLogger.Info($"     - First Name: '{npcComponent.FirstName}'");
                            ModLogger.Info($"     - Last Name: '{npcComponent.LastName}'");
                            ModLogger.Info($"     - ID: '{npcComponent.ID}'");

                            // Check for other interesting components
                            var movement = npcComponent.Movement;
                            var dialogue = npcComponent.DialogueHandler;
                            var avatar = npcComponent.Avatar;

                            ModLogger.Info($"     - Has Movement: {movement != null}");
                            ModLogger.Info($"     - Has Dialogue: {dialogue != null}");
                            ModLogger.Info($"     - Has Avatar: {avatar != null}");
                        }

                        // Check for other relevant components
                        var networkObject = prefab.GetComponent<FishNet.Object.NetworkObject>();
                        if (networkObject != null)
                        {
                            ModLogger.Info($"  ✓ Has NetworkObject (Prefab ID: {networkObject.PrefabId})");
                        }

                        // Check if it's an Employee
#if !MONO
                        var employee = prefab.GetComponent<Il2CppScheduleOne.Employees.Employee>();
#else
                        var employee = prefab.GetComponent<ScheduleOne.Employees.Employee>();
#endif
                        if (employee != null)
                        {
                            ModLogger.Info($"  👷 HAS EMPLOYEE COMPONENT!");
                        }

                        // Look for "Base" or generic names
                        if (prefabName.ToLower().Contains("base") ||
                            prefabName.ToLower().Contains("npc") ||
                            prefabName.ToLower().Contains("generic"))
                        {
                            ModLogger.Info($"  ⭐ POTENTIALLY USEFUL: Contains 'base', 'npc', or 'generic'");
                        }
                    }
                    catch (Exception e)
                    {
                        ModLogger.Error($"Error checking prefab {i}: {e.Message}");
                    }
                }

                ModLogger.Info($"=== SUMMARY ===");
                ModLogger.Info($"Total prefabs: {objectCount}");
                ModLogger.Info($"Prefabs with NPC component: {npcCount}");

                if (npcCount > 0)
                {
                    ModLogger.Info("🎉 SUCCESS: Found NPCs we can potentially use as base!");
                }
                else
                {
                    ModLogger.Warn("⚠️  No NPC prefabs found - might need alternative approach");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Failed to test network prefabs: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Test spawning a specific prefab by index to see if it works
        /// </summary>
        /// <param name="prefabIndex">Index of the prefab to test spawn</param>
        /// <param name="position">Where to spawn it</param>
        public static bool TestSpawnPrefab(int prefabIndex, Vector3 position)
        {
            try
            {
                ModLogger.Info($"=== TESTING SPAWN OF PREFAB {prefabIndex} ===");

                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager == null)
                {
                    ModLogger.Error("NetworkManager not found!");
                    return false;
                }

                var prefabObjects = networkManager.GetPrefabObjects<PrefabObjects>(0, false);
                if (prefabObjects == null)
                {
                    ModLogger.Error("No prefab objects found!");
                    return false;
                }

                if (prefabIndex >= prefabObjects.GetObjectCount())
                {
                    ModLogger.Error($"Prefab index {prefabIndex} is out of range (max: {prefabObjects.GetObjectCount() - 1})");
                    return false;
                }

                var prefab = prefabObjects.GetObject(true, prefabIndex);
                if (prefab == null)
                {
                    ModLogger.Error($"Prefab {prefabIndex} is null!");
                    return false;
                }

                ModLogger.Info($"Attempting to instantiate prefab '{prefab.name}' at {position}");

                // Instantiate the prefab
                var instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                if (instance == null)
                {
                    ModLogger.Error("Failed to instantiate prefab!");
                    return false;
                }

                ModLogger.Info($"✓ Successfully instantiated '{instance.name}'");

                // Try to spawn it on the network
                var networkObject = instance.GetComponent<FishNet.Object.NetworkObject>();
                if (networkObject == null)
                {
                    ModLogger.Error("Instance doesn't have NetworkObject component!");
                    UnityEngine.Object.Destroy(instance);
                    return false;
                }

                ModLogger.Info("Attempting to spawn on network...");

                // Check if we're the server (required for spawning)
                if (!networkManager.IsServer)
                {
                    ModLogger.Error("Cannot spawn - not server!");
                    UnityEngine.Object.Destroy(instance);
                    return false;
                }

                // Use the NetworkManager's server manager to spawn
                try
                {
                    networkManager.ServerManager.Spawn(networkObject);
                    ModLogger.Info("✓ Successfully spawned on network!");
                }
                catch (Exception spawnEx)
                {
                    ModLogger.Error($"Failed to spawn on network: {spawnEx.Message}");
                    UnityEngine.Object.Destroy(instance);
                    return false;
                }

                // Check if it has NPC component and log details
                var npc = instance.GetComponent<NPC>();
                if (npc != null)
                {
                    ModLogger.Info($"✓ NPC spawned: {npc.FirstName} {npc.LastName} (ID: {npc.ID})");
                    ModLogger.Info($"   Position: {instance.transform.position}");
                    ModLogger.Info($"   Active: {instance.gameObject.activeInHierarchy}");
                }

                return true;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Failed to test spawn prefab: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return false;
            }
        }
    }
}