using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using MelonLoader;
using Behind_Bars.Systems.Crimes;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.NPCs.Behaviour;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
using ScheduleOne.Police;
using ScheduleOne.Law;
using ScheduleOne.NPCs.Behaviour;
#endif

namespace Behind_Bars.Systems.CrimeDetection
{
    /// <summary>
    /// Manages NPC witness behavior when crimes are committed
    /// </summary>
    public class WitnessSystem
    {
        private Dictionary<string, WitnessState> _witnesses = new Dictionary<string, WitnessState>();

        public WitnessSystem()
        {
            ModLogger.Info("Witness system initialized");
        }

        /// <summary>
        /// Called when an NPC witnesses a crime
        /// </summary>
        public void NPCWitnessesCrime(NPC witness, CrimeInstance crime, Player perpetrator)
        {
            if (witness == null || crime == null || perpetrator == null)
                return;

            ModLogger.Info($"NPC {witness.name} witnessed crime: {crime.GetCrimeName()}");

            // Create or update witness state
            string witnessId = witness.ID;
            if (!_witnesses.ContainsKey(witnessId))
            {
                _witnesses[witnessId] = new WitnessState(witness);
            }

            var witnessState = _witnesses[witnessId];
            witnessState.AddWitnessedCrime(crime);

            // Handle witness behavior based on type
            if (witness is PoliceOfficer policeWitness)
            {
                HandlePoliceWitness(policeWitness, crime, perpetrator);
            }
            else
            {
                HandleCivilianWitness(witness, crime, perpetrator, witnessState);
            }
        }

        /// <summary>
        /// Handle police officer witnessing a crime
        /// </summary>
        private void HandlePoliceWitness(PoliceOfficer police, CrimeInstance crime, Player perpetrator)
        {
            ModLogger.Info($"Police officer {police.name} witnessed {crime.GetCrimeName()} - initiating immediate pursuit");

            // Police respond immediately
            if (crime.Severity >= 2.0f) // Serious crimes
            {
                police.BeginFootPursuit_Networked(perpetrator.NetworkObject.ToString());
            }
            else
            {
                // For minor crimes, just investigate
                police.BeginBodySearch_Networked(perpetrator.NetworkObject.ToString());
            }
        }

        /// <summary>
        /// Handle civilian NPC witnessing a crime
        /// </summary>
        private void HandleCivilianWitness(NPC witness, CrimeInstance crime, Player perpetrator, WitnessState witnessState)
        {
            ModLogger.Info($"Civilian {witness.name} witnessed {crime.GetCrimeName()} - processing response");

            // Determine witness behavior based on crime severity and witness personality
            float fearLevel = CalculateFearLevel(witness, crime);

            if (fearLevel > 0.7f)
            {
                // High fear - flee and call police
                StartWitnessFlee(witness, crime.Location);
                SchedulePoliceCall(witness, crime, perpetrator, 5f + UnityEngine.Random.Range(0f, 10f));
            }
            else if (fearLevel > 0.4f)
            {
                // Moderate fear - back away but watch
                StartWitnessBackAway(witness, crime.Location);
                SchedulePoliceCall(witness, crime, perpetrator, 15f + UnityEngine.Random.Range(5f, 15f));
            }
            else
            {
                // Low fear - might approach or just watch
                if (UnityEngine.Random.Range(0f, 1f) > 0.6f) // 40% chance to call police
                {
                    SchedulePoliceCall(witness, crime, perpetrator, 30f + UnityEngine.Random.Range(10f, 30f));
                }
            }

            // Mark witness as having seen the perpetrator
            witnessState.HasSeenPerpetrator = true;
            witnessState.PerpetratorId = perpetrator.PlayerCode;
        }

        /// <summary>
        /// Calculate how afraid a witness is of the crime they saw
        /// </summary>
        private float CalculateFearLevel(NPC witness, CrimeInstance crime)
        {
            float baseFear = 0.5f;

            // Crime type affects fear
            if (crime.Crime is Murder)
                baseFear = 0.9f;
            else if (crime.Crime is Manslaughter)
                baseFear = 0.7f;
            else if (crime.Crime is AssaultOnCivilian)
                baseFear = 0.6f;
            else if (crime.Crime is WitnessIntimidation)
                baseFear = 0.8f;

            // Distance affects fear (closer = more afraid)
            float distance = Vector3.Distance(witness.transform.position, crime.Location);
            float distanceFactor = Mathf.Clamp01(1.0f - (distance / 20f));

            // Add some randomness for personality
            float personalityFactor = UnityEngine.Random.Range(0.7f, 1.3f);

            return Mathf.Clamp01(baseFear * (0.5f + distanceFactor * 0.5f) * personalityFactor);
        }

