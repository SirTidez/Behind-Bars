using System.Collections.Generic;
using UnityEngine;
using BBHelpers = Behind_Bars.Helpers.Helpers;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Identifies the active supervising-officer workflow for a parolee.
    /// Owned by <see cref="DynamicParoleOfficerManager"/> and shared with
    /// downstream officer controllers to avoid duplicate intake/check-in sessions.
    /// </summary>
    internal enum SupervisingOfficerInteractionKind
    {
        Intake,
        CheckIn
    }

    /// <summary>
    /// Coordinates supervising-officer interaction ownership for intake and check-ins.
    /// This class does not spawn officers or persist parole state; it only arbitrates
    /// which parolee/officer pair currently owns a supervising-officer session.
    /// </summary>
    internal sealed class SupervisingOfficerInteractionCoordinator
    {
        private sealed class InteractionSession
        {
            public Player Parolee;
            public ParoleOfficerBehavior Officer;
            public SupervisingOfficerInteractionKind Kind;
            public ParoleCheckInSystem CheckInSystem;
        }

        private const float PollIntervalSeconds = 0.2f;
        private readonly HashSet<Player> pendingIntakeRequests = new HashSet<Player>();
        private readonly Dictionary<Player, InteractionSession> activeSessionsByParolee = new Dictionary<Player, InteractionSession>();
        private readonly Dictionary<ParoleOfficerBehavior, InteractionSession> activeSessionsByOfficer = new Dictionary<ParoleOfficerBehavior, InteractionSession>();
        private readonly List<InteractionSession> sessionsToRemove = new List<InteractionSession>();
        private float nextPollTime;

        public bool TryQueueInitialIntake(Player parolee)
        {
            if (parolee == null)
            {
                return false;
            }

            if (pendingIntakeRequests.Contains(parolee) || activeSessionsByParolee.ContainsKey(parolee))
            {
                return false;
            }

            pendingIntakeRequests.Add(parolee);
            return true;
        }

        public bool TryBeginInteraction(Player parolee, ParoleOfficerBehavior officer, SupervisingOfficerInteractionKind interactionKind)
        {
            if (parolee == null || officer == null)
            {
                return false;
            }

            if (IsOfficerBusy(officer))
            {
                return false;
            }

            if (interactionKind == SupervisingOfficerInteractionKind.Intake)
            {
                return pendingIntakeRequests.Contains(parolee) && !activeSessionsByParolee.ContainsKey(parolee);
            }

            return !activeSessionsByParolee.ContainsKey(parolee);
        }

        /// <summary>
        /// Reserve intake ownership for a parolee/officer pair before the downstream controller accepts it.
        /// </summary>
        public bool TryReserveIntake(Player parolee, ParoleOfficerBehavior officer)
        {
            return TryBeginInteraction(parolee, officer, SupervisingOfficerInteractionKind.Intake);
        }

        /// <summary>
        /// Reserve check-in ownership for a parolee/officer pair before the downstream controller accepts it.
        /// </summary>
        public bool TryReserveCheckIn(Player parolee, ParoleOfficerBehavior officer)
        {
            return TryBeginInteraction(parolee, officer, SupervisingOfficerInteractionKind.CheckIn);
        }

        public void MarkIntakeStarted(Player parolee, ParoleOfficerBehavior officer)
        {
            if (parolee == null || officer == null)
            {
                return;
            }

            if (!pendingIntakeRequests.Contains(parolee) || activeSessionsByParolee.ContainsKey(parolee))
            {
                return;
            }

            var session = new InteractionSession
            {
                Parolee = parolee,
                Officer = officer,
                Kind = SupervisingOfficerInteractionKind.Intake
            };

            pendingIntakeRequests.Remove(parolee);
            activeSessionsByParolee[parolee] = session;
            activeSessionsByOfficer[officer] = session;
        }

        public void MarkCheckInStarted(Player parolee, ParoleOfficerBehavior officer)
        {
            if (parolee == null || officer == null)
            {
                return;
            }

            if (activeSessionsByParolee.ContainsKey(parolee))
            {
                return;
            }

            var session = new InteractionSession
            {
                Parolee = parolee,
                Officer = officer,
                Kind = SupervisingOfficerInteractionKind.CheckIn,
                CheckInSystem = BBHelpers.GetComponentSafe<ParoleCheckInSystem>(officer.gameObject)
            };

            activeSessionsByParolee[parolee] = session;
            activeSessionsByOfficer[officer] = session;
        }

        /// <summary>
        /// Commit a check-in interaction after the parole system has admitted the parolee.
        /// </summary>
        public void StartCheckIn(Player parolee, ParoleOfficerBehavior officer)
        {
            MarkCheckInStarted(parolee, officer);
        }

        public void EndInteraction(Player parolee, ParoleOfficerBehavior officer, SupervisingOfficerInteractionKind interactionKind)
        {
            if (parolee == null)
            {
                return;
            }

            if (activeSessionsByParolee.TryGetValue(parolee, out var session))
            {
                if (session.Kind != interactionKind)
                {
                    return;
                }

                if (officer != null && session.Officer != officer)
                {
                    return;
                }

                activeSessionsByParolee.Remove(parolee);

                if (session.Officer != null && activeSessionsByOfficer.TryGetValue(session.Officer, out var officerSession) && officerSession == session)
                {
                    activeSessionsByOfficer.Remove(session.Officer);
                }
            }

            if (interactionKind == SupervisingOfficerInteractionKind.Intake)
            {
                pendingIntakeRequests.Remove(parolee);
            }
        }

        /// <summary>
        /// Release any reserved intake or active intake session for the supplied parolee/officer pair.
        /// </summary>
        public void CancelIntake(Player parolee, ParoleOfficerBehavior officer)
        {
            EndInteraction(parolee, officer, SupervisingOfficerInteractionKind.Intake);
        }

        /// <summary>
        /// Release any active check-in session for the supplied parolee/officer pair.
        /// </summary>
        public void CompleteCheckIn(Player parolee, ParoleOfficerBehavior officer)
        {
            EndInteraction(parolee, officer, SupervisingOfficerInteractionKind.CheckIn);
        }

        public void ClearPendingIntake(Player parolee)
        {
            if (parolee == null)
            {
                return;
            }

            pendingIntakeRequests.Remove(parolee);
        }

        public void ClearPlayer(Player player)
        {
            if (player == null)
            {
                return;
            }

            pendingIntakeRequests.Remove(player);

            if (activeSessionsByParolee.TryGetValue(player, out var session))
            {
                activeSessionsByParolee.Remove(player);

                if (session.Officer != null && activeSessionsByOfficer.TryGetValue(session.Officer, out var officerSession) && officerSession == session)
                {
                    activeSessionsByOfficer.Remove(session.Officer);
                }
            }
        }

        /// <summary>
        /// Returns true when the supplied parolee currently owns a supervising-officer interaction session.
        /// </summary>
        public bool HasActiveSession(Player parolee)
        {
            return parolee != null && activeSessionsByParolee.ContainsKey(parolee);
        }

        /// <summary>
        /// Returns true while an initial-intake request is queued but has not yet been
        /// accepted by the supervising officer.
        /// </summary>
        public bool HasPendingIntake(Player parolee)
        {
            return parolee != null && pendingIntakeRequests.Contains(parolee);
        }

        /// <summary>
        /// Returns true when the supplied officer currently owns a supervising-officer interaction session.
        /// </summary>
        public bool HasActiveSession(ParoleOfficerBehavior officer)
        {
            return officer != null && activeSessionsByOfficer.ContainsKey(officer);
        }

        /// <summary>
        /// Returns the current session kind for the supplied parolee, if any.
        /// </summary>
        public bool TryGetSessionKind(Player parolee, out SupervisingOfficerInteractionKind kind)
        {
            if (parolee != null && activeSessionsByParolee.TryGetValue(parolee, out var session) && session != null)
            {
                kind = session.Kind;
                return true;
            }

            kind = default;
            return false;
        }

        public void Poll()
        {
            if (activeSessionsByParolee.Count == 0)
            {
                return;
            }

            float currentTime = Time.unscaledTime;
            if (currentTime < nextPollTime)
            {
                return;
            }

            nextPollTime = currentTime + PollIntervalSeconds;
            CleanupActiveSessions();
        }

        private void CleanupActiveSessions()
        {
            sessionsToRemove.Clear();

            foreach (var session in activeSessionsByParolee.Values)
            {
                if (session == null || session.Parolee == null || session.Officer == null)
                {
                    sessionsToRemove.Add(session);
                    continue;
                }

                bool stillActive = session.Kind switch
                {
                    SupervisingOfficerInteractionKind.Intake => session.Officer.IsHandlingIntakeFor(session.Parolee),
                    SupervisingOfficerInteractionKind.CheckIn => IsCheckInStillActive(session),
                    _ => false
                };

                if (!stillActive)
                {
                    sessionsToRemove.Add(session);
                }
            }

            foreach (var session in sessionsToRemove)
            {
                if (session == null)
                {
                    continue;
                }

                if (session.Parolee != null && activeSessionsByParolee.TryGetValue(session.Parolee, out var existingSession) && existingSession == session)
                {
                    activeSessionsByParolee.Remove(session.Parolee);
                }

                if (session.Officer != null && activeSessionsByOfficer.TryGetValue(session.Officer, out var officerSession) && officerSession == session)
                {
                    activeSessionsByOfficer.Remove(session.Officer);
                }
            }
        }

        private bool IsOfficerBusy(ParoleOfficerBehavior officer)
        {
            if (officer == null)
            {
                return true;
            }

            return activeSessionsByOfficer.ContainsKey(officer);
        }

        private bool IsCheckInStillActive(InteractionSession session)
        {
            if (session == null || session.Officer == null || session.Parolee == null)
            {
                return false;
            }

            if (session.CheckInSystem == null)
            {
                session.CheckInSystem = BBHelpers.GetComponentSafe<ParoleCheckInSystem>(session.Officer.gameObject);
            }

            return session.CheckInSystem != null &&
                   session.CheckInSystem.IsProcessingCheckIn() &&
                   session.CheckInSystem.GetCurrentCheckInParolee() == session.Parolee;
        }
    }
}
