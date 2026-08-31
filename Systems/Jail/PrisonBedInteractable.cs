using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using Behind_Bars.Helpers;
using Behind_Bars.UI;
using BBHelpers = Behind_Bars.Helpers.Helpers;

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

        // Authored dressing references.  JailCellManager binds these transforms from the bunk
        // prefab; this component only toggles their visibility as setupStage advances.
        public Transform bedMat;
        public Transform whiteSheet;
        public Transform bedSheet;  
        public Transform pillow;
        
        // Bunk metadata used when creating the functional JailBed handoff.
        public bool isTopBunk = false;
        public string cellName = "";
        
        // Setup stage is the single source for dressing visibility: 0 is empty and 4 is complete.
        private int setupStage = 0; // 0 = empty, 4 = complete
        
        // Component references
        private InteractableObject interactableObject;
                
        // State invariant: isProcessing gates the delayed stage coroutine, isComplete gates the
        // final sleep handoff, and isClaimedByNpc blocks player setup while the bunk is reserved.
        // npcOwnerName is display-only ownership context for the claimed state.
        private bool isProcessing = false;
        private bool isComplete = false;
        private bool isClaimedByNpc = false;
        // The complete staged dressing hierarchy is serialized under the
        // anchor's authored PrisonBedInteractable prefab. JailCellManager
        // binds that hierarchy and this component only controls ownership and
        // player interaction state.
        private string npcOwnerName = string.Empty;
        
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

        /// <summary>
        /// Initialize the interactable and its staged visuals.  An NPC claim made before Unity
        /// invokes <c>Start</c> is preserved as a completed, player-inaccessible bunk; otherwise
        /// the bed starts at stage zero.
        /// </summary>
        private void InitializeBedSetup()
        {
            ModLogger.Debug($"Initializing prison bed setup for {(isTopBunk ? "top bunk" : "bottom bunk")} in {cellName}");
                        
            // Set up interaction component
            SetupInteractableComponent();
            
            // Preserve a claim made during staged NPC spawning. The owning
            // cell can reserve this bunk before Unity invokes Start on the
            // newly attached interactable.
            SetupStage = isClaimedByNpc ? 4 : 0;
            if (isClaimedByNpc)
            {
                isProcessing = false;
                isComplete = true;
            }
            
            // Force initial visual update to ensure everything starts hidden
            UpdateBedVisuals();
            
            ModLogger.Debug($"Prison bed setup initialized at stage {setupStage}");
        }
                
#if !MONO
        [HideFromIl2Cpp]
#endif
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
                ModLogger.Debug("Added InteractableObject component to prison bed");
            }
            
            // Configure the interaction
            UpdateInteractionState();
            interactableObject.SetInteractionType(InteractableObject.EInteractionType.Key_Press);
            interactableObject.MaxInteractionRange = 3f;
            
            // Set up event listeners with IL2CPP-safe casting
#if !MONO
            interactableObject.onHovered.AddListener((System.Action)OnHovered);
            interactableObject.onInteractStart.AddListener((System.Action)OnInteractStart);
#else
            interactableObject.onHovered.AddListener(OnHovered);
            interactableObject.onInteractStart.AddListener(OnInteractStart);
