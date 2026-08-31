using Behind_Bars.Helpers;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Systems.Jail;
using Behind_Bars.UI;
using System.Collections.Generic;
#if !MONO
using Il2CppScheduleOne.PlayerScripts;
#else
using ScheduleOne.PlayerScripts;
#endif

namespace Behind_Bars.Systems
{
    /// <summary>
    /// Manager-owned UI root that coordinates access to the scene-bound Behind Bars UI layer.
    /// The wrapped <see cref="BehindBarsUIManager"/> remains the concrete scene/UI implementation.
    /// </summary>
    /// <remarks>
    /// This type is a forwarding ownership boundary, not a second UI implementation. Its
    /// initialization flag controls access to the wrapped singleton; only the scene-specific
    /// initializer performs the wrapped UI initialization. The ordinary shutdown destroys jail
    /// info, while <see cref="ShutdownSceneUI"/> performs the full scene callback teardown.
    /// </remarks>
    public sealed class JusticeUIManager : ISubsystemLifecycle
    {
        private static JusticeUIManager? _compatibilityInstance;

        /// <summary>
        /// Fallback wrapper used before the system manager is available.
        /// </summary>
        /// <remarks>The compatibility instance is lazy and has no separate disposal path.</remarks>
        public static JusticeUIManager CompatibilityInstance => _compatibilityInstance ??= new JusticeUIManager();

        private bool _isInitialized;

        /// <inheritdoc />
        /// <remarks>
        /// Initialize only marks this forwarding shell ready; it does not create the scene UI.
        /// Call <see cref="InitializeSceneUI"/> once a gameplay HUD is available.
        /// </remarks>
        public void Initialize()
        {
            _isInitialized = true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// This lifecycle shutdown only destroys jail info and resets the shell flag. Scene
        /// listeners/coroutines owned by the wrapped UI are released by
        /// <see cref="ShutdownSceneUI"/>.
        /// </remarks>
        public void Shutdown()
        {
            if (!_isInitialized)
            {
                return;
            }

            try
            {
                BehindBarsUIManager.Instance.DestroyJailInfoUI();
            }
            catch (System.Exception ex)
            {
                ModLogger.Warn($"JusticeUIManager shutdown ignored UI teardown issue: {ex.Message}");
            }

            _isInitialized = false;
        }

        /// <summary>
        /// Initialize the wrapped scene UI service when a gameplay scene is ready.
        /// </summary>
        /// <remarks>Repeated calls are forwarded to the wrapped UI's own idempotent initializer.</remarks>
        public void InitializeSceneUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.Initialize();
        }

        /// <summary>Forward a retry of the wrapped UI prefab load.</summary>
        public void RetryLoadUIPrefab()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.RetryLoadUIPrefab();
        }

