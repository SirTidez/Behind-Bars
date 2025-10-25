using System;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.NPCs;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Monitors NPCHealth component for damage events and notifies BaseJailNPC
    /// This bridges the gap between the game's damage system and our custom guard responses
    /// </summary>
    public class NPCAttackMonitor : MonoBehaviour
    {
#if !MONO
        public NPCAttackMonitor(System.IntPtr ptr) : base(ptr) { }
#endif

        private BaseJailNPC jailNPC;
        private NPCHealth npcHealth;
        private float lastHealth;
        private float lastAttackCheck;
        private Player lastAttacker;

        public void Initialize(BaseJailNPC baseNPC)
        {
            jailNPC = baseNPC;
            npcHealth = GetComponent<NPCHealth>();

            if (npcHealth != null)
            {
                lastHealth = npcHealth.Health;
                ModLogger.Info($"NPCAttackMonitor: Initialized for {gameObject.name} with health: {lastHealth}");
            }
        }

        void Update()
        {
            if (npcHealth == null || jailNPC == null) return;

            // Check for health changes indicating damage
            float currentHealth = npcHealth.Health;

            if (currentHealth < lastHealth)
            {
                float damage = lastHealth - currentHealth;
                ModLogger.Info($"NPCAttackMonitor: {gameObject.name} took {damage} damage");

                // Check if this was recently attacked by a player
                // HoursSinceAttackedByPlayer is not accessable at this time, so skip for nows
                /*if (npcHealth.HoursSinceAttackedByPlayer < 1) // Less than 1 hour ago
                {
                    // Find the player who likely attacked (could be improved with more sophisticated tracking)
                    Player attacker = FindNearbyPlayer();
                    if (attacker != null)
                    {
                        ModLogger.Info($"NPCAttackMonitor: Player {attacker.name} likely attacked {gameObject.name}");
                        jailNPC.OnAttackedByPlayer(attacker);
                        lastAttacker = attacker;
                    }
                }*/

                lastHealth = currentHealth;
            }
            else if (currentHealth > lastHealth)
            {
                // Health increased (healing)
                lastHealth = currentHealth;
            }
        }

        /// <summary>
        /// Find the nearest player who might be the attacker
        /// </summary>
        private Player FindNearbyPlayer()
        {
            Player closestPlayer = null;
            float closestDistance = float.MaxValue;
            float maxRange = 5f; // Maximum range to consider a player as potential attacker

            try
            {
                // Get all active players in the scene
                Player[] players = FindObjectsOfType<Player>();

                foreach (Player player in players)
                {
                    if (player == null) continue;

                    float distance = Vector3.Distance(transform.position, player.transform.position);
                    if (distance < maxRange && distance < closestDistance)
                    {
                        closestPlayer = player;
                        closestDistance = distance;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"NPCAttackMonitor: Error finding nearby player: {ex.Message}");
            }

            return closestPlayer;
        }

        /// <summary>
        /// Manual attack notification for direct damage calls
        /// </summary>
        public void NotifyAttack(Player attacker, float damage)
        {
            if (jailNPC != null && attacker != null)
            {
                ModLogger.Info($"NPCAttackMonitor: Manual attack notification - {attacker.name} attacked {gameObject.name}");
                jailNPC.OnAttackedByPlayer(attacker);
                lastAttacker = attacker;
            }
        }

        /// <summary>
        /// Get the last known attacker
        /// </summary>
        public Player GetLastAttacker()
        {
            return lastAttacker;
        }
    }
}