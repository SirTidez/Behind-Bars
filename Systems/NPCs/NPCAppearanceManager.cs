using System;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Employees;
#else
using ScheduleOne.AvatarFramework;
using ScheduleOne.NPCs;
using ScheduleOne.Employees;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Centralized appearance management for BaseNPC spawns
    /// Fixes the "marshmallow man" issue by providing proper avatar settings
    /// </summary>
    public static class NPCAppearanceManager
    {
        // Cache of existing avatar settings from scene NPCs
        private static Dictionary<string, object> avatarSettingsCache = new Dictionary<string, object>();
        private static bool cacheInitialized = false;

        /// <summary>
        /// Get appropriate avatar settings for an NPC role
        /// This is the key method that fixes BaseNPC appearance issues
        /// Uses EmployeeManager for guaranteed working avatar settings
        /// </summary>
        /// <param name="role">The role of the NPC</param>
        /// <param name="firstName">First name for variation</param>
        /// <returns>Avatar settings object or null</returns>
        public static object GetAppearanceForRole(BaseNPCSpawner.NPCRole role, string firstName)
        {
            try
            {
                // First try EmployeeManager for guaranteed working avatars
                var employeeSettings = GetEmployeeManagerAppearance(role);
                if (employeeSettings != null)
                {
                    ModLogger.Debug($"✓ Using EmployeeManager appearance for {role}");
                    return employeeSettings;
                }

                // Fallback to cache if EmployeeManager fails
                if (!cacheInitialized)
                {
                    InitializeAppearanceCache();
                }

                // Try to get appropriate settings based on role
                switch (role)
                {
                    case BaseNPCSpawner.NPCRole.PrisonGuard:
                    case BaseNPCSpawner.NPCRole.IntakeOfficer:
                        return GetGuardAppearance(firstName);

                    case BaseNPCSpawner.NPCRole.PrisonInmate:
                        return GetInmateAppearance(firstName);

                    case BaseNPCSpawner.NPCRole.TestNPC:
                        return GetTestNPCAppearance(firstName);

                    default:
                        return GetFallbackAppearance();
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error getting appearance for {role}: {e.Message}");
                return GetFallbackAppearance();
            }
        }

        /// <summary>
        /// Get avatar settings from EmployeeManager - guaranteed to work
        /// </summary>
        /// <param name="role">NPC role to get appearance for</param>
        /// <returns>Valid AvatarSettings or null</returns>
        private static object GetEmployeeManagerAppearance(BaseNPCSpawner.NPCRole role)
        {
            try
            {
#if !MONO
                var employeeManager = EmployeeManager.Instance;
#else
                var employeeManager = EmployeeManager.Instance;
#endif
                if (employeeManager == null)
                {
                    ModLogger.Warn("EmployeeManager not available, using fallback");
                    return null;
                }

                // Determine if we want male or female appearance based on role
                bool male = UnityEngine.Random.Range(0f, 1f) > 0.3f; // 70% male for more variety

                // Get random working appearance from EmployeeManager
                employeeManager.GetRandomAppearance(male, out int index, out var settings);

                if (settings != null)
                {
                    // Create a copy so we don't modify the original
                    var copiedSettings = UnityEngine.Object.Instantiate(settings);

                    // Customize based on role
                    switch (role)
                    {
                        case BaseNPCSpawner.NPCRole.PrisonGuard:
                        case BaseNPCSpawner.NPCRole.IntakeOfficer:
                            CustomizeForGuard(copiedSettings);
                            break;

                        case BaseNPCSpawner.NPCRole.PrisonInmate:
                            CustomizeForInmate(copiedSettings);
                            break;
                    }

                    ModLogger.Debug($"✓ Generated {role} appearance from EmployeeManager (male: {male}, index: {index})");
                    return copiedSettings;
                }
                else
                {
                    ModLogger.Warn("EmployeeManager returned null settings");
                    return null;
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error getting EmployeeManager appearance: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Customize avatar settings for guard appearance
        /// </summary>
        private static void CustomizeForGuard(object settings)
        {
            try
            {
#if !MONO
                var avatarSettings = settings as Il2CppScheduleOne.AvatarFramework.AvatarSettings;
#else
                var avatarSettings = settings as ScheduleOne.AvatarFramework.AvatarSettings;
#endif
                if (avatarSettings == null) return;

                // Guards should look professional - adjust weight/gender slightly toward fit males
                if (avatarSettings.Gender > 0.6f) // If very feminine, make more neutral
                {
                    avatarSettings.Gender = UnityEngine.Random.Range(0.3f, 0.7f);
                }

                // Keep guards in good shape
                if (avatarSettings.Weight > 0.3f)
                {
                    avatarSettings.Weight = UnityEngine.Random.Range(-0.2f, 0.3f);
                }

                // IMPORTANT: Add guard uniform clothing layers
                // Clear any existing clothing and add guard uniform
                avatarSettings.BodyLayerSettings.Clear();

                // Add underwear first (always needed)
                bool isMale = avatarSettings.Gender < 0.5f;
                string underwearPath = isMale ? "Avatar/Layers/Bottom/MaleUnderwear" : "Avatar/Layers/Bottom/FemaleUnderwear";
                avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = underwearPath,
                    layerTint = Color.white
                });

                // Add guard shirt FIRST (shirts should be added before body details)
                avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = "Avatar/Layers/Top/ButtonUp",  // Try ButtonUp instead
                    layerTint = new Color(0.2f, 0.3f, 0.5f) // Dark blue police uniform
                });

                // Add guard pants (using jeans, colored dark)
                avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = AvatarResourcePaths.Bottoms.Jeans,
                    layerTint = new Color(0.15f, 0.15f, 0.2f) // Dark blue/black
                });

                // Add shoes as ACCESSORIES (using correct paths from S1API)
                avatarSettings.AccessorySettings.Clear();
                avatarSettings.AccessorySettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#endif
                {
                    path = AvatarResourcePaths.Footwear.CombatShoes,  // Combat boots for guards
                    color = Color.black
                });

                // Add police cap (always for guards)
                avatarSettings.AccessorySettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#endif
                {
                    path = AvatarResourcePaths.Headwear.PoliceCap,  // Actual police cap!
                    color = new Color(0.2f, 0.3f, 0.5f)  // Blue
                });

                ModLogger.Debug($"Applied guard customizations with {avatarSettings.BodyLayerSettings.Count} body layers and {avatarSettings.AccessorySettings.Count} accessories");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error customizing guard appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Customize avatar settings for inmate appearance
        /// </summary>
        private static void CustomizeForInmate(object settings)
        {
            try
            {
#if !MONO
                var avatarSettings = settings as Il2CppScheduleOne.AvatarFramework.AvatarSettings;
#else
                var avatarSettings = settings as ScheduleOne.AvatarFramework.AvatarSettings;
#endif
                if (avatarSettings == null) return;

                // Inmates can have more varied appearance
                avatarSettings.Weight += UnityEngine.Random.Range(-0.1f, 0.2f);
                avatarSettings.Weight = Mathf.Clamp(avatarSettings.Weight, -0.5f, 0.5f);

                // DON'T clear face layers - they contain the actual face!
                // We can add face details that act like tattoos

                // 60% chance of face tattoos/markings
                if (UnityEngine.Random.Range(0f, 1f) > 0.4f)
                {
                    // Add EyeShadow as black eye tattoo (100%)
                    avatarSettings.FaceLayerSettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                    {
                        layerPath = AvatarResourcePaths.FaceDetails.EyeShadow,
                        layerTint = new Color(0.0f, 0.0f, 0.0f, 1.0f)  // Pure black for face tattoo effect
                    });
                    ModLogger.Debug("Added eye shadow as black face tattoo");

                    // Add tired eyes for additional markings (50%)
                    if (UnityEngine.Random.Range(0f, 1f) > 0.5f)
                    {
                        avatarSettings.FaceLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = AvatarResourcePaths.FaceDetails.TiredEyes,
                            layerTint = new Color(0.0f, 0.0f, 0.0f, 0.8f)  // Dark black
                        });
                        ModLogger.Debug("Added tired eyes as face marking");
                    }

                    // Add freckles as face dots (30%)
                    if (UnityEngine.Random.Range(0f, 1f) > 0.7f)
                    {
                        avatarSettings.FaceLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = AvatarResourcePaths.FaceDetails.Freckles,
                            layerTint = new Color(0.0f, 0.0f, 0.0f, 1.0f)  // Black dots
                        });
                        ModLogger.Debug("Added freckles as black face dots");
                    }
                }

                // 20% chance of facial hair for male inmates (separate from tattoos)
                bool isMaleInmate = avatarSettings.Gender < 0.5f;
                if (isMaleInmate && UnityEngine.Random.Range(0f, 1f) > 0.8f)
                {
                    string[] facialHairOptions = {
                        AvatarResourcePaths.Face.Beard,
                        AvatarResourcePaths.Face.Mustache,
                        AvatarResourcePaths.Face.Goatee
                    };
                    string selectedFacialHair = facialHairOptions[UnityEngine.Random.Range(0, facialHairOptions.Length)];

                    avatarSettings.FaceLayerSettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                    {
                        layerPath = selectedFacialHair,
                        layerTint = avatarSettings.HairColor  // Match hair color
                    });
                }

                // IMPORTANT: Add inmate uniform clothing layers
                // Clear any existing clothing and add orange jumpsuit
                avatarSettings.BodyLayerSettings.Clear();

                // Add underwear first (always needed)
                bool isMale = avatarSettings.Gender < 0.5f;
                string underwearPath = isMale ? "Avatar/Layers/Bottom/MaleUnderwear" : "Avatar/Layers/Bottom/FemaleUnderwear";
                avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = underwearPath,
                    layerTint = Color.white
                });

                // Track if inmate has chest tattoo for shirt decision
                bool hasChestTattoo = false;

                // Add chest details and tattoos BEFORE clothes
                // 40% chance of chest hair tattoo for males
                if (isMale && UnityEngine.Random.Range(0f, 1f) > 0.6f)
                {
                    hasChestTattoo = true;
                    avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                    {
                        layerPath = AvatarResourcePaths.Body.ChestHair,
                        layerTint = new Color(0.05f, 0.05f, 0.1f, 0.95f)  // Very dark, like chest tattoo
                    });
                    ModLogger.Debug("Added chest tattoo (chest hair with dark tint)");
                }

                // 70% chance of upper body tattoos (arms/shoulders)
                if (UnityEngine.Random.Range(0f, 1f) > 0.3f)
                {
                    avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                    {
                        layerPath = AvatarResourcePaths.Body.UpperBodyTattoos,
                        layerTint = new Color(0.1f, 0.1f, 0.15f, 1f)  // Very dark blue/black, full opacity
                    });
                    ModLogger.Debug("Added upper body tattoos to inmate");
                }

                // Add shirt ONLY if inmate doesn't have chest tattoo (to show it off)
                if (!hasChestTattoo)
                {
                    avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                    {
                        layerPath = AvatarResourcePaths.Tops.TShirt,
                        layerTint = new Color(1f, 0.5f, 0f) // Orange
                    });
                    ModLogger.Debug("Added orange shirt (no chest tattoo)");
                }
                else
                {
                    ModLogger.Debug("Inmate is SHIRTLESS to show chest tattoo");
                }

                // Add orange jumpsuit pants
                avatarSettings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = AvatarResourcePaths.Bottoms.Jeans,
                    layerTint = new Color(1f, 0.5f, 0f) // Orange
                });

                // Add basic shoes/sandals as accessories (using correct path)
                avatarSettings.AccessorySettings.Clear();
                avatarSettings.AccessorySettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#endif
                {
                    path = AvatarResourcePaths.Footwear.Sandals,  // Prison sandals
                    color = new Color(0.3f, 0.3f, 0.3f)  // Gray
                });

                // 15% chance of gold chain (some inmates manage to keep jewelry)
                if (UnityEngine.Random.Range(0f, 1f) > 0.85f)
                {
                    avatarSettings.AccessorySettings.Add(new
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#else
                        ScheduleOne.AvatarFramework.AvatarSettings.AccessorySetting
#endif
                    {
                        path = "Avatar/Accessories/Neck/GoldChain/GoldChain",
                        color = new Color(1f, 0.843f, 0f)  // Gold color
                    });
                }

                ModLogger.Debug($"Applied inmate customizations with {avatarSettings.BodyLayerSettings.Count} body layers and {avatarSettings.AccessorySettings.Count} accessories");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error customizing inmate appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize the appearance cache by finding existing NPCs with working avatars
        /// </summary>
        private static void InitializeAppearanceCache()
        {
            try
            {
                ModLogger.Info("🎨 Initializing NPC appearance cache...");

                // Find all existing NPCs with avatar settings
                var existingNPCs = UnityEngine.Object.FindObjectsOfType<NPC>();
                ModLogger.Info($"Found {existingNPCs.Length} existing NPCs to analyze");

                int cachedSettings = 0;

                foreach (var npc in existingNPCs)
                {
                    try
                    {
                        // Skip our own spawned NPCs
                        if (npc.gameObject.name.Contains("JailGuard") ||
                            npc.gameObject.name.Contains("JailInmate") ||
                            npc.gameObject.name.Contains("TestNPC") ||
                            npc.gameObject.name.Contains("BaseNPC"))
                            continue;

                        var avatar = npc.Avatar;
                        if (avatar != null && avatar.CurrentSettings != null)
                        {
                            string npcName = npc.gameObject.name.ToLower();

                            // Cache settings based on NPC type/name
                            if (IsGuardNPC(npcName))
                            {
                                avatarSettingsCache[$"guard_{cachedSettings}"] = avatar.CurrentSettings;
                                ModLogger.Info($"✓ Cached guard appearance from {npc.gameObject.name}");
                                cachedSettings++;
                            }
                            else if (IsInmateNPC(npcName))
                            {
                                avatarSettingsCache[$"inmate_{cachedSettings}"] = avatar.CurrentSettings;
                                ModLogger.Info($"✓ Cached inmate appearance from {npc.gameObject.name}");
                                cachedSettings++;
                            }
                            else
                            {
                                // Generic civilian appearance
                                avatarSettingsCache[$"civilian_{cachedSettings}"] = avatar.CurrentSettings;
                                ModLogger.Debug($"✓ Cached civilian appearance from {npc.gameObject.name}");
                                cachedSettings++;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        ModLogger.Debug($"Error caching appearance from {npc.gameObject.name}: {e.Message}");
                    }
                }

                // If we found settings, also cache a fallback
                if (cachedSettings > 0)
                {
                    // Use the first civilian as fallback
                    foreach (var kvp in avatarSettingsCache)
                    {
                        if (kvp.Key.Contains("civilian"))
                        {
                            avatarSettingsCache["fallback"] = kvp.Value;
                            break;
                        }
                    }

                    // If no civilian, use any available
                    if (!avatarSettingsCache.ContainsKey("fallback"))
                    {
                        foreach (var kvp in avatarSettingsCache)
                        {
                            avatarSettingsCache["fallback"] = kvp.Value;
                            break;
                        }
                    }
                }

                cacheInitialized = true;
                ModLogger.Info($"🎨 Appearance cache initialized with {cachedSettings} settings");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing appearance cache: {e.Message}");
                cacheInitialized = true; // Prevent infinite loops
            }
        }

        /// <summary>
        /// Check if an NPC name suggests it's a guard/officer
        /// </summary>
        private static bool IsGuardNPC(string npcName)
        {
            return npcName.Contains("officer") ||
                   npcName.Contains("police") ||
                   npcName.Contains("cop") ||
                   npcName.Contains("guard") ||
                   npcName.Contains("security");
        }

        /// <summary>
        /// Check if an NPC name suggests it's an inmate/criminal
        /// </summary>
        private static bool IsInmateNPC(string npcName)
        {
            return npcName.Contains("billy") ||
                   npcName.Contains("kramer") ||
                   npcName.Contains("inmate") ||
                   npcName.Contains("prisoner") ||
                   npcName.Contains("criminal");
        }

        /// <summary>
        /// Get guard appearance settings
        /// </summary>
        private static object GetGuardAppearance(string firstName)
        {
            // Try to find guard-specific appearance
            foreach (var kvp in avatarSettingsCache)
            {
                if (kvp.Key.Contains("guard"))
                {
                    ModLogger.Info($"✓ Using cached guard appearance for {firstName}");
                    return kvp.Value;
                }
            }

            // Fallback to civilian
            return GetFallbackAppearance();
        }

        /// <summary>
        /// Get inmate appearance settings
        /// </summary>
        private static object GetInmateAppearance(string firstName)
        {
            // Try to find inmate-specific appearance (like Billy)
            foreach (var kvp in avatarSettingsCache)
            {
                if (kvp.Key.Contains("inmate"))
                {
                    ModLogger.Info($"✓ Using cached inmate appearance for {firstName}");
                    return kvp.Value;
                }
            }

            // Fallback to civilian
            return GetFallbackAppearance();
        }

        /// <summary>
        /// Get test NPC appearance settings
        /// </summary>
        private static object GetTestNPCAppearance(string firstName)
        {
            // For test NPCs, use any available appearance
            return GetFallbackAppearance();
        }

        /// <summary>
        /// Get fallback appearance when no specific type is available
        /// Uses BasicAvatarSettings pattern as last resort
        /// </summary>
        private static object GetFallbackAppearance()
        {
            if (avatarSettingsCache.ContainsKey("fallback"))
            {
                ModLogger.Info("✓ Using cached fallback appearance");
                return avatarSettingsCache["fallback"];
            }

            // Try to get any available appearance
            foreach (var kvp in avatarSettingsCache)
            {
                ModLogger.Info($"✓ Using available cached appearance: {kvp.Key}");
                return kvp.Value;
            }

            // Last resort: create basic avatar settings using game's pattern
            ModLogger.Warn("⚠️ No cached appearances available, creating basic avatar settings");
            return CreateBasicAvatarSettings(BaseNPCSpawner.NPCRole.TestNPC);
        }

        /// <summary>
        /// Create basic avatar settings if no cached ones are available
        /// This is a last resort to prevent marshmallow appearance
        /// Uses the proven BasicAvatarSettings pattern from the game
        /// </summary>
        private static object CreateBasicAvatarSettings(BaseNPCSpawner.NPCRole role)
        {
            try
            {
                ModLogger.Info($"🎨 Creating basic avatar settings for {role} using game's pattern");

#if !MONO
                var settings = ScriptableObject.CreateInstance<Il2CppScheduleOne.AvatarFramework.AvatarSettings>();
#else
                var settings = ScriptableObject.CreateInstance<ScheduleOne.AvatarFramework.AvatarSettings>();
#endif

                // Use game's proven BasicAvatarSettings pattern
                bool male = UnityEngine.Random.Range(0f, 1f) > 0.3f;
                int gender = male ? 0 : 1;

                // Basic physical attributes (following BasicAvatarSettings.GetAvatarSettings())
                settings.Gender = (float)gender * 0.7f; // GENDER_MULTIPLIER from BasicAvatarSettings
                settings.Weight = UnityEngine.Random.Range(-0.3f, 0.3f);
                settings.Height = 1f;

                // Skin tone
                Color[] skinTones = {
                    new Color(0.95f, 0.85f, 0.75f), // Light
                    new Color(0.85f, 0.75f, 0.65f), // Medium light
                    new Color(0.75f, 0.65f, 0.55f), // Medium
                    new Color(0.65f, 0.55f, 0.45f), // Medium dark
                    new Color(0.45f, 0.35f, 0.25f)  // Dark
                };
                settings.SkinColor = skinTones[UnityEngine.Random.Range(0, skinTones.Length)];

                // Hair
                Color[] hairColors = {
                    Color.black,
                    new Color(0.3f, 0.2f, 0.1f), // Dark brown
                    new Color(0.5f, 0.3f, 0.1f), // Medium brown
                    new Color(0.8f, 0.6f, 0.2f), // Light brown
                    new Color(0.9f, 0.8f, 0.3f)  // Blonde
                };
                settings.HairColor = hairColors[UnityEngine.Random.Range(0, hairColors.Length)];
                settings.HairPath = "Hair/Short_01"; // Basic hair path

                // Essential face layers (following BasicAvatarSettings pattern)
                settings.FaceLayerSettings.Clear();

                // Add basic mouth (essential for face)
                settings.FaceLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = "Avatar/Layers/Face/Mouth_01",
                    layerTint = Color.black
                });

                // Add eye shadow (from BasicAvatarSettings)
                settings.FaceLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = "Avatar/Layers/Face/EyeShadow",
                    layerTint = new Color(0f, 0f, 0f, 0.7f)
                });

                // Essential body layers (following BasicAvatarSettings pattern)
                settings.BodyLayerSettings.Clear();

                // Add nipples layer (essential for body appearance)
                settings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = "Avatar/Layers/Top/Nipples",
                    layerTint = new Color32(212, 181, 142, 255)
                });

                // Add underwear (essential - from BasicAvatarSettings constants)
                string underwearPath = male ? "Avatar/Layers/Bottom/MaleUnderwear" : "Avatar/Layers/Bottom/FemaleUnderwear";
                settings.BodyLayerSettings.Add(new
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = underwearPath,
                    layerTint = Color.white
                });

                // Add basic clothing based on role
                switch (role)
                {
                    case BaseNPCSpawner.NPCRole.PrisonGuard:
                    case BaseNPCSpawner.NPCRole.IntakeOfficer:
                        // Add guard uniform - shirt
                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Top/Shirt_Police",
                            layerTint = new Color(0.2f, 0.3f, 0.5f) // Dark blue
                        });

                        // Add guard pants
                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Bottom/Pants",
                            layerTint = new Color(0.15f, 0.15f, 0.2f) // Dark blue/black
                        });

                        // Add shoes
                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Bottom/Shoes",
                            layerTint = Color.black
                        });
                        break;

                    case BaseNPCSpawner.NPCRole.PrisonInmate:
                        // Add orange jumpsuit top
                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Top/T-Shirt",
                            layerTint = new Color(1f, 0.5f, 0f) // Orange
                        });

                        // Add orange jumpsuit pants
                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Bottom/Pants",
                            layerTint = new Color(1f, 0.5f, 0f) // Orange
                        });

                        // Add basic shoes
                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Bottom/Shoes",
                            layerTint = new Color(0.3f, 0.3f, 0.3f) // Gray
                        });
                        break;

                    default:
                        // Generic clothing for test NPCs
                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Top/Shirt",
                            layerTint = Color.white
                        });

                        settings.BodyLayerSettings.Add(new
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                            ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                        {
                            layerPath = "Avatar/Layers/Bottom/Jeans",
                            layerTint = new Color(0.3f, 0.3f, 0.5f) // Denim blue
                        });
                        break;
                }

                // Eye settings (from BasicAvatarSettings)
                settings.EyeBallTint = Color.blue;
                settings.LeftEyeLidColor = settings.SkinColor;
                settings.RightEyeLidColor = settings.SkinColor;
                settings.EyeballMaterialIdentifier = "Default";
                settings.PupilDilation = 1f;

                // Eye lid configuration
