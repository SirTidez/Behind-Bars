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
