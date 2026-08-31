using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
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
    /// Manual diagnostic scaffolding for the dynamic parole-officer spawning system.
    /// The methods carry <c>Conditional("DEBUG")</c> and are intended to be
    /// invoked in a live game session rather than an isolated automated runner.
    /// </summary>
    /// <remarks>
    /// These checks log observations and a few expected-count outcomes; they do
    /// not expose an assertion result to a test framework. Several tests mutate
    /// the local player's live parole state, and the harness does not snapshot or
    /// restore that state. Timing is coroutine-based, so a reported result also
    /// depends on the current scene, manager readiness, and game runtime state.
    /// </remarks>
    public class DynamicParoleOfficerSpawningTests : MonoBehaviour
    {
#if !MONO
        /// <summary>
        /// IL2CPP constructor required for an injected <see cref="MonoBehaviour"/>
        /// component to receive its native object pointer.
        /// </summary>
        /// <param name="ptr">Native IL2CPP object pointer supplied by Unity.</param>
        public DynamicParoleOfficerSpawningTests(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Test Configuration

        // Reserved for a future toggle/result aggregation flow; neither flag is
        // currently consulted by the manual test methods.
        private bool testsEnabled = false;
        private bool testResultsLogged = false;

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            // Start only advertises the manual harness. It intentionally does not
            // invoke tests or alter parole/officer state automatically.
            ModLogger.Info("=== Dynamic Parole Officer Spawning Tests ===");
            ModLogger.Info("Tests are scaffolded but not auto-running.");
            ModLogger.Info("Use console commands or UI buttons to run tests.");
        }

        #endregion

        #region Test Methods

        /// <summary>
        /// Test 1: Checks that no active officers are reported while the player
        /// is not on parole.
        /// </summary>
        /// <remarks>
        /// This test skips rather than changing the player's parole state when a
        /// parole record is already active. It observes the manager's current
        /// active count only; it does not prove that no delayed spawn is pending.
        /// </remarks>
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
                var rapSheet = Core.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord != null && rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST SKIPPED: Player is already on parole. Complete parole first.");
                    return;
                }

                // Check officer count
                var manager = Core.ResolveDynamicParoleOfficerManager();
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
        /// Test 2: Starts parole when needed and checks for a supervising officer.
        /// </summary>
        /// <remarks>
        /// When no active parole exists, this test mutates the live player state by
        /// starting a ten-game-minute parole with UI suppressed. The check is
        /// asynchronous and delayed by two scaled seconds; invoking the method
        /// does not synchronously prove that the officer spawned.
        /// </remarks>
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
                var paroleSystem = Core.ResolveParoleManager()?.ParoleSystem;
                if (paroleSystem == null)
                {
                    ModLogger.Error("TEST FAILED: ParoleSystem not available");
                    return;
                }

                // Check if already on parole
                var rapSheet = Core.GetRapSheet(player);
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
                MelonCoroutines.Start(CheckSupervisingOfficerAfterDelay());
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Delayed observer for the supervising-officer spawn check.
        /// </summary>
        /// <returns>Coroutine that waits briefly, then logs the manager's spawn state.</returns>
        /// <remarks>
        /// The delay uses Unity's scaled wait and the result is log-only; no test
        /// framework assertion or cleanup is performed.
        /// </remarks>
        private IEnumerator CheckSupervisingOfficerAfterDelay()
        {
            yield return new WaitForSeconds(2f);

            var manager = Core.ResolveDynamicParoleOfficerManager();
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
        /// Test 3: Observes patrol officers currently reported near the player.
        /// </summary>
        /// <remarks>
        /// The method forces one manager update and then logs each spawned patrol
        /// assignment's nearest-route distance. Despite the historical 200m title,
        /// the current implementation does not assert a distance threshold; it
        /// reports success whenever at least one patrol officer is spawned.
        /// It requires an already-active parole record and does not move the player.
        /// </remarks>
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
                var rapSheet = Core.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST SKIPPED: Player not on parole. Start parole first.");
                    return;
                }

                var manager = Core.ResolveDynamicParoleOfficerManager();
                if (manager == null)
                {
                    ModLogger.Error("TEST FAILED: DynamicParoleOfficerManager not initialized");
                    return;
                }

                // Force update to check spawning
                manager.ForceUpdate();

                // Wait and check
                MelonCoroutines.Start(CheckPatrolOfficersAfterDelay(player));
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Delayed observer for patrol assignments and their route distances.
        /// </summary>
        /// <param name="player">Live local player whose position is sampled after the delay.</param>
        /// <returns>Coroutine that waits three scaled seconds before logging observations.</returns>
        /// <remarks>
        /// A missing manager is treated as an error and stops the observer. The
        /// caller is expected to supply a live, non-null player. A zero spawned
        /// count is informational (the player may be far from every route), not a
        /// hard failure.
        /// </remarks>
        private IEnumerator CheckPatrolOfficersAfterDelay(Player player)
        {
            yield return new WaitForSeconds(3f);

            var manager = Core.ResolveDynamicParoleOfficerManager();
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
        /// Test 4: Attempts to end active parole and checks whether officers are later removed.
        /// </summary>
        /// <remarks>
        /// This test mutates the live parole record by calling
        /// <c>CompleteParoleForPlayer</c> when the parole system is available. It
        /// logs the count before the attempt, then performs an asynchronous count
        /// check after two scaled seconds; it does not restore parole or isolate
        /// unrelated active officers.
        /// </remarks>
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
                var rapSheet = Core.GetRapSheet(player);
                if (rapSheet?.CurrentParoleRecord == null || !rapSheet.CurrentParoleRecord.IsOnParole())
                {
                    ModLogger.Warn("TEST SKIPPED: Player not on parole. Start parole first.");
                    return;
                }

                var manager = Core.ResolveDynamicParoleOfficerManager();
                if (manager == null)
                {
                    ModLogger.Error("TEST FAILED: DynamicParoleOfficerManager not initialized");
                    return;
                }

                int beforeCount = manager.GetActiveOfficerCount();
                ModLogger.Info($"Officers before parole end: {beforeCount}");

                // End parole
                var paroleSystem = Core.ResolveParoleManager()?.ParoleSystem;
                if (paroleSystem != null)
                {
                    paroleSystem.CompleteParoleForPlayer(player);
                }

                // Wait and check
                MelonCoroutines.Start(CheckOfficersDespawnedAfterDelay());
            }
            catch (Exception ex)
            {
                ModLogger.Error($"TEST ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Delayed observer for officer cleanup after parole completion.
        /// </summary>
        /// <returns>Coroutine that waits two scaled seconds, then logs the active count.</returns>
        /// <remarks>
        /// The current observer assumes the manager is still available when the
        /// delay completes and treats any remaining active officer as a failure;
        /// no framework assertion or retry is used.
        /// </remarks>
        private IEnumerator CheckOfficersDespawnedAfterDelay()
        {
            yield return new WaitForSeconds(2f);

            var manager = Core.ResolveDynamicParoleOfficerManager();
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
        /// Test 5: Forces one player-region check and logs that the check ran.
        /// </summary>
        /// <remarks>
        /// The current method does not move the player or synthesize a region
        /// transition, so it cannot prove that a region change causes officer
        /// updates. It is a log-only diagnostic that requires an active parole
        /// record and a ready <c>PlayerLocationTracker</c>.
        /// </remarks>
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
                var rapSheet = Core.GetRapSheet(player);
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
        /// Test 6: Logs the nearest waypoint distance for every patrol route.
        /// </summary>
        /// <remarks>
        /// This is a diagnostic printout rather than an assertion: no expected
        /// coordinates, tolerance, or threshold is checked. Missing routes and
        /// calculation errors are represented by <see cref="float.MaxValue"/>.
        /// </remarks>
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
        /// Starts the manual diagnostic sequence in a separate coroutine.
        /// </summary>
        /// <remarks>
        /// The sequence is DEBUG-conditional and asynchronous. It runs tests 1, 2, 3, 6,
        /// and 5 in that order; the current harness intentionally does not invoke
        /// test 4 from this aggregate entry point. Results are written to the game
        /// log and live player state is not restored between steps.
        /// </remarks>
        [System.Diagnostics.Conditional("DEBUG")]
        public void RunAllTests()
        {
            ModLogger.Info("=== RUNNING ALL TESTS ===");
            MelonCoroutines.Start(RunAllTestsCoroutine());
        }

        /// <summary>
        /// Executes the currently wired manual test sequence with fixed delays.
        /// </summary>
        /// <returns>Coroutine that yields between log-based test invocations.</returns>
        /// <remarks>
        /// Delays use scaled Unity time and are scheduling gaps, not completion
        /// signals for the nested checks started by tests 2 and 3.
        /// </remarks>
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

        /// <summary>
        /// Resolves the current runtime's local player singleton.
        /// </summary>
        /// <returns>The local player, or null when the singleton is unavailable or access fails.</returns>
        /// <remarks>
        /// The compile-time runtime split selects the Mono or IL2CPP player type;
        /// exceptions are logged and converted to a null result for the manual
        /// callers.
        /// </remarks>
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

        /// <summary>
        /// Computes the minimum Euclidean distance from a position to a patrol route waypoint.
        /// </summary>
        /// <param name="assignment">Patrol assignment whose mapped route is queried.</param>
        /// <param name="position">World position from which to measure.</param>
        /// <returns>
        /// Minimum waypoint distance in world units, or <see cref="float.MaxValue"/>
        /// when the route is missing, empty, or cannot be read.
        /// </returns>
        /// <remarks>
        /// This helper measures straight-line distance to discrete waypoints; it
        /// does not measure distance along the route, account for obstacles, or
        /// establish the 200m spawning policy.
        /// </remarks>
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










