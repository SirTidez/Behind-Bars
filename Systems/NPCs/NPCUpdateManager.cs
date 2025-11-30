using System;
using System.Collections.Generic;
using Behind_Bars.Helpers;
using UnityEngine;
using Behind_Bars.Utils;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
#endif

namespace Behind_Bars.Systems.NPCs
{
#if MONO
    /// <summary>
    /// Centralized update manager for all jail NPCs.
    /// Replaces individual Update() loops with event-driven pattern.
    /// Reduces CPU overhead by consolidating per-frame checks into throttled intervals.
    /// </summary>
    public class NPCUpdateManager : MonoBehaviour
#else
    /// <summary>
    /// Centralized update manager for all jail NPCs.
    /// Replaces individual Update() loops with event-driven pattern.
    /// Reduces CPU overhead by consolidating per-frame checks into throttled intervals.
    /// </summary>
    public class NPCUpdateManager : MonoBehaviour
#endif
    {
        private static NPCUpdateManager _instance;
        public static NPCUpdateManager Instance => _instance;

        // Events for NPC subsystems
        public event Action<float> OnNPCStateUpdate;      // For state machine updates
        public event Action<float> OnNPCMovementCheck;    // For stuck detection
        public event Action OnNPCActionProcess;           // For action queues

        // Registered NPCs
        private readonly List<BaseJailNPC> _registeredNPCs = new List<BaseJailNPC>();

        // Update intervals (in seconds)
        private const float STATE_UPDATE_INTERVAL = 0.1f;      // 10 Hz - State machine updates
        private const float MOVEMENT_CHECK_INTERVAL = 0.5f;    // 2 Hz - Stuck detection
        private const float ACTION_PROCESS_INTERVAL = 0.033f;  // 30 Hz - Action queue processing

        private float _lastStateUpdate;
        private float _lastMovementCheck;
        private float _lastActionProcess;

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
            ModLogger.Info("NPCUpdateManager initialized - Event-driven NPC updates enabled");
        }

        void Update()
        {
            float currentTime = Time.time;

            // State updates (10 Hz) - Reduced from 60 fps
            if (currentTime - _lastStateUpdate >= STATE_UPDATE_INTERVAL)
            {
                OnNPCStateUpdate?.Invoke(currentTime);
                _lastStateUpdate = currentTime;
            }

            // Movement checks (2 Hz) - Reduced from 60 fps
            if (currentTime - _lastMovementCheck >= MOVEMENT_CHECK_INTERVAL)
            {
                OnNPCMovementCheck?.Invoke(currentTime);
                _lastMovementCheck = currentTime;
            }

            // Action processing (30 Hz) - Reduced from 60 fps
            if (currentTime - _lastActionProcess >= ACTION_PROCESS_INTERVAL)
            {
                OnNPCActionProcess?.Invoke();
                _lastActionProcess = currentTime;
            }
        }

        public void RegisterNPC(BaseJailNPC npc)
        {
            if (npc == null) return;

            if (!_registeredNPCs.Contains(npc))
            {
                _registeredNPCs.Add(npc);
                ModLogger.Debug($"NPCUpdateManager: Registered {npc.gameObject.name} ({_registeredNPCs.Count} total NPCs)");
            }
        }

        public void UnregisterNPC(BaseJailNPC npc)
        {
            if (npc == null) return;

            if (_registeredNPCs.Remove(npc))
            {
                ModLogger.Debug($"NPCUpdateManager: Unregistered {npc.gameObject.name} ({_registeredNPCs.Count} remaining NPCs)");
            }
        }

        public int GetRegisteredNPCCount()
        {
            return _registeredNPCs.Count;
        }

        void OnDestroy()
        {
            _registeredNPCs.Clear();
            _instance = null;
            ModLogger.Info("NPCUpdateManager destroyed");
        }
    }
}
