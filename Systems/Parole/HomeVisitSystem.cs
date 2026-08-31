using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using System.Collections;
using UnityEngine;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Manages unannounced home visits by parole officers.
    /// Visit frequency scales with LSI level.
    /// </summary>
    /// <remarks>
    /// Visit timestamps use the lightweight mod clock's game-minute values and are persisted
    /// on the current RapSheet parole record. The service methods do not check authority
    /// themselves; the authoritative parole monitor is expected to call them.
    /// </remarks>
    public class HomeVisitSystem
    {
        private static HomeVisitSystem _instance;
        private static bool _isManagedBySystemManager;

        /// <summary>
        /// Compatibility accessor. Prefers a manager-registered instance when available.
        /// </summary>
        public static HomeVisitSystem Instance
        {
            get
            {
                if (TryGetRegisteredInstance(out var existing))
                {
                    return existing;
                }

                return RegisterInstance(new HomeVisitSystem(), false);
            }
        }

        /// <summary>
        /// Returns true when a home-visit service is already registered.
        /// </summary>
        public static bool HasRegisteredInstance => _instance != null;

        /// <summary>
        /// Register the active home-visit service instance.
        /// </summary>
        public static HomeVisitSystem RegisterInstance(HomeVisitSystem instance, bool managedBySystemManager = false)
        {
            if (instance == null)
            {
                return null;
            }

            _instance = instance;
            _isManagedBySystemManager = managedBySystemManager;
            return _instance;
        }

        /// <summary>
        /// Create the manager-owned instance when none is registered yet.
        /// </summary>
        public static HomeVisitSystem BootstrapManagedInstance()
        {
            if (TryGetRegisteredInstance(out var existing))
            {
                return existing;
            }

            return RegisterInstance(new HomeVisitSystem(), true);
        }

        /// <summary>
        /// Returns the currently registered instance when present.
        /// </summary>
        public static bool TryGetRegisteredInstance(out HomeVisitSystem instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// Tears down the manager-owned instance while leaving compatibility-created instances alone.
        /// </summary>
        public static bool ShutdownManagedInstance()
        {
            if (_instance == null || !_isManagedBySystemManager)
            {
                return false;
            }

            _instance = null;
            _isManagedBySystemManager = false;
            return true;
        }

        /// <summary>
        /// Create a home-visit service instance suitable for explicit construction/injection.
        /// </summary>
        public HomeVisitSystem()
        {
        }

        /// <summary>
        /// Get the home visit interval in game minutes based on LSI level
        /// </summary>
        /// <param name="lsiLevel">LSI level selecting the fixed visit interval.</param>
        /// <returns>Three, two, one, or one-half game day in game minutes.</returns>
        public static float GetVisitIntervalGameMinutes(LSILevel lsiLevel)
        {
            // Game day = 1440 game minutes
            switch (lsiLevel)
            {
                case LSILevel.Minimum: return 1440f * 3f;  // Every 3 game days
                case LSILevel.Medium: return 1440f * 2f;   // Every 2 game days
                case LSILevel.High: return 1440f;           // Every game day
                case LSILevel.Severe: return 720f;          // Every half game day
                default: return 1440f * 3f;
            }
        }

        /// <summary>
        /// Schedule the next home visit for a player
        /// </summary>
        /// <param name="player">Parolee whose schedule change should be marked.</param>
        /// <param name="rapSheet">RapSheet containing the active parole record.</param>
        /// <remarks>
        /// The next visit is set to the current fallback game time plus the LSI interval and
        /// a random offset between minus and plus 25 percent. Calling this method again resets
        /// the prior timestamp and consumes a new random offset.
        /// </remarks>
        public void ScheduleNextVisit(Player player, RapSheet rapSheet)
        {
            if (rapSheet?.CurrentParoleRecord == null) return;

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float interval = GetVisitIntervalGameMinutes(rapSheet.LSILevel);

            // Add some randomness (±25% of interval)
            float randomOffset = UnityEngine.Random.Range(-interval * 0.25f, interval * 0.25f);
            float nextVisitTime = currentGameTime + interval + randomOffset;

            rapSheet.CurrentParoleRecord.SetNextHomeVisitGameTime(nextVisitTime);
            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

            ModLogger.Info($"[HOME VISIT] Scheduled next visit for {player.name} at game time {nextVisitTime:F0} (interval: {interval:F0} min)");
        }

        /// <summary>
        /// Check if it's time for a home visit and process it
        /// </summary>
        /// <param name="player">Parolee whose visit schedule should be evaluated.</param>
        /// <param name="rapSheet">RapSheet containing visit state and parole status.</param>
        /// <remarks>At most one due visit is processed per call; the next visit is scheduled after processing.</remarks>
        public void CheckAndProcessHomeVisit(Player player, RapSheet rapSheet)
        {
            if (rapSheet?.CurrentParoleRecord == null) return;
            if (!rapSheet.CurrentParoleRecord.IsOnParole()) return;

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float nextVisitTime = rapSheet.CurrentParoleRecord.GetNextHomeVisitGameTime();

            // Not yet scheduled or not yet time
            if (nextVisitTime <= 0f || currentGameTime < nextVisitTime) return;

            // Time for a home visit
            ProcessHomeVisit(player, rapSheet);
        }

        /// <summary>
        /// Process a home visit check
        /// </summary>
        /// <summary>
        /// Apply the current home-visit result, persist consequences, and schedule the next visit.
        /// </summary>
        /// <param name="player">Parolee whose presence is checked.</param>
        /// <param name="rapSheet">RapSheet receiving rapport, violation, and schedule changes.</param>
        /// <remarks>
        /// Being home resets missed visits and adds rapport. Being away increments the missed
        /// counter and reduces rapport; the third consecutive absence adds a formal violation
        /// and resets that counter. This path does not issue a warrant directly.
        /// </remarks>
        private void ProcessHomeVisit(Player player, RapSheet rapSheet)
        {
            var paroleRecord = rapSheet.CurrentParoleRecord;
            bool isAtHome = PlayerHomeDetector.IsPlayerAtHome(player);

            if (isAtHome)
            {
                // Player is home - conduct check
                paroleRecord.ResetHomeVisitsMissed();
                paroleRecord.AdjustRapport(2f); // Small rapport boost for being home

                ModLogger.Info($"[HOME VISIT] Player {player.name} is at home - visit successful");

                // Send officer text about successful visit
                Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                    "Compliance check completed at your residence. Stay on track.");
            }
            else
            {
                // Player is not home
                paroleRecord.IncrementHomeVisitsMissed();
                paroleRecord.AdjustRapport(-5f);
                int missed = paroleRecord.GetHomeVisitsMissed();

                ModLogger.Info($"[HOME VISIT] Player {player.name} is NOT at home - missed count: {missed}");

                if (missed >= 3)
                {
                    // 3+ consecutive absences = formal violation
                    var violation = new ViolationRecord(
                        ViolationType.Other,
                        $"Failed to be present for {missed} consecutive home visits",
                        2.0f);
                    rapSheet.AddParoleViolation(violation);
                    paroleRecord.AdjustComplianceScore(-10f);
                    paroleRecord.ResetHomeVisitsMissed(); // Reset counter after violation

                    Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                        $"You've missed {missed} consecutive home checks. A formal violation has been recorded.");
                }
                else
                {
                    Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                        $"Home check: you weren't at your residence. Missed home checks: {missed}/3 before violation.");
                }
            }

            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

            // Schedule next visit
            ScheduleNextVisit(player, rapSheet);
        }
    }
}
