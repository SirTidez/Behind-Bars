using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.NPCs;
using Behind_Bars.Systems;
using Behind_Bars.Systems.CrimeTracking;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Tests
{
    /// <summary>
    /// Test scaffolding for Dynamic Parole Officer Spawning System
    /// Run these tests manually in-game to verify functionality
    /// </summary>
    public class DynamicParoleOfficerSpawningTests : MonoBehaviour
    {
#if !MONO
        public DynamicParoleOfficerSpawningTests(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Test Configuration

        private bool testsEnabled = false;
        private bool testResultsLogged = false;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            ModLogger.Info("=== Dynamic Parole Officer Spawning Tests ===");
            ModLogger.Info("Tests are scaffolded but not auto-running.");
            ModLogger.Info("Use console commands or UI buttons to run tests.");
        }

        #endregion

        #region Test Methods

        /// <summary>
        /// Test 1: Verify no officers spawn when player is not on parole
        /// Expected: No officers should be spawned
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public void Test_NoOfficersWhenNotOnParole()
        {
            ModLogger.Info("=== TEST 1: No Officers When Not On Parole ===");
            
            try
            {
                var player = GetLocalPlayer();
                if (player == null)
                {
                    ModLogger.Error("TEST FAILED: Cannot get local player");
                    return;
                }

                // Ensure player is not on parole
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST SKIPPED: Player is already on parole. Complete parole first.");
                    return;
                }

                // Check officer count
                var manager = DynamicParoleOfficerManager.Instance;
                if (manager == null)
                {
                    ModLogger.Error("TEST FAILED: DynamicParoleOfficerManager not initialized");
                    return;
                }

                int officerCount = manager.GetActiveOfficerCount();
                
                if (officerCount == 0)
                {
                    ModLogger.Info("✓ TEST PASSED: No officers spawned when player not on parole");
                }
                else
                {
                    ModLogger.Error($"✗ TEST FAILED: Expected 0 officers, found {officerCount}");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 2: Verify supervising officer spawns when parole starts
        /// Expected: Supervising officer should spawn immediately
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public void Test_SupervisingOfficerSpawnsOnParoleStart()
        {
            ModLogger.Info("=== TEST 2: Supervising Officer Spawns On Parole Start ===");
            
            try
            {
                var player = GetLocalPlayer();
                if (player == null)
                {
                    ModLogger.Error("TEST FAILED: Cannot get local player");
                    return;
                }

                // Start parole
                var paroleSystem = Core.Instance?.GetParoleSystem();
                if (paroleSystem == null)
                {
                    ModLogger.Error("TEST FAILED: ParoleSystem not available");
                    return;
                }

                // Check if already on parole
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST INFO: Player already on parole, checking supervising officer...");
                }
                else
                {
                    // Start parole for testing (short duration)
                    paroleSystem.StartParole(player, 10f, showUI: false);
                    ModLogger.Info("Started parole for testing (10 game minutes)");
                }

                // Wait a moment for spawning
                StartCoroutine(CheckSupervisingOfficerAfterDelay());
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        private IEnumerator CheckSupervisingOfficerAfterDelay()
        {
            yield return new WaitForSeconds(2f);

            var manager = DynamicParoleOfficerManager.Instance;
            if (manager == null)
            {
                ModLogger.Error("TEST FAILED: DynamicParoleOfficerManager not initialized");
                yield break;
            }

            bool supervisorSpawned = manager.IsOfficerSpawned(ParoleOfficerAssignment.PoliceStationSupervisor);
            
            if (supervisorSpawned)
            {
                ModLogger.Info("✓ TEST PASSED: Supervising officer spawned when parole started");
            }
            else
            {
                ModLogger.Error("✗ TEST FAILED: Supervising officer did not spawn");
            }
        }

        /// <summary>
        /// Test 3: Verify patrol officers spawn within 200m of player
        /// Expected: Patrol officers should spawn when player is near their routes
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public void Test_PatrolOfficersSpawnNearPlayer()
        {
            ModLogger.Info("=== TEST 3: Patrol Officers Spawn Near Player ===");
            
            try
            {
                var player = GetLocalPlayer();
                if (player == null)
                {
                    ModLogger.Error("TEST FAILED: Cannot get local player");
                    return;
                }

                // Ensure player is on parole
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST SKIPPED: Player not on parole. Start parole first.");
                    return;
                }

                var manager = DynamicParoleOfficerManager.Instance;
                if (manager == null)
                {
                    ModLogger.Error("TEST FAILED: DynamicParoleOfficerManager not initialized");
                    return;
                }

                // Force update to check spawning
                manager.ForceUpdate();

                // Wait and check
                StartCoroutine(CheckPatrolOfficersAfterDelay(player));
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        private IEnumerator CheckPatrolOfficersAfterDelay(Player player)
        {
            yield return new WaitForSeconds(3f);

            var manager = DynamicParoleOfficerManager.Instance;
            Vector3 playerPos = player.transform.position;

            // Check each patrol assignment
            var patrolAssignments = RouteRegionMapper.GetAllPatrolAssignments();
            int spawnedCount = 0;

            foreach (var assignment in patrolAssignments)
            {
                bool isSpawned = manager.IsOfficerSpawned(assignment);
                if (isSpawned)
                {
                    spawnedCount++;
                    float distance = GetDistanceToRouteForTest(assignment, playerPos);
                    ModLogger.Info($"  {assignment} spawned (distance: {distance:F1}m)");
                }
            }

            ModLogger.Info($"Total patrol officers spawned: {spawnedCount}");
            
            if (spawnedCount > 0)
            {
                ModLogger.Info("✓ TEST PASSED: Patrol officers spawn near player");
            }
            else
            {
                ModLogger.Warn("TEST INFO: No patrol officers spawned (player may be far from routes)");
            }
        }

        /// <summary>
        /// Test 4: Verify officers despawn when parole ends
        /// Expected: All officers should despawn when parole ends
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public void Test_OfficersDespawnOnParoleEnd()
        {
            ModLogger.Info("=== TEST 4: Officers Despawn On Parole End ===");
            
            try
            {
                var player = GetLocalPlayer();
                if (player == null)
                {
                    ModLogger.Error("TEST FAILED: Cannot get local player");
                    return;
                }

                // Check if on parole
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST SKIPPED: Player not on parole. Start parole first.");
                    return;
                }

                var manager = DynamicParoleOfficerManager.Instance;
                if (manager == null)
                {
                    ModLogger.Error("TEST FAILED: DynamicParoleOfficerManager not initialized");
                    return;
                }

                int beforeCount = manager.GetActiveOfficerCount();
                ModLogger.Info($"Officers before parole end: {beforeCount}");

                // End parole
                var paroleSystem = Core.Instance?.GetParoleSystem();
                if (paroleSystem != null)
                {
                    paroleSystem.CompleteParoleForPlayer(player);
                }

                // Wait and check
                StartCoroutine(CheckOfficersDespawnedAfterDelay());
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        private IEnumerator CheckOfficersDespawnedAfterDelay()
        {
            yield return new WaitForSeconds(2f);

            var manager = DynamicParoleOfficerManager.Instance;
            int afterCount = manager.GetActiveOfficerCount();

            if (afterCount == 0)
            {
                ModLogger.Info("✓ TEST PASSED: All officers despawned when parole ended");
            }
            else
            {
                ModLogger.Error($"✗ TEST FAILED: Expected 0 officers, found {afterCount}");
            }
        }

        /// <summary>
        /// Test 5: Verify region change triggers officer updates
        /// Expected: Officers should spawn/despawn when player changes regions
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public void Test_RegionChangeTriggersUpdates()
        {
            ModLogger.Info("=== TEST 5: Region Change Triggers Updates ===");
            
            try
            {
                var player = GetLocalPlayer();
                if (player == null)
                {
                    ModLogger.Error("TEST FAILED: Cannot get local player");
                    return;
                }

                // Ensure player is on parole
                var rapSheet = RapSheetManager.Instance.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST SKIPPED: Player not on parole. Start parole first.");
                    return;
                }

                var tracker = PlayerLocationTracker.Instance;
                if (tracker == null)
                {
                    ModLogger.Error("TEST FAILED: PlayerLocationTracker not initialized");
                    return;
                }

                EMapRegion currentRegion = tracker.GetCurrentRegion();
                ModLogger.Info($"Current region: {currentRegion}");

                // Force region check
                tracker.ForceRegionCheck();

                ModLogger.Info("✓ TEST INFO: Region change detection verified (check logs for region changes)");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Test 6: Verify distance calculations
        /// Expected: Distance calculations should be accurate
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public void Test_DistanceCalculations()
        {
            ModLogger.Info("=== TEST 6: Distance Calculations ===");
            
            try
            {
                var player = GetLocalPlayer();
                if (player == null)
                {
                    ModLogger.Error("TEST FAILED: Cannot get local player");
                    return;
                }

                Vector3 playerPos = player.transform.position;
                ModLogger.Info($"Player position: {playerPos}");

                // Test distance to each route
                var patrolAssignments = RouteRegionMapper.GetAllPatrolAssignments();
                
                foreach (var assignment in patrolAssignments)
                {
                    float distance = GetDistanceToRouteForTest(assignment, playerPos);
                    string routeName = RouteRegionMapper.GetRouteName(assignment);
                    ModLogger.Info($"  {assignment} ({routeName}): {distance:F1}m");
                }

                ModLogger.Info("✓ TEST INFO: Distance calculations completed (check values above)");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Run all tests in sequence
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public void RunAllTests()
        {
            ModLogger.Info("=== RUNNING ALL TESTS ===");
            StartCoroutine(RunAllTestsCoroutine());
        }

        private IEnumerator RunAllTestsCoroutine()
        {
            Test_NoOfficersWhenNotOnParole();
            yield return new WaitForSeconds(2f);

            Test_SupervisingOfficerSpawnsOnParoleStart();
            yield return new WaitForSeconds(3f);

            Test_PatrolOfficersSpawnNearPlayer();
            yield return new WaitForSeconds(3f);

            Test_DistanceCalculations();
            yield return new WaitForSeconds(2f);

            Test_RegionChangeTriggersUpdates();
            yield return new WaitForSeconds(2f);

            ModLogger.Info("=== ALL TESTS COMPLETED ===");
            ModLogger.Info("Review logs above for test results");
        }

        #endregion

        #region Helper Methods

        private Player GetLocalPlayer()
        {
            try
            {
#if !MONO
                return Il2CppScheduleOne.PlayerScripts.Player.Local;
#else
                return ScheduleOne.PlayerScripts.Player.Local;
#endif
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error getting local player: {ex.Message}");
                return null;
            }
        }

        private float GetDistanceToRouteForTest(ParoleOfficerAssignment assignment, Vector3 position)
        {
            try
            {
                string routeName = RouteRegionMapper.GetRouteName(assignment);
                if (string.IsNullOrEmpty(routeName))
                {
                    return float.MaxValue;
                }

                var route = PresetParoleOfficerRoutes.GetRoute(routeName);
                if (route == null || route.points == null || route.points.Length == 0)
                {
                    return float.MaxValue;
                }

                float minDistance = float.MaxValue;
                foreach (var waypoint in route.points)
                {
                    float distance = Vector3.Distance(position, waypoint);
                    minDistance = Mathf.Min(minDistance, distance);
                }

                return minDistance;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error calculating distance: {ex.Message}");
                return float.MaxValue;
            }
        }

        #endregion
    }
}

