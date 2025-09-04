using System;
using System.Collections;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Guard-specific state machine with intervention capabilities
    /// </summary>
    public class GuardStateMachine : NPCStateMachine
    {
        // Guard Properties
        public string badgeNumber = "001";
        public GuardRole role = GuardRole.PatrolGuard;
        
        // Patrol Settings
        public Vector3[] patrolPoints = new Vector3[0];
        public Transform assignedPatrolPoint;
        public float patrolSpeed = 2f;
        public float waitTimeAtPoint = 5f;
        
        // Detection Settings
        public float detectionRange = 8f;
        public float suspicionThreshold = 0.7f;

        private float lastStateChangeTime;
        private Transform suspiciousTarget;

        protected override void InitializeStates()
        {
            // Register all guard states
            states[typeof(GuardIdleState)] = new GuardIdleState();
            states[typeof(GuardPatrolState)] = new GuardPatrolState();
            states[typeof(GuardInvestigateState)] = new GuardInvestigateState();
            states[typeof(GuardPatDownState)] = new GuardPatDownState();
            states[typeof(GuardInterventionState)] = new GuardInterventionState();
        }

        protected override void SetInitialState()
        {
            // Start with appropriate state based on role
            switch (role)
            {
                case GuardRole.PatrolGuard:
                    ChangeState<GuardPatrolState>();
                    break;
                case GuardRole.StationGuard:
                case GuardRole.WatchTowerGuard:
                case GuardRole.ResponseGuard:
                default:
                    ChangeState<GuardIdleState>();
                    break;
            }
        }

        protected override void Start()
        {
            // Don't initialize GuardStateMachine for TestNPCs at all
            if (gameObject.name.Contains("TestNPC"))
            {
                ModLogger.Warn($"⚠️  GuardStateMachine should not be on TestNPC {gameObject.name} - destroying it!");
                Destroy(this);
                return;
            }
            
            // Call base initialization first
            base.Start();
            
            // Assign a patrol point from the PatrolSystem
            if (role == GuardRole.PatrolGuard)
            {
                assignedPatrolPoint = PatrolSystem.AssignPatrolPointToGuard(transform);
                if (assignedPatrolPoint != null)
                {
                    ModLogger.Info($"Guard {gameObject.name} assigned to patrol point: {assignedPatrolPoint.name}");
                }
                else
                {
                    ModLogger.Warn($"Could not assign patrol point to guard: {gameObject.name}");
                }
            }
            
            //// Add door interaction component only to actual guards
            //if (GetComponent<GuardDoorInteraction>() == null)
            //{
            //    gameObject.AddComponent<GuardDoorInteraction>();
            //    ModLogger.Debug($"Added GuardDoorInteraction to {gameObject.name}");
            //}
        }

        protected override void Update()
        {
            base.Update();
            
            // Check for suspicious activity every few seconds
            if (Time.time - lastStateChangeTime > 2f)
            {
                CheckForSuspiciousActivity();
                lastStateChangeTime = Time.time;
            }
        }

        private void CheckForSuspiciousActivity()
        {
            // Only interrupt certain states
            if (IsInState<GuardPatDownState>() || IsInState<GuardInterventionState>()) return;

            // Look for inmates nearby
            var inmates = FindObjectsOfType<InmateStateMachine>();
            foreach (var inmate in inmates)
            {
                if (inmate == null) continue;
                
                float distance = Vector3.Distance(transform.position, inmate.transform.position);
                if (distance <= detectionRange)
                {
                    // Check if inmate is doing something suspicious
                    if (inmate.GetSuspicionLevel() > suspicionThreshold)
                    {
                        suspiciousTarget = inmate.transform;
                        ChangeState<GuardInvestigateState>();
                        break;
                    }
                    
                    // Check for aggression between inmates
                    if (inmate.IsInState<InmateAggressiveState>())
                    {
                        suspiciousTarget = inmate.transform;
                        ChangeState<GuardInterventionState>();
                        break;
                    }
                }
            }
        }

        public Transform GetSuspiciousTarget() => suspiciousTarget;
        public void ClearSuspiciousTarget() => suspiciousTarget = null;
        
        public GuardRole GetRole() => role;
        public Vector3[] GetPatrolPoints() => patrolPoints;
        public float GetPatrolSpeed() => patrolSpeed;
        public float GetWaitTime() => waitTimeAtPoint;
    }

    public enum GuardRole
    {
        PatrolGuard,
        StationGuard,
        WatchTowerGuard,
        ResponseGuard
    }

    #region Guard States

    public class GuardIdleState : IState
    {
        private float idleStartTime;
        private float nextActionTime;

        public void Enter(NPCStateMachine npc)
        {
            idleStartTime = Time.time;
            nextActionTime = Time.time + UnityEngine.Random.Range(10f, 30f);
            
            var guard = (GuardStateMachine)npc;
            ModLogger.Debug($"Guard {guard.badgeNumber} entering idle state");
        }

        public void Update(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            
            // Occasionally look around
            if (Time.time > nextActionTime)
            {
                // Random small rotation
                float randomRotation = UnityEngine.Random.Range(-45f, 45f);
                npc.transform.rotation = Quaternion.Euler(0, npc.transform.eulerAngles.y + randomRotation, 0);
                nextActionTime = Time.time + UnityEngine.Random.Range(15f, 45f);
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            // Nothing to clean up
        }
    }

    public class GuardPatrolState : IState
    {
        private int currentPatrolIndex = 0;
        private bool isMovingToPoint = false;

        public void Enter(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            var navAgent = npc.GetNavAgent();
            
            // Use assigned patrol point from PatrolSystem if available
            if (guard.assignedPatrolPoint != null)
            {
                ModLogger.Info($"Guard {guard.badgeNumber} starting patrol to assigned point: {guard.assignedPatrolPoint.name}");
                navAgent.SetDestination(guard.assignedPatrolPoint.position);
                isMovingToPoint = true;
                return;
            }
            
            // Fallback to old patrol points system
            if (guard.GetPatrolPoints().Length == 0)
            {
                ModLogger.Warn($"Guard {guard.badgeNumber} has no patrol points, switching to idle");
                guard.ChangeState<GuardIdleState>();
                return;
            }

            navAgent.speed = guard.GetPatrolSpeed();
            MoveToNextPatrolPoint(guard);
        }

        public void Update(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            
            if (isMovingToPoint)
            {
                // Check if we've reached our destination
                if (npc.HasReachedDestination())
                {
                    isMovingToPoint = false;
                    // Start wait timer
                    MelonCoroutines.Start(WaitAtPatrolPoint(guard));
                }
            }
        }

        private void MoveToNextPatrolPoint(GuardStateMachine guard)
        {
            var patrolPoints = guard.GetPatrolPoints();
            var navAgent = guard.GetNavAgent();
            
            if (patrolPoints.Length > 0)
            {
                Vector3 targetPoint = patrolPoints[currentPatrolIndex];
                navAgent.SetDestination(targetPoint);
                isMovingToPoint = true;
                
                ModLogger.Debug($"Guard {guard.badgeNumber} moving to patrol point {currentPatrolIndex}: {targetPoint}");
                
                // Move to next point for next time
                currentPatrolIndex = (currentPatrolIndex + 1) % patrolPoints.Length;
            }
        }

        private IEnumerator WaitAtPatrolPoint(GuardStateMachine guard)
        {
            float waitTime = guard.GetWaitTime();
            ModLogger.Debug($"Guard {guard.badgeNumber} waiting at patrol point for {waitTime} seconds");
            
            yield return new WaitForSeconds(waitTime);
            
            // Only continue patrol if still in patrol state (could have been interrupted)
            if (guard.IsInState<GuardPatrolState>())
            {
                MoveToNextPatrolPoint(guard);
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            isMovingToPoint = false;
        }
    }

    public class GuardInvestigateState : IState
    {
        private float investigationStartTime;

        public void Enter(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            var target = guard.GetSuspiciousTarget();
            
            investigationStartTime = Time.time;
            ModLogger.Info($"Guard {guard.badgeNumber} investigating suspicious activity");
            
            if (target != null)
            {
                // Move towards the suspicious target
                var navAgent = npc.GetNavAgent();
                navAgent.speed = guard.GetPatrolSpeed() * 1.5f; // Move faster when investigating
                navAgent.SetDestination(target.position);
            }
        }

        public void Update(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            var target = guard.GetSuspiciousTarget();
            
            if (target == null)
            {
                // Lost target, return to patrol
                guard.ClearSuspiciousTarget();
                if (guard.GetRole() == GuardRole.PatrolGuard)
                    guard.ChangeState<GuardPatrolState>();
                else
                    guard.ChangeState<GuardIdleState>();
                return;
            }

            // Check if we're close enough to start pat down
            float distance = Vector3.Distance(npc.transform.position, target.position);
            if (distance < 2f)
            {
                guard.ChangeState<GuardPatDownState>();
            }
            else if (Time.time - investigationStartTime > 15f)
            {
                // Give up investigation after 15 seconds
                guard.ClearSuspiciousTarget();
                if (guard.GetRole() == GuardRole.PatrolGuard)
                    guard.ChangeState<GuardPatrolState>();
                else
                    guard.ChangeState<GuardIdleState>();
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            // Reset speed
            var guard = (GuardStateMachine)npc;
            var navAgent = npc.GetNavAgent();
            navAgent.speed = guard.GetPatrolSpeed();
        }
    }

    public class GuardPatDownState : IState
    {
        private float patDownStartTime;

        public void Enter(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            patDownStartTime = Time.time;
            
            ModLogger.Info($"Guard {guard.badgeNumber} conducting pat down");
            
            // Stop moving
            var navAgent = npc.GetNavAgent();
            navAgent.ResetPath();
        }

        public void Update(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            
            // Pat down takes 5 seconds
            if (Time.time - patDownStartTime > 5f)
            {
                ModLogger.Info($"Guard {guard.badgeNumber} completed pat down");
                guard.ClearSuspiciousTarget();
                
                // Return to normal duty
                if (guard.GetRole() == GuardRole.PatrolGuard)
                    guard.ChangeState<GuardPatrolState>();
                else
                    guard.ChangeState<GuardIdleState>();
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            // Nothing to clean up
        }
    }

    public class GuardInterventionState : IState
    {
        private float interventionStartTime;

        public void Enter(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            interventionStartTime = Time.time;
            
            ModLogger.Info($"Guard {guard.badgeNumber} intervening in aggressive situation");
            
            var target = guard.GetSuspiciousTarget();
            if (target != null)
            {
                var navAgent = npc.GetNavAgent();
                navAgent.speed = guard.GetPatrolSpeed() * 2f; // Move fast to intervene
                navAgent.SetDestination(target.position);
            }
        }

        public void Update(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            var target = guard.GetSuspiciousTarget();
            
            if (target == null)
            {
                // Situation resolved, return to normal duty
                guard.ClearSuspiciousTarget();
                if (guard.GetRole() == GuardRole.PatrolGuard)
                    guard.ChangeState<GuardPatrolState>();
                else
                    guard.ChangeState<GuardIdleState>();
                return;
            }

            float distance = Vector3.Distance(npc.transform.position, target.position);
            if (distance < 3f)
            {
                // Close enough to intervene - tell the inmate to calm down
                var inmate = target.GetComponent<InmateStateMachine>();
                if (inmate != null)
                {
                    inmate.ChangeState<InmateIdleState>(); // Force inmate to calm down
                    ModLogger.Info($"Guard {guard.badgeNumber} successfully intervened");
                }
                
                guard.ClearSuspiciousTarget();
                if (guard.GetRole() == GuardRole.PatrolGuard)
                    guard.ChangeState<GuardPatrolState>();
                else
                    guard.ChangeState<GuardIdleState>();
            }
        }

        public void Exit(NPCStateMachine npc)
        {
            var guard = (GuardStateMachine)npc;
            var navAgent = npc.GetNavAgent();
            navAgent.speed = guard.GetPatrolSpeed(); // Reset speed
        }
    }

    #endregion
}