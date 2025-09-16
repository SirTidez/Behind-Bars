using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppInterop.Runtime;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.Interaction;
using ScheduleOne.DevUtilities;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Progressive bed-making interaction for prison cells
    /// Allows inmates to set up their bed by adding components in order: BedMat → WhiteSheet → BedSheet → Pillow
    /// </summary>
    public class PrisonBedInteractable : MonoBehaviour
    {
#if !MONO
        public PrisonBedInteractable(System.IntPtr ptr) : base(ptr) { }
#endif

        // Bed Components
        public Transform bedMat;
        public Transform whiteSheet;
        public Transform bedSheet;  
        public Transform pillow;
        
        // Bed Settings
        public bool isTopBunk = false;
        public string cellName = "";
        
        // Setup Progress
        private int setupStage = 0; // 0 = empty, 4 = complete
        
        // Component references
        private InteractableObject interactableObject;
        private MeshRenderer[] bedMatRenderers;
        private MeshRenderer[] whiteSheetRenderers;
        private MeshRenderer[] bedSheetRenderers;
        private MeshRenderer[] pillowRenderers;
        
        // GameObject references for enabling/disabling entire objects
        private GameObject[] bedMatObjects;
        private GameObject[] whiteSheetObjects;
        private GameObject[] bedSheetObjects;
        private GameObject[] pillowObjects;
        
        // State tracking
        private bool isProcessing = false;
        private bool isComplete = false;
        
        // Stage descriptions
        private readonly string[] stageActions = {
            "Place bed mat",
            "Add bottom sheet", 
            "Add top sheet",
            "Add pillow"
        };
        
        private readonly string[] stageMessages = {
            "Placing bed mat...",
            "Adding bottom sheet...",
            "Adding top sheet...", 
            "Adding pillow..."
        };

        void Start()
        {
            InitializeBedSetup();
        }

        private void InitializeBedSetup()
        {
            ModLogger.Info($"Initializing prison bed setup for {(isTopBunk ? "top bunk" : "bottom bunk")} in {cellName}");
            
            // Cache mesh renderers for each component
            CacheMeshRenderers();
            
            // Set up interaction component
            SetupInteractableComponent();
            
            // Initialize bed state (all components hidden initially)
            SetupStage = 0;
            
            // Force initial visual update to ensure everything starts hidden
            UpdateBedVisuals();
            
            ModLogger.Info($"Prison bed setup initialized at stage {setupStage}");
        }
        
        private void CacheMeshRenderers()
        {
            // Cache all mesh renderers and GameObjects for bed components
            if (bedMat != null)
            {
                bedMatRenderers = bedMat.GetComponentsInChildren<MeshRenderer>();
                bedMatObjects = GetChildGameObjects(bedMat);
            }
                
            if (whiteSheet != null)
            {
                whiteSheetRenderers = whiteSheet.GetComponentsInChildren<MeshRenderer>();
                whiteSheetObjects = GetChildGameObjects(whiteSheet);
            }
                
            if (bedSheet != null)
            {
                bedSheetRenderers = bedSheet.GetComponentsInChildren<MeshRenderer>();
                bedSheetObjects = GetChildGameObjects(bedSheet);
            }
                
            if (pillow != null)
            {
                pillowRenderers = pillow.GetComponentsInChildren<MeshRenderer>();
                pillowObjects = GetChildGameObjects(pillow);
            }
                
            ModLogger.Debug($"Cached components - BedMat: {bedMatRenderers?.Length ?? 0} renderers, {bedMatObjects?.Length ?? 0} objects");
            ModLogger.Debug($"WhiteSheet: {whiteSheetRenderers?.Length ?? 0} renderers, {whiteSheetObjects?.Length ?? 0} objects");
            ModLogger.Debug($"BedSheet: {bedSheetRenderers?.Length ?? 0} renderers, {bedSheetObjects?.Length ?? 0} objects");
            ModLogger.Debug($"Pillow: {pillowRenderers?.Length ?? 0} renderers, {pillowObjects?.Length ?? 0} objects");
        }
        
        private GameObject[] GetChildGameObjects(Transform parent)
        {
            if (parent == null) return null;
            
            var children = new List<GameObject>();
            children.Add(parent.gameObject); // Include the parent itself
            
            // Add all child objects recursively
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                children.Add(child.gameObject);
                
                // Add grandchildren recursively
                if (child.childCount > 0)
                {
                    var grandChildren = GetChildGameObjects(child);
                    if (grandChildren != null)
                        children.AddRange(grandChildren);
                }
            }
            
            return children.ToArray();
        }
        
        private void SetupInteractableComponent()
        {
            // Get or create InteractableObject component
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
                ModLogger.Info("Added InteractableObject component to prison bed");
            }
            
            // Configure the interaction
            UpdateInteractionState();
            interactableObject.SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            interactableObject.MaxInteractionRange = 3f;
            
            // Set up event listeners with IL2CPP-safe casting
