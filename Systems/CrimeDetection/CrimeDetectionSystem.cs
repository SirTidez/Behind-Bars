using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Crimes;
using Behind_Bars.Systems.CrimeTracking;

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
        private WitnessSystem _witnessSystem;
        private ContrabandDetectionSystem _contrabandDetectionSystem;
        
        // Detection settings
        public float MurderDetectionRadius = 25f;
        public float AssaultDetectionRadius = 15f;
        public float WeaponDetectionRadius = 30f;
        
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
                        policeWitness.BeginFootPursuit_Networked(perpetrator.NetworkObject.ObjectId.ToString());
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
                
            // Don't double-process police assaults (already handled by game)
            if (victim is PoliceOfficer)
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
            
            // Add to player's Schedule I crime data
            if (perpetrator.IsOwner)
            {
                perpetrator.CrimeData.AddCrime(crime);
                
                if (perpetrator.CrimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Investigating);
                }
                
                // Call police if witnessed by police
                var policeWitnesses = witnesses.OfType<PoliceOfficer>();
                foreach (var policeWitness in policeWitnesses)
                {
                    policeWitness.BeginFootPursuit_Networked(perpetrator.NetworkObject.ObjectId.ToString());
                }
            }
            
            // Add to cumulative record
            _crimeRecord.AddCrime(crimeInstance);
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
        }
        
        /// <summary>
        /// Find all NPCs within detection radius who can witness the crime
        /// </summary>
        private List<NPC> FindWitnesses(Vector3 crimeLocation, float detectionRadius)
        {
            var witnesses = new List<NPC>();
            
            // Find all NPCs in range
            var allNPCs = GameObject.FindObjectsOfType<NPC>();
            
            foreach (var npc in allNPCs)
            {
                if (npc == null || !npc.IsConscious)
                    continue;
                    
                float distance = Vector3.Distance(npc.transform.position, crimeLocation);
                
                if (distance <= detectionRadius)
                {
                    // Check if NPC has line of sight (simplified check)
                    if (HasLineOfSight(npc.transform.position, crimeLocation))
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
            if (victim is PoliceOfficer)
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
            if (victim is PoliceOfficer)
                return 4.0f; // Killing police is very serious
                
            var employee = victim.GetComponent<Employee>();
            if (employee != null)
                return 2.5f; // Killing employees is serious
                
            return 2.0f; // Standard murder
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