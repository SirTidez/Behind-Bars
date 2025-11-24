using UnityEngine;
using UnityEngine.AI;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Attaches NavMesh data to the jail from the asset bundle
    /// </summary>
    public static class JailNavMeshSetup
    {
        private static NavMeshDataInstance _jailNav;

        /// <summary>
        /// Attaches NavMesh data from the cached asset bundle to the jail
        /// </summary>
        public static void AttachJailNavMesh(Transform jailRoot)
        {
            ModLogger.Debug("Attaching NavMesh data from cached asset bundle...");
            
            try
            {
                var bundle = Core.CachedJailBundle;
                if (bundle == null)
                {
                    ModLogger.Error("Cached jail bundle not found");
                    return;
                }

                ModLogger.Debug("Cached bundle found, listing all assets to find NavMesh...");
                
                // List all assets to see what's available
                var allAssets = bundle.GetAllAssetNames();
                ModLogger.Debug($"Bundle contains {allAssets.Length} assets:");
                foreach (var asset in allAssets)
                {
                    ModLogger.Debug($"  Asset: {asset}");
                }

                // Try different possible NavMesh asset names (including exact name from IL2CPP logs)
                string[] possibleNames = { 
                    "navmesh-jail.asset", // Exact name from IL2CPP logs
                    "assets/csec_exporting/navmesh-jail.asset", // Full path from IL2CPP
                    "NavMesh-Jail", 
                    "navmesh-jail", 
                    "NavMesh", 
                    "navmesh", 
                    "Jail NavMesh", 
                    "jail navmesh" 
                };
                NavMeshData navMeshData = null;
                string foundName = null;

                foreach (var name in possibleNames)
                {
                    try
                    {
                        navMeshData = bundle.LoadAsset<NavMeshData>(name);
                        if (navMeshData != null)
                        {
                            foundName = name;
                            ModLogger.Debug($"Found NavMesh data with name: {name}");
                            break;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ModLogger.Debug($"Failed to load NavMesh with name '{name}': {ex.Message}");
                    }
                }

                if (navMeshData == null)
                {
                    ModLogger.Error("No NavMesh data found in bundle with any expected name");
                    return;
                }

                ModLogger.Debug($"Attempting to add NavMesh data at position {jailRoot.position}, rotation {jailRoot.rotation}");
                _jailNav = NavMesh.AddNavMeshData(navMeshData, jailRoot.position, jailRoot.rotation);
                ModLogger.Debug($"NavMesh.AddNavMeshData returned: valid={_jailNav.valid}, owner={_jailNav.owner}");
                
                // Verify the NavMesh is working
                VerifyNavMeshAttachment(jailRoot);
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Failed to attach NavMesh data: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Removes the attached NavMesh data
        /// </summary>
        public static void DetachJailNavMesh()
        {
            if (_jailNav.valid)
            {
                NavMesh.RemoveNavMeshData(_jailNav);
                ModLogger.Info("NavMesh data detached");
            }
        }

        /// <summary>
        /// Verify that the NavMesh was attached successfully
        /// </summary>
        private static void VerifyNavMeshAttachment(Transform jailRoot)
        {
            // Test a few positions to see if NavMesh is working
            Vector3[] testPositions = new Vector3[]
            {
                jailRoot.position + Vector3.forward * 2f,
                jailRoot.position + Vector3.right * 2f,
                jailRoot.position + Vector3.back * 2f,
                jailRoot.position + Vector3.left * 2f
            };

            int validPositions = 0;
            foreach (var pos in testPositions)
            {
                if (NavMesh.SamplePosition(pos, out NavMeshHit hit, 5.0f, NavMesh.AllAreas))
                {
                    validPositions++;
                    ModLogger.Debug($"Valid NavMesh position found at {hit.position}");
                }
            }

            if (validPositions > 0)
            {
                ModLogger.Debug($"✓ NavMesh verification complete! {validPositions}/{testPositions.Length} test positions valid");
            }
            else
            {
                ModLogger.Warn("NavMesh verification failed - no valid positions found");
            }
        }

        /// <summary>
        /// Check if jail has valid NavMesh
        /// </summary>
        public static bool HasValidNavMesh(Transform jailRoot)
        {
            if (!_jailNav.valid)
            {
                ModLogger.Debug("NavMeshDataInstance is not valid");
                return false;
            }

            // Test if we can sample a position near the jail
            Vector3 testPos = jailRoot.position + Vector3.up * 0.1f;
            bool hasNavMesh = NavMesh.SamplePosition(testPos, out NavMeshHit hit, 5.0f, NavMesh.AllAreas);
            
            if (hasNavMesh)
            {
                ModLogger.Debug($"NavMesh found at {hit.position}, distance: {Vector3.Distance(testPos, hit.position):F2}");
            }
            else
            {
                ModLogger.Warn("No NavMesh data found near jail position");
            }
            
            return hasNavMesh;
        }
    }
}