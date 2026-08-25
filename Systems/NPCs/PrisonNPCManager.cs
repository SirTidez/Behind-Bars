using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems.Jail;
using MelonLoader;
using BBHelpers = Behind_Bars.Helpers.Helpers;


#if !MONO
using Il2CppFishNet;
using Il2CppFishNet.Managing;
using Il2CppFishNet.Managing.Object;
using Il2CppFishNet.Object;
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne;
#else
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Object;
using ScheduleOne.NPCs;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Dialogue;
using ScheduleOne;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Manages prison NPCs with customizable appearances and behaviors
    /// Enhanced for IL2CPP compatibility and intake coordination
    /// </summary>
    public class PrisonNPCManager : MonoBehaviour
    {
#if !MONO
        public PrisonNPCManager(System.IntPtr ptr) : base(ptr) { }
#endif

        public static PrisonNPCManager Instance { get; private set; }
        private Coroutine? npcInitializationCoroutine;
        private readonly List<Coroutine> guardDialogueInitializationCoroutines = new();
        
        // NPC spawning status
        public bool IsSpawningComplete { get; private set; } = false;

        // NPC tracking
        private List<PrisonGuard> activeGuards = new List<PrisonGuard>();
        private List<PrisonInmate> activeInmates = new List<PrisonInmate>();
        
        // Guard coordination for IL2CPP-safe management
        private List<GuardBehavior> registeredGuards = new List<GuardBehavior>();
        private List<ParoleOfficerBehavior> registeredParoleOfficers = new List<ParoleOfficerBehavior>();
        private List<ReleaseOfficerBehavior> registeredReleaseOfficers = new List<ReleaseOfficerBehavior>();
        private GuardBehavior intakeOfficer = null;
        private ParoleOfficerBehavior paroleSupervisor = null;
        private bool isPatrolInProgress = false;
        private float nextPatrolTime = 0f;
        private readonly float PATROL_COOLDOWN = 300f; // 5 minutes between coordinated patrols
        
        // Enhanced spawn configuration
        public int maxGuards = 5; // 2 guard-room posts, intake/release, and the day-room patrol
        public int maxParoleOfficers = 6; // 1 supervising (stationary) + 5 patrol officers
        public int maxInmates = 8;
        
        // Spawn areas (will be set by JailController)
        private Transform[] guardSpawnPoints;
        private Transform[] inmateSpawnPoints;
        
        // Guard assignment tracking
        private readonly GuardBehavior.GuardAssignment[] guardAssignments = {
            GuardBehavior.GuardAssignment.GuardRoom0,
            GuardBehavior.GuardAssignment.GuardRoom1,
            GuardBehavior.GuardAssignment.Booking0,
            GuardBehavior.GuardAssignment.Booking1
        };
        

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                ModLogger.Debug("PrisonNPCManager initialized");
            }
            else
            {
                Destroy(this);
            }
        }

        private void Start()
        {
            // Initialize spawn points from JailController
            InitializeSpawnPoints();

            // Start NPC spawning process
            npcInitializationCoroutine = MelonCoroutines.Start(InitializeNPCs()) as Coroutine;
        }

        /// <summary>
        /// Stops scene-owned NPC initialization before the jail hierarchy unloads.
        /// Individual NPCs remain Unity scene objects and will receive their normal lifecycle callbacks.
        /// </summary>
        public void CancelForSceneExit()
        {
            if (npcInitializationCoroutine != null)
            {
                MelonCoroutines.Stop(npcInitializationCoroutine);
                npcInitializationCoroutine = null;
            }

            foreach (var coroutine in guardDialogueInitializationCoroutines)
            {
                if (coroutine != null)
                {
                    MelonCoroutines.Stop(coroutine);
                }
            }

            guardDialogueInitializationCoroutines.Clear();

            IsSpawningComplete = false;
            isPatrolInProgress = false;
            if (DynamicParoleOfficerManager.Instance != null)
            {
                UnityEngine.Object.Destroy(DynamicParoleOfficerManager.Instance.gameObject);
            }
            ModLogger.Debug("PrisonNPCManager cancelled scene initialization");
        }

        private void OnDestroy()
        {
            CancelForSceneExit();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Initialize spawn points from the jail controller
        /// </summary>
        private void InitializeSpawnPoints()
        {
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("JailController not found - cannot initialize spawn points");
                return;
            }

            // Collect guard spawn points from both areas
            var allGuardSpawns = new List<Transform>();
            
            // Add guard room spawns
            if (jailController.guardRoom.guardSpawns != null)
            {
                allGuardSpawns.AddRange(jailController.guardRoom.guardSpawns);
                ModLogger.Debug($"Found {jailController.guardRoom.guardSpawns.Count} guard room spawn points");
            }
            
            // Add booking spawns
            if (jailController.booking.guardSpawns != null)
            {
                allGuardSpawns.AddRange(jailController.booking.guardSpawns);
                ModLogger.Debug($"Found {jailController.booking.guardSpawns.Count} booking spawn points");
            }
            
            guardSpawnPoints = allGuardSpawns.ToArray();
            ModLogger.Debug($"Total guard spawn points available: {guardSpawnPoints.Length}");

            // Create inmate spawn points near the jail center
            CreateInmateSpawnPoints(jailController);

        }

        /// <summary>
        /// Create spawn points for inmates around the jail area
        /// </summary>
        private void CreateInmateSpawnPoints(JailController jailController)
        {
            var jailCenter = jailController.transform.position;
            var spawnPoints = new List<Transform>();
            
            // Create spawn points in a circle around the jail center
            int numPoints = 6;
            float radius = 8f;
            
            for (int i = 0; i < numPoints; i++)
            {
                float angle = (360f / numPoints) * i * Mathf.Deg2Rad;
                Vector3 spawnPos = jailCenter + new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius
                );
                spawnPos.y = jailCenter.y; // Keep same Y level as jail
                
                // Create a spawn point GameObject
                GameObject spawnPoint = new GameObject($"InmateSpawnPoint_{i}");
                spawnPoint.transform.position = spawnPos;
                spawnPoint.transform.SetParent(transform);
                spawnPoints.Add(spawnPoint.transform);
            }
            
            inmateSpawnPoints = spawnPoints.ToArray();
            ModLogger.Debug($"Created {inmateSpawnPoints.Length} inmate spawn points");
        }

        /// <summary>
        /// Initialize NPCs in the prison
        /// </summary>
        private IEnumerator InitializeNPCs()
        {
            ModLogger.Debug("Starting prison NPC initialization...");
            
            // Wait a bit for everything to be ready
            yield return new WaitForSeconds(2f);
            
            // Spawn guards first
            yield return SpawnGuards();
            
            // DynamicParoleOfficerManager is the sole owner of parole-officer creation.
            
            // Initialize dynamic parole officer manager
            InitializeDynamicParoleOfficerManager();
            
            // Then spawn inmates
            yield return SpawnInmates();
            
            // Mark spawning as complete
            IsSpawningComplete = true;
            ModLogger.Debug("✓ Prison NPC initialization completed");
        }

        /// <summary>
        /// Initialize the dynamic parole officer manager
        /// </summary>
        private void InitializeDynamicParoleOfficerManager()
        {
            try
            {
                ModLogger.Info("Initializing DynamicParoleOfficerManager...");
                
                // Create GameObject for the manager
                GameObject managerObject = new GameObject("DynamicParoleOfficerManager");
                managerObject.transform.SetParent(transform); // Parent to NPC manager for organization
                
                // Add the component
                var manager = BBHelpers.AddComponentSafe<DynamicParoleOfficerManager>(managerObject);
                if (manager == null)
                {
                    ModLogger.Error("Failed to add DynamicParoleOfficerManager component");
                    UnityEngine.Object.Destroy(managerObject);
                    return;
                }
                
                // Initialize it
                manager.Initialize();
                
                ModLogger.Info("✓ DynamicParoleOfficerManager initialized");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error initializing DynamicParoleOfficerManager: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawn the four staffed posts plus a dedicated day-room patrol officer.
        /// </summary>
        private IEnumerator SpawnGuards()
        {
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("JailController not found - cannot spawn guards");
                yield break;
            }

            ModLogger.Debug("Spawning four staffed guards and one dedicated day-room patrol guard...");
            
            // The staffed positions have fixed responsibilities. Do not use maxGuards
            // as an array bound: existing saves may still hold the previous value.
            for (int i = 0; i < guardAssignments.Length; i++)
            {
                var assignment = guardAssignments[i];
                Transform spawnPoint = GetSpawnPointForAssignment(assignment, jailController);

                if (spawnPoint == null)
                {
                    ModLogger.Error($"Could not find spawn point for assignment {assignment}");
                    continue;
                }

                // Give specific names to Booking guards (Intake & Release Officers)
                string guardName = GetGuardNameForAssignment(assignment, i);

                var guard = SpawnGuard(spawnPoint.position, guardName, $"G{1000 + i}", assignment);
                if (guard != null)
                {
                    activeGuards.Add(guard);
                    ModLogger.Debug($"✓ Spawned guard {guard.badgeNumber} at {assignment} ({spawnPoint.name})");
                }
                else
                {
                    ModLogger.Error($"Failed to spawn canonical guard for assignment {assignment}");
                }

                // Small delay between spawns
                yield return new WaitForSeconds(0.8f);
            }

            yield return SpawnDayRoomPatrolGuard(jailController);
            
            ModLogger.Debug($"✓ Spawned {activeGuards.Count} guards with assignments");
        }

        /// <summary>
        /// Creates a distinct day-room patrol guard using the authored jail patrol circuit.
        /// These markers sit on the walkable circulation paths; cell door transforms do not.
        /// </summary>
        private IEnumerator SpawnDayRoomPatrolGuard(JailController jailController)
        {
            Vector3[] patrolRoute = BuildDayRoomPatrolRoute(jailController, out Vector3[] inspectionTargets);
            if (patrolRoute.Length < 2)
            {
                ModLogger.Error("[NPC Spawn] Day-room patrol guard was not spawned: fewer than two authored jail patrol points were found");
                yield break;
            }

            string officerName = $"Day Room Officer {GetRandomOfficerName()}";
            var patrolGuard = SpawnGuard(
                patrolRoute[0],
                officerName,
                "G1004",
                GuardBehavior.GuardAssignment.DayRoomPatrol);

            if (patrolGuard == null)
            {
                ModLogger.Error("[NPC Spawn] Failed to spawn canonical day-room patrol guard");
                yield break;
            }

            activeGuards.Add(patrolGuard);
            var guardBehavior = BBHelpers.GetComponentSafe<GuardBehavior>(patrolGuard.gameObject);
            if (guardBehavior == null)
            {
                ModLogger.Error("[NPC Spawn] Day-room patrol guard is missing GuardBehavior; destroying invalid guard");
                UnityEngine.Object.Destroy(patrolGuard.gameObject);
                activeGuards.Remove(patrolGuard);
                yield break;
            }

            guardBehavior.AssignDayRoomPatrolRoute(patrolRoute, inspectionTargets);
            guardBehavior.AssignToRole(GuardBehavior.GuardRole.PatrolGuard);
            ModLogger.Info($"✓ Spawned day-room patrol guard {officerName} with {patrolRoute.Length} authored jail patrol points");
            yield return new WaitForSeconds(0.8f);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private Vector3[] BuildDayRoomPatrolRoute(JailController jailController, out Vector3[] inspectionTargets)
        {
            if (jailController == null)
            {
                inspectionTargets = Array.Empty<Vector3>();
                return Array.Empty<Vector3>();
            }

            // This ordering is the authored clockwise circuit in Jail.prefab: lower-right,
            // lower-left, upstairs-left, upstairs-right, then back down the right stair.
            // It deliberately follows the corridor points instead of positioning the agent
            // on cell-door transforms, which can be inside bars or off the runtime NavMesh.
            string[] markerOrder =
            {
                "Patrol_Laundry",
                "Patrol_Kitchen",
                "Patrol_Lower_Left",
                "Patrol_Upper_Left",
                "Patrol_Upper_Right"
            };

            var route = new List<Vector3>(markerOrder.Length);
            var lookTargets = new List<Vector3>(markerOrder.Length);
            foreach (string markerName in markerOrder)
            {
                Transform patrolMarker = FindJailPatrolMarker(jailController, markerName);
                if (patrolMarker == null)
                {
                    ModLogger.Warn($"[NPC Spawn] Authored day-room patrol marker '{markerName}' was not found");
                    continue;
                }

                route.Add(patrolMarker.position);
                lookTargets.Add(GetDayRoomCellInteriorTarget(jailController, patrolMarker.position));
            }

            inspectionTargets = lookTargets.ToArray();
            if (route.Count > 0)
            {
                ModLogger.Debug($"Day-room patrol route loaded {route.Count} authored jail markers with matched cell-interior look targets");
            }

            return route.ToArray();
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static Transform FindJailPatrolMarker(JailController jailController, string markerName)
        {
            foreach (Transform patrolPoint in jailController.patrolPoints)
            {
                if (patrolPoint != null && patrolPoint.name == markerName)
                {
                    return patrolPoint;
                }
            }

            return null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static Vector3 GetDayRoomCellInteriorTarget(JailController jailController, Vector3 patrolPoint)
        {
            Transform nearestInterior = null;
            float nearestDistanceSquared = float.MaxValue;
            foreach (var cell in jailController.cells)
            {
                Transform interior = cell?.cellBounds ?? cell?.cellTransform;
                if (interior == null)
                {
                    continue;
                }

                float distanceSquared = (interior.position - patrolPoint).sqrMagnitude;
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestInterior = interior;
                }
            }

            // The actual jail prefab always provides a cell root.  Keep the marker position
            // as a safe fallback for a malformed asset rather than pointing the guard away.
            return nearestInterior != null ? nearestInterior.position : patrolPoint;
        }
        
        /// <summary>
        /// Get the spawn point for a specific guard assignment
        /// </summary>
        private Transform GetSpawnPointForAssignment(GuardBehavior.GuardAssignment assignment, JailController jailController)
        {
            switch (assignment)
            {
                case GuardBehavior.GuardAssignment.GuardRoom0:
                    return jailController.guardRoom.guardSpawns.Count > 0 ? jailController.guardRoom.guardSpawns[0] : null;
                case GuardBehavior.GuardAssignment.GuardRoom1:
                    return jailController.guardRoom.guardSpawns.Count > 1 ? jailController.guardRoom.guardSpawns[1] : null;
                case GuardBehavior.GuardAssignment.Booking0:
                    return jailController.booking.guardSpawns.Count > 0 ? jailController.booking.guardSpawns[0] : null;
                case GuardBehavior.GuardAssignment.Booking1:
                    return jailController.booking.guardSpawns.Count > 1 ? jailController.booking.guardSpawns[1] : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Get specific names for guards based on their assignment
        /// </summary>
        private string GetGuardNameForAssignment(GuardBehavior.GuardAssignment assignment, int index)
        {
            switch (assignment)
            {
                case GuardBehavior.GuardAssignment.Booking0:
                    return $"Intake Officer {GetRandomOfficerName()}";
                case GuardBehavior.GuardAssignment.Booking1:
                    return $"Release Officer {GetRandomOfficerName()}";
                default:
                    return $"Officer {index + 1}"; // Guard Room officers get generic names
            }
        }

        /// <summary>
        /// Get a random name for an officer
        /// </summary>
        private string GetRandomOfficerName()
        {
            var names = new string[]
            {
                "Billy", "Kelly", "Johnson", "Martinez", "Thompson", "Garcia",
                "Rodriguez", "Wilson", "Anderson", "Davis", "Miller", "Moore",
                "Jackson", "Taylor", "Lee", "Harris", "Clark", "Lewis",
                "Walker", "Hall", "Allen", "Young", "King", "Wright"
            };
            return names[UnityEngine.Random.Range(0, names.Length)];
        }

        /// <summary>
        /// Spawn prison inmates in random cells using CellAssignmentManager
        /// </summary>
        private IEnumerator SpawnInmates()
        {
            var cellManager = Core.ResolveCellAssignmentManager();
            if (cellManager == null)
            {
                ModLogger.Error("CellAssignmentManager not available - cannot spawn inmates in cells");
                yield break;
            }

            ModLogger.Debug($"Spawning up to {maxInmates} inmates in random cells...");

            int inmatesSpawned = 0;
            int maxAttempts = maxInmates * 3; // Allow some failed attempts
            int attempts = 0;

            while (inmatesSpawned < maxInmates && attempts < maxAttempts)
            {
                attempts++;

                // Random inmate details
                string firstName = GetRandomInmateFirstName();
                string crimeType = GetRandomCrimeType();
                string inmateId = $"Prisoner_{inmatesSpawned+1:D3}";

                // Try to assign inmate to a random available cell
                int assignedCell = cellManager.AssignNPCToCell(inmateId, firstName);
                if (assignedCell == -1)
                {
                    ModLogger.Warn($"No available cell for inmate {firstName} - attempt {attempts}");
                    yield return new WaitForSeconds(0.1f);
                    continue;
                }

                // Get the actual cell transform directly from JailController
                Vector3 spawnPosition = GetCellSpawnPosition(assignedCell);
                if (spawnPosition == Vector3.zero)
                {
                    ModLogger.Error($"Could not get valid spawn position for cell {assignedCell}");
                    // Release the cell assignment since spawning failed
                    cellManager.ReleaseNPCFromCell(inmateId, firstName);
                    continue;
                }

                // Validate spawn position is not at jail center (common failure point)
                var jailCenter = Core.JailController?.transform.position ?? Vector3.zero;
                float distanceFromJailCenter = Vector3.Distance(spawnPosition, jailCenter);
                if (distanceFromJailCenter < 2f)
                {
                    ModLogger.Warn($"Spawn position for {firstName} is too close to jail center ({distanceFromJailCenter:F2}m) - may indicate spawn failure");
                }

                ModLogger.Debug($"Spawning {firstName} in cell {assignedCell} at position {spawnPosition} (distance from jail center: {distanceFromJailCenter:F2}m)");

                // Spawn the inmate
                var inmate = SpawnInmate(spawnPosition, firstName, inmateId, crimeType);
                if (inmate != null)
                {
                    activeInmates.Add(inmate);
                    inmate.assignedCell = assignedCell; // Store cell assignment

                    // Add InmateBehavior for random cell movement
                    var inmateBehavior = BBHelpers.AddComponentSafe<InmateBehavior>(inmate.gameObject);
                    if (inmateBehavior != null)
                    {
                        inmateBehavior.SetCellNumber(assignedCell);

                        // Vary behavior based on crime type
                        switch (crimeType?.ToLower())
                        {
                            case "assault":
                            case "battery":
                            case "violent":
                                // Violent criminals pace more aggressively
                                inmateBehavior.SetPacingBehavior(true);
                                inmateBehavior.SetMovementSpeed(2.0f);
                                break;

                            case "fraud":
                            case "theft":
                                // White collar/theft criminals are calmer
                                inmateBehavior.SetPacingBehavior(false);
                                inmateBehavior.SetMovementSpeed(1.2f);
                                break;

                            case "drug possession":
                            case "dui":
                                // Drug-related inmates are erratic
                                inmateBehavior.SetPacingBehavior(UnityEngine.Random.Range(0f, 1f) > 0.5f);
                                inmateBehavior.SetMovementSpeed(UnityEngine.Random.Range(1.0f, 2.5f));
                                break;

                            default:
                                // Random behavior for others
                                break;
                        }

                        ModLogger.Debug($"Added InmateBehavior to {inmateId} for cell {assignedCell}");
                    }

                    ModLogger.Debug($"✓ Spawned inmate {inmateId} ({crimeType}) in cell {assignedCell}");
                    inmatesSpawned++;
                }
                else
                {
                    ModLogger.Error($"Failed to spawn inmate {firstName} in cell {assignedCell}");
                    // Release the cell assignment since spawning failed
                    cellManager.ReleaseNPCFromCell(inmateId, firstName);
                }

                // Small delay between spawns
                yield return new WaitForSeconds(0.5f);
            }

            ModLogger.Debug($"✓ Spawned {inmatesSpawned} inmates in cells randomly");

            // Log cell assignment distribution for debugging
            if (cellManager != null)
            {
                cellManager.LogCellAssignments();
            }
        }

        /// <summary>
        /// Get count of inmates currently in a cell
        /// </summary>
        private int GetInmatesInCellCount(int cellNumber)
        {
            int count = 0;
            foreach (var inmate in activeInmates)
            {
                if (inmate != null && inmate.assignedCell == cellNumber)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Get proper spawn position for a specific cell using bounds calculation
        /// </summary>
        private Vector3 GetCellSpawnPosition(int cellNumber)
        {
            try
            {
                var jailController = Core.JailController;
                if (jailController == null)
                {
                    ModLogger.Error("JailController not available for cell positioning");
                    return Vector3.zero;
                }

                if (cellNumber < 0 || cellNumber >= jailController.cells.Count)
                {
                    ModLogger.Error($"Invalid cell number: {cellNumber} (total cells: {jailController.cells.Count})");
                    return Vector3.zero;
                }

                var cell = jailController.cells[cellNumber];
                if (cell == null)
                {
                    ModLogger.Error($"Cell {cellNumber} is null");
                    return Vector3.zero;
                }

                // First priority: Use cell spawn points if available
                if (cell.spawnPoints != null && cell.spawnPoints.Count > 0)
                {
                    int spawnIndex = UnityEngine.Random.Range(0, cell.spawnPoints.Count);
                    var spawnPoint = cell.spawnPoints[spawnIndex];
                    if (spawnPoint != null)
                    {
                        ModLogger.Debug($"Using spawn point {spawnIndex} in cell {cellNumber}: {spawnPoint.position}");
                        return spawnPoint.position;
                    }
                }

                // Second priority: Use cell bounds for proper positioning within cell
                if (cell.cellBounds != null)
                {
                    var boxCollider = cell.cellBounds.GetComponent<BoxCollider>();
                    if (boxCollider != null)
                    {
                        var bounds = boxCollider.bounds;
                        Vector3 boundsCenter = bounds.center;
                        Vector3 boundsSize = bounds.size;

                        // Get number of inmates already in cell to add offset
                        int inmatesInCell = GetInmatesInCellCount(cellNumber);

                        // Calculate offset based on how many are already there
                        float offsetX = 0f;
                        float offsetZ = 0f;
                        if (inmatesInCell > 0)
                        {
                            // Spread inmates around the cell
                            float angle = (inmatesInCell * 120f) % 360f; // 120 degree separation
                            offsetX = Mathf.Cos(angle * Mathf.Deg2Rad) * 1.0f;
                            offsetZ = Mathf.Sin(angle * Mathf.Deg2Rad) * 1.0f;
                        }

                        // Calculate position within cell bounds with offset
                        Vector3 spawnPos = new Vector3(
                            boundsCenter.x + offsetX + UnityEngine.Random.Range(-0.2f, 0.2f),
                            boundsCenter.y + 0.1f, // Slight elevation above floor
                            boundsCenter.z + offsetZ + UnityEngine.Random.Range(-0.2f, 0.2f)
                        );

                        ModLogger.Debug($"Using cell bounds for cell {cellNumber}: {spawnPos} (bounds: {bounds})");
                        return spawnPos;
                    }
                }

                // Third priority: Use cell transform with BoxCollider bounds
                if (cell.cellTransform != null)
                {
                    var boxCollider = cell.cellTransform.GetComponent<BoxCollider>();
                    if (boxCollider != null)
                    {
                        var bounds = boxCollider.bounds;
                        Vector3 boundsCenter = bounds.center;
                        Vector3 boundsSize = bounds.size;

                        // Get number of inmates already in cell to add offset
                        int inmatesInCell = GetInmatesInCellCount(cellNumber);

                        // Calculate offset based on how many are already there
                        float offsetX = 0f;
                        float offsetZ = 0f;
                        if (inmatesInCell > 0)
                        {
                            float angle = (inmatesInCell * 120f) % 360f;
                            offsetX = Mathf.Cos(angle * Mathf.Deg2Rad) * 1.0f;
                            offsetZ = Mathf.Sin(angle * Mathf.Deg2Rad) * 1.0f;
                        }

                        Vector3 spawnPos = new Vector3(
                            boundsCenter.x + offsetX + UnityEngine.Random.Range(-0.2f, 0.2f),
                            boundsCenter.y + 0.1f,
                            boundsCenter.z + offsetZ + UnityEngine.Random.Range(-0.2f, 0.2f)
                        );

                        ModLogger.Debug($"Using cell transform bounds for cell {cellNumber}: {spawnPos}");
                        return spawnPos;
                    }
                    else
                    {
                        // Fallback to cell transform position with offset for multiple inmates
                        Vector3 cellPos = cell.cellTransform.position;

                        // Get number of inmates already in cell
                        int inmatesInCell = GetInmatesInCellCount(cellNumber);
                        float angle = (inmatesInCell * 120f) % 360f;
                        float offsetX = inmatesInCell > 0 ? Mathf.Cos(angle * Mathf.Deg2Rad) * 0.8f : 0f;
                        float offsetZ = inmatesInCell > 0 ? Mathf.Sin(angle * Mathf.Deg2Rad) * 0.8f : 0f;

                        cellPos += new Vector3(
                            offsetX + UnityEngine.Random.Range(-0.2f, 0.2f),
                            0.1f,
                            offsetZ + UnityEngine.Random.Range(-0.2f, 0.2f)
                        );
                        ModLogger.Debug($"Using cell transform position for cell {cellNumber}: {cellPos}");
                        return cellPos;
                    }
                }

                ModLogger.Error($"Could not determine position for cell {cellNumber} - no valid transform or bounds found");
                return Vector3.zero;
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error getting cell spawn position for cell {cellNumber}: {e.Message}");
                return Vector3.zero;
            }
        }

        /// <summary>
        /// Spawn a single guard using BaseNPC prefab (ID 182)
        /// </summary>
        public PrisonGuard SpawnGuard(Vector3 position, string firstName = "Officer", string badgeNumber = "", GuardBehavior.GuardAssignment assignment = GuardBehavior.GuardAssignment.GuardRoom0)
        {
            try
            {
                var role = assignment == GuardBehavior.GuardAssignment.Booking0
                    ? BaseNPCSpawner.NPCRole.IntakeOfficer
                    : BaseNPCSpawner.NPCRole.PrisonGuard;
                if (!BaseNPCSpawner.TryCreatePreparedNativeNPC(role, firstName, "Guard", out var guardObject))
                {
                    ModLogger.Error($"[NPC Spawn] Failed to prepare canonical guard for {assignment}");
                    return null;
                }

                // Generate badge if needed
                if (string.IsNullOrEmpty(badgeNumber))
                {
                    badgeNumber = GenerateBadgeNumber();
                }

                var npcComponent = guardObject.GetComponent<NPC>() ?? guardObject.GetComponentInChildren<NPC>(true);
                FixNPCAppearance(guardObject, "guard");

                var guardBehavior = BBHelpers.AddComponentSafe<GuardBehavior>(guardObject);
                if (guardBehavior == null)
                {
                    ModLogger.Error("[NPC Spawn] GuardBehavior injection failed; refusing static fallback guard");
                    UnityEngine.Object.Destroy(guardObject);
                    return null;
                }

                // Add audio system components for voice commands
                AddAudioSystemToGuard(guardObject, npcComponent, true);

                var prisonGuard = BBHelpers.AddComponentSafe<PrisonGuard>(guardObject);
                if (prisonGuard == null)
                {
                    ModLogger.Error("[NPC Spawn] Failed to add PrisonGuard wrapper");
                    UnityEngine.Object.Destroy(guardObject);
                    return null;
                }
                prisonGuard.Initialize(badgeNumber, firstName, assignment);

                if (!BaseNPCSpawner.TryFinalizePreparedNativeNPC(guardObject, position))
                {
                    return null;
                }

                ModLogger.Debug($"[NPC Spawn] Spawned canonical guard {firstName} ({assignment})");
                return prisonGuard;
            }
            catch (Exception e)
            {
                ModLogger.Error($"[NPC Spawn] Error spawning guard: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Spawn a single parole officer using BaseNPC prefab (ID 182)
        /// </summary>
        public ParoleOfficer SpawnParoleOfficer(Vector3 position, string firstName = "Officer", string badgeNumber = "", ParoleOfficerBehavior.ParoleOfficerAssignment assignment = ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor)
        {
            try
            {
                bool isSupervisingOfficer = assignment == ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor;
                if (!BaseNPCSpawner.TryCreatePreparedNativeNPC(
                        BaseNPCSpawner.NPCRole.ParoleOfficer,
                        firstName,
                        isSupervisingOfficer ? string.Empty : "Parole Officer",
                        out var paroleOfficerObject))
                {
                    ModLogger.Error($"[NPC Spawn] Failed to prepare canonical parole officer for {assignment}");
                    return null;
                }

                // Generate badge if needed
                if (string.IsNullOrEmpty(badgeNumber))
                {
                    badgeNumber = GenerateBadgeNumber();
                }

                var npcComponent = paroleOfficerObject.GetComponent<NPC>() ?? paroleOfficerObject.GetComponentInChildren<NPC>(true);
                FixParoleOfficerAppearance(paroleOfficerObject, firstName);

                ParoleOfficerBehavior paroleBehavior = BBHelpers.AddComponentSafe<ParoleOfficerBehavior>(paroleOfficerObject);
                if (paroleBehavior == null)
                {
                    ModLogger.Error("[NPC Spawn] Failed to add canonical ParoleOfficerBehavior");
                    UnityEngine.Object.Destroy(paroleOfficerObject);
                    return null;
                }

                AddAudioSystemToGuard(paroleOfficerObject, npcComponent, true);

                var paroleOfficer = BBHelpers.AddComponentSafe<ParoleOfficer>(paroleOfficerObject);
                if (paroleOfficer == null)
                {
                    ModLogger.Error("[NPC Spawn] Failed to add ParoleOfficer wrapper");
                    UnityEngine.Object.Destroy(paroleOfficerObject);
                    return null;
                }
                paroleOfficer.Initialize(badgeNumber, firstName, assignment);

                if (assignment == ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor)
                {
                    var checkInSystem = BBHelpers.GetComponentSafe<ParoleCheckInSystem>(paroleOfficerObject);
                    if (checkInSystem == null)
                    {
                        checkInSystem = BBHelpers.AddComponentSafe<ParoleCheckInSystem>(paroleOfficerObject);
                        if (checkInSystem != null)
                        {
                            ModLogger.Debug($"✓ ParoleCheckInSystem added to supervising officer {paroleOfficerObject.name}");
                        }
                    }
                }

                if (!BaseNPCSpawner.TryFinalizePreparedNativeNPC(paroleOfficerObject, position))
                {
                    return null;
                }

                ModLogger.Debug($"[NPC Spawn] Spawned canonical parole officer {firstName} ({assignment})");
                return paroleOfficer;
            }
            catch (Exception e)
            {
                ModLogger.Error($"[NPC Spawn] Error spawning parole officer: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Spawn a single inmate using BaseNPCSpawner with working avatar system
        /// </summary>
        public PrisonInmate SpawnInmate(Vector3 position, string firstName = "Inmate", string prisonerID = "", string crimeType = "Unknown")
        {
            try
            {
                ModLogger.Debug($"🎯 Spawning inmate using BaseNPCSpawner: {firstName} (Crime: {crimeType})");

                // Generate prisoner ID if needed
                if (string.IsNullOrEmpty(prisonerID))
                {
                    prisonerID = GeneratePrisonerID();
                }

                // Use BaseNPCSpawner to spawn with proper avatar/appearance
                var inmateObject = BaseNPCSpawner.SpawnInmate(position, firstName, prisonerID);
                if (inmateObject == null)
                {
                    ModLogger.Error("❌ BaseNPCSpawner failed to spawn inmate");
                    return null;
                }

                // The NPC component is already configured by BaseNPCSpawner
                // Just add our PrisonInmate component for jail-specific behavior
                var prisonInmate = BBHelpers.AddComponentSafe<PrisonInmate>(inmateObject);
                prisonInmate.Initialize(prisonerID, firstName, crimeType);

                // BaseNPCSpawner already handles network spawning and NavMesh positioning

                ModLogger.Debug($"✓ BaseNPC inmate spawned: {firstName} (ID: {prisonerID}, Crime: {crimeType})");
                return prisonInmate;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning BaseNPC inmate: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get all active guards
        /// </summary>
        public List<PrisonGuard> GetActiveGuards()
        {
            // Clean up null references
            activeGuards.RemoveAll(g => g == null);
            return new List<PrisonGuard>(activeGuards);
        }

        /// <summary>
        /// Get all active inmates
        /// </summary>
        public List<PrisonInmate> GetActiveInmates()
        {
            // Clean up null references
            activeInmates.RemoveAll(i => i == null);
            return new List<PrisonInmate>(activeInmates);
        }

        /// <summary>
        /// Remove a guard from tracking
        /// </summary>
        public void RemoveGuard(PrisonGuard guard)
        {
            if (activeGuards.Contains(guard))
            {
                activeGuards.Remove(guard);
                ModLogger.Info($"Removed guard {guard.badgeNumber} from tracking");
            }
        }

        /// <summary>
        /// Remove an inmate from tracking
        /// </summary>
        public void RemoveInmate(PrisonInmate inmate)
        {
            if (activeInmates.Contains(inmate))
            {
                activeInmates.Remove(inmate);
                ModLogger.Info($"Removed inmate {inmate.prisonerID} from tracking");
            }
        }

        #region BaseNPC Helper Methods

        /// <summary>
        /// Gets the native NPC prefab through the shared game-version-compatible lookup.
        /// </summary>
        private GameObject GetBaseNPCPrefab()
        {
            return BaseNPCSpawner.GetBaseNPCPrefab();
        }

        /// <summary>
        /// Log the hierarchy of child GameObjects for debugging
        /// </summary>
        private void LogChildHierarchy(GameObject obj, int depth)
        {
            if (obj == null) return;
            
            string indent = new string(' ', depth * 2);
            var components = obj.GetComponents<Component>();
            ModLogger.Debug($"{indent}{obj.name} (Components: {components.Length})");
            
            foreach (var comp in components)
            {
                if (comp != null)
                {
                    ModLogger.Debug($"{indent}  - {comp.GetType().Name}");
                }
            }
            
            foreach (Transform child in obj.transform)
            {
                LogChildHierarchy(child.gameObject, depth + 1);
            }
        }

        /// <summary>
        /// Fix parole officer appearance using NPCAppearanceManager (more reliable than searching scene)
        /// This ensures consistent guard appearance even when spawned before other guards
        /// </summary>
        private void FixParoleOfficerAppearance(GameObject npcInstance, string firstName)
        {
            try
            {
                ModLogger.Debug($"🎨 Fixing parole officer appearance for {npcInstance.name} using NPCAppearanceManager");

#if !MONO
                var avatar = npcInstance.GetComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
                if (avatar == null)
                {
                    avatar = npcInstance.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
                }
                
                // Also check if NPC component has Avatar reference set
                if (avatar == null)
                {
                    var npcComponent = npcInstance.GetComponent<Il2CppScheduleOne.NPCs.NPC>();
                    if (npcComponent == null)
                    {
                        npcComponent = npcInstance.GetComponentInChildren<Il2CppScheduleOne.NPCs.NPC>();
                    }
                    if (npcComponent != null && npcComponent.Avatar != null)
                    {
                        avatar = npcComponent.Avatar;
                        ModLogger.Debug($"✓ Found Avatar via NPC.Avatar reference on {npcInstance.name}");
                    }
                }
#else
                var avatar = npcInstance.GetComponent<ScheduleOne.AvatarFramework.Avatar>();
                if (avatar == null)
                {
                    avatar = npcInstance.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
                }
                
                // Also check if NPC component has Avatar reference set
                if (avatar == null)
                {
                    var npcComponent = npcInstance.GetComponent<ScheduleOne.NPCs.NPC>();
                    if (npcComponent == null)
                    {
                        npcComponent = npcInstance.GetComponentInChildren<ScheduleOne.NPCs.NPC>();
                    }
                    if (npcComponent != null && npcComponent.Avatar != null)
                    {
                        avatar = npcComponent.Avatar;
                        ModLogger.Debug($"✓ Found Avatar via NPC.Avatar reference on {npcInstance.name}");
                    }
                }
#endif

                if (avatar == null)
                {
                    ModLogger.Warn($"⚠️ No Avatar component found on {npcInstance.name}, falling back to FixNPCAppearance");
                    LogChildHierarchy(npcInstance, 0);
                    FixNPCAppearance(npcInstance, "guard");
                    return;
                }

                // Use NPCAppearanceManager to get proper guard appearance (doesn't rely on existing NPCs)
                var appearanceSettings = NPCAppearanceManager.GetAppearanceForRole(BaseNPCSpawner.NPCRole.PrisonGuard, firstName);
                if (appearanceSettings != null)
                {
                    try
                    {
#if !MONO
                        var avatarSettings = appearanceSettings as Il2CppScheduleOne.AvatarFramework.AvatarSettings;
#else
                        var avatarSettings = appearanceSettings as ScheduleOne.AvatarFramework.AvatarSettings;
#endif
                        if (avatarSettings != null)
                        {
                            // Ensure Avatar GameObject is active
                            avatar.gameObject.SetActive(true);

                            // IL2CPP startup can throw in ApplyAccessorySettings when accessory prefabs are unresolved.
                            // For parole officers, use accessory-free settings to keep spawn stable.
                            SanitizeParoleOfficerAvatarSettings(avatarSettings);

                            // Apply the settings to the NPC's own Avatar
                            avatar.LoadAvatarSettings(avatarSettings);
                            ModLogger.Debug($"✓ Avatar settings loaded from NPCAppearanceManager for {npcInstance.name}");

                            // Try to trigger avatar refresh
                            avatar.enabled = false;
                            avatar.enabled = true;

                            ModLogger.Debug($"✓ Avatar refresh triggered for {npcInstance.name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Debug($"❌ Failed to load avatar settings from NPCAppearanceManager: {ex.Message}");
                        // Fallback to scene search method
                        ModLogger.Debug("Falling back to FixNPCAppearance method");
                        FixNPCAppearance(npcInstance, "guard");
                    }
                }
                else
                {
                    ModLogger.Warn($"⚠️ NPCAppearanceManager returned null settings, falling back to FixNPCAppearance");
                    FixNPCAppearance(npcInstance, "guard");
                }

                // Ensure avatar is active
                if (avatar.gameObject != npcInstance)
                {
                    avatar.gameObject.SetActive(true);
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error fixing parole officer appearance: {e.Message}");
                // Fallback to scene search method
                try
                {
                    FixNPCAppearance(npcInstance, "guard");
                }
                catch (Exception fallbackEx)
                {
                    ModLogger.Error($"Fallback FixNPCAppearance also failed: {fallbackEx.Message}");
                }
            }
        }

#if !MONO
        private void SanitizeParoleOfficerAvatarSettings(Il2CppScheduleOne.AvatarFramework.AvatarSettings avatarSettings)
        {
            if (avatarSettings == null)
            {
                return;
            }

            try
            {
                avatarSettings.AccessorySettings.Clear();
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Failed to sanitize IL2CPP parole accessory settings: {ex.Message}");
            }
        }
#else
        private void SanitizeParoleOfficerAvatarSettings(ScheduleOne.AvatarFramework.AvatarSettings avatarSettings)
        {
            if (avatarSettings == null)
            {
                return;
            }

            try
            {
                avatarSettings.AccessorySettings.Clear();
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Failed to sanitize Mono parole accessory settings: {ex.Message}");
            }
        }
#endif

        /// <summary>
        /// Fix BaseNPC appearance by copying from existing NPCs
        /// </summary>
        private void FixNPCAppearance(GameObject npcInstance, string npcType)
        {
            try
            {
                ModLogger.Debug($"🎨 Fixing appearance for {npcInstance.name} ({npcType})");

#if !MONO
                var avatar = npcInstance.GetComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
                if (avatar == null)
                {
                    avatar = npcInstance.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
                }
                
                // Also check if NPC component has Avatar reference set
                if (avatar == null)
                {
                    var npcComponent = npcInstance.GetComponent<Il2CppScheduleOne.NPCs.NPC>();
                    if (npcComponent == null)
                    {
                        npcComponent = npcInstance.GetComponentInChildren<Il2CppScheduleOne.NPCs.NPC>();
                    }
                    if (npcComponent != null && npcComponent.Avatar != null)
                    {
                        avatar = npcComponent.Avatar;
                        ModLogger.Debug($"✓ Found Avatar via NPC.Avatar reference on {npcInstance.name}");
                    }
                }
#else
                var avatar = npcInstance.GetComponent<ScheduleOne.AvatarFramework.Avatar>();
                if (avatar == null)
                {
                    avatar = npcInstance.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
                }
                
                // Also check if NPC component has Avatar reference set
                if (avatar == null)
                {
                    var npcComponent = npcInstance.GetComponent<ScheduleOne.NPCs.NPC>();
                    if (npcComponent == null)
                    {
                        npcComponent = npcInstance.GetComponentInChildren<ScheduleOne.NPCs.NPC>();
                    }
                    if (npcComponent != null && npcComponent.Avatar != null)
                    {
                        avatar = npcComponent.Avatar;
                        ModLogger.Debug($"✓ Found Avatar via NPC.Avatar reference on {npcInstance.name}");
                    }
                }
#endif

                if (avatar == null)
                {
                    ModLogger.Warn($"⚠️ No Avatar component found on {npcInstance.name} - logging hierarchy for debugging");
                    LogChildHierarchy(npcInstance, 0);
                    return;
                }

                // Find existing NPC with working avatar
                var sourceAvatar = FindSourceAvatar(npcType);
                if (sourceAvatar != null)
                {
                    try
                    {
#if !MONO
                        var sourceAvatarComponent = sourceAvatar as Il2CppScheduleOne.AvatarFramework.Avatar;
                        if (sourceAvatarComponent?.CurrentSettings != null)
                        {
                            avatar.LoadAvatarSettings(sourceAvatarComponent.CurrentSettings);
                            ModLogger.Debug($"✓ Avatar settings loaded from source avatar");
                        }
#else
                        var sourceAvatarComponent = sourceAvatar as ScheduleOne.AvatarFramework.Avatar;
                        if (sourceAvatarComponent?.CurrentSettings != null)
                        {
                            avatar.LoadAvatarSettings(sourceAvatarComponent.CurrentSettings);
                            ModLogger.Debug($"✓ Avatar settings loaded from source avatar");
                        }
#endif
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Warn($"Failed to load avatar settings: {ex.Message}");
                    }
                }
                else
                {
                    ModLogger.Warn($"⚠️ No source avatar found for {npcType}");
                }

                // Ensure avatar is active
                if (avatar.gameObject != npcInstance)
                {
                    avatar.gameObject.SetActive(true);
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error fixing NPC appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Find a source avatar based on NPC type
        /// </summary>
        private object FindSourceAvatar(string npcType)
        {
            // Use NPCRegistry for O(1) access instead of O(n) FindObjectsOfType
            var existingNPCs = NPCRegistryHelper.GetNPCsExcluding("Prison");

            if (npcType == "guard")
            {
                // For guards, find officer/police NPCs
                var guardAvatars = new List<object>();
                foreach (var npc in existingNPCs)
                {
                    var avatar = npc.Avatar;
                    if (avatar == null || avatar.CurrentSettings == null) continue;

                    string npcName = npc.gameObject.name.ToLower();
                    if (npcName.Contains("officer") || npcName.Contains("police") || npcName.Contains("guard"))
                    {
                        guardAvatars.Add(avatar);
                    }
                }

                if (guardAvatars.Count > 0)
                {
                    var selectedAvatar = guardAvatars[UnityEngine.Random.Range(0, guardAvatars.Count)];
                    ModLogger.Debug($"Selected random guard avatar from {guardAvatars.Count} options");
                    return selectedAvatar;
                }
            }
            else if (npcType == "inmate")
            {
                // For inmates, collect ALL civilian NPCs for variety
                var inmateAvatars = new List<object>();
                var inmateNames = new List<string>();

                foreach (var npc in existingNPCs)
                {
                    if (npc.gameObject.name.Contains("Prison")) continue;
                    var avatar = npc.Avatar;
                    if (avatar == null || avatar.CurrentSettings == null) continue;

                    string npcName = npc.gameObject.name.ToLower();

                    // Include Billy and other potential inmates
                    if (npcName.Contains("billy") || npcName.Contains("kramer") || npcName.Contains("inmate"))
                    {
                        inmateAvatars.Add(avatar);
                        inmateNames.Add(npc.gameObject.name);
                    }
                    // Also include civilian NPCs that aren't obviously authority figures
                    else if (!npcName.Contains("officer") && !npcName.Contains("police") &&
                             !npcName.Contains("guard") && !npcName.Contains("security") &&
                             !npcName.Contains("doctor") && !npcName.Contains("nurse") &&
                             !npcName.Contains("manager") && !npcName.Contains("boss"))
                    {
                        // Check if it's a reasonable civilian NPC
                        if (npc.FirstName != null && !string.IsNullOrEmpty(npc.FirstName))
                        {
                            inmateAvatars.Add(avatar);
                            inmateNames.Add(npc.gameObject.name);
                        }
                    }
                }

                if (inmateAvatars.Count > 0)
                {
                    int selectedIndex = UnityEngine.Random.Range(0, inmateAvatars.Count);
                    var selectedAvatar = inmateAvatars[selectedIndex];
                    ModLogger.Debug($"Selected random inmate avatar: {inmateNames[selectedIndex]} from {inmateAvatars.Count} options");
                    return selectedAvatar;
                }
            }

            // Fallback to any available avatar (but try to avoid authority figures for inmates)
            var fallbackAvatars = new List<object>();
            var fallbackNames = new List<string>();

            foreach (var npc in existingNPCs)
            {
                if (npc.gameObject.name.Contains("Prison")) continue;
                if (npc.Avatar != null && npc.Avatar.CurrentSettings != null)
                {
                    string npcName = npc.gameObject.name.ToLower();

                    // For inmates, prefer non-authority figures as fallback
                    if (npcType == "inmate")
                    {
                        if (!npcName.Contains("officer") && !npcName.Contains("police") && !npcName.Contains("guard"))
                        {
                            fallbackAvatars.Add(npc.Avatar);
                            fallbackNames.Add(npc.gameObject.name);
                        }
                    }
                    else
                    {
                        fallbackAvatars.Add(npc.Avatar);
                        fallbackNames.Add(npc.gameObject.name);
                    }
                }
            }

            if (fallbackAvatars.Count > 0)
            {
                int selectedIndex = UnityEngine.Random.Range(0, fallbackAvatars.Count);
                ModLogger.Info($"Using fallback avatar: {fallbackNames[selectedIndex]} from {fallbackAvatars.Count} options");
                return fallbackAvatars[selectedIndex];
            }

            return null;
        }

        /// <summary>
        /// Create completely custom inmate appearance from scratch
        /// </summary>
        private void CreateCustomInmateAppearance(GameObject inmateObject, string firstName, string crimeType)
        {
            try
            {
                ModLogger.Info($"🎨 Creating custom appearance for inmate {firstName} ({crimeType})");

                // FIRST: Try to ensure the BaseNPC has a working avatar by copying from an existing NPC
                bool avatarFixed = EnsureWorkingAvatar(inmateObject);
                if (!avatarFixed)
                {
                    ModLogger.Error($"Failed to fix avatar for {firstName} - will appear as marshmallow");
                    return;
                }

                // NOW: Try to customize the working avatar
                CustomizeExistingAvatar(inmateObject, firstName, crimeType);

                ModLogger.Info($"✓ Custom appearance created for {firstName}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating custom inmate appearance: {e.Message}");
                // Final fallback - just copy a working avatar
                ModLogger.Info($"Using fallback appearance copying for {firstName}");
                FixNPCAppearance(inmateObject, "inmate");
            }
        }

        /// <summary>
        /// Ensure the BaseNPC has a working avatar by copying from existing NPCs
        /// </summary>
        private bool EnsureWorkingAvatar(GameObject npcObject)
        {
            try
            {
                ModLogger.Info($"🔧 Ensuring working avatar for {npcObject.name}");

#if !MONO
                var avatar = npcObject.GetComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var avatar = npcObject.GetComponent<ScheduleOne.AvatarFramework.Avatar>();
#endif

                if (avatar == null)
                {
                    ModLogger.Warn("No avatar component found on BaseNPC - trying to add one");

                    // Try to add an Avatar component
                    try
                    {
#if !MONO
                        avatar = npcObject.AddComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                        avatar = npcObject.AddComponent<ScheduleOne.AvatarFramework.Avatar>();
#endif
                        ModLogger.Debug("✓ Added Avatar component to BaseNPC");
                    }
                    catch (Exception addEx)
                    {
                        ModLogger.Error($"Failed to add Avatar component: {addEx.Message}");
                        return false;
                    }
                }

                if (avatar == null)
                {
                    ModLogger.Error("Still no avatar component - BaseNPC may not support avatars");
                    return false;
                }

                // Check if it already has working settings
                if (avatar.CurrentSettings != null)
                {
                    ModLogger.Info("BaseNPC already has avatar settings - good!");
                    return true;
                }

                // Find any working NPC to copy avatar settings from
                var sourceAvatar = FindAnyWorkingAvatar();
                if (sourceAvatar == null)
                {
                    ModLogger.Error("No working avatar found to copy from");
                    return false;
                }

                // Copy the working avatar settings
                try
                {
#if !MONO
                    var sourceAvatarComponent = sourceAvatar as Il2CppScheduleOne.AvatarFramework.Avatar;
                    if (sourceAvatarComponent != null && sourceAvatarComponent.CurrentSettings != null)
                    {
                        ModLogger.Info($"Found source avatar settings of type: {sourceAvatarComponent.CurrentSettings.GetType().Name}");
                        avatar.LoadAvatarSettings(sourceAvatarComponent.CurrentSettings);
                        ModLogger.Info($"✓ Copied working avatar settings to {npcObject.name}");
                        return true;
                    }
                    else
                    {
                        ModLogger.Error($"Source avatar cast failed or has null settings - sourceAvatar type: {sourceAvatar?.GetType().Name}");
                    }
#else
                    var sourceAvatarComponent = sourceAvatar as ScheduleOne.AvatarFramework.Avatar;
                    if (sourceAvatarComponent != null && sourceAvatarComponent.CurrentSettings != null)
                    {
                        ModLogger.Info($"Found source avatar settings of type: {sourceAvatarComponent.CurrentSettings.GetType().Name}");
                        avatar.LoadAvatarSettings(sourceAvatarComponent.CurrentSettings);
                        ModLogger.Info($"✓ Copied working avatar settings to {npcObject.name}");
                        return true;
                    }
                    else
                    {
                        ModLogger.Error($"Source avatar cast failed or has null settings - sourceAvatar type: {sourceAvatar?.GetType().Name}");
                    }
#endif
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"Failed to copy avatar settings: {ex.Message}");
                    ModLogger.Error($"Stack trace: {ex.StackTrace}");
                }

                return false;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error ensuring working avatar: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find any working avatar from existing NPCs
        /// </summary>
        private object FindAnyWorkingAvatar()
        {
            // Use NPCRegistry for O(1) access instead of O(n) FindObjectsOfType
            var existingNPCs = NPCRegistryHelper.GetNPCsExcluding("Prison");
            ModLogger.Info($"🔍 Searching {existingNPCs.Count} NPCs for working avatars...");

            int checkedNPCs = 0;
            int npcsWithAvatars = 0;
            int npcsWithSettings = 0;

            // Prioritize civilian NPCs for inmates
            foreach (var npc in existingNPCs)
            {
                checkedNPCs++;

                if (npc.Avatar != null)
                {
                    npcsWithAvatars++;
                    ModLogger.Debug($"  NPC {npc.gameObject.name} has Avatar component");

                    if (npc.Avatar.CurrentSettings != null)
                    {
                        npcsWithSettings++;
                        string npcName = npc.gameObject.name.ToLower();
                        ModLogger.Debug($"  NPC {npc.gameObject.name} has CurrentSettings: {npc.Avatar.CurrentSettings.GetType().Name}");

                        // Prefer non-authority figures
                        if (!npcName.Contains("officer") && !npcName.Contains("police") && !npcName.Contains("guard"))
                        {
                            ModLogger.Info($"✓ Found working civilian avatar: {npc.gameObject.name}");
                            return npc.Avatar;
                        }
                    }
                    else
                    {
                        ModLogger.Debug($"  NPC {npc.gameObject.name} has null CurrentSettings");
                    }
                }
                else
                {
                    ModLogger.Debug($"  NPC {npc.gameObject.name} has no Avatar component");
                }
            }

            ModLogger.Info($"First pass complete: {checkedNPCs} NPCs, {npcsWithAvatars} with avatars, {npcsWithSettings} with settings");

            // Fallback to any working avatar
            foreach (var npc in existingNPCs)
            {
                if (npc.gameObject.name.Contains("Prison")) continue;
                if (npc.Avatar?.CurrentSettings != null)
                {
                    ModLogger.Info($"✓ Using fallback working avatar: {npc.gameObject.name}");
                    return npc.Avatar;
                }
            }

            ModLogger.Error($"❌ No working avatars found! Checked {checkedNPCs} NPCs, {npcsWithAvatars} had avatars, {npcsWithSettings} had settings");
            return null;
        }

        /// <summary>
        /// Customize an existing working avatar with variations
        /// </summary>
        private void CustomizeExistingAvatar(GameObject npcObject, string firstName, string crimeType)
        {
            try
            {
                ModLogger.Info($"🎨 Customizing existing avatar for {firstName}");

#if !MONO
                var avatar = npcObject.GetComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var avatar = npcObject.GetComponent<ScheduleOne.AvatarFramework.Avatar>();
#endif

                if (avatar?.CurrentSettings == null)
                {
                    ModLogger.Warn("No current settings to customize");
                    return;
                }

                // Try to customize the existing settings
                var settings = avatar.CurrentSettings;
                var settingsType = settings.GetType();

                ModLogger.Info($"Customizing settings of type: {settingsType.Name}");

                // Apply random variations to the existing working settings
                ApplyRandomVariations(settingsType, settings, crimeType);

                // Reload the modified settings
                avatar.LoadAvatarSettings(settings);

                ModLogger.Info($"✓ Customized avatar for {firstName}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error customizing existing avatar: {e.Message}");
            }
        }

        /// <summary>
        /// Apply random variations to existing avatar settings
        /// </summary>
        private void ApplyRandomVariations(System.Type settingsType, object settings, string crimeType)
        {
            try
            {
                // Skin color variations
                var skinColors = new Color[]
                {
                    new Color(1.0f, 0.8f, 0.6f),   // Light
                    new Color(0.9f, 0.7f, 0.5f),   // Medium light
                    new Color(0.8f, 0.6f, 0.4f),   // Medium
                    new Color(0.7f, 0.5f, 0.3f),   // Medium dark
                    new Color(0.6f, 0.4f, 0.2f),   // Dark
                };

                var hairColors = new Color[]
                {
                    new Color(0.1f, 0.1f, 0.1f),   // Black
                    new Color(0.3f, 0.2f, 0.1f),   // Dark brown
                    new Color(0.5f, 0.3f, 0.1f),   // Brown
                    new Color(0.7f, 0.5f, 0.3f),   // Light brown
                    new Color(0.9f, 0.7f, 0.4f),   // Blonde
                    new Color(0.5f, 0.5f, 0.5f),   // Gray
                };

                // Randomize appearance
                SetFieldIfExists(settingsType, settings, "SkinColor", skinColors[UnityEngine.Random.Range(0, skinColors.Length)]);
                SetFieldIfExists(settingsType, settings, "HairColor", hairColors[UnityEngine.Random.Range(0, hairColors.Length)]);

                // Physical variations
                SetFieldIfExists(settingsType, settings, "Height", UnityEngine.Random.Range(0.4f, 0.8f));
                SetFieldIfExists(settingsType, settings, "Weight", UnityEngine.Random.Range(0.3f, 0.7f));

                // Crime-specific modifications
                switch (crimeType?.ToLower())
                {
                    case "violent":
                    case "assault":
                        SetFieldIfExists(settingsType, settings, "Weight", UnityEngine.Random.Range(0.5f, 0.8f)); // Bulkier
                        break;

                    case "theft":
                    case "drug":
                        SetFieldIfExists(settingsType, settings, "Weight", UnityEngine.Random.Range(0.2f, 0.4f)); // Thinner
                        break;
                }

                ModLogger.Debug($"Applied random variations for {crimeType} type");
            }
            catch (Exception e)
            {
                ModLogger.Debug($"Some variations couldn't be applied: {e.Message}");
            }
        }

        /// <summary>
        /// Get any working avatar settings as a template
        /// </summary>
        private object GetAnyWorkingAvatarSettings()
        {
            // Use NPCRegistry for O(1) access instead of O(n) FindObjectsOfType
            var existingNPCs = NPCRegistryHelper.GetNPCsExcluding("Prison");
            foreach (var npc in existingNPCs)
            {
                if (npc.Avatar?.CurrentSettings != null)
                {
                    return npc.Avatar.CurrentSettings;
                }
            }
            return null;
        }

        /// <summary>
        /// Clone avatar settings so we can modify them
        /// </summary>
        private object CloneAvatarSettings(object originalSettings)
        {
            try
            {
                // Try to create a new instance of the same type
                var settingsType = originalSettings.GetType();

                // Try ScriptableObject.CreateInstance if it's a ScriptableObject
                if (typeof(ScriptableObject).IsAssignableFrom(settingsType))
                {
#if MONO
                    var newSettings = ScriptableObject.CreateInstance(settingsType);
#else
                    var newSettings = ScriptableObject.CreateInstance(settingsType.AssemblyQualifiedName);
#endif
                    CopySettingsFields(originalSettings, newSettings);
                    return newSettings;
                }

                // Try regular object creation
                var constructor = settingsType.GetConstructor(System.Type.EmptyTypes);
                if (constructor != null)
                {
                    var newSettings = constructor.Invoke(null);
                    CopySettingsFields(originalSettings, newSettings);
                    return newSettings;
                }

                ModLogger.Debug("Cannot clone avatar settings - using original");
                return originalSettings;
            }
            catch (Exception e)
            {
                ModLogger.Debug($"Failed to clone avatar settings: {e.Message}");
                return originalSettings;
            }
        }

        /// <summary>
        /// Copy fields from one settings object to another
        /// </summary>
        private void CopySettingsFields(object source, object destination)
        {
            try
            {
                var sourceType = source.GetType();
                var fields = sourceType.GetFields();

                foreach (var field in fields)
                {
                    try
                    {
                        var value = field.GetValue(source);
                        field.SetValue(destination, value);
                    }
                    catch
                    {
                        // Skip fields that can't be copied
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Debug($"Error copying settings fields: {e.Message}");
            }
        }

        /// <summary>
        /// Customize avatar settings for inmate appearance
        /// </summary>
        private void CustomizeInmateAppearance(object settings, string firstName, string crimeType)
        {
            try
            {
                var settingsType = settings.GetType();
                ModLogger.Info($"🎨 Customizing appearance for {firstName} - {crimeType} type");

                // Generate random physical characteristics
                var skinColors = new Color[]
                {
                    new Color(1.0f, 0.8f, 0.6f),   // Light
                    new Color(0.9f, 0.7f, 0.5f),   // Medium light
                    new Color(0.8f, 0.6f, 0.4f),   // Medium
                    new Color(0.7f, 0.5f, 0.3f),   // Medium dark
                    new Color(0.6f, 0.4f, 0.2f),   // Dark
                    new Color(0.5f, 0.3f, 0.15f),  // Very dark
                };

                var hairColors = new Color[]
                {
                    new Color(0.1f, 0.1f, 0.1f),   // Black
                    new Color(0.2f, 0.1f, 0.0f),   // Dark brown
                    new Color(0.4f, 0.2f, 0.1f),   // Brown
                    new Color(0.6f, 0.4f, 0.2f),   // Light brown
                    new Color(0.8f, 0.6f, 0.3f),   // Blonde
                    new Color(0.6f, 0.2f, 0.1f),   // Red
                    new Color(0.5f, 0.5f, 0.5f),   // Gray
                };

                // Customize basic appearance
                SetFieldIfExists(settingsType, settings, "SkinColor", skinColors[UnityEngine.Random.Range(0, skinColors.Length)]);
                SetFieldIfExists(settingsType, settings, "HairColor", hairColors[UnityEngine.Random.Range(0, hairColors.Length)]);

                // Physical build variation
                SetFieldIfExists(settingsType, settings, "Height", UnityEngine.Random.Range(0.3f, 0.8f));
                SetFieldIfExists(settingsType, settings, "Weight", UnityEngine.Random.Range(0.2f, 0.7f));
                SetFieldIfExists(settingsType, settings, "Gender", UnityEngine.Random.Range(0.2f, 0.9f)); // Mostly male but some variation

                // Facial features (if available)
                SetFieldIfExists(settingsType, settings, "EyebrowScale", UnityEngine.Random.Range(0.7f, 1.3f));
                SetFieldIfExists(settingsType, settings, "EyebrowThickness", UnityEngine.Random.Range(0.3f, 1.0f));
                SetFieldIfExists(settingsType, settings, "NoseScale", UnityEngine.Random.Range(0.8f, 1.2f));
                SetFieldIfExists(settingsType, settings, "MouthScale", UnityEngine.Random.Range(0.8f, 1.2f));
                SetFieldIfExists(settingsType, settings, "EarScale", UnityEngine.Random.Range(0.9f, 1.1f));
                SetFieldIfExists(settingsType, settings, "ChinScale", UnityEngine.Random.Range(0.8f, 1.2f));
                SetFieldIfExists(settingsType, settings, "ForeheadScale", UnityEngine.Random.Range(0.9f, 1.1f));

                // Age variations
                SetFieldIfExists(settingsType, settings, "Age", UnityEngine.Random.Range(0.2f, 0.8f));

                // Eye color
                var eyeColors = new Color[]
                {
                    new Color(0.3f, 0.2f, 0.1f),   // Brown
                    new Color(0.1f, 0.3f, 0.6f),   // Blue
                    new Color(0.2f, 0.5f, 0.2f),   // Green
                    new Color(0.2f, 0.2f, 0.2f),   // Dark
                    new Color(0.4f, 0.3f, 0.2f),   // Hazel
                };
                SetFieldIfExists(settingsType, settings, "EyeColor", eyeColors[UnityEngine.Random.Range(0, eyeColors.Length)]);

                // Crime-type specific modifications
                CustomizeForCrimeType(settingsType, settings, crimeType);

                // Try to set prison uniform if clothing system exists
                ApplyPrisonUniform(settingsType, settings);

                ModLogger.Debug($"✓ Custom appearance applied to {firstName}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error customizing inmate appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Customize appearance based on crime type
        /// </summary>
        private void CustomizeForCrimeType(System.Type settingsType, object settings, string crimeType)
        {
            try
            {
                switch (crimeType?.ToLower())
                {
                    case "violent":
                    case "assault":
                    case "murder":
                        // Tougher, more intimidating look
                        SetFieldIfExists(settingsType, settings, "Weight", UnityEngine.Random.Range(0.4f, 0.8f)); // Bulkier
                        SetFieldIfExists(settingsType, settings, "Height", UnityEngine.Random.Range(0.5f, 0.8f)); // Taller
                        SetFieldIfExists(settingsType, settings, "EyebrowThickness", UnityEngine.Random.Range(0.7f, 1.0f)); // Thicker brows
                        break;

                    case "theft":
                    case "burglary":
                    case "fraud":
                        // Slighter, more shifty look
                        SetFieldIfExists(settingsType, settings, "Weight", UnityEngine.Random.Range(0.2f, 0.5f)); // Thinner
                        SetFieldIfExists(settingsType, settings, "Height", UnityEngine.Random.Range(0.3f, 0.6f)); // Shorter average
                        break;

                    case "drug":
                    case "substance":
                        // Worn, weathered look
                        SetFieldIfExists(settingsType, settings, "Age", UnityEngine.Random.Range(0.4f, 0.8f)); // Older looking
                        SetFieldIfExists(settingsType, settings, "Weight", UnityEngine.Random.Range(0.2f, 0.4f)); // Thinner
                        break;

                    default:
                        // Generic criminal - no specific modifications
                        break;
                }
            }
            catch (Exception e)
            {
                ModLogger.Debug($"Error applying crime-type customization: {e.Message}");
            }
        }

        /// <summary>
        /// Try to apply prison uniform/clothing
        /// </summary>
        private void ApplyPrisonUniform(System.Type settingsType, object settings)
        {
            try
            {
                // Try to set clothing layers if the system supports it
                var bodyLayerField = settingsType.GetField("BodyLayerSettings");
                if (bodyLayerField != null)
                {
                    // Try to create prison uniform settings
                    ModLogger.Debug("Attempting to apply prison uniform");

                    // Orange jumpsuit color
                    var prisonOrange = new Color(1.0f, 0.5f, 0.0f);

                    // Try to find and modify clothing tint
                    SetFieldIfExists(settingsType, settings, "ClothingTint", prisonOrange);
                    SetFieldIfExists(settingsType, settings, "ShirtColor", prisonOrange);
                    SetFieldIfExists(settingsType, settings, "PantsColor", prisonOrange);
                }
            }
            catch (Exception e)
            {
                ModLogger.Debug($"Prison uniform application failed (this is normal): {e.Message}");
            }
        }

        /// <summary>
        /// Helper to set a field value if it exists
        /// </summary>
        private void SetFieldIfExists(System.Type type, object obj, string fieldName, object value)
        {
            try
            {
                var field = type.GetField(fieldName);
                if (field != null && field.FieldType.IsAssignableFrom(value.GetType()))
                {
                    field.SetValue(obj, value);
                    ModLogger.Debug($"Set {fieldName} = {value}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Debug($"Failed to set {fieldName}: {e.Message}");
            }
        }

        /// <summary>
        /// Apply random variations to inmate appearance for diversity (LEGACY - keeping for fallback)
        /// </summary>
        private void ApplyInmateVariations(GameObject inmateObject)
        {
            try
            {
                ModLogger.Info($"🎨 Applying random variations to {inmateObject.name}");

#if !MONO
                var avatar = inmateObject.GetComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var avatar = inmateObject.GetComponent<ScheduleOne.AvatarFramework.Avatar>();
#endif

                if (avatar == null || avatar.CurrentSettings == null)
                {
                    ModLogger.Warn("No avatar or settings found for variations");
                    return;
                }

                // Try to apply some random variations (these may not work depending on the avatar system)
                try
                {
                    var settings = avatar.CurrentSettings;

                    // Randomly adjust some basic properties if they exist
                    var settingsType = settings.GetType();

                    // Try to randomize skin color slightly
                    var skinColorField = settingsType.GetField("SkinColor");
                    if (skinColorField != null && skinColorField.FieldType == typeof(Color))
                    {
                        var currentColor = (Color)skinColorField.GetValue(settings);
                        // Slightly vary the skin tone
                        float variation = 0.1f;
                        var newColor = new Color(
                            Mathf.Clamp01(currentColor.r + UnityEngine.Random.Range(-variation, variation)),
                            Mathf.Clamp01(currentColor.g + UnityEngine.Random.Range(-variation, variation)),
                            Mathf.Clamp01(currentColor.b + UnityEngine.Random.Range(-variation, variation)),
                            currentColor.a
                        );
                        skinColorField.SetValue(settings, newColor);
                        ModLogger.Debug("Applied skin color variation");
                    }

                    // Try to randomize height slightly
                    var heightField = settingsType.GetField("Height");
                    if (heightField != null && heightField.FieldType == typeof(float))
                    {
                        var currentHeight = (float)heightField.GetValue(settings);
                        var newHeight = Mathf.Clamp01(currentHeight + UnityEngine.Random.Range(-0.15f, 0.15f));
                        heightField.SetValue(settings, newHeight);
                        ModLogger.Debug("Applied height variation");
                    }

                    // Try to randomize weight slightly
                    var weightField = settingsType.GetField("Weight");
                    if (weightField != null && weightField.FieldType == typeof(float))
                    {
                        var currentWeight = (float)weightField.GetValue(settings);
                        var newWeight = Mathf.Clamp01(currentWeight + UnityEngine.Random.Range(-0.2f, 0.2f));
                        weightField.SetValue(settings, newWeight);
                        ModLogger.Debug("Applied weight variation");
                    }

                    // Try to randomize hair color
                    var hairColorField = settingsType.GetField("HairColor");
                    if (hairColorField != null && hairColorField.FieldType == typeof(Color))
                    {
                        // Randomize hair color to common colors
                        var hairColors = new Color[]
                        {
                            new Color(0.1f, 0.1f, 0.1f), // Black
                            new Color(0.3f, 0.2f, 0.1f), // Dark brown
                            new Color(0.5f, 0.3f, 0.1f), // Brown
                            new Color(0.6f, 0.4f, 0.2f), // Light brown
                            new Color(0.4f, 0.4f, 0.4f), // Gray
                            new Color(0.8f, 0.6f, 0.3f), // Blonde
                            new Color(0.6f, 0.2f, 0.1f)  // Reddish
                        };
                        var randomHairColor = hairColors[UnityEngine.Random.Range(0, hairColors.Length)];
                        hairColorField.SetValue(settings, randomHairColor);
                        ModLogger.Debug("Applied hair color variation");
                    }

                    // Reload the avatar with modified settings
                    avatar.LoadAvatarSettings(settings);
                    ModLogger.Debug($"✓ Applied random variations to {inmateObject.name}");
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Some variations couldn't be applied (this is normal): {ex.Message}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying inmate variations: {e.Message}");
            }
        }

        /// <summary>
        /// Spawn NPC on network if we're the server
        /// </summary>
        private void SpawnOnNetworkIfServer(GameObject npcInstance)
        {
            try
            {
                var networkObject = npcInstance.GetComponent<NetworkObject>();
                if (networkObject == null) return;

                var networkManager = InstanceFinder.NetworkManager;
                if (networkManager != null && networkManager.IsServer)
                {
                    networkManager.ServerManager.Spawn(networkObject);
                    ModLogger.Debug($"✓ {npcInstance.name} spawned on network");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning on network: {e.Message}");
            }
        }

        /// <summary>
        /// Position NPC on NavMesh
        /// </summary>
        private void PositionOnNavMesh(GameObject npcInstance, Vector3 position)
        {
            try
            {
                npcInstance.transform.position = position;

                var navAgent = npcInstance.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (navAgent != null)
                {
                    if (UnityEngine.AI.NavMesh.SamplePosition(position, out UnityEngine.AI.NavMeshHit hit, 5.0f, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        navAgent.Warp(hit.position);
                        navAgent.enabled = true;
                        ModLogger.Debug($"✓ {npcInstance.name} positioned on NavMesh");
                    }
                    else
                    {
                        ModLogger.Warn($"⚠️ Could not find NavMesh for {npcInstance.name}");
                    }
                }

                npcInstance.SetActive(true);
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error positioning on NavMesh: {e.Message}");
            }
        }

        #endregion

        #region Utility Methods

        private string GenerateBadgeNumber()
        {
            return $"G{UnityEngine.Random.Range(1000, 9999)}";
        }

        private string GeneratePrisonerID()
        {
            return $"P{UnityEngine.Random.Range(10000, 99999)}";
        }

        // Track used names to avoid duplicates
        private static HashSet<string> usedInmateNames = new HashSet<string>();

        private string GetRandomInmateFirstName()
        {
            // Custom names have priority
            var customNames = new List<string> { "Tidez", "Spec", "Dre" };
            var regularNames = new List<string>
            {
                "Mike", "Tony", "Steve", "Dave", "Chris", "Mark", "Paul", "Jake",
                "Ryan", "Brad", "Kyle", "Sean", "Matt", "Dan", "Nick", "Alex",
                "Carlos", "Marcus", "Derek", "Tyler", "Jason", "Kevin", "Brian"
            };

            // First try to use custom names if not already used
            foreach (var customName in customNames)
            {
                if (!usedInmateNames.Contains(customName))
                {
                    usedInmateNames.Add(customName);
                    return customName;
                }
            }

            // Then try regular names
            var availableNames = regularNames.Where(n => !usedInmateNames.Contains(n)).ToList();

            // If all names are used, clear and start over (but keep custom names used)
            if (availableNames.Count == 0)
            {
                usedInmateNames.RemoveWhere(n => !customNames.Contains(n));
                availableNames = regularNames.Where(n => !usedInmateNames.Contains(n)).ToList();
            }

            if (availableNames.Count > 0)
            {
                var selectedName = availableNames[UnityEngine.Random.Range(0, availableNames.Count)];
                usedInmateNames.Add(selectedName);
                return selectedName;
            }

            // Fallback
            return "Prisoner";
        }

        private string GetRandomCrimeType()
        {
            var crimes = new string[]
            {
                "Theft", "Assault", "Drug Possession", "Burglary", "Fraud", 
                "Vandalism", "Public Disturbance", "Trespassing", "DUI",
                "Shoplifting", "Battery", "Disorderly Conduct"
            };
            return crimes[UnityEngine.Random.Range(0, crimes.Length)];
        }

        #endregion
        
        #region Guard Coordination Methods
        
        /// <summary>
        /// Register a guard with the manager for coordination
        /// </summary>
        public void RegisterGuard(GuardBehavior guard)
        {
            if (!registeredGuards.Contains(guard))
            {
                registeredGuards.Add(guard);

                // Track intake officer specifically
                if (guard.GetRole() == GuardBehavior.GuardRole.IntakeOfficer)
                {
                    intakeOfficer = guard;
                    ModLogger.Debug($"Registered intake officer: {guard.GetBadgeNumber()}");
                }

                ModLogger.Debug($"Registered guard {guard.GetBadgeNumber()} with PrisonNPCManager");
            }
        }
        
        /// <summary>
        /// Unregister a guard from the manager
        /// </summary>
        public void UnregisterGuard(GuardBehavior guard)
        {
            if (registeredGuards.Contains(guard))
            {
                registeredGuards.Remove(guard);

                if (guard == intakeOfficer)
                {
                    intakeOfficer = null;
                    ModLogger.Debug($"Unregistered intake officer: {guard.GetBadgeNumber()}");
                }

                ModLogger.Debug($"Unregistered guard {guard.GetBadgeNumber()} from PrisonNPCManager");
            }
        }
        
        /// <summary>
        /// Try to assign a coordinated patrol to guards
        /// </summary>
        public IEnumerator TryAssignPatrol(GuardBehavior requestingGuard)
        {
            // Check if it's time for a patrol and no patrol is in progress
            if (Time.time < nextPatrolTime || isPatrolInProgress)
            {
                yield break;
            }

            if (requestingGuard.GetCurrentActivity() != GuardBehavior.GuardActivity.Idle)
            {
                yield break;
            }

            // Find a partner from the same area
            var partner = FindPatrolPartner(requestingGuard);
            if (partner != null)
            {
                isPatrolInProgress = true;
                nextPatrolTime = Time.time + PATROL_COOLDOWN;

                requestingGuard.StartPatrol();
                partner.StartPatrol();
                ModLogger.Debug($"✓ Assigned coordinated patrol: {requestingGuard.GetBadgeNumber()} + {partner.GetBadgeNumber()}");
            }

            yield break;
        }
        
        /// <summary>
        /// Find a suitable patrol partner for a guard
        /// </summary>
        private GuardBehavior FindPatrolPartner(GuardBehavior requestingGuard)
        {
            foreach (var guard in registeredGuards)
            {
                if (guard == requestingGuard || guard.GetCurrentActivity() != GuardBehavior.GuardActivity.Idle) continue;

                // Must be from same area (both guard room or both booking)
                var requestingRole = requestingGuard.GetRole();
                var guardRole = guard.GetRole();

                bool sameArea = (requestingRole == GuardBehavior.GuardRole.GuardRoomStationary && guardRole == GuardBehavior.GuardRole.GuardRoomStationary) ||
                               (requestingRole == GuardBehavior.GuardRole.BookingStationary && guardRole == GuardBehavior.GuardRole.BookingStationary);

                if (sameArea)
                {
                    return guard;
                }
            }
            return null;
        }
        
        /// <summary>
        /// End patrol coordination state
        /// </summary>
        public void EndPatrolCoordination()
        {
            isPatrolInProgress = false;
            ModLogger.Debug("Patrol coordination ended");
        }
        
        /// <summary>
        /// Get the intake officer for prisoner processing
        /// </summary>
        public GuardBehavior GetIntakeOfficer()
        {
            return intakeOfficer;
        }
        
        /// <summary>
        /// Check if intake officer is available
        /// </summary>
        public bool IsIntakeOfficerAvailable()
        {
            if (intakeOfficer != null)
            {
                return !intakeOfficer.IsProcessingIntake();
            }
            return false;
        }
        
        /// <summary>
        /// Request prisoner escort from intake officer
        /// </summary>
        public bool RequestPrisonerEscort(GameObject prisoner)
        {
            if (prisoner == null)
            {
                ModLogger.Warn("Cannot request prisoner escort - prisoner GameObject is null");
                return false;
            }

            // Convert GameObject to Player component
#if !MONO
            var playerComponent = prisoner.GetComponent<Il2CppScheduleOne.PlayerScripts.Player>();
#else
            var playerComponent = prisoner.GetComponent<ScheduleOne.PlayerScripts.Player>();
#endif

            if (playerComponent == null)
            {
                ModLogger.Error($"GameObject {prisoner.name} does not have a Player component");
                return false;
            }

            if (intakeOfficer != null && !intakeOfficer.IsProcessingIntake())
            {
                intakeOfficer.StartIntakeProcess(playerComponent);
                ModLogger.Info($"Requested prisoner escort for {prisoner.name} from intake officer");
                return true;
            }

            ModLogger.Warn($"Cannot request prisoner escort - intake officer not available");
            return false;
        }
        
        /// <summary>
        /// Get all registered guards
        /// </summary>
        public List<GuardBehavior> GetRegisteredGuards()
        {
            // Clean up null references
            registeredGuards.RemoveAll(g => g == null);
            return new List<GuardBehavior>(registeredGuards);
        }

        #endregion
        
        #region Parole Officer Coordination Methods

        /// <summary>
        /// Register a guard with the manager for coordination
        /// </summary>
        public void RegisterParoleOfficer(ParoleOfficerBehavior officer)
        {
            if (!registeredParoleOfficers.Contains(officer))
            {
                registeredParoleOfficers.Add(officer);

                // Track intake officer specifically
                if (officer.GetRole() == ParoleOfficerBehavior.ParoleOfficerRole.SupervisingOfficer)
                {
                    paroleSupervisor = officer;
                    ModLogger.Debug($"Registered supervising officer: {officer.GetBadgeNumber()}");
                }

                ModLogger.Debug($"Registered officer {officer.GetBadgeNumber()} with PrisonNPCManager");
            }
        }

        /// <summary>
        /// Unregister a guard from the manager
        /// </summary>
        public void UnregisterParoleOfficer(ParoleOfficerBehavior officer)
        {
            if (registeredParoleOfficers.Contains(officer))
            {
                registeredParoleOfficers.Remove(officer);

                if (officer == paroleSupervisor)
                {
                    paroleSupervisor = null;
                    ModLogger.Debug($"Unregistered intake officer: {officer.GetBadgeNumber()}");
                }

                ModLogger.Debug($"Unregistered officer {officer.GetBadgeNumber()} from PrisonNPCManager");
            }
        }

        /// <summary>
        /// Register a release officer with the manager for canonical release-officer ownership.
        /// </summary>
        public void RegisterReleaseOfficer(ReleaseOfficerBehavior officer)
        {
            if (officer == null)
            {
                return;
            }

            if (!registeredReleaseOfficers.Contains(officer))
            {
                registeredReleaseOfficers.Add(officer);
                ModLogger.Debug($"Registered release officer {officer.GetBadgeNumber()} with PrisonNPCManager");
            }
        }

        /// <summary>
        /// Unregister a release officer from the manager.
        /// </summary>
        public void UnregisterReleaseOfficer(ReleaseOfficerBehavior officer)
        {
            if (officer == null)
            {
                return;
            }

            if (registeredReleaseOfficers.Remove(officer))
            {
                ModLogger.Debug($"Unregistered release officer {officer.GetBadgeNumber()} from PrisonNPCManager");
            }
        }

        /// <summary>
        /// Get all registered release officers.
        /// </summary>
        public List<ReleaseOfficerBehavior> GetRegisteredReleaseOfficers()
        {
            registeredReleaseOfficers.RemoveAll(officer => officer == null);
            return new List<ReleaseOfficerBehavior>(registeredReleaseOfficers);
        }

        /// <summary>
        /// Try to assign a coordinated patrol to officers
        /// </summary>
        public IEnumerator TryAssignPatrol(ParoleOfficerBehavior requestingOfficer)
        {
            // Check if it's time for a patrol and no patrol is in progress
            if (Time.time < nextPatrolTime || isPatrolInProgress)
            {
                yield break;
            }

            if (requestingOfficer.GetCurrentActivity() != ParoleOfficerBehavior.ParoleOfficerActivity.Idle)
            {
                yield break;
            }

            // Find a partner from the same area
            var partner = FindPatrolPartner(requestingOfficer);
            if (partner != null)
            {
                isPatrolInProgress = true;
                nextPatrolTime = Time.time + PATROL_COOLDOWN;

                requestingOfficer.StartPatrol();
                partner.StartPatrol();
                ModLogger.Debug($"✓ Assigned coordinated patrol: {requestingOfficer.GetBadgeNumber()} + {partner.GetBadgeNumber()}");
            }

            yield break;
        }

        /// <summary>
        /// Find a suitable patrol partner for a parole officer
        /// </summary>
        private ParoleOfficerBehavior FindPatrolPartner(ParoleOfficerBehavior requestingOfficer)
        {
            foreach (var officer in registeredParoleOfficers)
            {
                if (officer == requestingOfficer || officer.GetCurrentActivity() != ParoleOfficerBehavior.ParoleOfficerActivity.Idle) continue;

                // Must be from same area (both guard room or both booking)
                var requestingRole = requestingOfficer.GetRole();
                var officerRole = officer.GetRole();

                bool sameArea = (requestingRole == ParoleOfficerBehavior.ParoleOfficerRole.PatrolOfficer && officerRole == ParoleOfficerBehavior.ParoleOfficerRole.PatrolOfficer);

                if (sameArea)
                {
                    return officer;
                }
            }
            return null;
        }

        /// <summary>
        /// End patrol coordination state
        /// </summary>
        public void EndParolePatrolCoordination()
        {
            isPatrolInProgress = false;
            ModLogger.Debug("Patrol coordination ended");
        }

        /// <summary>
        /// Get the supervising officer for prisoner processing
        /// </summary>
        public ParoleOfficerBehavior GetSupervisingOfficer()
        {
            return paroleSupervisor;
        }

        /// <summary>
        /// Check if supervising officer is available
        /// </summary>
        public bool IsSupervisingOfficerAvailable()
        {
            return paroleSupervisor != null && !paroleSupervisor.IsProcessingIntake();
        }

        /// <summary>
        /// Request prisoner escort from parole officer
        /// TODO: Implement parole intake proccess here
        /// </summary>
        public bool RequestReleaseEscort(GameObject parolee)
        {
            if (IsSupervisingOfficerAvailable() && parolee != null)
            {
                // Convert GameObject to Player component
#if !MONO
                var playerComponent = parolee.GetComponent<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var playerComponent = parolee.GetComponent<ScheduleOne.PlayerScripts.Player>();
#endif

                if (playerComponent != null)
                {
                    paroleSupervisor.StartIntakeProcess(playerComponent);
                    ModLogger.Info($"Requested parole intake for {parolee.name} from parole supervisor");
                    return true;
                }
                else
                {
                    ModLogger.Error($"GameObject {parolee.name} does not have a Player component");
                    return false;
                }
            }

            ModLogger.Warn($"Cannot request prisoner escort - intake officer not available");
            return false;
        }

        /// <summary>
        /// Get all registered guards
        /// </summary>
        public List<ParoleOfficerBehavior> GetRegisteredParoleOfficers()
        {
            // Clean up null references
            registeredParoleOfficers.RemoveAll(g => g == null);
            return new List<ParoleOfficerBehavior>(registeredParoleOfficers);
        }

        #endregion

        #region Network Prefab Testing Methods

        /// <summary>
        /// Public method to test spawning a network prefab by index
        /// Call this from console or other testing methods
        /// </summary>
        /// <param name="prefabIndex">Index of the prefab to spawn</param>
        public void TestSpawnNetworkPrefab(int prefabIndex)
        {
            if (guardSpawnPoints != null && guardSpawnPoints.Length > 0)
            {
                Vector3 spawnPos = guardSpawnPoints[0].position;
                ModLogger.Info($"Testing spawn of prefab {prefabIndex} at {spawnPos}");
                NetworkPrefabTester.TestSpawnPrefab(prefabIndex, spawnPos);
            }
            else
            {
                ModLogger.Error("No guard spawn points available for testing!");
            }
        }

        /// <summary>
        /// Re-run the prefab detection test
        /// </summary>
        public void RetestNetworkPrefabs()
        {
            NetworkPrefabTester.TestFindNetworkPrefabs();
        }

        /// <summary>
        /// Add audio system components to a guard for voice commands
        /// </summary>
        private void AddAudioSystemToGuard(GameObject guardObject, object npcComponent, bool hasDynamicGuardBehavior)
        {
            try
            {
                ModLogger.Debug($"Adding audio system to guard: {guardObject.name}");

#if !MONO
                if (!hasDynamicGuardBehavior)
                {
                    ModLogger.Warn($"Skipping jail dialogue/audio components for fallback guard {guardObject.name} (no GuardBehavior)");
                    return;
                }
#endif

                var audioSource = guardObject.GetComponent<AudioSource>();
                if (audioSource == null)
                {
                    audioSource = guardObject.AddComponent<AudioSource>();
                }

                if (audioSource != null)
                {
                    audioSource.volume = 0.8f;
                    audioSource.pitch = 1.0f;
                    audioSource.playOnAwake = false;
                    audioSource.spatialBlend = 0.5f;
                    ModLogger.Debug($"✓ AudioSource configured for guard {guardObject.name}");
                }

                // Add JailNPCAudioController for guard voice management
                var jailAudioController = BBHelpers.AddComponentSafe<JailNPCAudioController>(guardObject);
                ModLogger.Debug($"✓ JailNPCAudioController added to guard {guardObject.name}");

                var dialogueInitializationCoroutine = MelonCoroutines.Start(
                    InitializeGuardDialogueWhenReady(guardObject, npcComponent)) as Coroutine;
                if (dialogueInitializationCoroutine != null)
                {
                    guardDialogueInitializationCoroutines.Add(dialogueInitializationCoroutine);
                }

                ModLogger.Debug($"✓ Audio system configured for guard: {guardObject.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding audio system to guard {guardObject.name}: {e.Message}");
            }
        }

        /// <summary>
        /// Defer guard dialogue hookup until the spawned BaseNPC has completed its native initialization.
        /// </summary>
        private IEnumerator InitializeGuardDialogueWhenReady(GameObject guardObject, object npcComponent)
        {
            if (guardObject == null)
            {
                yield break;
            }

            const int maxAttempts = 20;
            DialogueHandler dialogueHandler = null;
            DialogueController baseDialogueController = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (guardObject == null)
                {
                    yield break;
                }

#if !MONO
                var nativeNpc = npcComponent as Il2CppScheduleOne.NPCs.NPC;
#else
                var nativeNpc = npcComponent as ScheduleOne.NPCs.NPC;
#endif

                if (nativeNpc != null)
                {
                    dialogueHandler = nativeNpc.DialogueHandler;
                }

                dialogueHandler ??= guardObject.GetComponentInChildren<DialogueHandler>(true);
                baseDialogueController = guardObject.GetComponentInChildren<DialogueController>(true);

                if (dialogueHandler != null || baseDialogueController != null)
                {
                    break;
                }

                yield return null;
            }

            if (guardObject == null)
            {
                yield break;
            }

            if (baseDialogueController == null)
            {
                try
                {
#if !MONO
                    baseDialogueController = guardObject.AddComponent<Il2CppScheduleOne.Dialogue.DialogueController>();
#else
                    baseDialogueController = guardObject.AddComponent<ScheduleOne.Dialogue.DialogueController>();
#endif
                    ModLogger.Debug($"✓ Base DialogueController added to guard {guardObject.name}");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"Failed to add DialogueController to guard {guardObject.name}: {ex.Message}");
                    yield break;
                }
            }

            baseDialogueController.DialogueEnabled = true;
            baseDialogueController.UseDialogueBehaviour = true;

            var dialogueController = BBHelpers.GetComponentSafe<JailNPCDialogueController>(guardObject)
                ?? BBHelpers.AddComponentSafe<JailNPCDialogueController>(guardObject);

            if (dialogueController != null)
            {
                ModLogger.Debug($"✓ JailNPCDialogueController ready on guard {guardObject.name}");
            }
            else
            {
                ModLogger.Warn($"Failed to add JailNPCDialogueController to {guardObject.name}");
            }
        }

        #endregion
    }

    /// <summary>
     /// Custom prison guard class with enhanced behaviors and assignment system
     /// </summary>
    public class PrisonGuard : MonoBehaviour
    {
#if !MONO
        public PrisonGuard(System.IntPtr ptr) : base(ptr) { }
#endif

        public string badgeNumber;
        public string firstName;
        public GuardBehavior.GuardAssignment assignment;

        private GuardBehavior guardBehavior;

        public void Initialize(string badge, string name, GuardBehavior.GuardAssignment guardAssignment = GuardBehavior.GuardAssignment.GuardRoom0)
        {
            badgeNumber = badge;
            firstName = name;
            assignment = guardAssignment;

            // Get or add the guard behavior component
            guardBehavior = BBHelpers.GetComponentSafe<GuardBehavior>(gameObject);

            if (guardBehavior != null)
            {
                ModLogger.Debug($"About to initialize GuardBehavior for {name} with assignment {assignment}");
                try
                {
                    guardBehavior.Initialize(assignment, badge);
                    ModLogger.Debug($"GuardBehavior initialization completed for {name}");

                    // Force registration if it's an intake officer
                    if (assignment == GuardBehavior.GuardAssignment.Booking0)
                    {
                        ModLogger.Debug($"Manually registering intake officer {name}");
                        var npcManager = Core.Instance?.NpcManager;
                        if (npcManager != null)
                        {
                            npcManager.RegisterGuard(guardBehavior);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error initializing GuardBehavior for {name}: {ex.Message}");
                }
            }
            else
            {
                ModLogger.Warn($"GuardBehavior component not found on guard {name} - guard will remain as static fallback");
            }

            ModLogger.Debug($"Prison guard {name} initialized with badge {badge} and assignment {assignment}");
        }

        private void Start()
        {
            // Additional initialization if needed
        }

        public GuardBehavior.GuardRole GetRole() => guardBehavior?.GetRole() ?? GuardBehavior.GuardRole.GuardRoomStationary;
        public GuardBehavior.GuardAssignment GetAssignment() => assignment;
        public string GetBadgeNumber() => badgeNumber;
        public string GetFirstName() => firstName;
    }

    /// <summary>
    /// Custom prison guard class with enhanced behaviors and assignment system
    /// </summary>
    public class ParoleOfficer : MonoBehaviour
    {
#if !MONO
        public ParoleOfficer(System.IntPtr ptr) : base(ptr) { }
#endif

        public string badgeNumber;
        public string firstName;
        public ParoleOfficerBehavior.ParoleOfficerAssignment assignment;

        private ParoleOfficerBehavior officerBehavior;

        public void Initialize(string badge, string name, ParoleOfficerBehavior.ParoleOfficerAssignment guardAssignment = ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor)
        {
            badgeNumber = badge;
            firstName = name;
            assignment = guardAssignment;

            // Get or add the guard behavior component
            officerBehavior = BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(gameObject);

            if (officerBehavior != null)
            {
                ModLogger.Debug($"About to initialize ParoleOfficerBehavior for {name} with assignment {assignment}");
                try
                {
                    officerBehavior.Initialize(assignment, badge);
                    ModLogger.Debug($"ParoleOfficerBehavior initialization completed for {name}");

                    // Force registration if it's a supervising officer
                    if (assignment == ParoleOfficerBehavior.ParoleOfficerAssignment.PoliceStationSupervisor)
                    {
                        ModLogger.Debug($"Manually registering supervising officer {name}");
                        var npcManager = Core.Instance?.NpcManager;
                        if (npcManager != null)
                        {
                            npcManager.RegisterParoleOfficer(officerBehavior);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Error initializing ParoleOfficerBehavior for {name}: {ex.Message}");
                }
            }
            else
            {
                ModLogger.Error($"GuardBehavior component not found on guard {name}");
            }

            ModLogger.Debug($"Prison guard {name} initialized with badge {badge} and assignment {assignment}");
        }

        private void Start()
        {
            // Additional initialization if needed
        }

        public ParoleOfficerBehavior.ParoleOfficerRole GetRole() => officerBehavior?.GetRole() ?? ParoleOfficerBehavior.ParoleOfficerRole.PatrolOfficer;
        public ParoleOfficerBehavior.ParoleOfficerAssignment GetAssignment() => assignment;
        public string GetBadgeNumber() => badgeNumber;
        public string GetFirstName() => firstName;
    }

    /// <summary>
    /// Custom prison inmate class with enhanced behaviors
    /// </summary>
    public class PrisonInmate : MonoBehaviour
    {
#if !MONO
        public PrisonInmate(System.IntPtr ptr) : base(ptr) { }
#endif

        public string prisonerID;
        public string firstName;
        public string crimeType;
        public int sentenceDays = 30;
        public int assignedCell = -1; // Cell number assigned by CellAssignmentManager

        public void Initialize(string id, string name, string crime)
        {
            prisonerID = id;
            firstName = name;
            crimeType = crime;

            ModLogger.Debug($"Prison inmate {name} initialized with ID {id} for {crime}");
        }

        private void Start()
        {
            // Additional initialization if needed
        }

        public string GetPrisonerID() => prisonerID;
        public string GetFirstName() => firstName;
        public string GetCrimeType() => crimeType;
    }
}
