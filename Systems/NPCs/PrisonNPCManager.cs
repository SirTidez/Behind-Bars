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
        
        // Enhanced spawn configuration
        public int maxGuards = 4; // Exactly 4 guards: 2 in guard room, 2 in booking
        public int maxInmates = 8;
        
        // Spawn areas (will be set by JailController)
        public Transform[] guardSpawnPoints;
        public Transform[] inmateSpawnPoints;
        
        // Guard assignment tracking
        private readonly JailGuardBehavior.GuardAssignment[] guardAssignments = {
            JailGuardBehavior.GuardAssignment.GuardRoom0,
            JailGuardBehavior.GuardAssignment.GuardRoom1,
            JailGuardBehavior.GuardAssignment.Booking0,
            JailGuardBehavior.GuardAssignment.Booking1
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
            
            // Start NPC spawning process
            MelonCoroutines.Start(InitializeNPCs());
        }

        /// <summary>
        /// Initialize spawn points from the jail controller
        /// </summary>
        private void InitializeSpawnPoints()
        {
            var jailController = Core.ActiveJailController;
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
            var jailController = Core.ActiveJailController;
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
        private Transform GetSpawnPointForAssignment(JailGuardBehavior.GuardAssignment assignment, JailController jailController)
        {
            switch (assignment)
            {
                case JailGuardBehavior.GuardAssignment.GuardRoom0:
                    return jailController.guardRoom.guardSpawns.Count > 0 ? jailController.guardRoom.guardSpawns[0] : null;
                case JailGuardBehavior.GuardAssignment.GuardRoom1:
                    return jailController.guardRoom.guardSpawns.Count > 1 ? jailController.guardRoom.guardSpawns[1] : null;
                case JailGuardBehavior.GuardAssignment.Booking0:
                    return jailController.booking.guardSpawns.Count > 0 ? jailController.booking.guardSpawns[0] : null;
                case JailGuardBehavior.GuardAssignment.Booking1:
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
        public PrisonGuard SpawnGuard(Vector3 position, string firstName = "Officer", string badgeNumber = "", JailGuardBehavior.GuardAssignment assignment = JailGuardBehavior.GuardAssignment.GuardRoom0)
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
        public JailGuardBehavior.GuardAssignment assignment;
        
        private JailGuardBehavior guardBehavior;

        public void Initialize(string badge, string name, JailGuardBehavior.GuardAssignment guardAssignment = JailGuardBehavior.GuardAssignment.GuardRoom0)
        {
            badgeNumber = badge;
            firstName = name;
            assignment = guardAssignment;
            
            // Get or add the jail guard behavior component
            guardBehavior = GetComponent<JailGuardBehavior>();
            if (guardBehavior != null)
            {
                guardBehavior.Initialize(assignment, badge);
            }
            else
            {
                ModLogger.Error($"JailGuardBehavior component not found on guard {name}");
            }
            
            ModLogger.Info($"Prison guard {name} initialized with badge {badge} and assignment {assignment}");
        }

        private void Start()
        {
            // Additional initialization if needed
        }

        public JailGuardBehavior.GuardRole GetRole() => guardBehavior?.GetRole() ?? JailGuardBehavior.GuardRole.GuardRoomStationary;
        public JailGuardBehavior.GuardAssignment GetAssignment() => assignment;
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
        
        private InmateStateMachine stateMachine;

        public void Initialize(string id, string name, string crime)
        {
            prisonerID = id;
            firstName = name;
            crimeType = crime;
            
            // Get or add the existing state machine
            stateMachine = GetComponent<InmateStateMachine>();
            if (stateMachine != null)
            {
                stateMachine.prisonerID = id;
                stateMachine.crimeType = crime;
            }
            
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