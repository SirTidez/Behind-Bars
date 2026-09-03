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
    // Prevent the interaction that completes bed construction from carrying through
    // to the newly attached sleep listener during the same input press.
    private float sleepInteractionEnabledTime;
    private const float SleepInteractionArmDelay = 0.25f;
    
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
        sleepInteractionEnabledTime = Time.unscaledTime + SleepInteractionArmDelay;
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
            if (Time.unscaledTime < sleepInteractionEnabledTime)
            {
                ModLogger.Debug($"Ignored carried-through setup interaction for {bedName}");
                return;
            }

            var player = Player.Local;
            if (player == null)
            {
                ModLogger.Warn("Local player not found - cannot start sleep");
                return;
            }

            if (!CanSleep(out string noSleepReason))
            {
                interactableObject.SetInteractableState(InteractableObject.EInteractableState.Invalid);
                interactableObject.SetMessage(noSleepReason);
                ModLogger.Info($"Blocked out-of-hours sleep interaction for {bedName}: {noSleepReason}");
                return;
            }
            
            // SleepCanvas owns the active bed/sleep state in the current game API.
            // Do not write the removed Player.CurrentBed field directly.
            if (networkObject == null)
            {
                ModLogger.Debug("Jail bed has no NetworkObject; opening the native sleep menu without a bed binding");
            }

            // Match the native Bed interaction: open the menu and let the player press
            // its Sleep button. Calling SleepButtonPressed here skipped the normal UI/input
            // handoff and could leave DailySummary open without cursor ownership.
            try
            {
                Singleton<SleepCanvas>.Instance.OpenMenu();
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
    
    // Match Schedule I's native Bed eligibility window exactly: 18:00 through 04:00.
    private bool CanSleep(out string noSleepReason)
    {
        noSleepReason = string.Empty;

        if (GameManager.IS_TUTORIAL)
        {
            return true;
        }

        var timeManager = NetworkSingleton<TimeManager>.Instance;
        if (timeManager == null || !timeManager.IsCurrentTimeWithinRange(1800, 400))
        {
            noSleepReason = "Can't sleep before " + TimeManager.Get12HourTime(1800f);
            return false;
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
