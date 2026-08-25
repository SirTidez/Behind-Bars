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
    public sealed class JusticeUIManager : ISubsystemLifecycle
    {
        private static JusticeUIManager? _compatibilityInstance;

        /// <summary>
        /// Fallback wrapper used before the system manager is available.
        /// </summary>
        public static JusticeUIManager CompatibilityInstance => _compatibilityInstance ??= new JusticeUIManager();

        private bool _isInitialized;

        /// <inheritdoc />
        public void Initialize()
        {
            _isInitialized = true;
        }

        /// <inheritdoc />
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
        public void InitializeSceneUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.Initialize();
        }

        public void RetryLoadUIPrefab()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.RetryLoadUIPrefab();
        }

        public void ShowLoadingScreen(string message = "Loading Behind Bars...")
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowLoadingScreen(message);
        }

        public void UpdateLoadingProgress(float progress, string statusMessage = "")
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.UpdateLoadingProgress(progress, statusMessage);
        }

        public void HideLoadingScreen()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideLoadingScreen();
        }

        public bool IsLoadingScreenVisible()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            return BehindBarsUIManager.Instance.IsLoadingScreenVisible();
        }

        public void ShowInstructions()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowInstructions();
        }

        public void ShowParoleStatus()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowParoleStatus();
        }

        public void HideParoleStatus()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideParoleStatus();
        }

        public void ShowJailInfoUI(string crime, string timeInfo, string bailInfo)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowJailInfoUI(crime, timeInfo, bailInfo);
        }

        public void ShowJailInfoUI(string crime, string timeInfo, string bailInfo, float jailTimeSeconds, float bailAmount)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowJailInfoUI(crime, timeInfo, bailInfo, jailTimeSeconds, bailAmount);
        }

        public void HideJailInfoUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideJailInfoUI();
        }

        public void ShowNotification(string message, NotificationType type)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowNotification(message, type);
        }

        public void ShowCrimeDetails()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowCrimeDetails();
        }

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
        public void ShutdownSceneUI()
        {
            BehindBarsUIManager.Instance.ShutdownSceneUI();
            _isInitialized = false;
        }

        public void UpdateOfficerCommand(OfficerCommandData data)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.UpdateOfficerCommand(data);
        }

        public void HideOfficerCommand()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideOfficerCommand();
        }

        public BehindBarsUIWrapper? GetUIWrapper()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            return BehindBarsUIManager.Instance.GetUIWrapper();
        }

        public void ShowBailUI(float bailAmount)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.ShowBailUI(bailAmount);
        }

        public void HideBailUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideBailUI();
        }

        public bool IsBailUIVisible()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            return BehindBarsUIManager.Instance.IsBailUIVisible();
        }

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

        public void HideParoleConditionsUI()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            BehindBarsUIManager.Instance.HideParoleConditionsUI();
        }

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
