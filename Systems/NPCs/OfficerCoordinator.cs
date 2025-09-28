using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Coordinates between Intake and Release officers to prevent path conflicts and collisions
    /// </summary>
    public class OfficerCoordinator : MonoBehaviour
    {
#if !MONO
        public OfficerCoordinator(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Singleton

        private static OfficerCoordinator _instance;
        public static OfficerCoordinator Instance
        {
            get
            {
                if (_instance == null)
                {
                    var coordinator = FindObjectOfType<OfficerCoordinator>();
                    if (coordinator == null)
                    {
                        var go = new GameObject("OfficerCoordinator");
                        coordinator = go.AddComponent<OfficerCoordinator>();
                    }
                    _instance = coordinator;
                }
                return _instance;
            }
        }

        #endregion

        #region Active Escorts

        public enum EscortType
        {
            Intake,
            Release
        }

        public class ActiveEscort
        {
            public BaseJailNPC officer;
            public EscortType type;
            public Player player;
            public Vector3 currentDestination;
            public float startTime;
            public List<string> plannedRoute; // Door points the escort will pass through

            public ActiveEscort(BaseJailNPC officer, EscortType type, Player player)
            {
                this.officer = officer;
                this.type = type;
                this.player = player;
                this.startTime = Time.time;
                this.plannedRoute = new List<string>();
                this.currentDestination = Vector3.zero;
            }
        }

        private List<ActiveEscort> activeEscorts = new List<ActiveEscort>();
        private Dictionary<string, float> doorReservations = new Dictionary<string, float>();
        private const float DOOR_RESERVATION_TIME = 10f; // Reserve doors for 10 seconds

        #endregion

        #region Public Interface

        /// <summary>
        /// Register an officer starting an escort
        /// </summary>
        public bool RegisterEscort(BaseJailNPC officer, EscortType type, Player player)
        {
            if (officer == null || player == null) return false;

            // Check if there's already an escort for this player
            var existingEscort = activeEscorts.Find(e => e.player == player);
            if (existingEscort != null)
            {
                ModLogger.Warn($"OfficerCoordinator: Player {player.name} already has active escort by {existingEscort.officer.name}");
                return false;
            }

            // Check for potential conflicts
            if (HasPathConflict(type, player))
            {
                ModLogger.Info($"OfficerCoordinator: Delaying {type} escort for {player.name} due to path conflict");
                return false; // Caller should retry later
            }

            var escort = new ActiveEscort(officer, type, player);
            activeEscorts.Add(escort);

            ModLogger.Info($"OfficerCoordinator: Registered {type} escort for {player.name} by {officer.name}");
            return true;
        }

        /// <summary>
        /// Unregister an escort when complete
        /// </summary>
        public void UnregisterEscort(BaseJailNPC officer)
        {
            var escort = activeEscorts.Find(e => e.officer == officer);
            if (escort != null)
            {
                activeEscorts.Remove(escort);
                ModLogger.Info($"OfficerCoordinator: Unregistered {escort.type} escort for {escort.player?.name} by {officer.name}");
            }
        }

        /// <summary>
        /// Unregister all escorts for a specific player (for cleanup)
        /// </summary>
        public void UnregisterAllEscortsForPlayer(Player player)
        {
            if (player == null) return;

            var playerEscorts = activeEscorts.FindAll(e => e.player == player);
            foreach (var escort in playerEscorts)
            {
                activeEscorts.Remove(escort);
                ModLogger.Info($"OfficerCoordinator: Force unregistered {escort.type} escort for {player.name} by {escort.officer?.name}");
            }

            if (playerEscorts.Count > 0)
            {
                ModLogger.Info($"OfficerCoordinator: Cleared {playerEscorts.Count} stuck escorts for {player.name}");
            }
        }

        /// <summary>
        /// Check if a specific door/area is currently reserved
        /// </summary>
        public bool IsDoorReserved(string doorName)
        {
            return doorReservations.ContainsKey(doorName) &&
                   Time.time - doorReservations[doorName] < DOOR_RESERVATION_TIME;
        }

        /// <summary>
        /// Reserve a door for an officer
        /// </summary>
        public bool ReserveDoor(string doorName, BaseJailNPC officer)
        {
            if (IsDoorReserved(doorName))
            {
                ModLogger.Debug($"OfficerCoordinator: Door {doorName} already reserved");
                return false;
            }

            doorReservations[doorName] = Time.time;
            ModLogger.Debug($"OfficerCoordinator: Reserved door {doorName} for {officer.name}");
            return true;
        }

        /// <summary>
        /// Update escort destination for path conflict detection
        /// </summary>
        public void UpdateEscortDestination(BaseJailNPC officer, Vector3 destination)
        {
            var escort = activeEscorts.Find(e => e.officer == officer);
            if (escort != null)
            {
                escort.currentDestination = destination;
            }
        }

        #endregion

        #region Conflict Detection

        private bool HasPathConflict(EscortType newEscortType, Player newPlayer)
        {
            // Check if any active escorts would conflict
            foreach (var escort in activeEscorts)
            {
                // Same type escorts don't conflict with each other
                if (escort.type == newEscortType) continue;

                // Different types might conflict - check timing and routes
                float escortAge = Time.time - escort.startTime;

                // If existing escort is very new (< 5 seconds), wait
                if (escortAge < 5f)
                {
                    ModLogger.Debug($"OfficerCoordinator: Conflict detected - existing {escort.type} escort too recent ({escortAge:F1}s)");
                    return true;
                }

                // Check if routes would intersect at critical points
                if (WouldRoutesConflict(newEscortType, escort.type))
                {
                    ModLogger.Debug($"OfficerCoordinator: Route conflict between {newEscortType} and existing {escort.type}");
                    return true;
                }
            }

            return false;
        }

        private bool WouldRoutesConflict(EscortType newType, EscortType existingType)
        {
            // Intake: Cell → Hall → Booking → Storage → Hall → Prison → Cell
            // Release: Cell → Prison → Hall → Booking → Exit

            // Critical conflict points:
            // - Hall area (both use)
            // - Booking area (both use)
            // - Prison door (both use)

            // For now, simple rule: Don't start new escort if existing one is in critical phase
            return true; // Conservative approach - avoid all conflicts for now
        }

        #endregion

        #region Cleanup

        void Update()
        {
            // Clean up old escorts (timeout after 5 minutes)
            for (int i = activeEscorts.Count - 1; i >= 0; i--)
            {
                var escort = activeEscorts[i];
                if (Time.time - escort.startTime > 300f) // 5 minutes
                {
                    ModLogger.Warn($"OfficerCoordinator: Cleaning up timed-out {escort.type} escort for {escort.player?.name}");
                    activeEscorts.RemoveAt(i);
                }
            }

            // Clean up old door reservations
            var expiredDoors = new List<string>();
            foreach (var kvp in doorReservations)
            {
                if (Time.time - kvp.Value > DOOR_RESERVATION_TIME)
                {
                    expiredDoors.Add(kvp.Key);
                }
            }
            foreach (var door in expiredDoors)
            {
                doorReservations.Remove(door);
            }
        }

        #endregion
    }
}