using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Base class for NPC state machines
    /// </summary>
    public abstract class NPCStateMachine : MonoBehaviour
    {
        protected IState currentState;
        protected Dictionary<Type, IState> states = new Dictionary<Type, IState>();
        protected UnityEngine.AI.NavMeshAgent navAgent;
        protected bool isInitialized = false;

        protected virtual void Start()
        {
            navAgent = GetComponent<UnityEngine.AI.NavMeshAgent>();
            InitializeStates();
            SetInitialState();
            isInitialized = true;
            
            ModLogger.Debug($"State machine initialized for {gameObject.name}");
        }

        protected virtual void Update()
        {
            if (!isInitialized || currentState == null) return;
            
            currentState.Update(this);
        }

        protected virtual void InitializeStates() { }
        protected virtual void SetInitialState() { }

        public void ChangeState<T>() where T : IState
        {
            if (states.TryGetValue(typeof(T), out IState newState))
            {
                currentState?.Exit(this);
                currentState = newState;
                currentState.Enter(this);
                
                ModLogger.Debug($"{gameObject.name} changed to state: {typeof(T).Name}");
            }
        }

        public void ChangeState(Type stateType)
        {
            if (states.TryGetValue(stateType, out IState newState))
            {
                currentState?.Exit(this);
                currentState = newState;
                currentState.Enter(this);
                
                ModLogger.Debug($"{gameObject.name} changed to state: {stateType.Name}");
            }
        }

        public T GetState<T>() where T : IState
        {
            if (states.TryGetValue(typeof(T), out IState state))
                return (T)state;
            return default(T);
        }

        public bool IsInState<T>() where T : IState
        {
            return currentState != null && currentState.GetType() == typeof(T);
        }

        public UnityEngine.AI.NavMeshAgent GetNavAgent() => navAgent;
        public bool HasReachedDestination()
        {
            if (navAgent == null || !navAgent.enabled) return true;
            return !navAgent.pathPending && navAgent.remainingDistance < 1.5f;
        }
    }

    /// <summary>
    /// Interface for NPC states
    /// </summary>
    public interface IState
    {
        void Enter(NPCStateMachine npc);
        void Update(NPCStateMachine npc);
        void Exit(NPCStateMachine npc);
    }
}