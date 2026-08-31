using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Jail;
using MelonLoader;
using Behind_Bars.Systems.Crimes;

#if !MONO
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.NPCs.Behaviour;
#else
using ScheduleOne.DevUtilities;
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
        // Keys are native NPC IDs. The references and their crime lists are scene-local;
        // ResetSceneRuntimeState clears them before a newly loaded save can reuse an ID.
        private Dictionary<string, WitnessState> _witnesses = new Dictionary<string, WitnessState>();

        // Delayed Melon coroutines capture this generation. Incrementing it invalidates work
        // scheduled by an earlier Main scene without requiring unsafe coroutine enumeration.
        private int _sceneGeneration;

        /// <summary>Creates an empty, scene-local witness state store.</summary>
        public WitnessSystem()
        {
            ModLogger.Info("Witness system initialized");
        }

        /// <summary>
        /// Records an NPC's observation and dispatches the appropriate police/civilian response.
        /// </summary>
        /// <param name="witness">NPC that observed the crime.</param>
        /// <param name="crime">Local crime instance being observed.</param>
        /// <param name="perpetrator">Player attributed with the crime.</param>
        public void NPCWitnessesCrime(NPC witness, CrimeInstance crime, Player perpetrator)
        {
            if (witness == null || crime == null || perpetrator == null)
                return;

            ModLogger.Info($"NPC {witness.name} witnessed crime: {crime.GetCrimeName()}");

            // State is keyed by the native witness ID. A blank/duplicate ID therefore
            // shares one state under the current implementation; native IDs are expected
            // to be populated by the game.
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
            else if (CrimeDetectionSystem.Instance != null && CrimeDetectionSystem.Instance.IsModLawEnforcementNpc(witness))
            {
                // Mod officers are handled by officer behavior and arrest flows.
                // Do not treat them as civilian witnesses.
                ModLogger.Debug($"Skipping civilian witness behavior for law enforcement NPC {witness.name}");
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

            // The diagnostic message uses "pursuit" broadly; current behavior sends serious
            // crimes to foot pursuit while minor crimes use the native body-search response.
            if (crime.Severity >= 2.0f) // Serious crimes
            {
                NetworkHelper.TryBeginFootPursuit(police, perpetrator);
            }
            else
            {
                // For minor crimes, just investigate
                NetworkHelper.TryBeginBodySearch(police, perpetrator);
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
            ModLogger.Debug($"[FEAR CALC] Witness {witness.name} fear level: {fearLevel}");
            
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
            // Scale dynamically based on crime type detection radius
            float distance = Vector3.Distance(witness.transform.position, crime.Location);
            
            // Determine detection radius based on crime type
            float detectionRadius;
            if (crime.Crime is Murder)
                detectionRadius = 50f; // MurderDetectionRadius
            else if (crime.Crime is Manslaughter)
                detectionRadius = 50f; // MurderDetectionRadius (same as murder)
            else if (crime.Crime is AssaultOnCivilian)
                detectionRadius = 30f; // AssaultDetectionRadius
            else if (crime.Crime is WitnessIntimidation)
                detectionRadius = 30f; // AssaultDetectionRadius (uses same radius)
            else
                detectionRadius = 15f; // Default to assault radius
            
            // Normalize distance to detection radius and apply steeper curve for more sensitivity
            float normalizedDistance = Mathf.Clamp01(distance / detectionRadius);
            // Use cubic curve (distance^3) for much steeper falloff - makes distance VERY impactful
            // Closer witnesses get much higher fear, distance matters a lot more
            float distanceFactor = 1.0f - (normalizedDistance * normalizedDistance * normalizedDistance);

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
            TrySetPanicked(witness);
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

        private static void TrySetPanicked(NPC witness)
        {
            // SetPanicked is not exposed consistently across runtime/API shapes. Reflection is
            // intentionally best-effort: failure affects presentation only, not witness memory
            // or the delayed police-call contract.
            if (witness == null)
            {
                return;
            }

            try
            {
                var setPanickedMethod = witness.GetType().GetMethod("SetPanicked", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                setPanickedMethod?.Invoke(witness, null);
            }
            catch (System.Exception ex)
            {
                ModLogger.Debug($"Witness panic state unavailable for {witness.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Schedule a police call from a witness
        /// </summary>
        private void SchedulePoliceCall(NPC witness, CrimeInstance crime, Player perpetrator, float delay)
        {
            // delay is a Unity WaitForSeconds duration (seconds), while the captured
            // crime timestamp remains game-clock minutes. The generation token is the
            // cancellation mechanism for scene transitions.
            ModLogger.Info($"Scheduling police call from {witness.name} in {delay} seconds");

            MelonCoroutines.Start(DelayedPoliceCall(witness, crime, perpetrator, delay, _sceneGeneration));
        }

        /// <summary>
        /// Coroutine to call police after a delay
        /// </summary>
        private IEnumerator DelayedPoliceCall(NPC witness, CrimeInstance crime, Player perpetrator, float delay, int generation)
        {
            yield return new WaitForSeconds(delay);

            // Validate in order from cheapest/lifecycle checks to game-state checks before
            // touching LawManager. A crime with no native object reaches the final guard
            // and is intentionally logged but not mirrored into native law.
            // Melon coroutines are process-owned. A stale witness must never reach into
            // a newly loaded save after the scene that scheduled it has gone away.
            if (generation != _sceneGeneration || !Core.IsGameplaySceneActive)
            {
                yield break;
            }

            // Check if witness is still alive and conscious
            if (witness == null || !witness.IsConscious)
            {
                ModLogger.Info("Witness is no longer able to call police");
                yield break;
            }

            // Abort stale witness calls once arrest/jail processing has started.
            if (ShouldSuppressPoliceCall(perpetrator))
            {
                ModLogger.Debug($"Skipping delayed police call because {perpetrator?.name} is already in arrest/jail flow");
                yield break;
            }

            // Avoid duplicate native crime additions when the same offense is already active.
            if (ShouldSkipDuplicatePoliceCall(perpetrator, crime))
            {
                ModLogger.Debug($"Skipping delayed duplicate police call for {perpetrator?.name} on crime {crime?.GetCrimeName()}");
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
            var lawManager = Singleton<LawManager>.Instance;
            if (lawManager != null && perpetrator != null && crime.Crime != null)
            {
                lawManager.PoliceCalled(perpetrator, crime.Crime);

                // Escalate based on crime severity
                if (crime.Severity >= 2.0f)
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                }
                else
                {
                    perpetrator.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.NonLethal);
                }
            }
        }

        private bool ShouldSuppressPoliceCall(Player perpetrator)
        {
            // A delayed civilian call must not re-enter the native law system after arrest or
            // jail intake has already taken ownership of the incident.
            if (perpetrator == null)
            {
                return true;
            }

            if (perpetrator.IsArrested)
            {
                return true;
            }

            try
            {
                var jailTimeTracker = Core.ResolveJailTimeTracker();
                return jailTimeTracker != null && jailTimeTracker.IsInJail(perpetrator);
            }
            catch
            {
                // Current failure policy is fail-open when the jail tracker cannot be
                // resolved; the caller may proceed unless the native IsArrested flag says
                // otherwise.
                return false;
            }
        }

        private bool ShouldSkipDuplicatePoliceCall(Player perpetrator, CrimeInstance crimeInstance)
        {
            // Native pursuit state is treated as evidence that the offense is already active.
            // Otherwise compare both native type/display name and the assault-family alias so
            // differently represented assault charges cannot schedule duplicate calls.
            if (perpetrator == null || crimeInstance == null || crimeInstance.Crime == null)
            {
                return false;
            }

            var crimeData = perpetrator.CrimeData;
            if (crimeData == null)
            {
                return false;
            }

            if (crimeData.CurrentPursuitLevel != PlayerCrimeData.EPursuitLevel.None)
            {
                return true;
            }

            if (crimeData.Crimes == null || crimeData.Crimes.Count == 0)
            {
                return false;
            }

            string targetType = crimeInstance.Crime.GetType().Name;
            string targetName = crimeInstance.Crime.CrimeName ?? string.Empty;
            bool targetIsAssaultFamily = targetType.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0
                                        || targetName.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0;

            foreach (var crimeEntry in crimeData.Crimes)
            {
                var existingCrime = crimeEntry.Key;
                if (existingCrime == null)
                    continue;

                string existingType = existingCrime.GetType().Name;
                string existingName = existingCrime.CrimeName ?? string.Empty;

                if (string.Equals(existingType, targetType, StringComparison.Ordinal)
                    || string.Equals(existingName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                bool existingIsAssaultFamily = existingType.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0
                                             || existingName.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0;
                if (targetIsAssaultFamily && existingIsAssaultFamily)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Marks an already tracked witness as intimidated.
        /// </summary>
        /// <param name="witnessId">Native witness identifier.</param>
        public void MarkWitnessIntimidated(string witnessId)
        {
            // Intimidation does not create witness state. It only changes the delayed-call
            // odds for an NPC that has already observed at least one crime.
            if (_witnesses.ContainsKey(witnessId))
            {
                _witnesses[witnessId].WasIntimidated = true;
                ModLogger.Info($"Witness {witnessId} marked as intimidated");
            }
        }

        /// <summary>
        /// Checks whether a tracked witness has at least one observed crime.
        /// </summary>
        /// <param name="witnessId">Native witness identifier.</param>
        /// <returns>True when a witness state exists with a non-empty crime list.</returns>
        public bool HasWitnessedCrimes(string witnessId)
        {
            return _witnesses.ContainsKey(witnessId) && _witnesses[witnessId].WitnessedCrimes.Count > 0;
        }

        /// <summary>
        /// Gets a detached list of crimes witnessed by a specific NPC.
        /// </summary>
        /// <param name="witnessId">Native witness identifier.</param>
        /// <returns>A copied list, or an empty list when no state exists.</returns>
        public List<CrimeInstance> GetWitnessedCrimes(string witnessId)
        {
            if (_witnesses.ContainsKey(witnessId))
            {
                return new List<CrimeInstance>(_witnesses[witnessId].WitnessedCrimes);
            }
            return new List<CrimeInstance>();
        }

        /// <summary>
        /// Invalidates delayed witness work and releases scene NPC references at the
        /// Main-to-Menu boundary.
        /// </summary>
        public void ResetSceneRuntimeState()
        {
            // Existing coroutines are not enumerated or stopped; incrementing the token
            // makes each one self-cancel after its wait, while clearing references now.
            _sceneGeneration++;
            _witnesses.Clear();
        }
    }

    /// <summary>
    /// Tracks the state of an individual witness
    /// </summary>
    public class WitnessState
    {
        /// <summary>Gets or sets the scene NPC represented by this witness state.</summary>
        public NPC Witness { get; set; }

        /// <summary>Gets or sets the unique local crime instances observed by the witness.</summary>
        public List<CrimeInstance> WitnessedCrimes { get; set; } = new List<CrimeInstance>();

        /// <summary>Gets or sets whether this witness has identified the perpetrator.</summary>
        public bool HasSeenPerpetrator { get; set; } = false;

        /// <summary>Gets or sets the player identity recorded when the perpetrator was seen.</summary>
        public string PerpetratorId { get; set; } = "";

        /// <summary>Gets or sets whether intimidation reduced this witness's willingness to call.</summary>
        public bool WasIntimidated { get; set; } = false;

        /// <summary>Gets or sets the wall-clock time at which this witness first recorded a crime.</summary>
        public System.DateTime FirstWitnessTime { get; set; }

        /// <summary>Creates scene-local state for the specified witness NPC.</summary>
        /// <param name="witness">NPC whose observations are tracked.</param>
        public WitnessState(NPC witness)
        {
            Witness = witness;
            FirstWitnessTime = System.DateTime.Now;
        }

        /// <summary>
        /// Adds a crime reference once. Repeated witness notifications for the same instance do
        /// not create duplicate entries in this witness's history.
        /// </summary>
        /// <param name="crime">Crime instance observed by the witness.</param>
        public void AddWitnessedCrime(CrimeInstance crime)
        {
            if (!WitnessedCrimes.Contains(crime))
            {
                WitnessedCrimes.Add(crime);
            }
        }
    }
}