        /// <summary>
        /// Make witness flee from crime scene
        /// </summary>
        private void StartWitnessFlee(NPC witness, Vector3 crimeLocation)
        {
            ModLogger.Info($"Witness {witness.name} is fleeing from crime scene");

            // Find a direction away from the crime
            Vector3 fleeDirection = (witness.transform.position - crimeLocation).normalized;
            Vector3 fleeTarget = witness.transform.position + fleeDirection * UnityEngine.Random.Range(20f, 40f);

            // Try to move away (simplified - real implementation would use pathfinding)
            if (witness.Movement != null && witness.Movement.CanMove())
            {
                witness.Movement.SetDestination(fleeTarget);
            }

            // Play panicked animation/sound if available
#if MONO
            witness.PlayVO(ScheduleOne.VoiceOver.EVOLineType.Scared);
#else
            witness.PlayVO(Il2CppScheduleOne.VoiceOver.EVOLineType.Scared);
#endif
            witness.SetPanicked();
        }

        /// <summary>
        /// Make witness back away from crime scene
        /// </summary>
        private void StartWitnessBackAway(NPC witness, Vector3 crimeLocation)
        {
            ModLogger.Info($"Witness {witness.name} is backing away from crime scene");

            Vector3 backAwayDirection = (witness.transform.position - crimeLocation).normalized;
            Vector3 backAwayTarget = witness.transform.position + backAwayDirection * UnityEngine.Random.Range(5f, 15f);

            if (witness.Movement != null && witness.Movement.CanMove())
            {
                witness.Movement.SetDestination(backAwayTarget);
            }

#if MONO
            witness.PlayVO(ScheduleOne.VoiceOver.EVOLineType.Concerned);
#else
            witness.PlayVO(Il2CppScheduleOne.VoiceOver.EVOLineType.Concerned);
#endif
        }

        /// <summary>
        /// Schedule a police call from a witness
        /// </summary>
        private void SchedulePoliceCall(NPC witness, CrimeInstance crime, Player perpetrator, float delay)
        {
            ModLogger.Info($"Scheduling police call from {witness.name} in {delay} seconds");

            MelonCoroutines.Start(DelayedPoliceCall(witness, crime, perpetrator, delay));
        }

        /// <summary>
        /// Coroutine to call police after a delay
        /// </summary>
        private IEnumerator DelayedPoliceCall(NPC witness, CrimeInstance crime, Player perpetrator, float delay)
        {
            yield return new WaitForSeconds(delay);

            // Check if witness is still alive and conscious
            if (witness == null || !witness.IsConscious)
            {
                ModLogger.Info("Witness is no longer able to call police");
                yield break;
            }

            // Check if witness was intimidated (attacked after witnessing)
            string witnessId = witness.ID;
            if (_witnesses.ContainsKey(witnessId) && _witnesses[witnessId].WasIntimidated)
            {
                ModLogger.Info($"Witness {witness.name} was intimidated - reducing chance of police call");
                if (UnityEngine.Random.Range(0f, 1f) < 0.3f) // Only 30% chance to call if intimidated
                {
                    yield break;
                }
            }

            ModLogger.Info($"Witness {witness.name} is calling police about {crime.GetCrimeName()}");

            // Call police through the law manager
            var lawManager = LawManager.Instance;
            if (lawManager != null && perpetrator != null && crime.Crime != null)
            {
                lawManager.PoliceCalled(perpetrator, crime.Crime);

                // Escalate based on crime severity
                if (crime.Severity >= 2.0f)
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.NonLethal);
                }
                else if (perpetrator.CrimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Investigating);
                }
            }
        }

        /// <summary>
        /// Mark a witness as intimidated (attacked after witnessing a crime)
        /// </summary>
        public void MarkWitnessIntimidated(string witnessId)
        {
            if (_witnesses.ContainsKey(witnessId))
            {
                _witnesses[witnessId].WasIntimidated = true;
                ModLogger.Info($"Witness {witnessId} marked as intimidated");
            }
        }

        /// <summary>
        /// Check if an NPC has witnessed any crimes
        /// </summary>
        public bool HasWitnessedCrimes(string witnessId)
        {
            return _witnesses.ContainsKey(witnessId) && _witnesses[witnessId].WitnessedCrimes.Count > 0;
        }

        /// <summary>
        /// Get all crimes witnessed by a specific NPC
        /// </summary>
        public List<CrimeInstance> GetWitnessedCrimes(string witnessId)
        {
            if (_witnesses.ContainsKey(witnessId))
            {
                return new List<CrimeInstance>(_witnesses[witnessId].WitnessedCrimes);
            }
            return new List<CrimeInstance>();
        }
    }

    /// <summary>
    /// Tracks the state of an individual witness
    /// </summary>
    public class WitnessState
    {
        public NPC Witness { get; set; }
        public List<CrimeInstance> WitnessedCrimes { get; set; } = new List<CrimeInstance>();
        public bool HasSeenPerpetrator { get; set; } = false;
        public string PerpetratorId { get; set; } = "";
        public bool WasIntimidated { get; set; } = false;
        public System.DateTime FirstWitnessTime { get; set; }

        public WitnessState(NPC witness)
        {
            Witness = witness;
            FirstWitnessTime = System.DateTime.Now;
        }

        public void AddWitnessedCrime(CrimeInstance crime)
        {
            if (!WitnessedCrimes.Contains(crime))
            {
                WitnessedCrimes.Add(crime);
            }
        }
    }
}