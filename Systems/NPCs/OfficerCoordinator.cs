using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Coordinates intake/release escort ownership and short-lived door
    /// reservations.  Escort records are authoritative only within this
    /// coordinator; the underlying officer state machines remain responsible for
    /// completing their own workflows.
    /// </summary>
    public class OfficerCoordinator : MonoBehaviour
    {
#if !MONO
        public OfficerCoordinator(System.IntPtr ptr) : base(ptr) { }
#endif

        #region Singleton

        private static OfficerCoordinator _instance;

        /// <summary>
        /// Gets the scene coordinator, creating an injected component only when no
        /// existing instance can be resolved.  The singleton is not persistent
        /// across scene unloads.
        /// </summary>
        public static OfficerCoordinator Instance
        {
            get
            {
                if (_instance == null)
                {
                    var coordinator = BBHelpers.FindObjectOfTypeSafe<OfficerCoordinator>();
                    if (coordinator == null)
                    {
                        var go = new GameObject("OfficerCoordinator");
                        coordinator = BBHelpers.AddComponentSafe<OfficerCoordinator>(go);
                    }
                    _instance = coordinator;
                }
                return _instance;
            }
        }

        #endregion

        #region Active Escorts

        /// <summary>Identifies the intake or release route owned by an escort.</summary>
        public enum EscortType
        {
            Intake,
            Release
        }

        /// <summary>
        /// Snapshot of one officer/player escort.  <see cref="startTime"/> and
        /// door reservations use scaled Unity <c>Time.time</c> units; no automatic
        /// escort timeout is applied to the workflow itself.
        /// </summary>
        public class ActiveEscort
        {
            /// <summary>Officer that owns this escort record.</summary>
            public BaseJailNPC officer;
            /// <summary>Route kind used for conservative conflict checks.</summary>
            public EscortType type;
            /// <summary>Exact player retained by the escort.</summary>
            public Player player;
            /// <summary>Most recent destination reported by the owning officer.</summary>
            public Vector3 currentDestination;
            /// <summary>Real Unity time at which the record was created.</summary>
            public float startTime;
            /// <summary>Reserved route labels; currently initialized empty and not populated by the coordinator itself.</summary>
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

        // activeEscorts is the player/ officer escort registry. Door reservations
        // are only timestamped by door name; they are not tied back to an officer
        // and expire during Update rather than when an escort ends.
        private List<ActiveEscort> activeEscorts = new List<ActiveEscort>();
        private Dictionary<string, float> doorReservations = new Dictionary<string, float>();
        private readonly List<string> expiredDoors = new List<string>();
        private const float DOOR_RESERVATION_TIME = 10f; // Reserve doors for 10 seconds
        private const float DOOR_CLEANUP_INTERVAL = 0.5f;
        private float nextDoorCleanupTime;

        #endregion

        #region Public Interface

        /// <summary>
        /// Registers an exact officer/player escort after rejecting duplicate
        /// player ownership and conservative intake/release route conflicts.
        /// </summary>
        /// <param name="officer">Officer that will own the route.</param>
        /// <param name="type">Intake or release escort type.</param>
        /// <param name="player">Player retained by the route.</param>
        /// <returns>True when a new active escort record was added.</returns>
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
        /// Removes the first escort owned by the supplied officer.  It does not
        /// clear door reservations because those expire independently by timestamp.
        /// </summary>
        /// <param name="officer">Officer whose escort record should be removed.</param>
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
        /// Removes every escort record for the exact player, typically for
        /// destruction/arrest cleanup.  It does not alter door reservations.
        /// </summary>
        /// <param name="player">Player whose escort records should be removed.</param>
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
        /// Checks a timestamped door reservation using scaled Unity <c>Time.time</c>.
        /// Expired
        /// entries can remain observable until the next coordinator update pass.
        /// </summary>
        /// <param name="doorName">Door key used by the shared door route.</param>
        /// <returns>True while the reservation is younger than ten scaled Unity seconds.</returns>
        public bool IsDoorReserved(string doorName)
        {
            return doorReservations.ContainsKey(doorName) &&
                   Time.time - doorReservations[doorName] < DOOR_RESERVATION_TIME;
        }

        /// <summary>
        /// Reserves a door key for ten scaled Unity seconds.  The officer parameter is used
        /// for diagnostics only; ownership is not stored and there is no release
        /// method beyond expiry.
        /// </summary>
        /// <param name="doorName">Door key to reserve.</param>
        /// <param name="officer">Officer requesting the reservation.</param>
        /// <returns>False when the key is still reserved; otherwise true.</returns>
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
        /// Updates the destination snapshot for the first escort owned by an
        /// officer.  The value is diagnostic state; current conflict detection
        /// does not inspect the destination geometry.
        /// </summary>
        /// <param name="officer">Officer whose active escort should be updated.</param>
        /// <param name="destination">Latest world-space destination.</param>
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

        /// <summary>
        /// Rejects a new escort when a different-type escort is too recent or when
        /// the conservative route stub reports a conflict.  Same-type escorts are
        /// currently allowed without route comparison.
        /// </summary>
        /// <param name="newEscortType">Type of the candidate escort.</param>
        /// <param name="newPlayer">Candidate player; currently used only as context.</param>
        /// <returns>True when the candidate must wait.</returns>
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

        /// <summary>
        /// Conservative conflict placeholder for intake/release route crossing.
        /// It currently returns true for every cross-type comparison, regardless
        /// of the supplied route phases or player identity.
        /// </summary>
        /// <param name="newType">Candidate escort type.</param>
        /// <param name="existingType">Already active escort type.</param>
        /// <returns>Always true for the current conservative implementation.</returns>
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

        /// <summary>
        /// Cleans expired door timestamps every half scaled Unity second. Escort records
        /// intentionally have no timeout and remain until an owner explicitly
        /// unregisters them or another cleanup path removes them.
        /// </summary>
        void Update()
        {
            // NO TIMEOUT - escorts can take as long as needed for player to complete booking/release

            if (doorReservations.Count == 0)
            {
                return;
            }

            float currentTime = Time.time;
            if (currentTime < nextDoorCleanupTime)
            {
                return;
            }

            nextDoorCleanupTime = currentTime + DOOR_CLEANUP_INTERVAL;

            // Clean up old door reservations
            expiredDoors.Clear();
            foreach (var kvp in doorReservations)
            {
                if (currentTime - kvp.Value > DOOR_RESERVATION_TIME)
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
