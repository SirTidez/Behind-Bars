using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems.Crimes;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.NPCs;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.Employees;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.Police;
using ScheduleOne.Law;
using ScheduleOne.Employees;
#endif

namespace Behind_Bars.Systems.CrimeDetection
{
    /// <summary>
    /// Core system for detecting and processing player crimes
    /// </summary>
    public class CrimeDetectionSystem
    {
        private CrimeRecord _crimeRecord;
        public WitnessSystem _witnessSystem;
        private ContrabandDetectionSystem _contrabandDetectionSystem;
        private readonly Dictionary<string, float> _nativeMirrorSuppressionUntil = new Dictionary<string, float>();
        
        // Detection settings
        public float MurderDetectionRadius = 50f;
        public float AssaultDetectionRadius = 30f;
        public float WeaponDetectionRadius = 65f;
        private const float DefaultNativeMirrorSuppressionSeconds = 4f;
        
        public CrimeRecord CrimeRecord => _crimeRecord;
        public ContrabandDetectionSystem ContrabandDetection => _contrabandDetectionSystem;

        public static CrimeDetectionSystem Instance { get; private set; }
        
        public CrimeDetectionSystem()
        {
            _crimeRecord = new CrimeRecord();
            _witnessSystem = new WitnessSystem();
            _contrabandDetectionSystem = new ContrabandDetectionSystem(this);
            Instance = this;
            
            ModLogger.Info("Crime detection system initialized");
        }
        
