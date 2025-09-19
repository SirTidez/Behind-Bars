using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
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
        
        // NPC tracking
        private List<PrisonGuard> activeGuards = new List<PrisonGuard>();
        private List<PrisonInmate> activeInmates = new List<PrisonInmate>();
        
        // Guard coordination for IL2CPP-safe management
        private List<GuardBehavior> registeredGuards = new List<GuardBehavior>();
        private GuardBehavior intakeOfficer = null;
        private bool isPatrolInProgress = false;
        private float nextPatrolTime = 0f;
        private readonly float PATROL_COOLDOWN = 300f; // 5 minutes between coordinated patrols
        
        // Enhanced spawn configuration
        public int maxGuards = 4; // Exactly 4 guards: 2 in guard room, 2 in booking
        public int maxInmates = 8;
        
        // Spawn areas (will be set by JailController)
        public Transform[] guardSpawnPoints;
        public Transform[] inmateSpawnPoints;
        
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
                ModLogger.Info("PrisonNPCManager initialized");
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

            // TEST: Check for network prefabs before we try our old system
            TestNetworkPrefabs();

            // Start NPC spawning process
            MelonCoroutines.Start(InitializeNPCs());
        }

        /// <summary>
        /// Test method to check what NetworkObject prefabs are available
        /// This will help us determine if there's a usable "BaseNPC" prefab
        /// </summary>
        private void TestNetworkPrefabs()
        {
            ModLogger.Info("🔍 Testing for available network prefabs...");

            // Wait a frame to ensure FishNet is fully initialized
            MelonCoroutines.Start(DelayedPrefabTest());
        }

        private IEnumerator DelayedPrefabTest()
        {
            // Wait a few seconds for everything to be initialized
            yield return new WaitForSeconds(3f);

            // Run the test
            NetworkPrefabTester.TestFindNetworkPrefabs();

            // Wait a bit more then test spawning the PoliceNPC prefab (ID 39)
            yield return new WaitForSeconds(2f);
            TestSpawnPoliceNPC();
        }

        /// <summary>
        /// Test spawning the BaseNPC prefab we discovered
        /// </summary>
        private void TestSpawnPoliceNPC()
        {
            ModLogger.Info("🔬 Testing spawn of BaseNPC prefab (ID 182)...");
            ModLogger.Info("This is the generic NPC template the community member mentioned!");

            if (guardSpawnPoints != null && guardSpawnPoints.Length > 0)
            {
                Vector3 testPos = guardSpawnPoints[0].position + Vector3.right * 2f; // Offset so we don't collide
                NetworkPrefabTester.TestSpawnPrefab(182, testPos); // BaseNPC is prefab ID 182
            }
            else
            {
                ModLogger.Error("No guard spawn points available for BaseNPC test!");
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
                ModLogger.Info($"Found {jailController.guardRoom.guardSpawns.Count} guard room spawn points");
            }
            
            // Add booking spawns
            if (jailController.booking.guardSpawns != null)
            {
                allGuardSpawns.AddRange(jailController.booking.guardSpawns);
                ModLogger.Info($"Found {jailController.booking.guardSpawns.Count} booking spawn points");
            }
            
            guardSpawnPoints = allGuardSpawns.ToArray();
            ModLogger.Info($"Total guard spawn points available: {guardSpawnPoints.Length}");

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
            ModLogger.Info($"Created {inmateSpawnPoints.Length} inmate spawn points");
        }

        /// <summary>
        /// Initialize NPCs in the prison
        /// </summary>
        private IEnumerator InitializeNPCs()
        {
            ModLogger.Info("Starting prison NPC initialization...");
            
            // Wait a bit for everything to be ready
            yield return new WaitForSeconds(2f);
            
            // Spawn guards first
            yield return SpawnGuards();
            
            // Then spawn inmates
            yield return SpawnInmates();
            
            ModLogger.Info("✓ Prison NPC initialization completed");
        }

        /// <summary>
        /// Spawn exactly 4 prison guards with specific assignments
        /// </summary>
        private IEnumerator SpawnGuards()
        {
            var jailController = Core.JailController;
            if (jailController == null)
            {
                ModLogger.Error("JailController not found - cannot spawn guards");
                yield break;
            }

            ModLogger.Info("Spawning 4 guards with specific assignments...");
            
            // Spawn exactly 4 guards with specific assignments
            for (int i = 0; i < maxGuards; i++)
            {
                var assignment = guardAssignments[i];
                Transform spawnPoint = GetSpawnPointForAssignment(assignment, jailController);
                
                if (spawnPoint == null)
                {
                    ModLogger.Error($"Could not find spawn point for assignment {assignment}");
                    continue;
                }
                
                var guard = SpawnGuard(spawnPoint.position, $"Officer_{i+1}", $"G{1000 + i}", assignment);
                if (guard != null)
                {
                    activeGuards.Add(guard);
                    ModLogger.Info($"✓ Spawned guard {guard.badgeNumber} at {assignment} ({spawnPoint.name})");
                }
                else
                {
                    ModLogger.Error($"Failed to spawn guard for assignment {assignment}");
                }
                
                // Small delay between spawns
                yield return new WaitForSeconds(0.8f);
            }
            
            ModLogger.Info($"✓ Spawned {activeGuards.Count} guards with assignments");
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
        /// Spawn prison inmates at designated points
        /// </summary>
        private IEnumerator SpawnInmates()
        {
            if (inmateSpawnPoints == null || inmateSpawnPoints.Length == 0)
            {
                ModLogger.Warn("No inmate spawn points available");
                yield break;
            }

            ModLogger.Info($"Spawning up to {maxInmates} inmates...");
            
            int inmatesToSpawn = Mathf.Min(maxInmates, inmateSpawnPoints.Length);
            
            for (int i = 0; i < inmatesToSpawn; i++)
            {
                var spawnPoint = inmateSpawnPoints[i];
                if (spawnPoint == null) continue;
                
                // Random inmate details
                string firstName = GetRandomInmateFirstName();
                string crimeType = GetRandomCrimeType();
                
                var inmate = SpawnInmate(spawnPoint.position, firstName, $"Prisoner_{i+1:D3}", crimeType);
                if (inmate != null)
                {
                    activeInmates.Add(inmate);
                    ModLogger.Info($"✓ Spawned inmate {inmate.prisonerID} ({crimeType}) at position {i}");
                }
                else
                {
                    ModLogger.Error($"Failed to spawn inmate {i+1}");
                }
                
                // Small delay between spawns
                yield return new WaitForSeconds(0.5f);
            }
            
            ModLogger.Info($"✓ Spawned {activeInmates.Count} inmates");
        }

        /// <summary>
        /// Spawn a single guard with custom appearance and specific assignment
        /// </summary>
        public PrisonGuard SpawnGuard(Vector3 position, string firstName = "Officer", string badgeNumber = "", GuardBehavior.GuardAssignment assignment = GuardBehavior.GuardAssignment.GuardRoom0)
        {
            try
            {
                // Use DirectNPCBuilder to create the base NPC
                var guardObject = DirectNPCBuilder.CreateJailGuard(position, firstName, "Guard");
                if (guardObject == null)
                {
                    ModLogger.Error("DirectNPCBuilder.CreateJailGuard returned null");
                    return null;
                }

                // Add custom PrisonGuard component
                var prisonGuard = guardObject.AddComponent<PrisonGuard>();
                if (string.IsNullOrEmpty(badgeNumber))
                {
                    badgeNumber = GenerateBadgeNumber();
                }
                
                // Initialize the guard with assignment
                prisonGuard.Initialize(badgeNumber, firstName, assignment);
                
                // Apply custom appearance
#if MONO
                PrisonAvatarCustomizer.ApplyGuardAppearance(guardObject, badgeNumber);
#else
                ModLogger.Info($"Avatar customization disabled in IL2CPP for guard {badgeNumber}");
#endif
                
                ModLogger.Info($"✓ Created prison guard: {firstName} (Badge: {badgeNumber}, Assignment: {assignment})");
                return prisonGuard;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning guard: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Spawn a single inmate with custom appearance
        /// </summary>
        public PrisonInmate SpawnInmate(Vector3 position, string firstName = "Inmate", string prisonerID = "", string crimeType = "Unknown")
        {
            try
            {
                // Use DirectNPCBuilder to create the base NPC
                var inmateObject = DirectNPCBuilder.CreateJailInmate(position, firstName, "Prisoner");
                if (inmateObject == null)
                {
                    ModLogger.Error("DirectNPCBuilder.CreateJailInmate returned null");
                    return null;
                }

                // Add custom PrisonInmate component
                var prisonInmate = inmateObject.AddComponent<PrisonInmate>();
                if (string.IsNullOrEmpty(prisonerID))
                {
                    prisonerID = GeneratePrisonerID();
                }
                
                // Initialize the inmate
                prisonInmate.Initialize(prisonerID, firstName, crimeType);
                
                // Apply custom appearance
#if MONO
                PrisonAvatarCustomizer.ApplyInmateAppearance(inmateObject, prisonerID, crimeType);
#else
                ModLogger.Info($"Avatar customization disabled in IL2CPP for inmate {prisonerID}");
#endif
                
                ModLogger.Info($"✓ Created prison inmate: {firstName} (ID: {prisonerID}, Crime: {crimeType})");
                return prisonInmate;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error spawning inmate: {e.Message}");
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

        #region Utility Methods

        private string GenerateBadgeNumber()
        {
            return $"G{UnityEngine.Random.Range(1000, 9999)}";
        }

        private string GeneratePrisonerID()
        {
            return $"P{UnityEngine.Random.Range(10000, 99999)}";
        }

        private string GetRandomInmateFirstName()
        {
            var names = new string[]
            {
                "Mike", "Tony", "Steve", "Dave", "Chris", "Mark", "Paul", "Jake",
                "Ryan", "Brad", "Kyle", "Sean", "Matt", "Dan", "Nick", "Alex",
                "Carlos", "Marcus", "Derek", "Tyler", "Jason", "Kevin", "Brian"
            };
            return names[UnityEngine.Random.Range(0, names.Length)];
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
                    ModLogger.Info($"Registered intake officer: {guard.GetBadgeNumber()}");
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
                    ModLogger.Info($"Unregistered intake officer: {guard.GetBadgeNumber()}");
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
                ModLogger.Info($"✓ Assigned coordinated patrol: {requestingGuard.GetBadgeNumber()} + {partner.GetBadgeNumber()}");
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
            return intakeOfficer != null && !intakeOfficer.IsProcessingIntake();
        }
        
        /// <summary>
        /// Request prisoner escort from intake officer
        /// </summary>
        public bool RequestPrisonerEscort(GameObject prisoner)
        {
            if (IsIntakeOfficerAvailable() && prisoner != null)
            {
                // Convert GameObject to Player component
#if !MONO
                var playerComponent = prisoner.GetComponent<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var playerComponent = prisoner.GetComponent<ScheduleOne.PlayerScripts.Player>();
#endif

                if (playerComponent != null)
                {
                    intakeOfficer.StartIntakeProcess(playerComponent);
                    ModLogger.Info($"Requested prisoner escort for {prisoner.name} from intake officer");
                    return true;
                }
                else
                {
                    ModLogger.Error($"GameObject {prisoner.name} does not have a Player component");
                    return false;
                }
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
            guardBehavior = GetComponent<GuardBehavior>();
            if (guardBehavior == null)
            {
                guardBehavior = gameObject.AddComponent<GuardBehavior>();
            }

            if (guardBehavior != null)
            {
                ModLogger.Info($"About to initialize GuardBehavior for {name} with assignment {assignment}");
                try
                {
                    guardBehavior.Initialize(assignment, badge);
                    ModLogger.Info($"GuardBehavior initialization completed for {name}");

                    // Force registration if it's an intake officer
                    if (assignment == GuardBehavior.GuardAssignment.Booking0)
                    {
                        ModLogger.Info($"Manually registering intake officer {name}");
                        if (PrisonNPCManager.Instance != null)
                        {
                            PrisonNPCManager.Instance.RegisterGuard(guardBehavior);
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
                ModLogger.Error($"GuardBehavior component not found on guard {name}");
            }

            ModLogger.Info($"Prison guard {name} initialized with badge {badge} and assignment {assignment}");
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