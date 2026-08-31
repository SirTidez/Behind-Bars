using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Money;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Money;
#endif

namespace Behind_Bars.Systems.Parole
{
    /// <summary>
    /// Manages parole supervision fees assessed every 7 game days.
    /// Fee amount scales with LSI level.
    /// </summary>
    /// <remarks>
    /// Fee scheduling uses the lightweight mod clock's game-minute values and writes directly
    /// to the current RapSheet parole record. These methods do not perform an authority check
    /// themselves; the active parole monitor is expected to call assessment on the authoritative
    /// path.
    /// </remarks>
    public class ParoleFeeSystem
    {
        private static ParoleFeeSystem _instance;
        private static bool _isManagedBySystemManager;

        /// <summary>
        /// Compatibility accessor. Prefers a manager-registered instance when available.
        /// </summary>
        public static ParoleFeeSystem Instance
        {
            get
            {
                if (TryGetRegisteredInstance(out var existing))
                {
                    return existing;
                }

                return RegisterInstance(new ParoleFeeSystem(), false);
            }
        }

        /// <summary>
        /// Returns true when a fee-system instance is already registered.
        /// </summary>
        public static bool HasRegisteredInstance => _instance != null;

        /// <summary>
        /// Register the active fee-system instance.
        /// </summary>
        public static ParoleFeeSystem RegisterInstance(ParoleFeeSystem instance, bool managedBySystemManager = false)
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
        public static ParoleFeeSystem BootstrapManagedInstance()
        {
            if (TryGetRegisteredInstance(out var existing))
            {
                return existing;
            }

            return RegisterInstance(new ParoleFeeSystem(), true);
        }

        /// <summary>
        /// Returns the currently registered instance when present.
        /// </summary>
        public static bool TryGetRegisteredInstance(out ParoleFeeSystem instance)
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
        /// Fee interval in game minutes (7 game days)
        /// </summary>
        /// <remarks>The value is 10,080 fallback game minutes, not wall-clock minutes.</remarks>
        public const float FEE_INTERVAL_GAME_MINUTES = 1440f * 7f;

        /// <summary>
        /// Create a parole-fee service instance suitable for explicit construction/injection.
        /// </summary>
        public ParoleFeeSystem()
        {
        }

        /// <summary>
        /// Get the weekly fee amount based on LSI level
        /// </summary>
        /// <param name="lsiLevel">LSI level selecting the fixed fee table.</param>
        /// <returns>Fee amount in currency units, or zero for no/unknown LSI.</returns>
        public static float GetWeeklyFee(LSILevel lsiLevel)
        {
            switch (lsiLevel)
            {
                case LSILevel.Minimum: return 50f;
                case LSILevel.Medium: return 100f;
                case LSILevel.High: return 200f;
                case LSILevel.Severe: return 500f;
                default: return 0f;
            }
        }

        /// <summary>
        /// Schedule the first fee assessment for a new parole term
        /// </summary>
        /// <param name="player">Parolee whose RapSheet change should be marked.</param>
        /// <param name="rapSheet">RapSheet containing the active parole record.</param>
        /// <remarks>
        /// The next-fee time is reset to the current fallback game time plus seven game days
        /// whenever this method is called. Missing parole state is a no-op.
        /// </remarks>
        public void InitializeFees(Player player, RapSheet rapSheet)
        {
            if (rapSheet?.CurrentParoleRecord == null) return;

            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            rapSheet.CurrentParoleRecord.SetNextFeeGameTime(currentGameTime + FEE_INTERVAL_GAME_MINUTES);
            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

            ModLogger.Info($"[FEES] Initialized fee schedule for {player.name} - first fee at game time {currentGameTime + FEE_INTERVAL_GAME_MINUTES:F0}");
        }

        /// <summary>
        /// Check if fees are due and assess them
        /// </summary>
        /// <param name="player">Parolee receiving the assessment message.</param>
        /// <param name="rapSheet">RapSheet containing fee schedule and LSI level.</param>
        /// <remarks>
        /// At most one fee is assessed per call; the next due time is moved forward by seven
        /// game days from the current fallback clock. This method persists the charge and
        /// queues officer text but does not process payment.
        /// </remarks>
        public void CheckAndAssessFees(Player player, RapSheet rapSheet)
        {
            if (rapSheet?.CurrentParoleRecord == null) return;
            if (!rapSheet.CurrentParoleRecord.IsOnParole()) return;

            var paroleRecord = rapSheet.CurrentParoleRecord;
            float currentGameTime = GameTimeManager.Instance.GetCurrentGameTimeInMinutes();
            float nextFeeTime = paroleRecord.GetNextFeeGameTime();

            if (nextFeeTime <= 0f || currentGameTime < nextFeeTime) return;

            // Assess fee
            float fee = GetWeeklyFee(rapSheet.LSILevel);
            paroleRecord.AddFeesOwed(fee);

            // Schedule next fee
            paroleRecord.SetNextFeeGameTime(currentGameTime + FEE_INTERVAL_GAME_MINUTES);
            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

            // Notify player
            Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                $"Supervision fee assessed: ${fee:F0}. Total owed: ${paroleRecord.GetTotalFeesOwed():F0}. Pay at your next check-in.");

            ModLogger.Info($"[FEES] Assessed ${fee:F0} fee for {player.name}. Total owed: ${paroleRecord.GetTotalFeesOwed():F0}");
        }

