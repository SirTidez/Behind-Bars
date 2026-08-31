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
    /// Scene-local dispatcher for registered jail NPC state and movement checks.
    /// Work is consolidated into throttled, scaled <c>Time.time</c> intervals;
    /// registration itself is a list operation, not an event subscription.
    /// </summary>
    public class NPCUpdateManager : MonoBehaviour
#else
    /// <summary>
    /// Scene-local dispatcher for registered jail NPC state and movement checks.
    /// Work is consolidated into throttled, scaled <c>Time.time</c> intervals;
    /// registration itself is a list operation, not an event subscription.
    /// </summary>
    public class NPCUpdateManager : MonoBehaviour
#endif
    {
        private static NPCUpdateManager _instance;

        /// <summary>
        /// Gets the scene's centralized NPC dispatcher.  This property does not
        /// create a manager; it remains null until a scene instance reaches
        /// <see cref="Awake"/>.
        /// </summary>
        public static NPCUpdateManager Instance => _instance;

        // Registered NPCs receive both dispatches from one cadence loop. Null
        // entries are pruned before the loop begins.
        private readonly List<BaseJailNPC> _registeredNPCs = new List<BaseJailNPC>();

        // Update intervals are scaled Unity seconds: state work runs at 10 Hz and
        // movement/stuck checks at 2 Hz. The manager does not catch up missed ticks.
        private const float STATE_UPDATE_INTERVAL = 0.1f;      // 10 Hz - State machine updates
        private const float MOVEMENT_CHECK_INTERVAL = 0.5f;    // 2 Hz - Stuck detection

        private float _lastStateUpdate;
        private float _lastMovementCheck;

        /// <summary>
        /// Establishes the singleton and rejects a duplicate component. The
        /// duplicate component itself is destroyed; the existing manager keeps
        /// the registered-NPC list.
        /// </summary>
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

        /// <summary>
        /// Removes dead registrations, then dispatches state and movement work at
        /// their independent scaled Unity-time cadence. A single NPC may receive both
        /// callbacks during the same update when both intervals elapse.
        /// </summary>
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

        /// <summary>Removes destroyed/null NPC references before dispatch.</summary>
        private void CleanupNullNpcs()
        {
            for (int i = _registeredNPCs.Count - 1; i >= 0; i--)
            {
                if (_registeredNPCs[i] == null)
                    _registeredNPCs.RemoveAt(i);
            }
        }

        /// <summary>
        /// Adds an NPC once to the centralized dispatch list. Registration does
        /// not initialize the NPC or invoke an update immediately.
        /// </summary>
        /// <param name="npc">NPC to receive centralized callbacks.</param>
        public void RegisterNPC(BaseJailNPC npc)
        {
            if (npc == null) return;

            if (!_registeredNPCs.Contains(npc))
            {
                _registeredNPCs.Add(npc);
                ModLogger.Debug($"NPCUpdateManager: Registered {npc.gameObject.name} ({_registeredNPCs.Count} total NPCs)");
            }
        }

        /// <summary>Removes an NPC from future centralized callbacks.</summary>
        /// <param name="npc">NPC to remove from the dispatch list.</param>
        public void UnregisterNPC(BaseJailNPC npc)
        {
            if (npc == null) return;

            if (_registeredNPCs.Remove(npc))
            {
                ModLogger.Debug($"NPCUpdateManager: Unregistered {npc.gameObject.name} ({_registeredNPCs.Count} remaining NPCs)");
            }
        }

        /// <summary>Returns the current list size, including any nulls not yet pruned.</summary>
        public int GetRegisteredNPCCount()
        {
            return _registeredNPCs.Count;
        }

        /// <summary>Clears registrations and releases the scene singleton.</summary>
        void OnDestroy()
        {
            _registeredNPCs.Clear();
            _instance = null;
            ModLogger.Info("NPCUpdateManager destroyed");
        }
    }
}
