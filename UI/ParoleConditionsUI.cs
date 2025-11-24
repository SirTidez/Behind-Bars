using UnityEngine;
using UnityEngine.UI;
using Behind_Bars.Helpers;
using Behind_Bars.Systems;
using Behind_Bars.Systems.CrimeTracking;
using Behind_Bars.Utils;
using System.Collections;
using System.Collections.Generic;

#if !MONO
using Il2CppTMPro;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.DevUtilities;
#else
using TMPro;
using ScheduleOne.UI;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.UI
{
    /// <summary>
    /// Full-screen modal UI component that displays release summary and parole conditions
    /// Player must acknowledge before they can move away from the police station
    /// </summary>
    public class ParoleConditionsUI : MonoBehaviour
    {
#if !MONO
        public ParoleConditionsUI(System.IntPtr ptr) : base(ptr) { }
#endif

        private GameObject _overlayPanel;
        private GameObject _mainPanel;
        private Image _overlayImage;
        private Image _mainPanelImage;
        private TextMeshProUGUI _titleText;
        
        // Bail section
        private TextMeshProUGUI _bailLabelText;
        private TextMeshProUGUI _bailValueText;
        
        // Fine section
        private TextMeshProUGUI _fineLabelText;
        private TextMeshProUGUI _fineValueText;
        private TextMeshProUGUI _finePaymentTimeText;
        
        // Parole term section
        private TextMeshProUGUI _termLengthLabelText;
        private TextMeshProUGUI _termLengthValueText;
        
        // LSI level section
        private TextMeshProUGUI _lsiLevelLabelText;
        private TextMeshProUGUI _lsiLevelValueText;
        
        // LSI breakdown section
        private TextMeshProUGUI _lsiBreakdownLabelText;
        private TextMeshProUGUI _lsiBreakdownText;
        
        // Jail time comparison section
        private TextMeshProUGUI _jailTimeLabelText;
        private TextMeshProUGUI _jailTimeComparisonText;
        
        // Recent crimes section
        private TextMeshProUGUI _recentCrimesLabelText;
        private TextMeshProUGUI _recentCrimesListText;
        
        // General conditions section
        private TextMeshProUGUI _generalConditionsLabelText;
        private TextMeshProUGUI _generalConditionsListText;
        
        // Special conditions section
        private TextMeshProUGUI _specialConditionsLabelText;
        private TextMeshProUGUI _specialConditionsListText;
        
        private TextMeshProUGUI _dismissalInstructionText;
        private CanvasGroup _canvasGroup;

        private bool _isInitialized = false;
        private bool _isVisible = false;
        private Coroutine _keyDetectionCoroutine;

        public void Start()
        {
            if (!_isInitialized)
            {
                CreateUI();
            }
        }

        /// <summary>
        /// Create the parole conditions UI elements
        /// </summary>
        public void CreateUI()
        {
            try
            {
                // Get the player HUD canvas
                Canvas hudCanvas = GetPlayerHUDCanvas();

                // If canvas not found, wait a bit and try again
                if (hudCanvas == null)
                {
                    ModLogger.Warn("ParoleConditionsUI: Player HUD Canvas not found on first attempt, waiting...");
                    MelonLoader.MelonCoroutines.Start(WaitForCanvasAndCreate());
                    return;
                }

                CreateUIWithCanvas(hudCanvas);
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating ParoleConditionsUI: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the player's HUD canvas
        /// </summary>
        private Canvas GetPlayerHUDCanvas()
        {
            Canvas canvas = null;

#if !MONO
            try
            {
                var hudInstance = Singleton<Il2CppScheduleOne.UI.HUD>.Instance;
                if (hudInstance != null && hudInstance.Pointer != System.IntPtr.Zero)
                {
                    canvas = hudInstance.canvas;
                }
            }
            catch (System.Exception)
            {
                // HUD singleton not available yet
            }
#else
            try
            {
                canvas = Singleton<HUD>.Instance?.canvas;
            }
            catch (System.Exception)
            {
                // HUD singleton not available yet
            }
#endif

            return canvas;
        }

        /// <summary>
        /// Create UI with a known canvas
        /// </summary>
        private void CreateUIWithCanvas(Canvas mainCanvas)
        {
            try
            {
                if (_isInitialized)
                {
                    ModLogger.Debug("ParoleConditionsUI: Already initialized, skipping");
                    return;
                }

                // Create overlay panel (full screen, semi-transparent)
                _overlayPanel = new GameObject("ParoleConditionsOverlay");
                _overlayPanel.transform.SetParent(mainCanvas.transform, false);

                RectTransform overlayRect = _overlayPanel.AddComponent<RectTransform>();
                overlayRect.anchorMin = Vector2.zero;
                overlayRect.anchorMax = Vector2.one;
                overlayRect.offsetMin = Vector2.zero;
                overlayRect.offsetMax = Vector2.zero;

                _overlayImage = _overlayPanel.AddComponent<Image>();
                _overlayImage.color = new Color(0f, 0f, 0f, 0.8f); // 80% opacity black

                // Create main panel (centered, 85% of screen)
                _mainPanel = new GameObject("ParoleConditionsMainPanel");
                _mainPanel.transform.SetParent(_overlayPanel.transform, false);

                RectTransform mainRect = _mainPanel.AddComponent<RectTransform>();
                mainRect.anchorMin = new Vector2(0.075f, 0.075f);
                mainRect.anchorMax = new Vector2(0.925f, 0.925f);
                mainRect.offsetMin = Vector2.zero;
                mainRect.offsetMax = Vector2.zero;

                _mainPanelImage = _mainPanel.AddComponent<Image>();
                _mainPanelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.95f); // Dark panel background

                // Add border
                var outline = _mainPanel.AddComponent<Outline>();
                outline.effectColor = new Color(0.5f, 0.5f, 0.5f, 1f);
                outline.effectDistance = new Vector2(2, -2);

                // Add CanvasGroup for fade animations
                _canvasGroup = _overlayPanel.AddComponent<CanvasGroup>();
                _canvasGroup.alpha = 0f;
                _canvasGroup.blocksRaycasts = true;
                _canvasGroup.interactable = true;

                // Create title text
                GameObject titleObj = new GameObject("TitleText");
                titleObj.transform.SetParent(_mainPanel.transform, false);

                RectTransform titleRect = titleObj.AddComponent<RectTransform>();
                titleRect.anchorMin = new Vector2(0f, 0.92f);
                titleRect.anchorMax = new Vector2(1f, 1f);
                titleRect.offsetMin = new Vector2(20f, 0f);
                titleRect.offsetMax = new Vector2(-20f, -10f);

                _titleText = titleObj.AddComponent<TextMeshProUGUI>();
                _titleText.text = "RELEASE SUMMARY & PAROLE CONDITIONS";
                _titleText.fontSize = 28f;
                _titleText.color = Color.white;
                _titleText.fontStyle = FontStyles.Bold;
                _titleText.alignment = TextAlignmentOptions.Center;
                _titleText.enableWordWrapping = false;

                float currentY = 0.88f;
                float sectionHeight = 0.06f; // Reduced from 0.08f
                float spacing = 0.015f; // Reduced from 0.02f
                float compactSectionHeight = 0.05f; // For smaller sections
                
                // LEFT COLUMN (0-0.48): Bail, Fine, Parole Term, Charges, Conditions
                float leftColumnStart = 0f;
                float leftColumnEnd = 0.48f;
                float leftCurrentY = currentY;
                
                // RIGHT COLUMN (0.52-1.0): LSI Level, LSI Breakdown, Jail Time
                float rightColumnStart = 0.52f;
                float rightColumnEnd = 1f;
                float rightCurrentY = currentY;

                // === LEFT COLUMN ===
                
                // Create Bail Paid section (LEFT COLUMN)
                CreateSectionInColumn("BailSection", leftColumnStart, leftColumnEnd, ref leftCurrentY, sectionHeight, spacing, out _bailLabelText, out _bailValueText);
                _bailLabelText.text = "Bail Paid:";
                _bailValueText.text = "";

                // Create Fine section (LEFT COLUMN)
                CreateSectionInColumn("FineSection", leftColumnStart, leftColumnEnd, ref leftCurrentY, sectionHeight, spacing, out _fineLabelText, out _fineValueText);
                _fineLabelText.text = "Total Fine:";
                _fineValueText.text = "";
                
                // Fine payment time (smaller text below fine value)
                GameObject finePaymentTimeObj = new GameObject("FinePaymentTime");
                finePaymentTimeObj.transform.SetParent(_fineLabelText.transform.parent, false);
                RectTransform finePaymentTimeRect = finePaymentTimeObj.AddComponent<RectTransform>();
                finePaymentTimeRect.anchorMin = new Vector2(0f, 0f);
                finePaymentTimeRect.anchorMax = new Vector2(1f, 0.4f);
                finePaymentTimeRect.offsetMin = new Vector2(0f, -5f);
                finePaymentTimeRect.offsetMax = new Vector2(0f, 0f);
                _finePaymentTimeText = finePaymentTimeObj.AddComponent<TextMeshProUGUI>();
                _finePaymentTimeText.text = "";
                _finePaymentTimeText.fontSize = 14f;
                _finePaymentTimeText.color = new Color(0.9f, 0.9f, 0.5f);
                _finePaymentTimeText.alignment = TextAlignmentOptions.Left;
                _finePaymentTimeText.enableWordWrapping = false;

                // Create Parole Term section (LEFT COLUMN)
                CreateSectionInColumn("TermSection", leftColumnStart, leftColumnEnd, ref leftCurrentY, sectionHeight, spacing, out _termLengthLabelText, out _termLengthValueText);
                _termLengthLabelText.text = "Parole Term:";
                _termLengthValueText.text = "";
                _termLengthValueText.color = new Color(0.5f, 1f, 0.5f); // Light green

                // === RIGHT COLUMN ===
                
                // Create LSI Level section with breakdown inline (RIGHT COLUMN)
                rightCurrentY -= spacing;
                GameObject lsiSectionObj = new GameObject("LSISection");
                lsiSectionObj.transform.SetParent(_mainPanel.transform, false);
                RectTransform lsiSectionRect = lsiSectionObj.AddComponent<RectTransform>();
                lsiSectionRect.anchorMin = new Vector2(rightColumnStart, rightCurrentY - 0.18f);
                lsiSectionRect.anchorMax = new Vector2(rightColumnEnd, rightCurrentY);
                lsiSectionRect.offsetMin = new Vector2(10f, 0f);
                lsiSectionRect.offsetMax = new Vector2(-10f, -5f);

                // LSI Level label
                GameObject lsiLevelLabelObj = new GameObject("LSILevelLabel");
                lsiLevelLabelObj.transform.SetParent(lsiSectionObj.transform, false);
                RectTransform lsiLevelLabelRect = lsiLevelLabelObj.AddComponent<RectTransform>();
                lsiLevelLabelRect.anchorMin = new Vector2(0f, 0.85f);
                lsiLevelLabelRect.anchorMax = new Vector2(1f, 1f);
                lsiLevelLabelRect.offsetMin = Vector2.zero;
                lsiLevelLabelRect.offsetMax = Vector2.zero;
                _lsiLevelLabelText = lsiLevelLabelObj.AddComponent<TextMeshProUGUI>();
                _lsiLevelLabelText.text = "Supervision Level:";
                _lsiLevelLabelText.fontSize = 16f;
                _lsiLevelLabelText.color = new Color(0.8f, 0.8f, 0.8f);
                _lsiLevelLabelText.fontStyle = FontStyles.Bold;
                _lsiLevelLabelText.alignment = TextAlignmentOptions.Left;
                _lsiLevelLabelText.enableWordWrapping = false;

                // LSI Level value
                GameObject lsiLevelValueObj = new GameObject("LSILevelValue");
                lsiLevelValueObj.transform.SetParent(lsiSectionObj.transform, false);
                RectTransform lsiLevelValueRect = lsiLevelValueObj.AddComponent<RectTransform>();
                lsiLevelValueRect.anchorMin = new Vector2(0f, 0.7f);
                lsiLevelValueRect.anchorMax = new Vector2(1f, 0.85f);
                lsiLevelValueRect.offsetMin = Vector2.zero;
                lsiLevelValueRect.offsetMax = Vector2.zero;
                _lsiLevelValueText = lsiLevelValueObj.AddComponent<TextMeshProUGUI>();
                _lsiLevelValueText.text = "";
                _lsiLevelValueText.fontSize = 18f;
                _lsiLevelValueText.alignment = TextAlignmentOptions.Left;
                _lsiLevelValueText.enableWordWrapping = false;

                // LSI breakdown label
                GameObject lsiBreakdownLabelObj = new GameObject("LSIBreakdownLabel");
                lsiBreakdownLabelObj.transform.SetParent(lsiSectionObj.transform, false);
                RectTransform lsiBreakdownLabelRect = lsiBreakdownLabelObj.AddComponent<RectTransform>();
                lsiBreakdownLabelRect.anchorMin = new Vector2(0f, 0.55f);
                lsiBreakdownLabelRect.anchorMax = new Vector2(1f, 0.7f);
                lsiBreakdownLabelRect.offsetMin = Vector2.zero;
                lsiBreakdownLabelRect.offsetMax = Vector2.zero;
                _lsiBreakdownLabelText = lsiBreakdownLabelObj.AddComponent<TextMeshProUGUI>();
                _lsiBreakdownLabelText.text = "LSI Calculation:";
                _lsiBreakdownLabelText.fontSize = 14f;
                _lsiBreakdownLabelText.color = new Color(0.8f, 0.8f, 0.8f);
                _lsiBreakdownLabelText.fontStyle = FontStyles.Bold;
                _lsiBreakdownLabelText.alignment = TextAlignmentOptions.Left;
                _lsiBreakdownLabelText.enableWordWrapping = false;

                // LSI breakdown text
                GameObject lsiBreakdownTextObj = new GameObject("LSIBreakdownText");
                lsiBreakdownTextObj.transform.SetParent(lsiSectionObj.transform, false);
                RectTransform lsiBreakdownTextRect = lsiBreakdownTextObj.AddComponent<RectTransform>();
                lsiBreakdownTextRect.anchorMin = new Vector2(0f, 0f);
                lsiBreakdownTextRect.anchorMax = new Vector2(1f, 0.55f);
                lsiBreakdownTextRect.offsetMin = new Vector2(5f, 0f);
                lsiBreakdownTextRect.offsetMax = Vector2.zero;
                _lsiBreakdownText = lsiBreakdownTextObj.AddComponent<TextMeshProUGUI>();
                _lsiBreakdownText.text = "";
                _lsiBreakdownText.fontSize = 12f;
                _lsiBreakdownText.color = new Color(0.9f, 0.9f, 0.7f); // Light yellow
                _lsiBreakdownText.alignment = TextAlignmentOptions.TopLeft;
                _lsiBreakdownText.enableWordWrapping = true;

                rightCurrentY -= 0.18f + spacing;

                // Create Jail Time Comparison section (RIGHT COLUMN)
                CreateSectionInColumn("JailTimeSection", rightColumnStart, rightColumnEnd, ref rightCurrentY, compactSectionHeight, spacing, out _jailTimeLabelText, out _jailTimeComparisonText);
                _jailTimeLabelText.text = "Jail Time:";
                _jailTimeComparisonText.text = "";
                _jailTimeComparisonText.fontSize = 14f; // Smaller for right column

                // === BACK TO LEFT COLUMN ===
                
                // Create Recent Crimes section (LEFT COLUMN)
                leftCurrentY -= spacing;
                GameObject recentCrimesSectionObj = new GameObject("RecentCrimesSection");
                recentCrimesSectionObj.transform.SetParent(_mainPanel.transform, false);
                RectTransform recentCrimesSectionRect = recentCrimesSectionObj.AddComponent<RectTransform>();
                recentCrimesSectionRect.anchorMin = new Vector2(leftColumnStart, leftCurrentY - 0.12f);
                recentCrimesSectionRect.anchorMax = new Vector2(leftColumnEnd, leftCurrentY);
                recentCrimesSectionRect.offsetMin = new Vector2(10f, 0f);
                recentCrimesSectionRect.offsetMax = new Vector2(-10f, -5f);

                // Recent crimes label
                GameObject recentCrimesLabelObj = new GameObject("RecentCrimesLabel");
                recentCrimesLabelObj.transform.SetParent(recentCrimesSectionObj.transform, false);
                RectTransform recentCrimesLabelRect = recentCrimesLabelObj.AddComponent<RectTransform>();
                recentCrimesLabelRect.anchorMin = new Vector2(0f, 0.7f);
                recentCrimesLabelRect.anchorMax = new Vector2(1f, 1f);
                recentCrimesLabelRect.offsetMin = Vector2.zero;
                recentCrimesLabelRect.offsetMax = Vector2.zero;
                _recentCrimesLabelText = recentCrimesLabelObj.AddComponent<TextMeshProUGUI>();
                _recentCrimesLabelText.text = "Charges:";
                _recentCrimesLabelText.fontSize = 18f;
                _recentCrimesLabelText.color = new Color(0.8f, 0.8f, 0.8f);
                _recentCrimesLabelText.fontStyle = FontStyles.Bold;
                _recentCrimesLabelText.alignment = TextAlignmentOptions.Left;
                _recentCrimesLabelText.enableWordWrapping = false;

                // Recent crimes list
                GameObject recentCrimesListObj = new GameObject("RecentCrimesList");
                recentCrimesListObj.transform.SetParent(recentCrimesSectionObj.transform, false);
                RectTransform recentCrimesListRect = recentCrimesListObj.AddComponent<RectTransform>();
                recentCrimesListRect.anchorMin = new Vector2(0f, 0f);
                recentCrimesListRect.anchorMax = new Vector2(1f, 0.7f);
                recentCrimesListRect.offsetMin = new Vector2(5f, 0f);
                recentCrimesListRect.offsetMax = Vector2.zero;
                _recentCrimesListText = recentCrimesListObj.AddComponent<TextMeshProUGUI>();
                _recentCrimesListText.text = "";
                _recentCrimesListText.fontSize = 14f; // Reduced from 15f
                _recentCrimesListText.color = new Color(1f, 0.7f, 0.7f); // Light red/pink
                _recentCrimesListText.alignment = TextAlignmentOptions.TopLeft;
                _recentCrimesListText.enableWordWrapping = true;

                leftCurrentY -= 0.12f + spacing;

                // Create General Conditions section (LEFT COLUMN)
                GameObject generalConditionsSectionObj = new GameObject("GeneralConditionsSection");
                generalConditionsSectionObj.transform.SetParent(_mainPanel.transform, false);
                RectTransform generalConditionsSectionRect = generalConditionsSectionObj.AddComponent<RectTransform>();
                generalConditionsSectionRect.anchorMin = new Vector2(leftColumnStart, leftCurrentY - 0.12f);
                generalConditionsSectionRect.anchorMax = new Vector2(leftColumnEnd, leftCurrentY);
                generalConditionsSectionRect.offsetMin = new Vector2(10f, 0f);
                generalConditionsSectionRect.offsetMax = new Vector2(-10f, -5f);

                // General conditions label
                GameObject generalConditionsLabelObj = new GameObject("GeneralConditionsLabel");
                generalConditionsLabelObj.transform.SetParent(generalConditionsSectionObj.transform, false);
                RectTransform generalConditionsLabelRect = generalConditionsLabelObj.AddComponent<RectTransform>();
                generalConditionsLabelRect.anchorMin = new Vector2(0f, 0.7f);
                generalConditionsLabelRect.anchorMax = new Vector2(1f, 1f);
                generalConditionsLabelRect.offsetMin = Vector2.zero;
                generalConditionsLabelRect.offsetMax = Vector2.zero;
                _generalConditionsLabelText = generalConditionsLabelObj.AddComponent<TextMeshProUGUI>();
                _generalConditionsLabelText.text = "General Conditions:";
                _generalConditionsLabelText.fontSize = 18f;
                _generalConditionsLabelText.color = new Color(0.8f, 0.8f, 0.8f);
                _generalConditionsLabelText.fontStyle = FontStyles.Bold;
                _generalConditionsLabelText.alignment = TextAlignmentOptions.Left;
                _generalConditionsLabelText.enableWordWrapping = false;

                // General conditions list
                GameObject generalConditionsListObj = new GameObject("GeneralConditionsList");
                generalConditionsListObj.transform.SetParent(generalConditionsSectionObj.transform, false);
                RectTransform generalConditionsListRect = generalConditionsListObj.AddComponent<RectTransform>();
                generalConditionsListRect.anchorMin = new Vector2(0f, 0f);
                generalConditionsListRect.anchorMax = new Vector2(1f, 0.7f);
                generalConditionsListRect.offsetMin = new Vector2(5f, 0f);
                generalConditionsListRect.offsetMax = Vector2.zero;
                _generalConditionsListText = generalConditionsListObj.AddComponent<TextMeshProUGUI>();
                _generalConditionsListText.text = "";
                _generalConditionsListText.fontSize = 14f; // Reduced from 15f
                _generalConditionsListText.color = Color.white;
                _generalConditionsListText.alignment = TextAlignmentOptions.TopLeft;
                _generalConditionsListText.enableWordWrapping = true;

                leftCurrentY -= 0.12f + spacing;

                // Create Special Conditions section (LEFT COLUMN) - leave room for dismissal instruction
                GameObject specialConditionsSectionObj = new GameObject("SpecialConditionsSection");
                specialConditionsSectionObj.transform.SetParent(_mainPanel.transform, false);
                RectTransform specialConditionsSectionRect = specialConditionsSectionObj.AddComponent<RectTransform>();
                // Ensure we leave at least 0.1f at the bottom for dismissal instruction
                float specialConditionsHeight = Math.Min(0.12f, leftCurrentY - 0.1f);
                specialConditionsSectionRect.anchorMin = new Vector2(leftColumnStart, leftCurrentY - specialConditionsHeight);
                specialConditionsSectionRect.anchorMax = new Vector2(leftColumnEnd, leftCurrentY);
                specialConditionsSectionRect.offsetMin = new Vector2(10f, 0f);
                specialConditionsSectionRect.offsetMax = new Vector2(-10f, -5f);

                // Special conditions label
                GameObject specialConditionsLabelObj = new GameObject("SpecialConditionsLabel");
                specialConditionsLabelObj.transform.SetParent(specialConditionsSectionObj.transform, false);
                RectTransform specialConditionsLabelRect = specialConditionsLabelObj.AddComponent<RectTransform>();
                specialConditionsLabelRect.anchorMin = new Vector2(0f, 0.7f);
                specialConditionsLabelRect.anchorMax = new Vector2(1f, 1f);
                specialConditionsLabelRect.offsetMin = Vector2.zero;
                specialConditionsLabelRect.offsetMax = Vector2.zero;
                _specialConditionsLabelText = specialConditionsLabelObj.AddComponent<TextMeshProUGUI>();
                _specialConditionsLabelText.text = "Special Conditions:";
                _specialConditionsLabelText.fontSize = 18f;
                _specialConditionsLabelText.color = new Color(0.8f, 0.8f, 0.8f);
                _specialConditionsLabelText.fontStyle = FontStyles.Bold;
                _specialConditionsLabelText.alignment = TextAlignmentOptions.Left;
                _specialConditionsLabelText.enableWordWrapping = false;

                // Special conditions list
                GameObject specialConditionsListObj = new GameObject("SpecialConditionsList");
                specialConditionsListObj.transform.SetParent(specialConditionsSectionObj.transform, false);
                RectTransform specialConditionsListRect = specialConditionsListObj.AddComponent<RectTransform>();
                specialConditionsListRect.anchorMin = new Vector2(0f, 0f);
                specialConditionsListRect.anchorMax = new Vector2(1f, 0.7f);
                specialConditionsListRect.offsetMin = new Vector2(5f, 0f);
                specialConditionsListRect.offsetMax = Vector2.zero;
                _specialConditionsListText = specialConditionsListObj.AddComponent<TextMeshProUGUI>();
                _specialConditionsListText.text = "";
                _specialConditionsListText.fontSize = 14f; // Reduced from 15f
                _specialConditionsListText.color = new Color(1f, 0.8f, 0.5f); // Slightly orange/yellow
                _specialConditionsListText.alignment = TextAlignmentOptions.TopLeft;
                _specialConditionsListText.enableWordWrapping = true;

                // Create dismissal instruction (at bottom)
                GameObject dismissalObj = new GameObject("DismissalInstruction");
                dismissalObj.transform.SetParent(_mainPanel.transform, false);
                RectTransform dismissalRect = dismissalObj.AddComponent<RectTransform>();
                dismissalRect.anchorMin = new Vector2(0f, 0f);
                dismissalRect.anchorMax = new Vector2(1f, 0.08f);
                dismissalRect.offsetMin = new Vector2(20f, 15f);
                dismissalRect.offsetMax = new Vector2(-20f, 0f);

                _dismissalInstructionText = dismissalObj.AddComponent<TextMeshProUGUI>();
                _dismissalInstructionText.text = "";
                _dismissalInstructionText.fontSize = 18f;
                _dismissalInstructionText.color = new Color(1f, 0.8f, 0f); // Yellow/orange
                _dismissalInstructionText.fontStyle = FontStyles.Bold;
                _dismissalInstructionText.alignment = TextAlignmentOptions.Center;
                _dismissalInstructionText.enableWordWrapping = false;

                // Start hidden
                _overlayPanel.SetActive(false);

                // Apply font fix to all text components if font is cached
                TMPFontFix.FixAllTMPFonts(_mainPanel, "base");

                _isInitialized = true;
                ModLogger.Debug("ParoleConditionsUI created successfully");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error creating ParoleConditionsUI with canvas: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper method to create a label/value section in a specific column
        /// </summary>
        private void CreateSectionInColumn(string sectionName, float columnStart, float columnEnd, ref float currentY, float sectionHeight, float spacing, 
            out TextMeshProUGUI labelText, out TextMeshProUGUI valueText)
        {
            currentY -= sectionHeight + spacing;

            GameObject sectionObj = new GameObject(sectionName);
            sectionObj.transform.SetParent(_mainPanel.transform, false);

            RectTransform sectionRect = sectionObj.AddComponent<RectTransform>();
            sectionRect.anchorMin = new Vector2(columnStart, currentY);
            sectionRect.anchorMax = new Vector2(columnEnd, currentY + sectionHeight);
            sectionRect.offsetMin = new Vector2(10f, 0f);
            sectionRect.offsetMax = new Vector2(-10f, 0f);

            // Label
            GameObject labelObj = new GameObject($"{sectionName}Label");
            labelObj.transform.SetParent(sectionObj.transform, false);
            RectTransform labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0.5f);
            labelRect.anchorMax = new Vector2(0.4f, 1f);
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.fontSize = 18f;
            labelText.color = new Color(0.8f, 0.8f, 0.8f);
            labelText.fontStyle = FontStyles.Bold;
            labelText.alignment = TextAlignmentOptions.Left;
            labelText.enableWordWrapping = false;

            // Value
            GameObject valueObj = new GameObject($"{sectionName}Value");
            valueObj.transform.SetParent(sectionObj.transform, false);
            RectTransform valueRect = valueObj.AddComponent<RectTransform>();
            valueRect.anchorMin = new Vector2(0.4f, 0f);
            valueRect.anchorMax = new Vector2(1f, 1f);
            valueRect.offsetMin = new Vector2(10f, 0f);
            valueRect.offsetMax = Vector2.zero;

            valueText = valueObj.AddComponent<TextMeshProUGUI>();
            valueText.fontSize = 18f;
            valueText.color = Color.white;
            valueText.alignment = TextAlignmentOptions.Left;
            valueText.enableWordWrapping = false;
        }

        /// <summary>
        /// Helper method to create a label/value section
        /// </summary>
        private void CreateSection(string sectionName, ref float currentY, float sectionHeight, float spacing, 
            out TextMeshProUGUI labelText, out TextMeshProUGUI valueText)
        {
            currentY -= sectionHeight + spacing;

            GameObject sectionObj = new GameObject(sectionName);
            sectionObj.transform.SetParent(_mainPanel.transform, false);

            RectTransform sectionRect = sectionObj.AddComponent<RectTransform>();
            sectionRect.anchorMin = new Vector2(0f, currentY);
            sectionRect.anchorMax = new Vector2(1f, currentY + sectionHeight);
            sectionRect.offsetMin = new Vector2(30f, 0f);
            sectionRect.offsetMax = new Vector2(-30f, 0f);

            // Label
            GameObject labelObj = new GameObject($"{sectionName}Label");
            labelObj.transform.SetParent(sectionObj.transform, false);
            RectTransform labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0.5f);
            labelRect.anchorMax = new Vector2(0.4f, 1f);
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.fontSize = 18f;
            labelText.color = new Color(0.8f, 0.8f, 0.8f);
            labelText.fontStyle = FontStyles.Bold;
            labelText.alignment = TextAlignmentOptions.Left;
            labelText.enableWordWrapping = false;

            // Value
            GameObject valueObj = new GameObject($"{sectionName}Value");
            valueObj.transform.SetParent(sectionObj.transform, false);
            RectTransform valueRect = valueObj.AddComponent<RectTransform>();
            valueRect.anchorMin = new Vector2(0.4f, 0f);
            valueRect.anchorMax = new Vector2(1f, 1f);
            valueRect.offsetMin = new Vector2(10f, 0f);
            valueRect.offsetMax = Vector2.zero;
            valueText = valueObj.AddComponent<TextMeshProUGUI>();
            valueText.fontSize = 18f;
            valueText.color = Color.white;
            valueText.fontStyle = FontStyles.Bold;
            valueText.alignment = TextAlignmentOptions.Left;
            valueText.enableWordWrapping = false;
        }

        /// <summary>
        /// Wait for HUD canvas to be available and then create UI
        /// </summary>
        private IEnumerator WaitForCanvasAndCreate()
        {
            int attempts = 0;
            const int maxAttempts = 10;

            while (attempts < maxAttempts)
            {
                yield return new WaitForSeconds(0.5f);

                Canvas hudCanvas = GetPlayerHUDCanvas();
                if (hudCanvas != null)
                {
                    ModLogger.Info($"ParoleConditionsUI: Player HUD Canvas found after {attempts + 1} attempts");
                    CreateUIWithCanvas(hudCanvas);
                    yield break;
                }

                attempts++;
            }

            ModLogger.Error($"ParoleConditionsUI: Could not find Player HUD Canvas after {maxAttempts} attempts");
        }

        /// <summary>
        /// Show the parole conditions UI with specified data
        /// </summary>
        public void Show(float bailAmountPaid, float fineAmount, float termLengthGameMinutes, LSILevel lsiLevel,
            (int totalScore, int crimeCountScore, int severityScore, int violationScore, int pastParoleScore, LSILevel resultingLevel) lsiBreakdown,
            (float originalSentenceTime, float timeServed) jailTimeInfo, List<string> recentCrimes,
            List<string> generalConditions, List<string> specialConditions)
        {
            if (!_isInitialized)
            {
                ModLogger.Warn("ParoleConditionsUI: Not initialized, cannot show");
                return;
            }

            try
            {
                // Ensure text components are not null
                if (_bailValueText == null || _fineValueText == null || _termLengthValueText == null ||
                    _lsiLevelValueText == null || _lsiBreakdownText == null || _jailTimeComparisonText == null ||
                    _recentCrimesListText == null ||
                    _generalConditionsListText == null || _specialConditionsListText == null || 
                    _dismissalInstructionText == null)
                {
                    ModLogger.Error("ParoleConditionsUI: Text components are null - UI may not be properly initialized");
                    return;
                }

                // Set bail amount (or "Timed Out" if none)
                if (bailAmountPaid > 0f)
                {
                    _bailValueText.text = $"${bailAmountPaid:F0} (Bailed Out)";
                    _bailValueText.color = new Color(0.5f, 1f, 0.5f); // Light green
                }
                else
                {
                    _bailValueText.text = "Timed Out";
                    _bailValueText.color = new Color(0.9f, 0.7f, 0.3f); // Orange/yellow
                }

                // Set fine amount
                _fineValueText.text = $"${fineAmount:F0}";
                _fineValueText.color = Color.white;
                
                // Set fine payment time (placeholder)
                _finePaymentTimeText.text = "Payment due to parole supervisor (to be implemented)";

                // Format term length
                string termLengthFormatted = GameTimeManager.FormatGameTime(termLengthGameMinutes);
                _termLengthValueText.text = termLengthFormatted;

                // Set LSI level
                string lsiDescription = GetLSIDescription(lsiLevel);
                _lsiLevelValueText.text = lsiDescription;
                // Color based on LSI level
                switch (lsiLevel)
                {
                    case LSILevel.None:
                        _lsiLevelValueText.color = Color.gray;
                        break;
                    case LSILevel.Minimum:
                        _lsiLevelValueText.color = new Color(0.5f, 1f, 0.5f); // Light green
                        break;
                    case LSILevel.Medium:
                        _lsiLevelValueText.color = new Color(1f, 1f, 0.5f); // Yellow
                        break;
                    case LSILevel.High:
                        _lsiLevelValueText.color = new Color(1f, 0.7f, 0.3f); // Orange
                        break;
                    case LSILevel.Severe:
                        _lsiLevelValueText.color = new Color(1f, 0.5f, 0.5f); // Light red
                        break;
                    default:
                        _lsiLevelValueText.color = Color.white;
                        break;
                }

                // Set LSI breakdown
                if (lsiBreakdown.totalScore > 0)
                {
                    System.Text.StringBuilder breakdownText = new System.Text.StringBuilder();
                    breakdownText.AppendLine($"Total Score: {lsiBreakdown.totalScore}/100");
                    breakdownText.AppendLine($"• Crime Count: {lsiBreakdown.crimeCountScore} pts");
                    breakdownText.AppendLine($"• Crime Severity: {lsiBreakdown.severityScore} pts");
                    breakdownText.AppendLine($"• Parole Violations: {lsiBreakdown.violationScore} pts");
                    breakdownText.AppendLine($"• Past Parole Failures: {lsiBreakdown.pastParoleScore} pts");
                    breakdownText.Append($"→ Result: {GetLSIDescription(lsiBreakdown.resultingLevel)}");
                    _lsiBreakdownText.text = breakdownText.ToString();
                }
                else
                {
                    _lsiBreakdownText.text = "No LSI assessment available";
                }

                // Set jail time comparison
                if (jailTimeInfo.originalSentenceTime > 0f && jailTimeInfo.timeServed >= 0f)
                {
                    string originalTime = GameTimeManager.FormatGameTime(jailTimeInfo.originalSentenceTime);
                    string timeServed = GameTimeManager.FormatGameTime(jailTimeInfo.timeServed);
                    
                    // Calculate percentage served
                    float percentageServed = (jailTimeInfo.timeServed / jailTimeInfo.originalSentenceTime) * 100f;
                    
                    _jailTimeComparisonText.text = $"Served: {timeServed} / Sentence: {originalTime} ({percentageServed:F0}%)";
                    
                    // Color based on completion
                    if (percentageServed >= 100f)
                    {
                        _jailTimeComparisonText.color = new Color(0.5f, 1f, 0.5f); // Light green - full sentence served
                    }
                    else if (percentageServed >= 50f)
                    {
                        _jailTimeComparisonText.color = new Color(1f, 1f, 0.5f); // Yellow - partial sentence
                    }
                    else
                    {
                        _jailTimeComparisonText.color = new Color(1f, 0.7f, 0.5f); // Orange - early release
                    }
                }
                else if (jailTimeInfo.timeServed > 0f)
                {
                    // We have time served but no original sentence time (sentence completed and tracking cleared)
                    string timeServed = GameTimeManager.FormatGameTime(jailTimeInfo.timeServed);
                    _jailTimeComparisonText.text = $"Time Served: {timeServed} (Full sentence completed)";
                    _jailTimeComparisonText.color = new Color(0.5f, 1f, 0.5f); // Light green - full sentence served
                }
                else
                {
                    _jailTimeComparisonText.text = "Time served: Unknown";
                    _jailTimeComparisonText.color = Color.gray;
                }

                // Format recent crimes list
                if (recentCrimes != null && recentCrimes.Count > 0)
                {
                    System.Text.StringBuilder crimesText = new System.Text.StringBuilder();
                    for (int i = 0; i < recentCrimes.Count; i++)
                    {
                        crimesText.Append($"• {recentCrimes[i]}");
                        if (i < recentCrimes.Count - 1)
                        {
                            crimesText.AppendLine();
                        }
                    }
                    _recentCrimesListText.text = crimesText.ToString();
                }
                else
                {
                    _recentCrimesListText.text = "• No charges recorded";
                }

                // Format general conditions list
                if (generalConditions != null && generalConditions.Count > 0)
                {
                    System.Text.StringBuilder conditionsText = new System.Text.StringBuilder();
                    for (int i = 0; i < generalConditions.Count; i++)
                    {
                        conditionsText.Append($"• {generalConditions[i]}");
                        if (i < generalConditions.Count - 1)
                        {
                            conditionsText.AppendLine();
                        }
                    }
                    _generalConditionsListText.text = conditionsText.ToString();
                }
                else
                {
                    _generalConditionsListText.text = "• No general conditions assigned";
                }

                // Format special conditions list
                if (specialConditions != null && specialConditions.Count > 0)
                {
                    System.Text.StringBuilder conditionsText = new System.Text.StringBuilder();
                    for (int i = 0; i < specialConditions.Count; i++)
                    {
                        conditionsText.Append($"• {specialConditions[i]}");
                        if (i < specialConditions.Count - 1)
                        {
                            conditionsText.AppendLine();
                        }
                    }
                    _specialConditionsListText.text = conditionsText.ToString();
                }
                else
                {
                    _specialConditionsListText.text = "• No special conditions assigned";
                }

                // Set dismissal instruction
                string keyName = Core.BailoutKey.ToString().Replace("KeyCode.", "");
                _dismissalInstructionText.text = $"Press [{keyName}] to acknowledge and continue";

                // Show UI
                _overlayPanel.SetActive(true);
                _isVisible = true;

                // Fade in
                MelonLoader.MelonCoroutines.Start(FadeIn());

                // Start key detection
                if (_keyDetectionCoroutine != null)
                {
                    MelonLoader.MelonCoroutines.Stop(_keyDetectionCoroutine);
                }
                _keyDetectionCoroutine = MelonLoader.MelonCoroutines.Start(WaitForDismissal()) as Coroutine;

                ModLogger.Info($"ParoleConditionsUI: Showing release summary - Bail: ${bailAmountPaid:F0}, Fine: ${fineAmount:F0}, Term: {termLengthFormatted}, LSI: {lsiLevel}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error showing ParoleConditionsUI: {ex.Message}");
            }
        }

        /// <summary>
        /// Get human-readable description of LSI level
        /// </summary>
        private string GetLSIDescription(LSILevel lsiLevel)
        {
            switch (lsiLevel)
            {
                case LSILevel.None:
                    return "No assessment";
                case LSILevel.Minimum:
                    return "Minimum risk - Low supervision";
                case LSILevel.Medium:
                    return "Medium risk - Moderate supervision";
                case LSILevel.High:
                    return "High risk - Intensive supervision";
                case LSILevel.Severe:
                    return "Severe risk - Maximum supervision";
                default:
                    return "Unknown";
            }
        }

        /// <summary>
        /// Hide the parole conditions UI
        /// </summary>
        public void Hide()
        {
            if (!_isInitialized || !_overlayPanel.activeSelf)
                return;

            try
            {
                _isVisible = false;

                // Stop key detection
                if (_keyDetectionCoroutine != null)
                {
                    MelonLoader.MelonCoroutines.Stop(_keyDetectionCoroutine);
                    _keyDetectionCoroutine = null;
                }

                // Fade out and hide
                MelonLoader.MelonCoroutines.Start(FadeOut());

                ModLogger.Info("ParoleConditionsUI: Hiding parole conditions");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error hiding ParoleConditionsUI: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if UI is currently visible
        /// </summary>
        public bool IsVisible()
        {
            return _isInitialized && _overlayPanel != null && _overlayPanel.activeSelf && _canvasGroup.alpha > 0 && _isVisible;
        }

        /// <summary>
        /// Wait for dismissal key press
        /// </summary>
        private IEnumerator WaitForDismissal()
        {
            bool keyWasPressed = false;

            while (_isVisible)
            {
                if (Input.GetKey(Core.BailoutKey))
                {
                    if (!keyWasPressed)
                    {
                        keyWasPressed = true;
                        ModLogger.Info("ParoleConditionsUI: Dismissal key pressed");
                        Hide();
                        yield break;
                    }
                }
                else
                {
                    keyWasPressed = false;
                }

                yield return null;
            }
        }

        /// <summary>
        /// Fade in animation
        /// </summary>
        private IEnumerator FadeIn()
        {
            float fadeTime = 0.3f;
            float elapsed = 0f;

            while (elapsed < fadeTime)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(0f, 1f, elapsed / fadeTime);
                yield return null;
            }

            _canvasGroup.alpha = 1f;
        }

        /// <summary>
        /// Fade out animation
        /// </summary>
        private IEnumerator FadeOut()
        {
            float fadeTime = 0.3f;
            float elapsed = 0f;
            float startAlpha = _canvasGroup.alpha;

            while (elapsed < fadeTime)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / fadeTime);
                yield return null;
            }

            _canvasGroup.alpha = 0f;
            _overlayPanel.SetActive(false);
        }
    }
}
