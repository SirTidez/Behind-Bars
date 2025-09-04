using System;
using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Inmate-specific state machine with behavioral patterns
    /// </summary>
    public class InmateStateMachine : NPCStateMachine
    {
        // Inmate Properties
        public string prisonerID = "INM001";
        public InmateStatus status = InmateStatus.NewInmate;
        public string crimeType = "Unknown";
        
        // Behavior Settings
        public float behaviorScore = 50f; // 0-100, higher is better behavior
        public float aggressionLevel = 0f; // 0-1, higher is more aggressive
        public float suspicionLevel = 0f; // 0-1, higher is more suspicious
        
        // Area Settings
        public Vector3[] idlePositions = new Vector3[0];
        public Vector3 cellPosition;

        private float lastBehaviorUpdate;
        private float stateTimer;

        protected override void InitializeStates()
        {
            // Register all inmate states
            states[typeof(InmateIdleState)] = new InmateIdleState();
            states[typeof(InmateWanderState)] = new InmateWanderState();
            states[typeof(InmateRecreationState)] = new InmateRecreationState();
            states[typeof(InmateSuspiciousState)] = new InmateSuspiciousState();
            states[typeof(InmateAggressiveState)] = new InmateAggressiveState();
        }

        protected override void SetInitialState()
        {
            ChangeState<InmateIdleState>();
        }

        protected override void Update()
        {
            base.Update();
            
            // Update behavior patterns periodically
            if (Time.time - lastBehaviorUpdate > 5f)
            {
                UpdateBehaviorPatterns();
                lastBehaviorUpdate = Time.time;
            }
        }

        private void UpdateBehaviorPatterns()
        {
            // Randomly adjust behavior over time
            float behaviorChange = UnityEngine.Random.Range(-1f, 1f);
            behaviorScore = Mathf.Clamp(behaviorScore + behaviorChange, 0f, 100f);
            
            // Update aggression and suspicion based on behavior
            if (behaviorScore < 30f)
            {
                aggressionLevel = Mathf.Min(1f, aggressionLevel + UnityEngine.Random.Range(0f, 0.1f));
                suspicionLevel = Mathf.Min(1f, suspicionLevel + UnityEngine.Random.Range(0f, 0.05f));
            }
            else if (behaviorScore > 70f)
            {
                aggressionLevel = Mathf.Max(0f, aggressionLevel - UnityEngine.Random.Range(0f, 0.05f));
                suspicionLevel = Mathf.Max(0f, suspicionLevel - UnityEngine.Random.Range(0f, 0.02f));
            }

            // Check for state transitions based on behavior
            CheckBehaviorStateTransitions();
        }

        private void CheckBehaviorStateTransitions()
        {
            // Don't interrupt certain states
            if (IsInState<InmateAggressiveState>()) return;
            
            // High aggression leads to aggressive state
            if (aggressionLevel > 0.8f && !IsInState<InmateAggressiveState>())
            {
                ChangeState<InmateAggressiveState>();
            }
            // High suspicion leads to suspicious behavior
            else if (suspicionLevel > 0.6f && !IsInState<InmateSuspiciousState>())
            {
                ChangeState<InmateSuspiciousState>();
            }
            // Normal behavior transitions
            else if (suspicionLevel < 0.3f && aggressionLevel < 0.3f)
            {
                // Random state selection for normal inmates
                if (UnityEngine.Random.Range(0f, 1f) < 0.1f) // 10% chance per check
                {
                    var normalStates = new Type[] { typeof(InmateIdleState), typeof(InmateWanderState), typeof(InmateRecreationState) };
                    var randomState = normalStates[UnityEngine.Random.Range(0, normalStates.Length)];
                    ChangeState(randomState);
                }
            }
        }

        public float GetSuspicionLevel() => suspicionLevel;
        public float GetAggressionLevel() => aggressionLevel;
        public float GetBehaviorScore() => behaviorScore;
        public InmateStatus GetStatus() => status;
        public Vector3[] GetIdlePositions() => idlePositions;
        public Vector3 GetCellPosition() => cellPosition;

        public void ModifyBehaviorScore(float change)
        {
            behaviorScore = Mathf.Clamp(behaviorScore + change, 0f, 100f);
        }
    }

    public enum InmateStatus
    {
        NewInmate,
        RegularInmate,
        ModelInmate,
        TroubleInmate
    }

    #region Inmate States

    public class InmateIdleState : IState
    {
        private float idleStartTime;
        private float nextStateChangeTime;

        public void Enter(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            idleStartTime = Time.time;
            nextStateChangeTime = Time.time + UnityEngine.Random.Range(10f, 30f);
            
            // Stop any movement
            var navAgent = npc.GetNavAgent();
            if (navAgent != null) navAgent.ResetPath();
            
            ModLogger.Debug($"Inmate {inmate.prisonerID} entering idle state");
        }

        public void Update(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            
            // Stay idle for a while, then potentially change state
            if (Time.time > nextStateChangeTime && UnityEngine.Random.Range(0f, 1f) < 0.3f)
            {
                // Choose next state based on behavior
                if (inmate.GetBehaviorScore() > 60f)
                {
                    inmate.ChangeState<InmateRecreationState>();
                }
                else
                {
                    inmate.ChangeState<InmateWanderState>();
                }
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            // Nothing to clean up
        }
    }

    public class InmateWanderState : IState
    {
        private float wanderStartTime;
        private bool isMoving = false;

        public void Enter(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            wanderStartTime = Time.time;
            
            MoveToRandomPosition(inmate);
            ModLogger.Debug($"Inmate {inmate.prisonerID} starting to wander");
        }

        public void Update(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            
            if (isMoving && npc.HasReachedDestination())
            {
                isMoving = false;
                // Wait a bit, then either move again or go idle
                MelonCoroutines.Start(WaitThenDecideNextAction(inmate));
            }
            
            // Don't wander too long
            if (Time.time - wanderStartTime > 60f)
            {
                inmate.ChangeState<InmateIdleState>();
            }
        }

        private void MoveToRandomPosition(InmateStateMachine inmate)
        {
            var idlePositions = inmate.GetIdlePositions();
            var navAgent = inmate.GetNavAgent();
            
            if (idlePositions.Length > 0 && navAgent != null)
            {
                Vector3 targetPos = idlePositions[UnityEngine.Random.Range(0, idlePositions.Length)];
                navAgent.SetDestination(targetPos);
                isMoving = true;
                
                ModLogger.Debug($"Inmate {inmate.prisonerID} wandering to {targetPos}");
            }
        }

        private IEnumerator WaitThenDecideNextAction(InmateStateMachine inmate)
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(3f, 8f));
            
            if (inmate.IsInState<InmateWanderState>())
            {
                if (UnityEngine.Random.Range(0f, 1f) < 0.6f)
                {
                    MoveToRandomPosition(inmate);
                }
                else
                {
                    inmate.ChangeState<InmateIdleState>();
                }
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            isMoving = false;
        }
    }

    public class InmateRecreationState : IState
    {
        private float recreationStartTime;

        public void Enter(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            recreationStartTime = Time.time;
            
            // Move to a recreation area
            var idlePositions = inmate.GetIdlePositions();
            if (idlePositions.Length > 0)
            {
                var navAgent = npc.GetNavAgent();
                Vector3 recPos = idlePositions[UnityEngine.Random.Range(0, idlePositions.Length)];
                navAgent.SetDestination(recPos);
            }
            
            ModLogger.Debug($"Inmate {inmate.prisonerID} starting recreation");
        }

        public void Update(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            
            // Recreation improves behavior slightly
            if (Time.time - recreationStartTime > 20f)
            {
                inmate.ModifyBehaviorScore(UnityEngine.Random.Range(0f, 2f));
                inmate.ChangeState<InmateIdleState>();
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            // Nothing to clean up
        }
    }

    public class InmateSuspiciousState : IState
    {
        private float suspiciousStartTime;

        public void Enter(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            suspiciousStartTime = Time.time;
            
            ModLogger.Info($"Inmate {inmate.prisonerID} acting suspiciously");
            
            // Move to a corner or secluded area
            var navAgent = npc.GetNavAgent();
            var idlePositions = inmate.GetIdlePositions();
            if (idlePositions.Length > 0)
            {
                Vector3 secludedPos = idlePositions[UnityEngine.Random.Range(0, idlePositions.Length)];
                navAgent.SetDestination(secludedPos);
            }
        }

        public void Update(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            
            // Suspicious behavior continues for a while
            if (Time.time - suspiciousStartTime > 15f)
            {
                // Reduce suspicion level gradually
                inmate.suspicionLevel = Mathf.Max(0f, inmate.suspicionLevel - 0.1f);
                
                if (inmate.suspicionLevel < 0.4f)
                {
                    inmate.ChangeState<InmateIdleState>();
                }
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            ModLogger.Debug($"Inmate {inmate.prisonerID} no longer acting suspiciously");
        }
    }

    public class InmateAggressiveState : IState
    {
        private float aggressionStartTime;

        public void Enter(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            aggressionStartTime = Time.time;
            
            ModLogger.Warn($"Inmate {inmate.prisonerID} becoming aggressive!");
            
            // Stop current movement and look threatening
            var navAgent = npc.GetNavAgent();
            navAgent.ResetPath();
        }

        public void Update(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            
            // Aggression continues until intervention or timeout
            if (Time.time - aggressionStartTime > 30f)
            {
                // Calm down gradually
                inmate.aggressionLevel = Mathf.Max(0f, inmate.aggressionLevel - 0.2f);
                
                if (inmate.aggressionLevel < 0.5f)
                {
                    inmate.ChangeState<InmateIdleState>();
                }
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            var inmate = (InmateStateMachine)npc;
            ModLogger.Info($"Inmate {inmate.prisonerID} calmed down");
        }
    }

    #endregion
}