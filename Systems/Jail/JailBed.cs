using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems;

#if MONO
using FishNet.Object;
#endif



#if !MONO
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.DevUtilities;
using Il2CppFishNet.Object;
using Il2CppInterop.Runtime;
#else
using ScheduleOne.Interaction;
using ScheduleOne.GameTime;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using ScheduleOne.DevUtilities;
#endif

// Note: Behind Bars doesn't use FishNet, but we need to mimic the Schedule I Bed behavior
#if MONO
public class JailBed : MonoBehaviour
#else
public class JailBed : MonoBehaviour
#endif
{
    public string bedName = "Jail Bed";
    public bool isTopBunk = false;
    
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
        
        // Try to get or add a NetworkObject component using IL2CPP-safe method
#if !MONO
        // IL2CPP-safe NetworkObject access
        var components = GetComponents<MonoBehaviour>();
        foreach (var comp in components)
        {
            if (comp != null && comp.GetType().Name.Contains("NetworkObject"))
            {
                networkObject = comp as NetworkObject;
                break;
            }
        }
#else
        // Mono version
        networkObject = GetComponent<NetworkObject>();
#endif
        
        if (networkObject == null)
        {
            // For jail beds, we might not be able to add a NetworkObject directly
            // We'll try a different approach in the Interacted method
            ModLogger.Debug("Could not add NetworkObject to jail bed - will use alternative approach");
        }
        
        // Get or create InteractableObject using simplified approach
        if (interactableObject == null)
        {
            interactableObject = GetComponent<InteractableObject>();

            if (interactableObject == null)
            {
                try
                {
#if !MONO
                    // IL2CPP-safe component addition
                    var component = gameObject.AddComponent(Il2CppType.Of<InteractableObject>());
                    interactableObject = component.Cast<InteractableObject>();
#else
                    // Mono version
                    interactableObject = gameObject.AddComponent<InteractableObject>();
#endif
                    ModLogger.Info($"✓ Created InteractableObject for {bedName}");
                }
                catch (System.Exception ex)
                {
                    ModLogger.Error($"Failed to create InteractableObject for {bedName}: {ex.Message}");
                    return; // Don't continue initialization if we can't create the interactable
                }
            }
            else
            {
                ModLogger.Info($"✓ Found existing InteractableObject for {bedName}");
            }
        }
        
        // Configure the interactable exactly like Schedule I
        interactableObject.SetMessage("Sleep");
        interactableObject.MaxInteractionRange = 5f; // Match Schedule I's range
        
        // Set up event handlers - using Unity Events like Schedule I
#if !MONO
        interactableObject.onHovered.AddListener((System.Action)Hovered);
        interactableObject.onInteractStart.AddListener((System.Action)Interacted);
#else
        interactableObject.onHovered.AddListener(Hovered);
        interactableObject.onInteractStart.AddListener(Interacted);
#endif
        
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
            
            // SleepCanvas owns the active bed/sleep state in the current game API.
            // Do not write the removed Player.CurrentBed field directly.
            if (networkObject == null)
            {
                ModLogger.Debug("Jail bed has no NetworkObject; opening the native sleep menu without a bed binding");
            }

            // Open Schedule I's current native sleep menu.
            try
            {
                Singleton<SleepCanvas>.Instance.OpenMenu();
                Singleton<SleepCanvas>.Instance.SleepButtonPressed();
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
    
    // CanSleep method for jail beds - allow sleep at 6PM (18:00) or later, or before 7AM (end of day cycle)
    // Day cycle is 7am-4am, so allow sleep from 6PM to 7AM next day
    private bool CanSleep(out string noSleepReason)
    {
        noSleepReason = string.Empty;
        
        // Get current game hour (0-23)
        int currentHour = GameTimeManager.Instance.GetCurrentGameHour();
        
        // Get current game minute for more accurate logging
        int currentMinute = GameTimeManager.Instance.GetCurrentGameMinute();
        
        // Allow sleep if:
        // - It's 6PM (18:00) or later (evening/night)
        // - OR it's before 7AM (0-6) (early morning - end of day cycle)
        // This covers the full day cycle from 7am to 4am next day
        bool canSleep = currentHour >= 18 || currentHour < 7;
        
        if (canSleep)
        {
            // Format time for display (handle 12-hour format)
            string timeDisplay = FormatTimeForDisplay(currentHour, currentMinute);
            ModLogger.Debug($"[JailBed] Sleep check - Current game time: {timeDisplay} ({currentHour:00}:{currentMinute:00}) - Sleep ALLOWED (after 6PM or before 7AM)");
            return true;
        }
        else
        {
            // Format time for display
            string timeDisplay = FormatTimeForDisplay(currentHour, currentMinute);
            noSleepReason = $"Cannot sleep until 6PM (current time: {timeDisplay})";
            ModLogger.Debug($"[JailBed] Sleep check - Current game time: {timeDisplay} ({currentHour:00}:{currentMinute:00}) - Sleep NOT ALLOWED (between 7AM and 6PM)");
            return false;
        }
    }
    
    /// <summary>
    /// Format game hour and minute into a readable time string
    /// </summary>
    private string FormatTimeForDisplay(int hour, int minute)
    {
        // Convert to 12-hour format for better readability
        int displayHour = hour % 12;
        if (displayHour == 0) displayHour = 12;
        string period = hour < 12 ? "AM" : "PM";
        return $"{displayHour}:{minute:00} {period}";
    }
    
    void OnValidate()
    {
        if (string.IsNullOrEmpty(bedName))
        {
            bedName = isTopBunk ? "Top Bunk" : "Bottom Bunk";
        }
    }
}
