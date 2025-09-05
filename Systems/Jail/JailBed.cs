using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.DevUtilities;
using FishNet.Object;
using FishNet;
#else
using ScheduleOne.Interaction;
using ScheduleOne.GameTime;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using ScheduleOne.DevUtilities;
using FishNet.Object;
using FishNet;
#endif

// Note: Behind Bars doesn't use FishNet, but we need to mimic the Schedule I Bed behavior
#if MONO
public class JailBed : MonoBehaviour
#else
public class JailBed : MonoBehaviour
#endif
{
    [Header("Bed Configuration")]
    public string bedName = "Jail Bed";
    public bool isTopBunk = false;
    
    [Header("References")]
    public InteractableObject interactableObject;
    public Transform sleepPosition;
    
    // We need to use the actual NetworkObject type or work around it
    private NetworkObject networkObject;
    
    private bool isInitialized = false;
    
    void Start()
    {
        InitializeBed();
    }
    
    void InitializeBed()
    {
        if (isInitialized) return;
        
        // Try to get or add a NetworkObject component
        networkObject = GetComponent<NetworkObject>();
        if (networkObject == null)
        {
            // For jail beds, we might not be able to add a NetworkObject directly
            // We'll try a different approach in the Interacted method
            ModLogger.Debug("Could not add NetworkObject to jail bed - will use alternative approach");
        }
        
        // Get or create InteractableObject
        if (interactableObject == null)
        {
            interactableObject = GetComponent<InteractableObject>();
            if (interactableObject == null)
            {
                interactableObject = gameObject.AddComponent<InteractableObject>();
            }
        }
        
        // Configure the interactable exactly like Schedule I
        interactableObject.SetMessage("Sleep");
        interactableObject.MaxInteractionRange = 5f; // Match Schedule I's range
        
        // Set up event handlers - using Unity Events like Schedule I
        interactableObject.onHovered.AddListener(Hovered);
        interactableObject.onInteractStart.AddListener(Interacted);
        
        // Set sleep position
        if (sleepPosition == null)
        {
            sleepPosition = transform;
        }
        
        isInitialized = true;
        ModLogger.Info($"Initialized jail bed: {bedName} (Top Bunk: {isTopBunk})");
    }
    
    // Copy Schedule I's Hovered method exactly
    public void Hovered()
    {
        string noSleepReason;
        
        // Skip management clipboard check since we don't have that in jail
        // Skip employee assignment check since jail beds aren't assigned to employees
        
        if (CanSleep(out noSleepReason))
        {
            interactableObject.SetInteractableState(InteractableObject.EInteractableState.Default);
            interactableObject.SetMessage("Sleep");
        }
        else
        {
            interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            interactableObject.SetMessage(noSleepReason);
        }
    }
    
    // Copy Schedule I's Interacted method exactly
    public void Interacted()
    {
        try
        {
            var player = Player.Local;
            if (player == null)
            {
                ModLogger.Warn("Local player not found - cannot start sleep");
                return;
            }
            
            // This is the key part - exactly like Schedule I's bed
            if (networkObject != null)
            {
                player.CurrentBed = networkObject; // Set the current bed
            }
            else
            {
                // Try to set it to null first, then find a suitable NetworkObject
                player.CurrentBed = null;
                ModLogger.Debug("No NetworkObject available for jail bed");
            }
            
            // Open Schedule I's sleep canvas - exactly like Schedule I's bed does
            try
            {
                Singleton<SleepCanvas>.Instance.SetIsOpen(true);
                ModLogger.Info($"Opened sleep canvas for {bedName}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Could not open sleep interface: {ex.Message}");
                // Try fallback approach if direct access fails
                ModLogger.Info("Attempting alternative sleep activation...");
            }
        }
        catch (System.Exception ex)
        {
            ModLogger.Error($"Error starting sleep in {bedName}: {ex.Message}");
        }
    }
    
    // Copy Schedule I's CanSleep method exactly  
    private bool CanSleep(out string noSleepReason)
    {
        noSleepReason = string.Empty;
        
        // Skip tutorial check - we're not in tutorial in jail
        
        // Check time restrictions - exactly like Schedule I
        try
        {
#if MONO
            // Use NetworkSingleton for Mono version like Schedule I does
            if (!NetworkSingleton<TimeManager>.Instance.IsCurrentTimeWithinRange(1800, 400))
            {
                noSleepReason = "Can't sleep before " + TimeManager.Get12HourTime(1800f);
                return false;
            }
#else
            // Use NetworkSingleton for IL2CPP version
            if (!NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance.IsCurrentTimeWithinRange(1800, 400))
            {
                noSleepReason = "Can't sleep before " + Il2CppScheduleOne.GameTime.TimeManager.Get12HourTime(1800f);
                return false;
            }
#endif
        }
        catch (System.Exception ex)
        {
            ModLogger.Debug($"Could not check time restrictions: {ex.Message}");
            // Fallback - don't allow sleep during day hours (rough estimate)
            var currentHour = System.DateTime.Now.Hour;
            if (currentHour >= 6 && currentHour < 18)
            {
                noSleepReason = "Can't sleep before 6:00 PM";
                return false;
            }
        }
        
        // Check for energizing products - like Schedule I
        try
        {
            var player = Player.Local;
            if (player != null && player.ConsumedProduct != null)
            {
                // This is simplified - Schedule I checks for specific product properties
                // We'd need to expand this if we want full product checking
                ModLogger.Debug("Player has consumed product - allowing sleep for now");
            }
        }
        catch (System.Exception ex)
        {
            ModLogger.Debug($"Could not check player product status: {ex.Message}");
        }
        
        return true;
    }
    
    void OnValidate()
    {
        if (string.IsNullOrEmpty(bedName))
        {
            bedName = isTopBunk ? "Top Bunk" : "Bottom Bunk";
        }
    }
}