        /// <summary>
        /// Process an NPC death and determine if it's a crime
        /// </summary>
        public void ProcessNPCDeath(NPC victim, Player perpetrator, bool wasIntentional = true)
        {
            if (victim == null || perpetrator == null)
                return;
                
            ModLogger.Info($"Processing NPC death - Victim: {victim.name}, Perpetrator: {perpetrator.name}, Intentional: {wasIntentional}");
            
            // Determine crime type and severity
            Crime crime;
            float severity;
            
            if (wasIntentional)
            {
                string victimType = GetVictimType(victim);
                crime = new Murder(victimType);
                severity = GetMurderSeverity(victim);
            }
            else
            {
                crime = new Manslaughter();
                severity = 1.5f;
            }
            
            // Create crime instance
            var crimeInstance = new CrimeInstance(crime, victim.transform.position, severity);
            
            // Find witnesses
            var witnesses = FindWitnesses(victim.transform.position, MurderDetectionRadius);
            foreach (var witness in witnesses)
            {
                crimeInstance.AddWitness(witness);
                _witnessSystem.NPCWitnessesCrime(witness, crimeInstance, perpetrator);
            }
            
            // Add to player's Schedule I crime data for immediate police response
            if (perpetrator.IsOwner)
            {
                perpetrator.CrimeData.AddCrime(crime);
                
                // Escalate based on severity
                if (severity >= 3.0f) // High severity murder
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Lethal);
                }
                else if (severity >= 2.0f) // Standard murder
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.NonLethal);
                }
                else // Manslaughter
                {
                    if (perpetrator.CrimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                    {
                        perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                    }
                    else
                    {
                        perpetrator.CrimeData.Escalate();
                    }
                }
                
                // Call police if witnessed
                if (witnesses.Count > 0)
                {
                    var closestWitness = witnesses.OrderBy(w => Vector3.Distance(w.transform.position, victim.transform.position)).First();
                    if (closestWitness is PoliceOfficer policeWitness)
                    {
                        // Immediate police response
                        NetworkHelper.TryBeginFootPursuit(policeWitness, perpetrator);
                    }
                    else
                    {
                        // Civilian witness will call police (handled by WitnessSystem)
                        var lawManager = LawManager.Instance;
                        if (lawManager != null)
                        {
                            lawManager.PoliceCalled(perpetrator, crime);
                        }
                    }
                }
            }
            
            // Add to our cumulative crime record
            _crimeRecord.AddCrime(crimeInstance);
        }
        
        /// <summary>
        /// Process an assault on a civilian NPC
        /// </summary>
        public void ProcessCivilianAssault(NPC victim, Player perpetrator, bool isLethal = false)
        {
            if (victim == null || perpetrator == null)
                return;
                 
            // Do not process law enforcement assaults as civilian assaults.
            if (IsLawEnforcementNpc(victim))
                return;
                
            ModLogger.Info($"Processing civilian assault - Victim: {victim.name}, Perpetrator: {perpetrator.name}, Lethal: {isLethal}");
            
            Crime crime = new AssaultOnCivilian();
            float severity = isLethal ? 2.0f : 1.0f;
            
            var crimeInstance = new CrimeInstance(crime, victim.transform.position, severity);
            
            // Find witnesses
            var witnesses = FindWitnesses(victim.transform.position, AssaultDetectionRadius);
            foreach (var witness in witnesses)
            {
                crimeInstance.AddWitness(witness);
                _witnessSystem.NPCWitnessesCrime(witness, crimeInstance, perpetrator);
            }
            
            // Register assault in mod-managed tracking and suppress mirrored native AddCrime ingestion
            // for this event so wanted UI does not double count the same assault.
            if (perpetrator.IsOwner)
            {
                var crimeData = perpetrator.CrimeData;
                if (crimeData?.Crimes != null)
                {
                    SuppressNativeCrimeMirror(perpetrator, crime, DefaultNativeMirrorSuppressionSeconds, includeAssaultFamilyAlias: true);
                    crimeData.Crimes.Add(crime, 1);
                }

                if (crimeData != null && crimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                {
                    crimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Investigating);
                }

                var policeWitnesses = witnesses.OfType<PoliceOfficer>();
                foreach (var policeWitness in policeWitnesses)
                {
                    NetworkHelper.TryBeginFootPursuit(policeWitness, perpetrator);
                }
            }

            _crimeRecord.AddCrime(crimeInstance);
            ModLogger.Debug($"Processed civilian assault using mod-managed tracking for {victim.name}");
        }

        /// <summary>
        /// Process an assault on law-enforcement NPCs spawned by Behind Bars systems.
        /// This applies an additional Assault charge and escalates pursuit for immediate arrest flow.
        /// </summary>
        public void ProcessOfficerAssault(NPC victim, Player perpetrator)
        {
            if (victim == null || perpetrator == null)
                return;

            if (!IsLawEnforcementNpc(victim))
                return;

            Crime assaultCrime = new Assault();
            var crimeInstance = new CrimeInstance(assaultCrime, victim.transform.position, 2.0f);

            if (perpetrator.IsOwner && perpetrator.CrimeData != null)
            {
                // Use direct dictionary insertion (same pattern as civilian assault) to avoid duplicate
                // AddCrime postfix interactions while still reflecting in native crime data.
                if (perpetrator.CrimeData.Crimes != null)
                {
                    SuppressNativeCrimeMirror(perpetrator, assaultCrime, DefaultNativeMirrorSuppressionSeconds, includeAssaultFamilyAlias: false);
                    perpetrator.CrimeData.Crimes.Add(assaultCrime, 1);
                }
                perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
            }

            _crimeRecord.AddCrime(crimeInstance);
            ModLogger.Info($"Processed officer assault by {perpetrator.name} on {victim.name}");
        }
        
        /// <summary>
        /// Process witness intimidation (attacking someone who witnessed a crime)
        /// </summary>
        public void ProcessWitnessIntimidation(NPC witness, Player perpetrator)
        {
            if (witness == null || perpetrator == null)
                return;
                
            ModLogger.Info($"Processing witness intimidation - Witness: {witness.name}, Perpetrator: {perpetrator.name}");
            
            Crime crime = new WitnessIntimidation();
            float severity = 1.5f;
            
            var crimeInstance = new CrimeInstance(crime, witness.transform.position, severity);
            
            // This crime itself can be witnessed
            var witnesses = FindWitnesses(witness.transform.position, AssaultDetectionRadius);
            foreach (var newWitness in witnesses)
            {
                if (newWitness != witness) // Don't count the intimidated witness
                {
                    crimeInstance.AddWitness(newWitness);
                    _witnessSystem.NPCWitnessesCrime(newWitness, crimeInstance, perpetrator);
                }
            }
            
            // Add to player's crime data
            if (perpetrator.IsOwner)
            {
                perpetrator.CrimeData.AddCrime(crime);
                
                if (perpetrator.CrimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                }
                else
                {
                    perpetrator.CrimeData.Escalate();
                }
            }
            
            _crimeRecord.AddCrime(crimeInstance);
            
            // CRITICAL: Mark the witness as intimidated so they're less likely to call police
            if (witness != null && !string.IsNullOrEmpty(witness.ID))
            {
                _witnessSystem.MarkWitnessIntimidated(witness.ID);
                ModLogger.Info($"Marked witness {witness.name} (ID: {witness.ID}) as intimidated");
            }
        }
        
        /// <summary>
        /// Find all NPCs within detection radius who can witness the crime
        /// </summary>
        private List<NPC> FindWitnesses(Vector3 crimeLocation, float detectionRadius)
        {
            var witnesses = new List<NPC>();
            
            // Use NPCRegistry for O(1) access instead of O(n) FindObjectsOfType
            var allNPCs = NPCRegistryHelper.GetConsciousNPCs();
            
            foreach (var npc in allNPCs)
            {
                if (npc == null)
                    continue;
                    
                float distance = Vector3.Distance(npc.transform.position, crimeLocation);
                
                if (distance <= detectionRadius)
                {
                    // Check if NPC has line of sight (simplified check) and isn't the victim themselves
                    if (HasLineOfSight(npc.transform.position, crimeLocation) && crimeLocation != npc.transform.position)
                    {
                        witnesses.Add(npc);
                    }
                }
            }
            
            ModLogger.Info($"Found {witnesses.Count} witnesses at crime scene");
            return witnesses;
        }
        
        /// <summary>
        /// Simple line of sight check
        /// </summary>
        private bool HasLineOfSight(Vector3 witnessPos, Vector3 crimePos)
        {
            // Simple raycast to check for obstacles
            Vector3 direction = (crimePos - witnessPos).normalized;
            float distance = Vector3.Distance(witnessPos, crimePos);
            
            // Adjust heights for better LOS check
            witnessPos.y += 1.7f; // Eye height
            crimePos.y += 1.0f;   // Center height
            
            LayerMask obstacleMask = LayerMask.GetMask("Default", "Building", "Walls");
            
            if (Physics.Raycast(witnessPos, direction, out RaycastHit hit, distance, obstacleMask))
            {
                return false; // Something is blocking the view
            }
            
            return true;
        }
        
        /// <summary>
        /// Determine the type of victim for crime classification
        /// </summary>
        private string GetVictimType(NPC victim)
        {
            if (IsLawEnforcementNpc(victim))
                return "Police";
                
            // Check if victim has employee-type components
            var employee = victim.GetComponent<Employee>();
            if (employee != null)
                return "Employee";
                
            return "Civilian";
        }
        
        /// <summary>
        /// Calculate murder severity based on victim type
        /// </summary>
        private float GetMurderSeverity(NPC victim)
        {
            if (IsLawEnforcementNpc(victim))
                return 4.0f; // Killing police is very serious
                
            var employee = victim.GetComponent<Employee>();
            if (employee != null)
                return 2.5f; // Killing employees is serious
                
            return 2.0f; // Standard murder
        }

        /// <summary>
        /// True when NPC is a native police officer or a mod officer role.
        /// </summary>
        public bool IsLawEnforcementNpc(NPC npc)
        {
            if (npc == null)
                return false;

            if (npc is PoliceOfficer)
                return true;

            return IsModLawEnforcementNpc(npc);
        }

        /// <summary>
        /// True for Behind Bars officer roles that are not native PoliceOfficer.
        /// </summary>
        public bool IsModLawEnforcementNpc(NPC npc)
        {
            if (npc == null)
                return false;

            var npcObject = npc.gameObject;
            if (npcObject == null)
                return false;

            // Keep native police out of mod-officer handling paths.
            if (npc is PoliceOfficer || BBHelpers.GetComponentSafe<PoliceOfficer>(npcObject) != null)
                return false;

            if (BBHelpers.GetComponentSafe<GuardBehavior>(npcObject) != null)
                return true;

            if (BBHelpers.GetComponentSafe<ParoleOfficerBehavior>(npcObject) != null)
                return true;

            if (BBHelpers.GetComponentSafe<ReleaseOfficerBehavior>(npcObject) != null)
                return true;

            if (BBHelpers.GetComponentSafe<IntakeOfficerStateMachine>(npcObject) != null)
                return true;

            if (BBHelpers.GetComponentSafe<PrisonGuard>(npcObject) != null)
                return true;

            if (BBHelpers.GetComponentSafe<ParoleOfficer>(npcObject) != null)
                return true;

            string npcName = npc.name ?? string.Empty;
            return npcName.StartsWith("Intake Officer ", StringComparison.OrdinalIgnoreCase)
                   || npcName.StartsWith("Release Officer ", StringComparison.OrdinalIgnoreCase)
                   || npcName.StartsWith("Parole Officer ", StringComparison.OrdinalIgnoreCase)
                   || npcName.StartsWith("Supervising Officer ", StringComparison.OrdinalIgnoreCase)
                   || npcName.StartsWith("Station Officer ", StringComparison.OrdinalIgnoreCase);
        }

        public bool ShouldMirrorNativeCrime(Player player, Crime crime)
        {
            if (player == null || crime == null)
            {
                return true;
            }

            CleanupNativeMirrorSuppressions();

            if (IsNativeMirrorSuppressed(player, crime.GetType().FullName))
            {
                return false;
            }

            if (IsNativeMirrorSuppressed(player, crime.CrimeName))
            {
                return false;
            }

            if (IsAssaultFamilyCrime(crime) && IsNativeMirrorSuppressed(player, "ASSAULT_FAMILY"))
            {
                return false;
            }

            return true;
        }

        private void SuppressNativeCrimeMirror(Player player, Crime crime, float durationSeconds, bool includeAssaultFamilyAlias)
        {
            if (player == null || crime == null)
            {
                return;
            }

            float expiresAt = Time.time + Mathf.Max(0.1f, durationSeconds);

            SetNativeMirrorSuppression(player, crime.GetType().FullName, expiresAt);
            SetNativeMirrorSuppression(player, crime.CrimeName, expiresAt);

            if (includeAssaultFamilyAlias && IsAssaultFamilyCrime(crime))
            {
                SetNativeMirrorSuppression(player, "ASSAULT_FAMILY", expiresAt);
            }
        }

        private void SetNativeMirrorSuppression(Player player, string crimeKey, float expiresAt)
        {
            if (player == null || string.IsNullOrEmpty(crimeKey))
            {
                return;
            }

            _nativeMirrorSuppressionUntil[BuildNativeMirrorKey(player, crimeKey)] = expiresAt;
        }

        private bool IsNativeMirrorSuppressed(Player player, string crimeKey)
        {
            if (player == null || string.IsNullOrEmpty(crimeKey))
            {
                return false;
            }

            string key = BuildNativeMirrorKey(player, crimeKey);
            if (_nativeMirrorSuppressionUntil.TryGetValue(key, out float expiresAt))
            {
                if (Time.time <= expiresAt)
                {
                    return true;
                }

                _nativeMirrorSuppressionUntil.Remove(key);
            }

            return false;
        }

        private string BuildNativeMirrorKey(Player player, string crimeKey)
        {
            string playerKey = string.IsNullOrEmpty(player.PlayerCode) ? player.name : player.PlayerCode;
            if (string.IsNullOrEmpty(playerKey))
            {
                playerKey = "unknown";
            }

            return $"{playerKey}:{crimeKey}";
        }

        private void CleanupNativeMirrorSuppressions()
        {
            if (_nativeMirrorSuppressionUntil.Count == 0)
            {
                return;
            }

            float now = Time.time;
            var expiredKeys = _nativeMirrorSuppressionUntil
                .Where(kvp => kvp.Value < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (string expiredKey in expiredKeys)
            {
                _nativeMirrorSuppressionUntil.Remove(expiredKey);
            }
        }

        private bool IsAssaultFamilyCrime(Crime crime)
        {
            if (crime == null)
            {
                return false;
            }

            string typeName = crime.GetType().Name;
            if (!string.IsNullOrEmpty(typeName) && typeName.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string crimeName = crime.CrimeName;
            return !string.IsNullOrEmpty(crimeName) && crimeName.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        
        /// <summary>
        /// Clear all accumulated crimes (called when player serves sentence)
        /// </summary>
        public void ClearAllCrimes()
        {
            _crimeRecord.ClearAllCrimes();
        }
        
        /// <summary>
        /// Get current wanted level
        /// </summary>
        public float GetWantedLevel()
        {
            return _crimeRecord.CurrentWantedLevel;
        }
        
        /// <summary>
        /// Get summary of all crimes for UI display
        /// </summary>
        public Dictionary<string, int> GetCrimeSummary()
        {
            return _crimeRecord.GetCrimeSummary();
        }

        public List<CrimeInstance> GetAllActiveCrimes()
        {
            return _crimeRecord.GetActiveCrimes();
        }

        /// <summary>
        /// Calculate total fine amount for all accumulated crimes
        /// </summary>
        public float CalculateTotalFines()
        {
            return _crimeRecord.CalculateTotalFines();
        }
        
        /// <summary>
        /// Process a contraband search on a player (called when police search player)
        /// </summary>
        public void ProcessContrabandSearch(Player player)
        {
            if (player == null)
                return;
                
            ModLogger.Info($"Processing contraband search for {player.name}");
            
            var contrabandCrimes = _contrabandDetectionSystem.PerformContrabandSearch(player);
            
            if (contrabandCrimes.Count > 0)
            {
                _contrabandDetectionSystem.ProcessContrabandCrimes(contrabandCrimes, player);
                ModLogger.Info($"Contraband search resulted in {contrabandCrimes.Count} additional crimes");
            }
        }
    }
}