#if !MONO
            interactableObject.onInteractStart.AddListener((System.Action)OnInteractStart);
#else
            interactableObject.onInteractStart.AddListener(OnInteractStart);
#endif
            
            ModLogger.Info("Prison bed InteractableObject component configured");
        }
        
        public int SetupStage
        {
            get => setupStage;
            private set
            {
                setupStage = Mathf.Clamp(value, 0, 4);
                UpdateBedVisuals();
                UpdateInteractionState();
            }
        }
        
        private void UpdateBedVisuals()
        {
            // Enable/disable both GameObjects AND MeshRenderers to ensure visibility
            bool showMat = setupStage >= 1;
            bool showWhiteSheet = setupStage >= 2;
            bool showBedSheet = setupStage >= 3;
            bool showPillow = setupStage >= 4;
            
            // Enable/disable GameObjects
            SetGameObjectsEnabled(bedMatObjects, showMat);
            SetGameObjectsEnabled(whiteSheetObjects, showWhiteSheet);
            SetGameObjectsEnabled(bedSheetObjects, showBedSheet);
            SetGameObjectsEnabled(pillowObjects, showPillow);
            
            // ALSO enable/disable MeshRenderers directly for double certainty
            SetRenderersEnabled(bedMatRenderers, showMat);
            SetRenderersEnabled(whiteSheetRenderers, showWhiteSheet);
            SetRenderersEnabled(bedSheetRenderers, showBedSheet);
            SetRenderersEnabled(pillowRenderers, showPillow);
            
            ModLogger.Debug($"Updated bed visuals for stage {setupStage} - Mat: {showMat}, WhiteSheet: {showWhiteSheet}, BedSheet: {showBedSheet}, Pillow: {showPillow}");
        }
        
        private void SetGameObjectsEnabled(GameObject[] objects, bool enabled)
        {
            if (objects == null) return;
            
            foreach (var obj in objects)
            {
                if (obj != null && obj.activeSelf != enabled)
                {
                    obj.SetActive(enabled);
                    ModLogger.Debug($"Set {obj.name} active state to {enabled}");
                }
            }
        }
        
        private void SetRenderersEnabled(MeshRenderer[] renderers, bool enabled)
        {
            if (renderers == null) 
            {
                ModLogger.Debug("Renderers array is null");
                return;
            }
            
            ModLogger.Debug($"Setting {renderers.Length} renderers to enabled={enabled}");
            
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = enabled;
                    ModLogger.Debug($"Set renderer {renderer.gameObject.name} enabled to {enabled}");
                }
                else
                {
                    ModLogger.Debug("Found null renderer in array");
                }
            }
        }
        
        private void UpdateInteractionState()
        {
            if (interactableObject == null) return;
            
            if (isComplete)
            {
                interactableObject.SetMessage("Sleep");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            }
            else if (isProcessing)
            {
                interactableObject.SetMessage("Setting up bed...");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }
            else if (setupStage < 4)
            {
                // Check if player has required item for current stage
                string requiredItem = GetRequiredItemForStage(setupStage);
                if (!string.IsNullOrEmpty(requiredItem) && !CheckPlayerHasRequiredItem(requiredItem))
                {
                    string itemName = GetItemDisplayName(requiredItem);
                    interactableObject.SetMessage($"Need {itemName}");
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                }
                else
                {
                    string message = $"{stageActions[setupStage]} ({setupStage + 1}/4)";
                    interactableObject.SetMessage(message);
                    interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
                }
            }
        }
        
        private void OnInteractStart()
        {
            if (isProcessing) return;
            
            if (isComplete)
            {
                // Bed is complete - this should be handled by JailBed component
                ModLogger.Debug("Bed is complete - interaction should be handled by JailBed");
                return;
            }
            
            if (setupStage >= 4)
            {
                // Complete the bed setup
                CompleteBedSetup();
                return;
            }
            
            // Check if player has required item for current stage
            string requiredItem = GetRequiredItemForStage(setupStage);
            if (!string.IsNullOrEmpty(requiredItem) && !CheckPlayerHasRequiredItem(requiredItem))
            {
                string itemName = GetItemDisplayName(requiredItem);
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"You need a {itemName} to continue setting up the bed.", 
                        NotificationType.Warning
                    );
                }
                ModLogger.Info($"Player lacks required item {requiredItem} for bed setup stage {setupStage}");
                return;
            }
            
            // Start next setup stage
            MelonCoroutines.Start(ProcessBedSetupStage());
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private IEnumerator ProcessBedSetupStage()
        {
            isProcessing = true;
            UpdateInteractionState();
            
            ModLogger.Info($"Processing bed setup stage {setupStage + 1}");
            
            // Get the required item before processing
            string requiredItem = GetRequiredItemForStage(setupStage);
            
            // Show progress notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    stageMessages[setupStage], 
                    NotificationType.Progress
                );
            }
            
            // Setup time delay
            yield return new WaitForSeconds(1.5f);
            
            // Consume the required item
            if (!string.IsNullOrEmpty(requiredItem))
            {
                ConsumeRequiredItem(requiredItem);
            }
            
            // Advance to next stage
            SetupStage++;
            
            // Check if bed is complete
            if (setupStage >= 4)
            {
                yield return new WaitForSeconds(0.5f);
                CompleteBedSetup();
            }
            else
            {
                isProcessing = false;
                UpdateInteractionState();
                
                // Show completion notification for this stage
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"Bed setup: {setupStage}/4 complete", 
                        NotificationType.Progress
                    );
                }
            }
            
            ModLogger.Info($"Bed setup stage completed. Current stage: {setupStage}");
        }
        
        private void CompleteBedSetup()
        {
            ModLogger.Info($"Completing bed setup for {(isTopBunk ? "top bunk" : "bottom bunk")} in {cellName}");
            
            isComplete = true;
            isProcessing = false;
            
            // Add JailBed component for sleeping functionality
            var jailBed = GetComponent<JailBed>();
            if (jailBed == null)
            {
                jailBed = gameObject.AddComponent<JailBed>();
                jailBed.bedName = $"{cellName} {(isTopBunk ? "Top Bunk" : "Bottom Bunk")}";
                jailBed.isTopBunk = isTopBunk;
                jailBed.sleepPosition = transform;
                
                ModLogger.Info($"Added JailBed component to {jailBed.bedName}");
            }
            
            // Update interaction to show sleep option
            UpdateInteractionState();
            
            // Show completion notification
            if (BehindBarsUIManager.Instance != null)
            {
                BehindBarsUIManager.Instance.ShowNotification(
                    "Bed setup complete! You can now sleep here.", 
                    NotificationType.Progress
                );
            }
            
            ModLogger.Info($"Prison bed setup completed and converted to functional bed");
        }
        
        /// <summary>
        /// Reset bed to unmade state (for new inmates)
        /// </summary>
        public void ResetBed()
        {
            ModLogger.Info($"Resetting bed setup for {(isTopBunk ? "top bunk" : "bottom bunk")} in {cellName}");
            
            // Remove JailBed component if it exists
            var jailBed = GetComponent<JailBed>();
            if (jailBed != null)
            {
                DestroyImmediate(jailBed);
                ModLogger.Debug("Removed JailBed component");
            }
            
            // Reset state
            isComplete = false;
            isProcessing = false;
            SetupStage = 0;
            
            ModLogger.Info("Bed reset to unmade state");
        }
        
        /// <summary>
        /// Check if this bed is fully set up
        /// </summary>
        public bool IsComplete => isComplete && setupStage >= 4;
        
        /// <summary>
        /// Get setup progress (0.0 to 1.0)
        /// </summary>
        public float GetProgress() => setupStage / 4f;

        /// <summary>
        /// Get the required item ID for a specific bed setup stage
        /// </summary>
        private string GetRequiredItemForStage(int stage)
        {
            switch (stage)
            {
                case 0: // Place bed mat
                    return "behindbars.bedroll";
                case 1: // Add bottom sheet - uses sheets & pillow item
                case 2: // Add top sheet - uses sheets & pillow item
                case 3: // Add pillow - consumes the sheets & pillow item
                    return "behindbars.sheetsnpillows";
                default:
                    return null;
            }
        }
        
        /// <summary>
        /// Get a user-friendly display name for an item
        /// </summary>
        private string GetItemDisplayName(string itemId)
        {
            switch (itemId)
            {
                case "behindbars.bedroll":
                    return "bed roll";
                case "behindbars.sheetsnpillows":
                    return "sheets & pillow";
                default:
                    return itemId;
            }
        }
        
        /// <summary>
        /// Check if the player has the required item in their inventory
        /// </summary>
        private bool CheckPlayerHasRequiredItem(string itemId)
        {
            try
            {
#if !MONO
                var inventory = Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance;
#else
                var inventory = ScheduleOne.PlayerScripts.PlayerInventory.Instance;
#endif
                if (inventory == null)
                {
                    ModLogger.Warn("PlayerInventory instance not found for item check");
                    return false;
                }

#if !MONO
                var itemDef = Il2CppScheduleOne.Registry.GetItem(itemId);
#else
                var itemDef = ScheduleOne.Registry.GetItem(itemId);
#endif
                if (itemDef == null)
                {
                    ModLogger.Error($"Item definition not found for {itemId}");
                    return false;
                }

                // TODO: Fix inventory API usage after game update
                ModLogger.Debug($"Inventory API changed - assuming player has {itemId}");
                return true; // Temporary workaround

                ModLogger.Debug($"Item {itemId} not found in player inventory");
                return false;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error checking for required item {itemId}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Remove the required item from player inventory when placing bed component
        /// </summary>
        private void ConsumeRequiredItem(string itemId)
        {
            try
            {
#if !MONO
                var inventory = Il2CppScheduleOne.PlayerScripts.PlayerInventory.Instance;
#else
                var inventory = ScheduleOne.PlayerScripts.PlayerInventory.Instance;
#endif
                if (inventory == null)
                {
                    ModLogger.Error("PlayerInventory instance not found for item consumption");
                    return;
                }

                // TODO: Fix inventory API usage after game update
                ModLogger.Info($"Inventory API changed - simulating consumption of {itemId}");
                
                string itemName = GetItemDisplayName(itemId);
                if (BehindBarsUIManager.Instance != null)
                {
                    BehindBarsUIManager.Instance.ShowNotification(
                        $"Used {itemName}", 
                        NotificationType.Progress
                    );
                }
                
                return;

                ModLogger.Warn($"Could not find {itemId} in inventory to consume");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error consuming required item {itemId}: {ex.Message}");
            }
        }

        void OnValidate()
        {
            // Auto-set cell name if empty
            if (string.IsNullOrEmpty(cellName))
            {
                var parent = transform.parent;
                if (parent != null)
                    cellName = parent.name;
            }
        }
    }
}