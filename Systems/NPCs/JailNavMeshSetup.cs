using UnityEngine;
using UnityEngine.AI;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Attaches the jail's authored NavMesh data from the cached asset bundle.
    /// This helper owns only the static <see cref="NavMeshDataInstance"/> registration;
    /// it does not validate every room, route, agent type, or NPC destination.
    /// </summary>
    public static class JailNavMeshSetup
    {
        /// <summary>
        /// Handle returned by Unity for the currently attached jail NavMesh data. A valid
        /// handle means the data was registered, not that all jail locations are connected.
        /// </summary>
        private static NavMeshDataInstance _jailNav;

        /// <summary>
        /// Attaches NavMesh data from the cached asset bundle at the jail root's transform.
        /// The bundle must already be cached and <paramref name="jailRoot"/> must represent
        /// the same world transform used by the authored data.
        /// </summary>
        /// <param name="jailRoot">World transform used as the NavMesh data origin.</param>
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
                
                // Log the bundle inventory for asset-name diagnosis; this is not a
                // substitute for validating the resulting NavMesh graph.
                var allAssets = bundle.GetAllAssetNames();
                ModLogger.Debug($"Bundle contains {allAssets.Length} assets:");
                foreach (var asset in allAssets)
                {
                    ModLogger.Debug($"  Asset: {asset}");
                }

                // Try known names, including the name observed in IL2CPP logs. The first
                // successful load wins, so a matching name does not prove route parity.
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
                
                // Probe a few positions after registration. This is a smoke test only;
                // it cannot certify every jail room or agent-specific path.
                VerifyNavMeshAttachment(jailRoot);
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Failed to attach NavMesh data: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Removes the currently registered jail NavMesh data, if its Unity handle is valid.
        /// This does not restore NPC positions or re-enable agents that failed validation.
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
        /// Performs a small smoke test around the jail root and logs how many sample points
        /// can be projected. A passing sample does not guarantee complete connectivity or
        /// successful paths for every NPC agent.
        /// </summary>
        /// <param name="jailRoot">Origin used to choose the four probe points.</param>
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
        /// Checks the registration handle and samples one point near the jail root.
        /// This is intentionally a coarse readiness check, not a route- or room-level
        /// validation; callers still need to validate their own destination paths.
        /// </summary>
        /// <param name="jailRoot">Origin used for the readiness sample.</param>
        /// <returns>True when the handle is valid and the local sample succeeds.</returns>
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