        /// <summary>Forward a loading-screen presentation request.</summary>
        /// <param name="message">Message displayed by the wrapped UI.</param>
        public void ShowLoadingScreen(string message = "Loading Behind Bars...")
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowLoadingScreen(message);
        }

        /// <summary>Forward loading progress and its optional status text.</summary>
        /// <param name="progress">Progress value interpreted by the wrapped UI.</param>
        /// <param name="statusMessage">Optional status message displayed with the progress.</param>
        public void UpdateLoadingProgress(float progress, string statusMessage = "")
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.UpdateLoadingProgress(progress, statusMessage);
        }

        /// <summary>Forward a request to hide the loading screen.</summary>
        public void HideLoadingScreen()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideLoadingScreen();
        }

        /// <summary>Query loading-screen visibility from the wrapped UI.</summary>
        /// <returns><see langword="true"/> when the wrapped loading screen is visible.</returns>
        public bool IsLoadingScreenVisible()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            return BehindBarsUIManager.Instance.IsLoadingScreenVisible();
        }

        /// <summary>Forward the full Behind Bars instructions view request.</summary>
        public void ShowInstructions()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowInstructions();
        }

        /// <summary>Forward a parole-status presentation request.</summary>
        public void ShowParoleStatus()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowParoleStatus();
        }

        /// <summary>Forward a request to hide the parole-status surface.</summary>
        public void HideParoleStatus()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideParoleStatus();
        }

        /// <summary>Forward static jail information to the wrapped custody UI.</summary>
        /// <param name="crime">Charge text to display.</param>
        /// <param name="timeInfo">Formatted sentence text to display.</param>
        /// <param name="bailInfo">Formatted bail text to display.</param>
        public void ShowJailInfoUI(string crime, string timeInfo, string bailInfo)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowJailInfoUI(crime, timeInfo, bailInfo);
        }

        /// <summary>Forward jail information with dynamic sentence/bail updates.</summary>
        /// <param name="crime">Charge text to display.</param>
        /// <param name="timeInfo">Formatted sentence text to display.</param>
        /// <param name="bailInfo">Formatted bail text to display.</param>
        /// <param name="jailTimeSeconds">Legacy duration parameter forwarded unchanged to the wrapped UI.</param>
        /// <param name="bailAmount">Numeric bail amount used by dynamic updates.</param>
        public void ShowJailInfoUI(string crime, string timeInfo, string bailInfo, float jailTimeSeconds, float bailAmount)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowJailInfoUI(crime, timeInfo, bailInfo, jailTimeSeconds, bailAmount);
        }

        /// <summary>Forward a request to hide jail information.</summary>
        public void HideJailInfoUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideJailInfoUI();
        }

        /// <summary>Forward a notification to the wrapped UI.</summary>
        /// <param name="message">Notification text.</param>
        /// <param name="type">Notification presentation category.</param>
        public void ShowNotification(string message, NotificationType type)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowNotification(message, type);
        }

        /// <summary>Forward a request to show crime details.</summary>
        public void ShowCrimeDetails()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowCrimeDetails();
        }

        /// <summary>Forward complete jail-info UI destruction.</summary>
        public void DestroyJailInfoUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.DestroyJailInfoUI();
        }

        /// <summary>
        /// Releases scene-owned UI callbacks while retaining the manager service for the next Main scene.
        /// </summary>
        /// <remarks>
        /// This is the scene teardown boundary for the wrapped UI. It also resets the shell's
        /// initialized flag so later forwarding calls can re-enter through the next scene setup.
        /// </remarks>
        public void ShutdownSceneUI()
        {
            BehindBarsUIManager.Instance.ShutdownSceneUI();
            _isInitialized = false;
        }

        /// <summary>
        /// Forward an officer instruction update and preserve its ownership of the shared HUD slot.
        /// </summary>
        /// <param name="data">Latest officer instruction data.</param>
        /// <remarks>
        /// Officer commands have precedence over the lower-priority recreation tier-status
        /// surface. The wrapped UI hides tier status before updating/rebuilding the command;
        /// tier status may resume only after <see cref="HideOfficerCommand"/> clears the command.
        /// </remarks>
        public void UpdateOfficerCommand(OfficerCommandData data)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.UpdateOfficerCommand(data);
        }

        /// <summary>Forward officer-command dismissal, releasing the shared HUD slot.</summary>
        /// <remarks>Clearing the command allows the tier-status surface to arbitrate again.</remarks>
        public void HideOfficerCommand()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideOfficerCommand();
        }

        /// <summary>Get the wrapped custody UI adapter, initializing the shell if needed.</summary>
        /// <returns>The current wrapper, or <see langword="null"/> when the wrapped UI has none.</returns>
        public BehindBarsUIWrapper? GetUIWrapper()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            return BehindBarsUIManager.Instance.GetUIWrapper();
        }

        /// <summary>Forward bail UI presentation with its numeric amount.</summary>
        /// <param name="bailAmount">Bail amount shown by the wrapped UI.</param>
        public void ShowBailUI(float bailAmount)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowBailUI(bailAmount);
        }

        /// <summary>Forward a request to hide bail UI.</summary>
        public void HideBailUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideBailUI();
        }

        /// <summary>Query bail UI visibility from the wrapped UI.</summary>
        /// <returns><see langword="true"/> when bail UI is visible.</returns>
        public bool IsBailUIVisible()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            return BehindBarsUIManager.Instance.IsBailUIVisible();
        }

        /// <summary>Forward the parole-conditions presentation to the wrapped UI.</summary>
        /// <param name="player">Player whose conditions are displayed.</param>
        /// <param name="bailAmountPaid">Bail paid for the term.</param>
        /// <param name="fineAmount">Fine amount associated with the term.</param>
        /// <param name="termLengthGameMinutes">Parole term length in game minutes.</param>
        /// <param name="lsiLevel">Resulting LSI level.</param>
        /// <param name="lsiBreakdown">Score breakdown used by the presentation.</param>
        /// <param name="jailTimeInfo">Original sentence and time-served values.</param>
        /// <param name="recentCrimes">Recent charge descriptions.</param>
        /// <param name="generalConditions">General condition descriptions.</param>
        /// <param name="specialConditions">Active special condition descriptions.</param>
        public void ShowParoleConditionsUI(
            Player player,
            float bailAmountPaid,
            float fineAmount,
            float termLengthGameMinutes,
            LSILevel lsiLevel,
            (int totalScore, int crimeCountScore, int severityScore, int violationScore, int pastParoleScore, LSILevel resultingLevel) lsiBreakdown,
            (float originalSentenceTime, float timeServed) jailTimeInfo,
            List<string> recentCrimes,
            List<string> generalConditions,
            List<string> specialConditions)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowParoleConditionsUI(
                player,
                bailAmountPaid,
                fineAmount,
                termLengthGameMinutes,
                lsiLevel,
                lsiBreakdown,
                jailTimeInfo,
                recentCrimes,
                generalConditions,
                specialConditions);
        }

        /// <summary>Forward a request to hide parole conditions UI.</summary>
        public void HideParoleConditionsUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideParoleConditionsUI();
        }

        /// <summary>Query parole-conditions UI visibility from the wrapped UI.</summary>
        /// <returns><see langword="true"/> when the conditions surface is visible.</returns>
        public bool IsParoleConditionsUIVisible()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            return BehindBarsUIManager.Instance.IsParoleConditionsUIVisible();
        }
    }
}
