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

        /// <summary>
        /// Tracks witnesses for the current gameplay scene and owns delayed civilian
        /// police-call work. This state is volatile and is reset at the scene boundary.
        /// </summary>
        public WitnessSystem _witnessSystem;
        private ContrabandDetectionSystem _contrabandDetectionSystem;
        private readonly CrimeIncidentLedger _incidentLedger = new CrimeIncidentLedger();
        private readonly Dictionary<string, float> _nativeMirrorSuppressionUntil = new Dictionary<string, float>();
        
        /// <summary>Maximum radius used when looking for witnesses to a murder or manslaughter.</summary>
        public float MurderDetectionRadius = 50f;

        /// <summary>Maximum radius used when looking for witnesses to an assault.</summary>
        public float AssaultDetectionRadius = 30f;

        /// <summary>Maximum radius reserved for weapon-detection callers.</summary>
        public float WeaponDetectionRadius = 65f;

        // These are Unity real-time seconds because the mirror callback is a short-lived
        // duplicate-suppression window, not a gameplay-time crime timer.
        private const float DefaultNativeMirrorSuppressionSeconds = 4f;
        
        /// <summary>Gets the volatile crime record used by the wanted overlay.</summary>
        public CrimeRecord CrimeRecord => _crimeRecord;

        /// <summary>Gets the contraband classifier owned by this detection system.</summary>
        public ContrabandDetectionSystem ContrabandDetection => _contrabandDetectionSystem;

        /// <summary>Gets the process-wide detection system instance created by the constructor.</summary>
        public static CrimeDetectionSystem Instance { get; private set; }
        
        /// <summary>
        /// Creates the local crime, witness, and contraband services and publishes this
        /// instance for callers that need to coordinate native and Behind Bars paths.
        /// </summary>
        public CrimeDetectionSystem()
        {
            _crimeRecord = new CrimeRecord();
            _witnessSystem = new WitnessSystem();
            _contrabandDetectionSystem = new ContrabandDetectionSystem(this);
            Instance = this;
            
            ModLogger.Info("Crime detection system initialized");
        }
        
        /// <summary>
        /// Processes an NPC death, creates the corresponding crime, and routes witness
        /// and native response handling.
        /// </summary>
        /// <param name="victim">NPC who died.</param>
        /// <param name="perpetrator">Player attributed with the death.</param>
        /// <param name="wasIntentional">Whether to classify the death as murder rather than manslaughter.</param>
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
        /// Processes an assault on a civilian NPC and records witness-driven response state.
        /// </summary>
        /// <param name="victim">Civilian NPC who was assaulted.</param>
        /// <param name="perpetrator">Player attributed with the assault.</param>
        /// <param name="isLethal">Whether to use the lethal severity tier.</param>
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
            
            // Register the offense immediately, but leave the game's live pursuit state at
            // None. Civilian witnesses schedule their own police call; that call is the
            // point at which the native law system should enter investigation/pursuit.
            if (perpetrator.IsOwner)
            {
                var crimeData = perpetrator.CrimeData;
                if (crimeData?.Crimes != null)
                {
                    SuppressNativeCrimeMirror(perpetrator, crime, DefaultNativeMirrorSuppressionSeconds, includeAssaultFamilyAlias: true);
                    crimeData.Crimes.Add(crime, 1);
                }

                var policeWitnesses = witnesses.OfType<PoliceOfficer>();
                foreach (var policeWitness in policeWitnesses)
                {
                    NetworkHelper.TryBeginFootPursuit(policeWitness, perpetrator);
                }
            }

            _crimeRecord.AddCrime(crimeInstance);
            ModLogger.Info($"Processed civilian assault for {victim.name}; native wanted escalation is deferred to witness police calls ({witnesses.Count} witness(es))");
        }

        /// <summary>
        /// Process an assault on a law-enforcement NPC. Street incidents retain the game's
        /// wanted-state escalation, while incidents already inside the jail use the local
        /// lockdown controller and deliberately leave wanted state unchanged.
        /// </summary>
        /// <param name="victim">Law-enforcement NPC who was assaulted.</param>
        /// <param name="perpetrator">Player attributed with the assault.</param>
        /// <param name="applyWantedLevel">Whether the street path should escalate native wanted state.</param>
        /// <param name="persistToRapSheet">Whether the charge should be copied to the persisted rap sheet.</param>
        /// <param name="mirrorNativeCrime">Whether the incident should be sent through native CrimeData.</param>
        public void ProcessOfficerAssault(
            NPC victim,
            Player perpetrator,
            bool applyWantedLevel = true,
            bool persistToRapSheet = false,
            bool mirrorNativeCrime = true)
        {
            if (victim == null || perpetrator == null)
                return;

            if (!IsLawEnforcementNpc(victim))
                return;

            Crime assaultCrime = new AssaultOnOfficer();
            var crimeInstance = new CrimeInstance(assaultCrime, victim.transform.position, 2.0f)
            {
                IncidentId = Guid.NewGuid().ToString("N"),
                Source = "BehindBars",
                // The native type remains AssaultOnOfficer for the existing sentence
                // configuration, while the player-facing charge uses the same base
                // Assault label as the native street path plus its enhancement.
                Description = "Assault",
                // A prisoner is already in custody, so the disciplinary charge must
                // remain on their record without altering their street wanted state.
                CountsTowardWantedLevel = applyWantedLevel
            };
            crimeInstance.AddEnhancement(new CrimeEnhancement(
                CrimeEnhancementKind.LawEnforcementVictim,
                victim.ID ?? string.Empty));

            if (perpetrator.IsOwner && perpetrator.CrimeData != null)
            {
                // Mirror street assaults into the native crime system. Custody-only
                // assaults deliberately skip this mirror: adding it would reintroduce
                // the game's wanted/pursuit path after the jail lockdown has taken over.
                if (applyWantedLevel && mirrorNativeCrime && perpetrator.CrimeData.Crimes != null)
                {
                    SuppressNativeCrimeMirror(perpetrator, assaultCrime, DefaultNativeMirrorSuppressionSeconds, includeAssaultFamilyAlias: false);
                    perpetrator.CrimeData.Crimes.Add(assaultCrime, 1);
                }
                if (applyWantedLevel)
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                }
            }

            _crimeRecord.AddCrime(crimeInstance);
            if (persistToRapSheet)
            {
                var rapSheet = Core.ResolveRapSheetManager().GetRapSheet(perpetrator);
                rapSheet?.AddCrime(crimeInstance);
            }
            ModLogger.Info($"[Charge Pipeline] Processed custody incident={crimeInstance.IncidentId} base=AssaultOnOfficer enhancement=LawEnforcementVictim wanted escalation={applyWantedLevel}");
        }
        
        /// <summary>
        /// Processes witness intimidation and reduces the target witness's future call likelihood.
        /// </summary>
        /// <param name="witness">Witness NPC being intimidated.</param>
        /// <param name="perpetrator">Player attributed with the intimidation.</param>
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
            
            // Current behavior computes direction and distance before these local height
            // offsets. The offsets therefore do not change the already-computed ray in
            // this implementation; preserve that fact in the documentation rather than
            // implying that this pass changes the LOS algorithm.
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
        /// <param name="npc">NPC whose role should be classified.</param>
        /// <returns>True for native PoliceOfficer instances or recognized mod officer roles.</returns>
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
        /// <param name="npc">NPC whose mod officer role should be classified.</param>
        /// <returns>True for recognized mod officer components/name prefixes.</returns>
        public bool IsModLawEnforcementNpc(NPC npc)
        {
            if (npc == null)
                return false;

            var npcObject = npc.gameObject;
            if (npcObject == null)
                return false;

            // Keep native police out of mod-officer handling paths.
            // Component checks are authoritative for injected roles; name prefixes are a
            // compatibility fallback for officer objects that do not expose a registered
            // behavior component yet.
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

        /// <summary>
        /// Determines whether a native crime callback should be mirrored into the local
        /// charge pipeline. A null input is permissive; otherwise active type, display-name,
        /// and assault-family suppression keys are consulted.
        /// </summary>
        /// <param name="player">Player whose native crime data may emit the callback.</param>
        /// <param name="crime">Native crime candidate being evaluated.</param>
        /// <returns>False only while a matching short-lived suppression entry is active.</returns>
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
            // Suppression is written under every key that a later native callback can expose:
            // CLR/native type name, display name, and optionally the shared assault-family alias.
            // This prevents one Behind Bars event from being counted again when the game
            // reports the same offense through a different native representation.
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
            // The player prefix keeps identical crime keys from different players from
            // sharing a suppression window. expiresAt is in Unity real-time seconds.
            if (player == null || string.IsNullOrEmpty(crimeKey))
            {
                return;
            }

            _nativeMirrorSuppressionUntil[BuildNativeMirrorKey(player, crimeKey)] = expiresAt;
        }

        private bool IsNativeMirrorSuppressed(Player player, string crimeKey)
        {
            // Expired entries are removed on read as well as by the periodic cleanup so a
            // stale key cannot suppress a later event indefinitely.
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
            // PlayerCode is preferred for network-stable identity; the scene object name is
            // retained as the compatibility fallback used by older/native player objects.
            string playerKey = string.IsNullOrEmpty(player.PlayerCode) ? player.name : player.PlayerCode;
            if (string.IsNullOrEmpty(playerKey))
            {
                playerKey = "unknown";
            }

            return $"{playerKey}:{crimeKey}";
        }

        private void CleanupNativeMirrorSuppressions()
        {
            // Cleanup is deliberately opportunistic: callers invoke it before evaluating a
            // native event, so this short-lived dictionary needs no separate Update loop.
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
            // Native street and mod-generated officer assaults may use different types or
            // display labels. Matching either string lets the alias suppress both forms.
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
        /// Clears volatile accumulated crimes, normally after sentence resolution.
        /// </summary>
        public void ClearAllCrimes()
        {
            _crimeRecord.ClearAllCrimes();
        }

        /// <summary>
        /// Clears only volatile per-Main-scene detection state. This record drives the
        /// live wanted overlay and pending arrest intake; it is not the persisted rap
        /// sheet and must not leak an unsaved crime into a subsequently loaded save.
        /// </summary>
        public void ResetSceneRuntimeState()
        {
            int activeCrimeCount = _crimeRecord.TotalCrimeCount;
            _crimeRecord.ClearAllCrimes();
            _nativeMirrorSuppressionUntil.Clear();
            _incidentLedger.Clear();
            _witnessSystem.ResetSceneRuntimeState();
            ModLogger.Info($"CrimeDetectionSystem cleared {activeCrimeCount} volatile crime record(s) for Main-scene exit");
        }
        
        /// <summary>
        /// Gets the current volatile wanted level.
        /// </summary>
        /// <returns>The wanted aggregate maintained by the local crime record.</returns>
        public float GetWantedLevel()
        {
            return _crimeRecord.CurrentWantedLevel;
        }
        
        /// <summary>
        /// Gets a display-name count summary of volatile crimes.
        /// </summary>
        /// <returns>A new summary dictionary.</returns>
        public Dictionary<string, int> GetCrimeSummary()
        {
            return _crimeRecord.GetCrimeSummary();
        }

        /// <summary>
        /// Gets a copy of the currently active local crime instances after expiration cleanup.
        /// These are volatile detection records, not the persisted rap sheet.
        /// </summary>
        public List<CrimeInstance> GetAllActiveCrimes()
        {
            return _crimeRecord.GetActiveCrimes();
        }

        /// <summary>
        /// Records one native game crime at the AddCrime seam. The generated incident ID
        /// correlates later arrest capture with contextual enhancements.
        /// </summary>
        /// <param name="player">Player whose native crime event was observed.</param>
        /// <param name="crime">Native crime object at the AddCrime seam.</param>
        /// <param name="location">World location captured for the local charge.</param>
        /// <param name="severity">Severity used by wanted/fine calculations.</param>
        /// <param name="enhancement">Optional contextual enhancement.</param>
        /// <returns>The local correlated charge, or null when required inputs are absent.</returns>
        public CrimeInstance RecordNativeCrimeEvent(Player player, Crime crime, Vector3 location, float severity, CrimeEnhancement enhancement = null)
        {
            var incident = _incidentLedger.RecordNativeCrime(player, crime, location, severity, enhancement);
            if (incident != null)
            {
                _crimeRecord.AddCrime(incident);
            }

            return incident;
        }

        /// <summary>
        /// Resolves native crimes found at custody entry to the same incident records that
        /// were created when the game emitted AddCrime.
        /// </summary>
        /// <param name="player">Player entering custody.</param>
        /// <param name="crime">Native crime object reported at custody entry.</param>
        /// <param name="quantity">Number of native charges to resolve.</param>
        /// <param name="location">Fallback charge location.</param>
        /// <param name="severity">Fallback charge severity.</param>
        /// <returns>Resolved or fallback charges, bounded by the requested quantity.</returns>
        public List<CrimeInstance> ResolveNativeArrestCrimes(Player player, Crime crime, int quantity, Vector3 location, float severity)
        {
            return _incidentLedger.ResolveArrestCharges(player, crime, quantity, location, severity);
        }

        /// <summary>
        /// Calculates total fines for all retained volatile crimes.
        /// </summary>
        /// <returns>Severity-weighted total fine.</returns>
        public float CalculateTotalFines()
        {
            return _crimeRecord.CalculateTotalFines();
        }
        
        /// <summary>
        /// Processes a contraband search on a player, adding resulting charges to the local
        /// record and owner-authoritative native CrimeData.
        /// </summary>
        /// <param name="player">Player searched by the game/police flow.</param>
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
