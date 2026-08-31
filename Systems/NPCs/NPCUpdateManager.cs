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

        // Registered NPCs
        private readonly List<BaseJailNPC> _registeredNPCs = new List<BaseJailNPC>();

        // Update intervals (in seconds)
        private const float STATE_UPDATE_INTERVAL = 0.1f;      // 10 Hz - State machine updates
        private const float MOVEMENT_CHECK_INTERVAL = 0.5f;    // 2 Hz - Stuck detection

        private float _lastStateUpdate;
        private float _lastMovementCheck;

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
            CleanupNullNpcs();

            bool runStateUpdate = currentTime - _lastStateUpdate >= STATE_UPDATE_INTERVAL;
            bool runMovementCheck = currentTime - _lastMovementCheck >= MOVEMENT_CHECK_INTERVAL;

            if (!runStateUpdate && !runMovementCheck)
                return;

            for (int i = 0; i < _registeredNPCs.Count; i++)
            {
                var npc = _registeredNPCs[i];
                if (npc == null)
                    continue;

                if (runStateUpdate)
                    npc.DispatchStateUpdate(currentTime);

                if (runMovementCheck)
                    npc.DispatchMovementCheck(currentTime);
            }

            if (runStateUpdate)
                _lastStateUpdate = currentTime;

            if (runMovementCheck)
                _lastMovementCheck = currentTime;
        }

        private void CleanupNullNpcs()
        {
            for (int i = _registeredNPCs.Count - 1; i >= 0; i--)
            {
                if (_registeredNPCs[i] == null)
                    _registeredNPCs.RemoveAt(i);
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
