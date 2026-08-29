using System;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Jail;
using Behind_Bars.Systems.NPCs;
using UnityEngine;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Top-level ownership shell for jail-domain coordination.
    /// This manager owns the primary <see cref="JailSystem"/> instance, collaborates with
    /// release and booking processes, and intentionally does not own the lifetime of
    /// <see cref="ReleaseManager"/>, <see cref="BookingProcess"/>, or <see cref="JailTimeTracker"/>.
    /// </summary>
    public class JailManager
    {
        private readonly JailSystem jailSystem;
        private readonly JailTimeTracker jailTimeTracker;
        private readonly System.Collections.Generic.Dictionary<string, ReleaseManager.ReleaseType> pendingReleaseTypes =
            new System.Collections.Generic.Dictionary<string, ReleaseManager.ReleaseType>();
        private ReleaseManager? releaseManager;
        private BookingProcess? bookingProcess;

        /// <summary>
        /// Gets the owned jail system.
        /// </summary>
        public JailSystem JailSystem => jailSystem;

        /// <summary>
        /// Gets the attached release manager, if any.
        /// </summary>
        public ReleaseManager? ReleaseManager => releaseManager;

        /// <summary>
        /// Gets the attached booking process, if any.
        /// </summary>
        public BookingProcess? BookingProcess => bookingProcess;

        /// <summary>
        /// Gets the jail time tracker collaborator.
        /// </summary>
        public JailTimeTracker JailTimeTracker => jailTimeTracker;

        /// <summary>
        /// Creates a jail ownership shell around the supplied jail system.
        /// </summary>
        /// <param name="jailSystem">The owned jail system instance.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="jailSystem"/> is null.</exception>
        public JailManager(JailSystem jailSystem)
        {
            this.jailSystem = jailSystem ?? throw new ArgumentNullException(nameof(jailSystem));
            jailTimeTracker = JailTimeTracker.Instance;
        }

        /// <summary>
        /// Attaches the release manager collaborator without taking ownership of its lifetime.
        /// </summary>
        /// <param name="releaseManager">The release manager to attach, or null to detach.</param>
        public void AttachReleaseManager(ReleaseManager? releaseManager)
        {
            this.releaseManager = releaseManager;
        }

        /// <summary>
        /// Attaches the booking process collaborator without taking ownership of its lifetime.
        /// </summary>
        /// <param name="bookingProcess">The booking process to attach, or null to detach.</param>
        public void AttachBookingProcess(BookingProcess? bookingProcess)
        {
            this.bookingProcess = bookingProcess;
        }

        /// <summary>
        /// Resolve the current booking-process collaborator from the active scene if needed and cache it on the manager shell.
        /// </summary>
        public BookingProcess? ResolveBookingProcess()
        {
            if (bookingProcess == null)
            {
                bookingProcess = Core.ResolveBookingProcess();
            }

            return bookingProcess;
        }

        /// <summary>
        /// Run the active booking process for a jailed player through the manager-owned booking seam.
        /// This centralizes booking orchestration while preserving the current booking-process implementation.
        /// </summary>
        public System.Collections.IEnumerator RunBookingProcess(Player player, JailSystem.JailSentence sentence, float fallbackWaitSeconds = 5f)
        {
            if (!Core.IsGameplaySceneActive || player == null || sentence == null)
            {
                yield break;
            }

            var activeBookingProcess = ResolveBookingProcess();
            if (activeBookingProcess != null)
            {
                ModLogger.Info($"JailManager starting BookingProcess for {player.name} with sentence: {sentence.JailTime}s, Fine: ${sentence.FineAmount}");
                bool bookingFinalized = false;
                Action<Player> finalizedHandler = finalizedPlayer =>
                {
                    if (finalizedPlayer == player)
                    {
                        bookingFinalized = true;
                    }
                };

                BookingLifecycleCoordinator.BookingFinalized += finalizedHandler;
                try
                {
                    activeBookingProcess.StartBooking(player, sentence);

                    // Canonical completion is event driven. The process-state check
                    // remains only as an interruption guard for scene unloads or a
                    // cancelled booking; it is not used to detect normal completion.
                    while (Core.IsGameplaySceneActive && !bookingFinalized && activeBookingProcess.IsBookingInProgress())
                    {
                        yield return null;
                    }

                    if (!Core.IsGameplaySceneActive || player == null)
                    {
                        yield break;
                    }

                    if (bookingFinalized)
                    {
                        ModLogger.Info($"JailManager received final booking completion for {player.name}");
                    }
                    else
                    {
                        ModLogger.Warn($"JailManager booking for {player.name} ended without a finalization signal");
                    }
                }
                finally
                {
                    BookingLifecycleCoordinator.BookingFinalized -= finalizedHandler;
                }

                yield break;
            }

            ModLogger.Error("JailManager could not resolve a BookingProcess - using fallback wait");
            yield return new WaitForSeconds(fallbackWaitSeconds);
            if (!Core.IsGameplaySceneActive)
            {
                yield break;
            }
        }

        /// <summary>
        /// Apply the canonical custody-state control policy for jailed or releasing players.
        /// This preserves the current behavior of keeping player controls enabled while in custody.
        /// </summary>
        public void ApplyCustodyState(Player player, bool inJail)
        {
            if (player == null)
            {
                return;
            }

            if (inJail)
            {
                ModLogger.Info("JailManager applying custody state - keeping all controls enabled");
            }
            else
            {
                ModLogger.Info("JailManager applying release state - enabling all controls");
            }

            try
            {
                MaintainCustodyControls();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"JailManager failed to apply custody state: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-apply the active custody control policy. This is safe to call repeatedly from wait loops.
        /// </summary>
        public void MaintainCustodyControls()
        {
            PlayerSingleton<PlayerInventory>.Instance.enabled = true;
            PlayerSingleton<PlayerInventory>.Instance.SetInventoryEnabled(true);
            PlayerSingleton<PlayerCamera>.Instance.SetCanLook(true);
            PlayerSingleton<PlayerCamera>.Instance.LockMouse();
#if MONO
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#else
            PlayerSingleton<PlayerMovement>.Instance.CanMove = true;
#endif
            Singleton<HUD>.Instance.canvas.enabled = true;
            Singleton<HUD>.Instance.SetCrosshairVisible(true);
        }

        /// <summary>
        /// Mark a player as currently in jail through the manager-owned tracker seam.
        /// </summary>
        public void MarkPlayerInJail(Player player)
        {
            if (player == null)
            {
                return;
            }

            jailTimeTracker.SetInJail(player);
        }

        /// <summary>
        /// Clear a player's jail-status flag through the manager-owned tracker seam.
        /// </summary>
        public void ClearPlayerInJail(Player player)
        {
            if (player == null)
            {
                return;
            }

            jailTimeTracker.ClearInJail(player);
        }

        /// <summary>
        /// Start tracking an active sentence through the manager-owned tracker seam.
        /// </summary>
        public void StartSentenceTracking(Player player, float sentenceGameMinutes, Action<Player> onComplete)
        {
            if (player == null)
            {
                return;
            }

            jailTimeTracker.StartTracking(player, sentenceGameMinutes, onComplete);
        }

        /// <summary>
        /// Stop tracking an active sentence through the manager-owned tracker seam.
        /// </summary>
        public void StopSentenceTracking(Player player)
        {
            if (player == null)
            {
                return;
            }

            jailTimeTracker.StopTracking(player);
        }

        /// <summary>
        /// Check whether a player currently has an active tracked sentence.
        /// </summary>
        public bool IsTrackingSentence(Player player)
        {
            return player != null && jailTimeTracker.IsTracking(player);
        }

        /// <summary>
        /// Get the remaining tracked sentence time for a player.
        /// </summary>
        public float GetRemainingSentenceTime(Player player)
        {
            return player == null ? 0f : jailTimeTracker.GetRemainingTime(player);
        }

        /// <summary>
        /// Get the formatted remaining tracked sentence time for a player.
        /// </summary>
        public string GetFormattedRemainingSentenceTime(Player player)
        {
            return player == null ? string.Empty : jailTimeTracker.GetFormattedRemainingTime(player);
        }

        /// <summary>
        /// Get the original tracked sentence time for a player.
        /// </summary>
        public float GetOriginalSentenceTime(Player player)
        {
            return player == null ? 0f : jailTimeTracker.GetOriginalSentenceTime(player);
        }

        /// <summary>
        /// Get the amount of time served on the tracked sentence for a player.
        /// </summary>
        public float GetTimeServed(Player player)
        {
            return player == null ? 0f : jailTimeTracker.GetTimeServed(player);
        }

        /// <summary>
        /// Mark a pending release type for the player so custody cleanup can complete before the final release.
        /// </summary>
        public void MarkPendingReleaseType(Player player, ReleaseManager.ReleaseType releaseType)
        {
            if (player == null)
            {
                return;
            }

            pendingReleaseTypes[Core.ResolvePlayerKey(player)] = releaseType;
            ModLogger.Info($"[JAIL TRACKING] Marked pending release type {releaseType} for {player.name}");
        }

        /// <summary>
        /// Check whether the player has a pending release type waiting for custody cleanup.
        /// </summary>
        public bool HasPendingReleaseType(Player player)
        {
            return player != null && pendingReleaseTypes.ContainsKey(Core.ResolvePlayerKey(player));
        }

        /// <summary>
        /// Determines whether bail can initiate a release without colliding with the active
        /// intake officer's cell-return workflow.
        /// </summary>
        public bool IsBailReleaseReady(Player player)
        {
            if (player == null)
            {
                return false;
            }

            if (releaseManager == null && !ReleaseManager.HasRegisteredInstance)
            {
                return false;
            }

            var npcManager = Core.Instance?.NpcManager;
            var intakeOfficer = npcManager?.GetIntakeOfficer();
            return intakeOfficer != null && !intakeOfficer.IsProcessingIntake();
        }

        /// <summary>
        /// Consume the pending release type for the player, defaulting to time served when no pending release exists.
        /// </summary>
        public ReleaseManager.ReleaseType ConsumePendingReleaseType(Player player)
        {
            if (player == null)
            {
                return ReleaseManager.ReleaseType.TimeServed;
            }

            string playerKey = Core.ResolvePlayerKey(player);
            if (pendingReleaseTypes.TryGetValue(playerKey, out var releaseType))
            {
                pendingReleaseTypes.Remove(playerKey);
                ModLogger.Info($"[JAIL TRACKING] Consumed pending release type {releaseType} for {player.name}");
                return releaseType;
            }

            return ReleaseManager.ReleaseType.TimeServed;
        }

        /// <summary>
        /// Initiate the enhanced release flow through the manager-owned jail/release seam.
        /// </summary>
        public void InitiateEnhancedRelease(Player player, ReleaseManager.ReleaseType releaseType, float bailAmount = 0f)
        {
            if (player == null)
            {
                ModLogger.Error("Cannot initiate release for null player");
                return;
            }

            try
            {
                ModLogger.Info($"Initiating enhanced {releaseType} release for {player.name}");
                jailSystem.StorePlayerExitPosition(player);

                var activeReleaseManager = releaseManager ?? Core.ResolveReleaseManager();
                if (activeReleaseManager == null)
                {
                    ModLogger.Warn("JailManager: active release manager missing during release initiation; retrying bootstrap");
                    activeReleaseManager = ReleaseManager.BootstrapManagedInstance();
                    if (activeReleaseManager != null)
                    {
                        releaseManager = activeReleaseManager;
                    }
                }
                if (activeReleaseManager != null)
                {
                    string reason = releaseType switch
                    {
                        ReleaseManager.ReleaseType.TimeServed => "Time served",
                        ReleaseManager.ReleaseType.BailPayment => $"Bail paid: ${bailAmount:F0}",
                        ReleaseManager.ReleaseType.CourtOrder => "Court order",
                        ReleaseManager.ReleaseType.Emergency => "Emergency release",
                        _ => "Release ordered"
                    };

                    bool releaseStarted = activeReleaseManager.InitiateRelease(player, releaseType, bailAmount, reason);
                    if (releaseStarted)
                    {
                        ModLogger.Info($"Enhanced release started for {player.name}");
                    }
                    else
                    {
                        ModLogger.Warn($"Failed to start enhanced release for {player.name} - falling back to direct release");
                        jailSystem.ReleasePlayerFromJail(player);
                    }
                }
                else
                {
                    ModLogger.Warn("ReleaseManager not available - using legacy release");
                    jailSystem.ReleasePlayerFromJail(player);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error initiating enhanced release: {ex.Message}");
                jailSystem.ReleasePlayerFromJail(player);
            }
        }

        /// <summary>
        /// Safely initiate enhanced release, checking for an existing release first.
        /// </summary>
        public void SafeInitiateEnhancedRelease(Player player, ReleaseManager.ReleaseType releaseType, float bailAmount = 0f)
        {
            if (player == null)
            {
                return;
            }

            var activeReleaseManager = releaseManager ?? Core.ResolveReleaseManager();
            if (activeReleaseManager == null)
            {
                activeReleaseManager = ReleaseManager.BootstrapManagedInstance();
                if (activeReleaseManager != null)
                {
                    releaseManager = activeReleaseManager;
                }
            }
            if (activeReleaseManager != null && activeReleaseManager.IsReleaseInProgress(player))
            {
                ModLogger.Info($"Player {player.name} release skipped - release already in progress (early release system handling it)");
                return;
            }

            ModLogger.Info($"Initiating {releaseType} release for {player.name}");
            InitiateEnhancedRelease(player, releaseType, bailAmount);
        }

        /// <summary>
        /// Cancel any active release flow for a player through the manager-owned jail/release seam.
        /// </summary>
        public void CancelActiveRelease(Player player)
        {
            if (player == null)
            {
                return;
            }

            var activeReleaseManager = releaseManager ?? Core.ResolveReleaseManager();
            if (activeReleaseManager == null)
            {
                activeReleaseManager = ReleaseManager.BootstrapManagedInstance();
                if (activeReleaseManager != null)
                {
                    releaseManager = activeReleaseManager;
                }
            }
            activeReleaseManager?.CancelPlayerRelease(player);
        }

        /// <summary>
        /// Reset active booking/release collaborators for a player before a new arrest begins.
        /// This keeps release-manager interaction behind the manager seam while preserving the
        /// current behavior that booking owns its own runtime cleanup.
        /// </summary>
        public void ResetActiveJailFlow(Player player)
        {
            if (player == null)
            {
                return;
            }

            var activeBookingProcess = ResolveBookingProcess();
            if (activeBookingProcess != null)
            {
                ModLogger.Info("BookingProcess found - it will handle its own cleanup");
            }

            CancelActiveRelease(player);
        }

        /// <summary>
        /// Resolve the stored bail amount for a player, calculating and caching a fallback amount
        /// when the booking flow did not already store one.
        /// </summary>
        public float ResolveBailAmount(Player player)
        {
            if (player == null)
            {
                return 0f;
            }

            float bailAmount = 0f;
            var bailSystem = Core.ResolveBailSystem();
            if (bailSystem == null)
            {
                ModLogger.Warn("[BAIL] BailSystem not available - bail payment will not work");
                return 0f;
            }

            bailAmount = bailSystem.GetBailAmount(player);
            if (bailAmount > 0f)
            {
                ModLogger.Info($"[BAIL] Retrieved stored bail amount: ${bailAmount:F0} for {player.name}");
                return bailAmount;
            }

            float fineAmount = jailSystem.CalculateTotalCrimeFinesForManager(player);
            if (fineAmount <= 0f)
            {
                return 0f;
            }

            var bailOffer = bailSystem.CalculateBailAmount(player, fineAmount);
            bailAmount = bailOffer.Amount;
            bailSystem.StoreBailAmount(player, bailAmount);
            ModLogger.Info($"[BAIL] Calculated bail amount: ${bailAmount:F0} for {player.name} (based on fine: ${fineAmount:F0})");
            return bailAmount;
        }

        /// <summary>
        /// Complete the post-sentence release handoff through the manager-owned jail/release seam.
        /// </summary>
        public void CompletePostSentenceRelease(Player player)
        {
            if (player == null)
            {
                return;
            }

            var releaseType = ConsumePendingReleaseType(player);
            float bailAmount = releaseType == ReleaseManager.ReleaseType.BailPayment
                ? ResolveBailAmount(player)
                : 0f;
            SafeInitiateEnhancedRelease(player, releaseType, bailAmount);
        }

        /// <summary>
        /// Complete the post-booking jail-time flow through the manager-owned jail/release seam.
        /// </summary>
        public System.Collections.IEnumerator StartJailTimeAfterBooking(Player player, JailSystem.JailSentence sentence)
        {
            if (player == null || sentence == null)
            {
                yield break;
            }

            ModLogger.Info($"JailManager starting jail time after booking for {player.name} - {sentence.JailTime}s");

            yield return jailSystem.WaitForJailSentence(sentence.JailTime, player);

            if (!Core.IsGameplaySceneActive || player == null)
            {
                yield break;
            }

            CompletePostSentenceRelease(player);
        }

        /// <summary>
        /// Calculate a fallback bail amount through the owned jail system.
        /// </summary>
        public float CalculateBailAmount(float fineAmount, JailSystem.JailSeverity severity)
        {
            return jailSystem.CalculateBailAmount(fineAmount, severity);
        }

        /// <summary>
        /// Clear jail status and associated release cleanup through the jail system owner.
        /// </summary>
        public void ClearPlayerJailStatus(Player player)
        {
            jailSystem.ClearPlayerJailStatus(player);
        }

        /// <summary>
        /// Forward an immediate arrest through the jail system owner.
        /// </summary>
        public System.Collections.IEnumerator HandleImmediateArrest(Player player)
        {
            return jailSystem.HandleImmediateArrest(player);
        }

        /// <summary>
        /// Resets attached collaborator references without altering owned jail-system state.
        /// </summary>
        public void Shutdown()
        {
            pendingReleaseTypes.Clear();
            releaseManager = null;
            bookingProcess = null;
        }
    }

    /// <summary>
    /// Compatibility extension methods that forward legacy `NpcManager` registry lookups to
    /// the currently attached prison NPC manager. These keep out-of-scope callers compiling
    /// while the NPC ownership cut continues.
    /// </summary>
    internal static class NpcManagerRegistryExtensions
    {
        public static System.Collections.Generic.List<GuardBehavior> GetRegisteredGuards(this NpcManager? npcManager)
        {
            return npcManager?.PrisonNpcManager?.GetRegisteredGuards()
                   ?? new System.Collections.Generic.List<GuardBehavior>();
        }

        public static System.Collections.Generic.List<ParoleOfficerBehavior> GetRegisteredParoleOfficers(this NpcManager? npcManager)
        {
            return npcManager?.PrisonNpcManager?.GetRegisteredParoleOfficers()
                   ?? new System.Collections.Generic.List<ParoleOfficerBehavior>();
        }
    }
}
