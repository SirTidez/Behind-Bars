using System;
using System.Collections.Generic;
using System.Linq;
using Behind_Bars.Helpers;
using static Behind_Bars.Systems.NPCs.ParoleOfficerBehavior;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Maps EMapRegion enum values to parole officer routes and assignments.
    /// Provides static methods for region-to-assignment mapping.
    /// </summary>
    public static class RouteRegionMapper
    {
        #region Static Mappings

        /// <summary>
        /// Mapping dictionary: Region -> List of assignments that patrol this region.
        /// Values are rebuilt only by a successful Initialize call.
        /// </summary>
        private static Dictionary<EMapRegion, List<ParoleOfficerAssignment>> regionToAssignments;

        /// <summary>
        /// Snapshot of AssignmentToRouteMap copied during Initialize. Changes to
        /// the source map afterward are not reflected here.
        /// </summary>
        private static Dictionary<ParoleOfficerAssignment, string> assignmentToRoute;

        /// <summary>
        /// True only after both mapping dictionaries are populated and LogMappings
        /// has been reached without an exception.
        /// </summary>
        private static bool isInitialized = false;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the route/region snapshots once. Exceptions are caught and
        /// leave isInitialized false; no fallback dictionaries are installed, so
        /// a caller after repeated failure may still encounter a null mapping.
        /// </summary>
        public static void Initialize()
        {
            if (isInitialized)
            {
                ModLogger.Debug("RouteRegionMapper: Already initialized");
                return;
            }

            try
            {
                // Initialize assignment-to-route mapping from existing ParoleOfficerBehavior mapping
                assignmentToRoute = new Dictionary<ParoleOfficerAssignment, string>();
                foreach (var kvp in ParoleOfficerBehavior.AssignmentToRouteMap)
                {
                    assignmentToRoute[kvp.Key] = kvp.Value;
                }

                // Initialize region-to-assignments mapping
                regionToAssignments = new Dictionary<EMapRegion, List<ParoleOfficerAssignment>>();

                // Downtown/Police Station region
                regionToAssignments[EMapRegion.Downtown] = new List<ParoleOfficerAssignment>
                {
                    ParoleOfficerAssignment.PoliceStationPatrol
                };

                // Uptown/East region
                regionToAssignments[EMapRegion.Uptown] = new List<ParoleOfficerAssignment>
                {
                    ParoleOfficerAssignment.UptownPatrol
                };

                // Westside region
                regionToAssignments[EMapRegion.Westside] = new List<ParoleOfficerAssignment>
                {
                    ParoleOfficerAssignment.WestsidePatrol
                };

                // Northtown region
                regionToAssignments[EMapRegion.Northtown] = new List<ParoleOfficerAssignment>
                {
                    ParoleOfficerAssignment.NorthtownPatrol
                };

                // Docks/Canal region
                regionToAssignments[EMapRegion.Docks] = new List<ParoleOfficerAssignment>
                {
                    ParoleOfficerAssignment.DocksPatrol
                };

                // Note: PoliceStationSupervisor is not region-based, always spawned when on parole

                isInitialized = true;
                ModLogger.Debug("RouteRegionMapper: Initialized successfully");
                LogMappings();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"RouteRegionMapper: Error during initialization: {ex.Message}");
                isInitialized = false;
            }
        }

        /// <summary>
        /// Logs the successfully built region-to-assignment snapshot.
        /// </summary>
        private static void LogMappings()
        {
            ModLogger.Debug("=== RouteRegionMapper Mappings ===");
            foreach (var kvp in regionToAssignments)
            {
                string assignments = string.Join(", ", kvp.Value);
                ModLogger.Debug($"  {kvp.Key}: [{assignments}]");
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Gets a copy of the assignments in the initialized region snapshot.
        /// An unknown region returns an empty list; initialization failure is not
        /// converted into a usable fallback map.
        /// </summary>
        /// <param name="region">The map region</param>
        /// <returns>List of assignments, or empty list if region not found</returns>
        public static List<ParoleOfficerAssignment> GetAssignmentsForRegion(EMapRegion region)
        {
            EnsureInitialized();

            if (regionToAssignments.TryGetValue(region, out var assignments))
            {
                return new List<ParoleOfficerAssignment>(assignments);
            }

            ModLogger.Debug($"RouteRegionMapper: No assignments found for region {region}");
            return new List<ParoleOfficerAssignment>();
        }

        /// <summary>
        /// Gets the route name from the initialization-time assignment snapshot.
        /// A missing assignment returns null.
        /// </summary>
        /// <param name="assignment">The parole officer assignment</param>
        /// <returns>Route name, or null if assignment not found</returns>
        public static string GetRouteName(ParoleOfficerAssignment assignment)
        {
            EnsureInitialized();

            if (assignmentToRoute.TryGetValue(assignment, out var routeName))
            {
                return routeName;
            }

            ModLogger.Debug($"RouteRegionMapper: No route found for assignment {assignment}");
            return null;
        }

        /// <summary>
        /// Checks the initialized region snapshot for an assignment. The
        /// supervising officer is explicitly non-region-based.
        /// </summary>
        /// <param name="assignment">The parole officer assignment</param>
        /// <param name="region">The map region</param>
        /// <returns>True if assignment patrols the region</returns>
        public static bool AssignmentPatrolsRegion(ParoleOfficerAssignment assignment, EMapRegion region)
        {
            EnsureInitialized();

            // Supervising officer is not region-based
            if (assignment == ParoleOfficerAssignment.PoliceStationSupervisor)
            {
                return false;
            }

            if (regionToAssignments.TryGetValue(region, out var assignments))
            {
                return assignments.Contains(assignment);
            }

            return false;
        }

        /// <summary>
        /// Returns all route assignments in the initialization-time route
        /// snapshot, excluding the supervising officer.
        /// </summary>
        /// <returns>List of all patrol assignments</returns>
        public static List<ParoleOfficerAssignment> GetAllPatrolAssignments()
        {
            EnsureInitialized();

            return assignmentToRoute.Keys
                .Where(a => a != ParoleOfficerAssignment.PoliceStationSupervisor)
                .ToList();
        }

        /// <summary>
        /// Classifies an assignment without consulting the initialized mappings.
        /// </summary>
        /// <param name="assignment">The assignment to check</param>
        /// <returns>True if it's a patrol assignment</returns>
        public static bool IsPatrolAssignment(ParoleOfficerAssignment assignment)
        {
            return assignment != ParoleOfficerAssignment.PoliceStationSupervisor;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Attempts lazy initialization when needed. It logs and retries after a
        /// failed initialization but does not validate that either dictionary is
        /// non-null before the caller continues.
        /// </summary>
        private static void EnsureInitialized()
        {
            if (!isInitialized)
            {
                ModLogger.Warn("RouteRegionMapper: Not initialized, initializing now");
                Initialize();
            }
        }

        #endregion
    }
}