        /// <summary>
        /// Attempt to pay outstanding fees from player's cash
        /// </summary>
        /// <param name="player">Parolee whose cash and fee record are mutated.</param>
        /// <param name="rapSheet">RapSheet containing the outstanding fee balance.</param>
        /// <returns><see langword="true"/> for no balance or full payment; <see langword="false"/> for partial/no payment or an error.</returns>
        /// <remarks>
        /// Payment is cash-only. A full payment records the amount and adds rapport; a partial
        /// payment deducts all available cash and leaves the remainder owed. A zero-cash attempt
        /// records a missed payment. The current cash bridge does not provide a rollback or
        /// post-deduction confirmation if the underlying money call fails.
        /// </remarks>
        public bool AttemptPayment(Player player, RapSheet rapSheet)
        {
            if (rapSheet?.CurrentParoleRecord == null) return false;

            var paroleRecord = rapSheet.CurrentParoleRecord;
            float owed = paroleRecord.GetTotalFeesOwed();

            if (owed <= 0f)
            {
                ModLogger.Debug($"[FEES] No fees owed for {player.name}");
                return true; // Nothing owed, consider it a success
            }

            try
            {
                // Try to deduct money from player
                float playerCash = GetPlayerCash(player);

                if (playerCash >= owed)
                {
                    // Full payment
                    DeductPlayerCash(player, owed);
                    paroleRecord.RecordFeePayment(owed);
                    paroleRecord.AdjustRapport(2f); // Small rapport boost for paying
                    Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

                    ModLogger.Info($"[FEES] Player {player.name} paid ${owed:F0} in supervision fees");
                    return true;
                }
                else if (playerCash > 0f)
                {
                    // Partial payment
                    DeductPlayerCash(player, playerCash);
                    paroleRecord.RecordFeePayment(playerCash);
                    Core.ResolveRapSheetManager().MarkRapSheetChanged(player);

                    ModLogger.Info($"[FEES] Player {player.name} made partial payment: ${playerCash:F0} of ${owed:F0}");
                    return false;
                }
                else
                {
                    // No money - record missed payment
                    HandleMissedPayment(player, rapSheet);
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[FEES] Error processing payment for {player.name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handle a missed fee payment
        /// </summary>
        /// <param name="player">Parolee receiving the consequence message.</param>
        /// <param name="rapSheet">RapSheet whose missed-payment count and rapport are updated.</param>
        /// <remarks>
        /// First miss applies compliance/rapport penalties, second adds a formal violation, and
        /// third-plus adds a repeated-payment violation. The third-plus branch only sends a
        /// message that a warrant may be issued; it does not issue the warrant itself.
        /// </remarks>
        public void HandleMissedPayment(Player player, RapSheet rapSheet)
        {
            if (rapSheet?.CurrentParoleRecord == null) return;

            var paroleRecord = rapSheet.CurrentParoleRecord;
            paroleRecord.IncrementMissedPayments();
            int missed = paroleRecord.GetMissedPayments();

            if (missed == 1)
            {
                // First miss: compliance penalty + rapport hit
                paroleRecord.AdjustComplianceScore(-5f);
                paroleRecord.AdjustRapport(-5f);
                Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                    $"Payment overdue: ${paroleRecord.GetTotalFeesOwed():F0}. First missed payment recorded.");
            }
            else if (missed == 2)
            {
                // Second miss: formal violation
                var violation = new ViolationRecord(
                    ViolationType.Other,
                    $"Failed to pay supervision fees (${paroleRecord.GetTotalFeesOwed():F0} outstanding)",
                    1.5f);
                rapSheet.AddParoleViolation(violation);
                paroleRecord.AdjustRapport(-10f);
                Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                    $"Second missed payment. Formal violation recorded. Outstanding: ${paroleRecord.GetTotalFeesOwed():F0}.");
            }
            else
            {
                // Third+ miss: warrant
                var violation = new ViolationRecord(
                    ViolationType.Other,
                    $"Repeated failure to pay supervision fees (${paroleRecord.GetTotalFeesOwed():F0} outstanding, {missed} missed payments)",
                    2.5f);
                rapSheet.AddParoleViolation(violation);
                Core.ResolveParoleManager()?.SendSupervisingOfficerText(player,
                    $"Payment default: {missed} missed payments. Agent warrant may be issued.");
            }

            Core.ResolveRapSheetManager().MarkRapSheetChanged(player);
        }

        /// <summary>
        /// Get player's current cash amount
        /// </summary>
        /// <param name="player">Currently unused; the cash service is global.</param>
        /// <returns>Global cash balance, or zero when the runtime money service is unavailable.</returns>
        private float GetPlayerCash(Player player)
        {
            try
            {
#if !MONO
                var networkSingleton = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance;
#else
                var networkSingleton = ScheduleOne.DevUtilities.NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance;
#endif
                if (networkSingleton != null)
                {
                    return networkSingleton.cashBalance;
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[FEES] Error getting player cash: {ex.Message}");
            }
            return 0f;
        }

        /// <summary>
        /// Deduct cash from the player
        /// </summary>
        /// <param name="player">Currently unused; the cash service is global.</param>
        /// <param name="amount">Cash amount to pass to the runtime money service.</param>
        /// <remarks>Errors are logged and swallowed; callers do not receive a success signal.</remarks>
        private void DeductPlayerCash(Player player, float amount)
        {
            try
            {
#if !MONO
                var networkSingleton = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Money.MoneyManager>.Instance;
#else
                var networkSingleton = ScheduleOne.DevUtilities.NetworkSingleton<ScheduleOne.Money.MoneyManager>.Instance;
#endif
                if (networkSingleton != null)
                {
                    networkSingleton.ChangeCashBalance(-amount, true);
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"[FEES] Error deducting cash: {ex.Message}");
            }
        }
    }
}