#if !MONO
                var eyeConfig = new Il2CppScheduleOne.AvatarFramework.Eye.EyeLidConfiguration();
#else
                var eyeConfig = new ScheduleOne.AvatarFramework.Eye.EyeLidConfiguration();
#endif
                eyeConfig.topLidOpen = 0.8f;
                eyeConfig.bottomLidOpen = 0.2f;
                settings.LeftEyeRestingState = eyeConfig;
                settings.RightEyeRestingState = eyeConfig;

                // Eyebrow settings
                settings.EyebrowScale = 1f;
                settings.EyebrowThickness = 1f;
                settings.EyebrowRestingHeight = 0f;
                settings.EyebrowRestingAngle = 0f;

                // Initialize accessories (empty for basic)
                settings.AccessorySettings.Clear();

                ModLogger.Info($"✓ Created basic avatar settings for {role} (male: {male}) with essential layers");
                return settings;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating basic avatar settings: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Apply appearance customizations specific to jail NPCs
        /// </summary>
        public static void ApplyJailCustomizations(GameObject npcInstance, BaseNPCSpawner.NPCRole role)
        {
            try
            {
                ModLogger.Info($"🎨 Applying jail customizations to {npcInstance.name}");

#if !MONO
                var avatar = npcInstance.GetComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
                if (avatar == null)
                {
                    avatar = npcInstance.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Avatar>();
                }
#else
                var avatar = npcInstance.GetComponent<ScheduleOne.AvatarFramework.Avatar>();
                if (avatar == null)
                {
                    avatar = npcInstance.GetComponentInChildren<ScheduleOne.AvatarFramework.Avatar>();
                }
#endif

                if (avatar == null)
                {
                    ModLogger.Warn($"⚠️ No Avatar component found for customization on {npcInstance.name}");
                    return;
                }

                // Role-specific customizations
                switch (role)
                {
                    case BaseNPCSpawner.NPCRole.PrisonGuard:
                    case BaseNPCSpawner.NPCRole.IntakeOfficer:
                        ApplyGuardCustomizations(avatar);
                        break;

                    case BaseNPCSpawner.NPCRole.PrisonInmate:
                        ApplyInmateCustomizations(avatar);
                        break;
                }

                ModLogger.Info($"✓ Jail customizations applied to {npcInstance.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying jail customizations: {e.Message}");
            }
        }

        /// <summary>
        /// Apply guard-specific visual customizations
        /// </summary>
        private static void ApplyGuardCustomizations(object avatar)
        {
            // Guard customizations would go here
            // For now, just ensure the avatar is properly configured
            ModLogger.Debug("Guard customizations applied");
        }

        /// <summary>
        /// Apply inmate-specific visual customizations
        /// </summary>
        private static void ApplyInmateCustomizations(object avatar)
        {
            // Inmate customizations would go here
            // For now, just ensure the avatar is properly configured
            ModLogger.Debug("Inmate customizations applied");
        }

        /// <summary>
        /// Clear the appearance cache (useful for testing)
        /// </summary>
        public static void ClearCache()
        {
            avatarSettingsCache.Clear();
            cacheInitialized = false;
            ModLogger.Info("🎨 Appearance cache cleared");
        }

        /// <summary>
        /// Get cache status for debugging
        /// </summary>
        public static string GetCacheStatus()
        {
            return $"Cache initialized: {cacheInitialized}, Cached settings: {avatarSettingsCache.Count}";
        }
    }
}