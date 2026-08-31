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
        // A session is indexed in both dictionaries.  Every add/remove path must
        // keep the parolee and officer indexes in lockstep so one participant
        // cannot be reported idle while the other still owns the interaction.
        private sealed class InteractionSession
        {
            /// <summary>The exact player retained by this interaction.</summary>
            public Player Parolee;
            /// <summary>The supervising officer that owns the interaction.</summary>
            public ParoleOfficerBehavior Officer;
            /// <summary>The workflow kind used to validate completion/cancellation.</summary>
            public SupervisingOfficerInteractionKind Kind;
            /// <summary>Cached check-in controller used by the cleanup watchdog.</summary>
            public ParoleCheckInSystem CheckInSystem;
        }

        // Polling uses unscaled real time so ownership cleanup still runs while a
        // dialogue or pause changes the game's time scale.
        private const float PollIntervalSeconds = 0.2f;
        private readonly HashSet<Player> pendingIntakeRequests = new HashSet<Player>();
        private readonly Dictionary<Player, InteractionSession> activeSessionsByParolee = new Dictionary<Player, InteractionSession>();
        private readonly Dictionary<ParoleOfficerBehavior, InteractionSession> activeSessionsByOfficer = new Dictionary<ParoleOfficerBehavior, InteractionSession>();
        private readonly List<InteractionSession> sessionsToRemove = new List<InteractionSession>();
        private float nextPollTime;

        /// <summary>
        /// Queues one initial-intake request when the player has neither a pending
        /// request nor an active session.  Queueing does not assign an officer;
        /// the later mark method commits the bidirectional ownership record.
        /// </summary>
        /// <param name="parolee">Player awaiting supervising-officer intake.</param>
        /// <returns>True when a new pending request was added.</returns>
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

        /// <summary>
        /// Checks whether an officer/player pair may begin the requested workflow.
        /// This is a predicate only: it does not add a session or consume a pending
        /// intake request.  Call the matching mark method after the downstream
        /// controller accepts the interaction.
        /// </summary>
        /// <param name="parolee">Player the workflow would own.</param>
        /// <param name="officer">Supervising officer that would own the workflow.</param>
        /// <param name="interactionKind">Intake or check-in ownership being tested.</param>
        /// <returns>True when neither participant is currently busy and the workflow is eligible.</returns>
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

        /// <summary>
        /// Commits a previously queued intake by removing its pending marker and
        /// inserting the same session into both ownership dictionaries.  Invalid,
        /// duplicate, or unqueued requests are ignored.
        /// </summary>
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

        /// <summary>
        /// Commits a check-in session into both ownership dictionaries after the
        /// parole manager accepts the check-in.  Existing player ownership wins.
        /// </summary>
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

        /// <summary>
        /// Removes an active session only when its kind and, when supplied, owner
        /// officer match.  Intake cancellation also clears a pending request.
        /// </summary>
        /// <param name="parolee">Player whose ownership should be released.</param>
        /// <param name="officer">Expected owner; null permits cleanup by player/kind.</param>
        /// <param name="interactionKind">Kind that must match the stored session.</param>
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

        /// <summary>Removes only the pending-intake marker for a player.</summary>
        public void ClearPendingIntake(Player parolee)
        {
            if (parolee == null)
            {
                return;
            }

            pendingIntakeRequests.Remove(parolee);
        }

        /// <summary>
        /// Removes pending and active ownership for a player, including the
        /// reverse officer index when its session still points to that player.
        /// </summary>
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

        /// <summary>
        /// Runs the unscaled-time cleanup watchdog at the configured interval.
        /// It removes sessions whose player/officer or downstream controller no
        /// longer reports the owning workflow as active.
        /// </summary>
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

        /// <summary>
        /// Finds stale sessions without mutating the dictionary during iteration,
        /// then removes each session only if both indexes still reference it.
        /// </summary>
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

        /// <summary>Checks the officer-side ownership index used by admission predicates.</summary>
        private bool IsOfficerBusy(ParoleOfficerBehavior officer)
        {
            if (officer == null)
            {
                return true;
            }

            return activeSessionsByOfficer.ContainsKey(officer);
        }

        /// <summary>
        /// Verifies a check-in session against its live controller and exact player
        /// identity so a reused officer cannot retain stale ownership.
        /// </summary>
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