#endif
            
            ModLogger.Debug("Prison bed InteractableObject component configured");
        }
        
        /// <summary>
        /// Current staged dressing progress, clamped to the inclusive range 0 through 4.  Setting
        /// the stage immediately refreshes both dressing visibility and interaction messaging.
        /// </summary>
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
        
        /// <summary>
        /// Toggle the authored dressing objects to match the current setup stage.  This method does
        /// not create or destroy dressing objects and tolerates missing transform references.
        /// </summary>
        private void UpdateBedVisuals()
        {
            // Simply enable/disable the GameObjects - much cleaner approach
            bool showMat = setupStage >= 1;
            bool showWhiteSheet = setupStage >= 2;
            bool showBedSheet = setupStage >= 3;
            bool showPillow = setupStage >= 4;
            
            // Enable/disable the bed component GameObjects
            SetDressingVisible(bedMat, showMat);
            SetDressingVisible(whiteSheet, showWhiteSheet);
            SetDressingVisible(bedSheet, showBedSheet);
            SetDressingVisible(pillow, showPillow);
            
            ModLogger.Debug($"Updated bed visuals for stage {setupStage} - Mat: {showMat}, WhiteSheet: {showWhiteSheet}, BedSheet: {showBedSheet}, Pillow: {showPillow}");
        }
        
        /// <summary>
        /// Called when player hovers over the bed - updates interaction state in real-time
        /// </summary>
        private void OnHovered()
        {
            UpdateInteractionState();
        }
        
        /// <summary>
        /// Recompute the interaction prompt and validity from claim, completion, processing, and
        /// item-availability state.  Claimed beds are blocked, completed beds advertise sleep, and
        /// incomplete beds advertise the next required component when it is available.
        /// </summary>
        private void UpdateInteractionState()
        {
            if (interactableObject == null) return;

            if (isClaimedByNpc)
            {
                interactableObject.SetMessage($"{npcOwnerName}'s assigned bunk");
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                return;
            }
            
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
        
        /// <summary>
        /// Gate a player interaction and start at most one delayed setup-stage coroutine.  A claimed
        /// or already-processing bed is ignored; a complete bed only logs because <c>JailBed</c>
        /// owns the sleep interaction after setup completes.
        /// </summary>
        private void OnInteractStart()
        {
            if (isProcessing || isClaimedByNpc) return;
            
            if (isComplete)
            {
                // Bed is complete; the JailBed component attached during CompleteBedSetup owns
                // the resulting sleep interaction, so this staged setup component only logs/returns.
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
                if (Core.ResolveUIManager() != null)
                {
                    Core.ResolveUIManager().ShowNotification(
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

        /// <summary>
        /// Marks this bed as an occupied inmate's completed bunk.  NPC beds
        /// are visual-only and deliberately cannot be claimed by the player.
        /// </summary>
        public void ClaimForNpc(string ownerName)
        {
            isClaimedByNpc = true;
            npcOwnerName = string.IsNullOrWhiteSpace(ownerName) ? "Inmate" : ownerName;
            isProcessing = false;
            isComplete = true;
            SetupStage = 4;
            ModLogger.Debug($"Claimed prison bunk in {cellName} for NPC {npcOwnerName}");
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void SetDressingVisible(Transform dressing, bool visible)
        {
            if (dressing != null)
            {
                dressing.gameObject.SetActive(visible);
            }
        }

#if !MONO
        [HideFromIl2Cpp]
#endif
        /// <summary>
        /// Process one staged bed action with its fixed 1.5-second delay.  The stage advances first,
        /// consumes the bedroll after stage 0 and sheets/pillow after stage 3, and converts the bed
        /// to a functional <c>JailBed</c> after the final half-second completion delay.
        /// </summary>
        private IEnumerator ProcessBedSetupStage()
        {
            isProcessing = true;
            UpdateInteractionState();
            
            ModLogger.Info($"Processing bed setup stage {setupStage + 1}");
            
            // Show progress notification
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    stageMessages[setupStage], 
                    NotificationType.Progress
                );
            }
            
            // Setup time delay
            yield return new WaitForSeconds(1.5f);
            
            // Advance to next stage
            SetupStage++;
            
            // Consume items only at the right times:
            // - Bedroll is consumed after stage 0 (placing bed mat)
            // - Sheets & pillow is consumed only after stage 3 (when bed is complete)
            // - Stages 1 and 2 use sheetsnpillows but don't consume it yet
            if (setupStage == 1)
            {
                // Just finished stage 0 - consume bedroll
                ConsumeRequiredItem("behindbars.bedroll");
            }
            else if (setupStage >= 4)
            {
                // Just finished stage 3 - bed is complete, consume sheets & pillow
                ConsumeRequiredItem("behindbars.sheetsnpillows");
            }
            // Stages 1 and 2 don't consume the item - it's needed for later stages
            
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
                if (Core.ResolveUIManager() != null)
                {
                    Core.ResolveUIManager().ShowNotification(
                        $"Bed setup: {setupStage}/4 complete", 
                        NotificationType.Progress
                    );
                }
            }
            
            ModLogger.Info($"Bed setup stage completed. Current stage: {setupStage}");
        }
        
        /// <summary>
        /// Mark staged setup complete, attach and initialize the functional <c>JailBed</c> when it
        /// is absent, then refresh the sleep prompt and completion notification.
        /// </summary>
        private void CompleteBedSetup()
        {
            ModLogger.Info($"Completing bed setup for {(isTopBunk ? "top bunk" : "bottom bunk")} in {cellName}");
            
            isComplete = true;
            isProcessing = false;
            
            // Add JailBed component for sleeping functionality
            var jailBed = BBHelpers.GetComponentSafe<JailBed>(gameObject);
            if (jailBed == null)
            {
                jailBed = BBHelpers.AddComponentSafe<JailBed>(gameObject);
                jailBed.bedName = $"{cellName} {(isTopBunk ? "Top Bunk" : "Bottom Bunk")}";
                jailBed.isTopBunk = isTopBunk;
                jailBed.sleepPosition = transform;
                
                ModLogger.Info($"Added JailBed component to {jailBed.bedName}");
            }
            
            // Update interaction to show sleep option
            UpdateInteractionState();
            
            // Show completion notification
            if (Core.ResolveUIManager() != null)
            {
                Core.ResolveUIManager().ShowNotification(
                    "Bed setup complete! You can now sleep here.", 
                    NotificationType.Progress
                );
            }
            
            ModLogger.Info($"Prison bed setup completed and converted to functional bed");
        }
        
        /// <summary>
        /// Resets dressing state to stage zero and removes the functional JailBed component when present.
        /// This reduced reset does not clear the NPC-claim flag or owner name, so it is not a complete ownership reset and
        /// the bed may remain player-inaccessible through the claimed-state invariant.
        /// </summary>
        public void ResetBed()
        {
            ModLogger.Info($"Resetting bed setup for {(isTopBunk ? "top bunk" : "bottom bunk")} in {cellName}");
            
            // Remove JailBed component if it exists
            var jailBed = BBHelpers.GetComponentSafe<JailBed>(gameObject);
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
        /// Returns true only when the completion flag is set and the clamped setup stage has reached 4. An NPC claim also
        /// sets this state, while ResetBed can clear completion without clearing the separate NPC-claim flag.
        /// </summary>
        public bool IsComplete => isComplete && setupStage >= 4;
        
        /// <summary>
        /// Returns setupStage divided by 4 as normalized progress. SetupStage is clamped to 0-4, so normal values are 0.0
        /// through 1.0; the method does not independently inspect isComplete or NPC ownership.
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
                    return "behindbars.bedroll"; // Correct item ID from PrisonItemRegistry
                case 1: // Add bottom sheet - uses sheets & pillow item
                case 2: // Add top sheet - uses sheets & pillow item  
                case 3: // Add pillow - consumes the sheets & pillow item
                    return "behindbars.sheetsnpillows"; // Correct item ID from PrisonItemRegistry
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

                // Use the updated inventory API
                uint itemCount = inventory.GetAmountOfItem(itemId);
                bool hasItem = itemCount > 0;
                
                ModLogger.Debug($"Player has {itemCount} of item {itemId}: {hasItem}");
                return hasItem;
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

                // Check how many items the player has before consuming
                uint itemCount = inventory.GetAmountOfItem(itemId);
                if (itemCount > 0)
                {
                    // Consume 1 item
                    inventory.RemoveAmountOfItem(itemId, 1);
                    
                    string itemName = GetItemDisplayName(itemId);
                    ModLogger.Info($"Consumed 1 {itemName} from player inventory (had {itemCount})");
                    
                    if (Core.ResolveUIManager() != null)
                    {
                        Core.ResolveUIManager().ShowNotification(
                            $"Used {itemName}", 
                            NotificationType.Progress
                        );
                    }
                }
                else
                {
                    ModLogger.Warn($"Could not find {itemId} in inventory to consume (player has {itemCount})");
                }
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
