using System.Collections;
using Behind_Bars.Helpers;
using Behind_Bars.Utils;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
using TMPro;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Approved 320x90 top-left recreation status card. It shares the officer-command
    /// slot and is synchronously suppressed whenever an officer command takes ownership.
    /// </summary>
    public sealed class TierStatusUI : MonoBehaviour
    {
        private static readonly Color Gold = new Color(1f, 0.9f, 0.3f, 1f);
        private static readonly Color Calm = new Color(0.45f, 0.86f, 0.9f, 1f);
        private static readonly Color Warning = new Color(1f, 0.74f, 0.27f, 1f);
        private static readonly Color Urgent = new Color(1f, 0.36f, 0.33f, 1f);
        private static readonly Color NeutralBorder = new Color(0.3f, 0.3f, 0.3f, 1f);

        private GameObject _panel;
        private Image _backgroundImage;
        private Image _leadingEdgeImage;
        private Image _cellBackgroundImage;
        private Image _progressFillImage;
        private Outline _panelOutline;
        private Outline _cellOutline;
        private TextMeshProUGUI _headerText;
        private TextMeshProUGUI _timerText;
        private TextMeshProUGUI _timerLabelText;
        private TextMeshProUGUI _activeTierText;
        private TextMeshProUGUI _assignedTierText;
        private TextMeshProUGUI _cellLabelText;
        private TextMeshProUGUI _cellNumberText;
        private CanvasGroup _canvasGroup;
        private RectTransform _progressFillRect;
        private Coroutine _fadeCoroutine;
        private Coroutine _canvasInitializationCoroutine;
        private bool _isInitialized;
        private bool _isWarning;
        private bool _isUrgent;
        private Color _stateColor = Calm;

#if !MONO
        public TierStatusUI(System.IntPtr ptr) : base(ptr) { }
#endif

        private void Start()
        {
            if (!_isInitialized)
            {
                CreateUI();
            }
        }

        private void Update()
        {
            if (!_isInitialized || _panel == null || !_panel.activeSelf || _leadingEdgeImage == null)
            {
                return;
            }

            float pulseAlpha = _isUrgent
                ? Mathf.Lerp(0.45f, 1f, (Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f) + 1f) * 0.5f)
                : 1f;
            _leadingEdgeImage.color = new Color(_stateColor.r, _stateColor.g, _stateColor.b, pulseAlpha);
            if (_cellOutline != null)
            {
                _cellOutline.effectColor = _isWarning
                    ? new Color(_stateColor.r, _stateColor.g, _stateColor.b, pulseAlpha)
                    : NeutralBorder;
            }
        }

        public void CreateUI()
        {
            Canvas hudCanvas = GetPlayerHUDCanvas();
            if (hudCanvas == null)
            {
                if (_canvasInitializationCoroutine == null)
                {
                    _canvasInitializationCoroutine = MelonCoroutines.Start(WaitForCanvasAndCreate()) as Coroutine;
                }
                return;
            }

            CreateUIWithCanvas(hudCanvas);
        }

        private Canvas GetPlayerHUDCanvas()
        {
            try
            {
#if !MONO
                var hud = Singleton<Il2CppScheduleOne.UI.HUD>.Instance;
                return hud != null && hud.Pointer != System.IntPtr.Zero ? hud.canvas : null;
#else
                return Singleton<HUD>.Instance?.canvas;
#endif
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private void CreateUIWithCanvas(Canvas hudCanvas)
        {
            if (_isInitialized || hudCanvas == null)
            {
                return;
            }

            if (!TMPFontFix.EnsureFontCached(hudCanvas))
            {
                ModLogger.Error("TierStatusUI: Could not resolve a valid TMP font/material pair");
                return;
            }

            _panel = new GameObject("TierStatusPanel");
            _panel.transform.SetParent(hudCanvas.transform, false);
            RectTransform panelRect = _panel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(10f, -10f);
            panelRect.sizeDelta = new Vector2(320f, 90f);

            _canvasGroup = _panel.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;

            _backgroundImage = _panel.AddComponent<Image>();
            _backgroundImage.color = new Color(0f, 0f, 0f, 0.8f);
            _backgroundImage.raycastTarget = false;
            _panelOutline = _panel.AddComponent<Outline>();
            _panelOutline.effectColor = NeutralBorder;
            _panelOutline.effectDistance = new Vector2(1f, -1f);

            _leadingEdgeImage = CreateImage(_panel.transform, "WarningEdge", 0f, 0f, 4f, 90f, Calm);
            _leadingEdgeImage.gameObject.SetActive(false);

            _headerText = CreateText(_panel.transform, "HeaderText", 10f, 4f, 230f, 18f, 12f, Gold, FontStyles.Bold);
            _timerText = CreateText(_panel.transform, "TimerText", 10f, 25f, 72f, 34f, 21f, Calm, FontStyles.Bold);
            _timerText.overflowMode = TextOverflowModes.Overflow;
            _timerLabelText = CreateText(_panel.transform, "TimerLabelText", 90f, 24f, 150f, 16f, 10f, Color.white, FontStyles.Bold);
            _activeTierText = CreateText(_panel.transform, "ActiveTierText", 90f, 39f, 150f, 21f, 15f, Color.white, FontStyles.Bold);
            _assignedTierText = CreateText(_panel.transform, "AssignedTierText", 10f, 62f, 230f, 16f, 10f, new Color(0.7f, 0.7f, 0.7f, 1f), FontStyles.Italic);

            _cellBackgroundImage = CreateImage(_panel.transform, "CellBadge", 250f, 10f, 60f, 58f, new Color(0.08f, 0.09f, 0.1f, 1f));
            _cellOutline = _cellBackgroundImage.gameObject.AddComponent<Outline>();
            _cellOutline.effectColor = NeutralBorder;
            _cellOutline.effectDistance = new Vector2(1f, -1f);
            _cellLabelText = CreateText(_cellBackgroundImage.transform, "CellLabelText", 0f, 8f, 60f, 16f, 10f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
            _cellNumberText = CreateText(_cellBackgroundImage.transform, "CellNumberText", 0f, 20f, 60f, 36f, 21f, Color.white, FontStyles.Bold, TextAlignmentOptions.Center);
            _cellNumberText.overflowMode = TextOverflowModes.Overflow;

            CreateImage(_panel.transform, "ProgressTrack", 10f, 81f, 300f, 3f, new Color(0.16f, 0.17f, 0.18f, 1f));
            _progressFillImage = CreateImage(_panel.transform, "ProgressFill", 10f, 81f, 0f, 3f, Calm);
            _progressFillRect = _progressFillImage.rectTransform;

            _cellLabelText.text = "CELL";
            TMPFontFix.FixAllTMPFonts(_panel, "base");
            _panel.SetActive(false);
            _isInitialized = true;
            ModLogger.Debug("TierStatusUI created in the shared top-left officer-command slot");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void Show(TierStatusData data)
        {
            if (!_isInitialized)
            {
                CreateUI();
            }
            if (!_isInitialized || data == null)
            {
                return;
            }

            UpdateStatus(data);
            bool wasVisible = _panel.activeSelf && _canvasGroup.alpha > 0.99f;
            _panel.SetActive(true);
            if (wasVisible)
            {
                return;
            }

            StartFade(1f, 0.15f, deactivateWhenComplete: false);
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        internal void UpdateStatus(TierStatusData data)
        {
            if (!_isInitialized || data == null)
            {
                return;
            }

            _headerText.text = data.HeaderText;
            _timerText.text = data.TimerText;
            _timerLabelText.text = data.TimerLabel;
            _activeTierText.text = data.ActiveTierText;
            _assignedTierText.text = data.AssignedTierText;
            _cellNumberText.text = data.CellText;

            _isWarning = data.IsAssignedTierActive && data.RemainingRealSeconds <= 30f;
            _isUrgent = data.IsAssignedTierActive && data.RemainingRealSeconds <= 10f;
            _stateColor = _isUrgent ? Urgent : _isWarning ? Warning : Calm;

            _timerText.color = _stateColor;
            _timerLabelText.color = _isWarning ? _stateColor : Color.white;
            _progressFillImage.color = _stateColor;
            _progressFillRect.sizeDelta = new Vector2(300f * Mathf.Clamp01(data.PhaseProgress), 3f);
            _leadingEdgeImage.gameObject.SetActive(_isWarning);
            _panelOutline.effectColor = _isUrgent ? Urgent : _isWarning ? Warning : NeutralBorder;
            _cellOutline.effectColor = _isWarning ? _stateColor : NeutralBorder;
            _cellBackgroundImage.color = _isUrgent
                ? new Color(0.14f, 0.04f, 0.04f, 1f)
                : new Color(0.08f, 0.09f, 0.1f, 1f);
        }

        internal void Hide()
        {
            if (!_isInitialized || _panel == null || !_panel.activeSelf)
            {
                return;
            }
            StartFade(0f, 0.15f, deactivateWhenComplete: true);
        }

        internal void HideImmediate()
        {
            CancelFade();
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
            }
            if (_panel != null)
            {
                _panel.SetActive(false);
            }
        }

        internal bool IsVisible()
        {
            return _isInitialized && _panel != null && _panel.activeSelf && _canvasGroup != null && _canvasGroup.alpha > 0f;
        }

        public void CancelForSceneExit()
        {
            CancelFade();
            if (_canvasInitializationCoroutine != null)
            {
                MelonCoroutines.Stop(_canvasInitializationCoroutine);
                _canvasInitializationCoroutine = null;
            }

            if (_panel != null)
            {
                _panel.SetActive(false);
            }

            _panel = null;
            _backgroundImage = null;
            _leadingEdgeImage = null;
            _cellBackgroundImage = null;
            _progressFillImage = null;
            _panelOutline = null;
            _cellOutline = null;
            _headerText = null;
            _timerText = null;
            _timerLabelText = null;
            _activeTierText = null;
            _assignedTierText = null;
            _cellLabelText = null;
            _cellNumberText = null;
            _canvasGroup = null;
            _progressFillRect = null;
            _isInitialized = false;
        }

        private static Image CreateImage(Transform parent, string name, float x, float y, float width, float height, Color color)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            Image image = obj.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static TextMeshProUGUI CreateText(
            Transform parent,
            string name,
            float x,
            float y,
            float width,
            float height,
            float fontSize,
            Color color,
            FontStyles style,
            TextAlignmentOptions alignment = TextAlignmentOptions.Left)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            RectTransform rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            TextMeshProUGUI text = obj.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.color = color;
            text.fontStyle = style;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        private void StartFade(float targetAlpha, float duration, bool deactivateWhenComplete)
        {
            CancelFade();
            _fadeCoroutine = MelonCoroutines.Start(FadeTo(targetAlpha, duration, deactivateWhenComplete)) as Coroutine;
        }

        private void CancelFade()
        {
            if (_fadeCoroutine != null)
            {
                MelonCoroutines.Stop(_fadeCoroutine);
                _fadeCoroutine = null;
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator FadeTo(float targetAlpha, float duration, bool deactivateWhenComplete)
        {
            float startAlpha = _canvasGroup != null ? _canvasGroup.alpha : 0f;
            float elapsed = 0f;
            while (elapsed < duration && _canvasGroup != null && _panel != null)
            {
                elapsed += Time.unscaledDeltaTime;
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
                yield return null;
            }

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = targetAlpha;
            }
            if (deactivateWhenComplete && _panel != null)
            {
                _panel.SetActive(false);
            }
            _fadeCoroutine = null;
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator WaitForCanvasAndCreate()
        {
            const int maxAttempts = 20;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                Canvas hudCanvas = GetPlayerHUDCanvas();
                if (hudCanvas != null)
                {
                    _canvasInitializationCoroutine = null;
                    CreateUIWithCanvas(hudCanvas);
                    yield break;
                }
            }

            _canvasInitializationCoroutine = null;
            ModLogger.Error("TierStatusUI: Player HUD canvas was unavailable after 10 real seconds");
        }

        private void OnDestroy()
        {
            CancelForSceneExit();
        }
    }
}
