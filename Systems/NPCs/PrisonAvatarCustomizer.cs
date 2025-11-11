using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Behind_Bars.Helpers;
using MelonLoader;

#if !MONO
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Clothing;
using Il2CppScheduleOne.Tools;
using Il2CppInterop.Runtime.Attributes;
#else
using ScheduleOne.AvatarFramework;
using ScheduleOne.NPCs;
using ScheduleOne.Clothing;
using ScheduleOne.Tools;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Handles avatar customization for prison NPCs with realistic appearances
    /// </summary>
    public static class PrisonAvatarCustomizer
    {
        private static List<Color> SkinTones = new List<Color>
        {
            new Color(0.95f, 0.85f, 0.75f), // Light
            new Color(0.85f, 0.75f, 0.65f), // Medium light
            new Color(0.75f, 0.65f, 0.55f), // Medium
            new Color(0.65f, 0.55f, 0.45f), // Medium dark
            new Color(0.45f, 0.35f, 0.25f)  // Dark
        };

        private static List<Color> HairColors = new List<Color>
        {
            Color.black,                        // Black
            new Color(0.3f, 0.2f, 0.1f),      // Dark brown
            new Color(0.5f, 0.3f, 0.1f),      // Medium brown
            new Color(0.8f, 0.6f, 0.2f),      // Light brown
            new Color(0.9f, 0.8f, 0.3f),      // Blonde
            new Color(0.5f, 0.5f, 0.5f),      // Gray
            new Color(0.8f, 0.4f, 0.2f)       // Auburn
        };

        /// <summary>
        /// Apply prison guard appearance to an NPC
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public static void ApplyGuardAppearance(GameObject npc, string badgeNumber = "")
        {
            try
            {
                ModLogger.Info($"Applying guard appearance to {npc.name}");

                var avatar = GetOrCreateAvatar(npc);
                if (avatar == null)
                {
                    ModLogger.Error($"Could not get avatar for guard {npc.name}");
                    return;
                }

                // Create guard-specific avatar settings
                var settings = CreateGuardAvatarSettings(badgeNumber);
                if (settings != null)
                {
                    // Apply the settings to the avatar
                    ApplyAvatarSettings(avatar, settings);
                    
                    // Apply guard uniform
                    ApplyGuardClothing(npc, avatar);
                    
                    ModLogger.Info($"✓ Guard appearance applied to {npc.name} (Badge: {badgeNumber})");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying guard appearance to {npc.name}: {e.Message}");
            }
        }

        /// <summary>
        /// Apply prison inmate appearance to an NPC
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        public static void ApplyInmateAppearance(GameObject npc, string prisonerID = "", string crimeType = "")
        {
            try
            {
                ModLogger.Info($"Applying inmate appearance to {npc.name}");

                var avatar = GetOrCreateAvatar(npc);
                if (avatar == null)
                {
                    ModLogger.Error($"Could not get avatar for inmate {npc.name}");
                    return;
                }

                // Create inmate-specific avatar settings
                var settings = CreateInmateAvatarSettings(prisonerID, crimeType);
                if (settings != null)
                {
                    // Apply the settings to the avatar
                    ApplyAvatarSettings(avatar, settings);
                    
                    // Apply prison uniform
                    ApplyInmateClothing(npc, avatar);
                    
                    ModLogger.Info($"✓ Inmate appearance applied to {npc.name} (ID: {prisonerID}, Crime: {crimeType})");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying inmate appearance to {npc.name}: {e.Message}");
            }
        }

        /// <summary>
        /// Get existing avatar or create a basic one
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private static
#if !MONO
            Il2CppScheduleOne.AvatarFramework.Avatar
#else
            ScheduleOne.AvatarFramework.Avatar
#endif
            GetOrCreateAvatar(GameObject npc)
        {
            // Try to find existing avatar
            var avatar = npc.GetComponentInChildren<
#if !MONO
                Il2CppScheduleOne.AvatarFramework.Avatar
#else
                ScheduleOne.AvatarFramework.Avatar
#endif
            >();

            if (avatar == null)
            {
                avatar = npc.GetComponent<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.Avatar
#else
                    ScheduleOne.AvatarFramework.Avatar
#endif
                >();
            }

            // If still no avatar, try to find an Avatar child object
            if (avatar == null)
            {
                var avatarChild = npc.transform.Find("Avatar");
                if (avatarChild != null)
                {
                    avatar = avatarChild.GetComponent<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Avatar
#else
                        ScheduleOne.AvatarFramework.Avatar
#endif
                    >();
                }
            }

            if (avatar == null)
            {
                ModLogger.Warn($"No avatar found on {npc.name}, DirectNPCBuilder should have created one");
                return null;
            }

            return avatar;
        }

        /// <summary>
        /// Create avatar settings for prison guards
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private static
#if !MONO
            Il2CppScheduleOne.AvatarFramework.AvatarSettings
#else
            ScheduleOne.AvatarFramework.AvatarSettings
#endif
            CreateGuardAvatarSettings(string badgeNumber)
        {
            try
            {
                var settings = ScriptableObject.CreateInstance<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings
#else
                    ScheduleOne.AvatarFramework.AvatarSettings
#endif
                >();

                // Professional guard appearance
                settings.Gender = UnityEngine.Random.Range(0.2f, 0.8f); // Mix of male/female
                settings.Weight = UnityEngine.Random.Range(-0.2f, 0.3f); // Fit build
                settings.Height = UnityEngine.Random.Range(0.95f, 1.05f); // Average to tall
                
                // Random but professional skin tone
                settings.SkinColor = SkinTones[UnityEngine.Random.Range(0, SkinTones.Count)];
                
                // Professional hair colors (no wild colors)
                var professionalHairColors = new List<Color>
                {
                    Color.black,
                    new Color(0.3f, 0.2f, 0.1f),  // Dark brown
                    new Color(0.5f, 0.3f, 0.1f),  // Medium brown
                    new Color(0.5f, 0.5f, 0.5f)   // Gray
                };
                settings.HairColor = professionalHairColors[UnityEngine.Random.Range(0, professionalHairColors.Count)];

                // Set hair path to short, professional styles
                // This would need to be mapped to actual game hair asset paths
                settings.HairPath = GetProfessionalHairPath();
                
                // Set basic face/body features to avoid faceless appearance
                // Note: EyeColor property might not exist, will be handled in ApplyRandomFaceFeatures
                
                // Add some body/face variation but keep it professional
                ApplyRandomFaceFeatures(settings);

                ModLogger.Debug($"Created guard avatar settings for badge {badgeNumber}");
                return settings;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating guard avatar settings: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Create avatar settings for prison inmates
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private static
#if !MONO
            Il2CppScheduleOne.AvatarFramework.AvatarSettings
#else
            ScheduleOne.AvatarFramework.AvatarSettings
#endif
            CreateInmateAvatarSettings(string prisonerID, string crimeType)
        {
            try
            {
                var settings = ScriptableObject.CreateInstance<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings
#else
                    ScheduleOne.AvatarFramework.AvatarSettings
#endif
                >();

                // More varied inmate appearance
                settings.Gender = UnityEngine.Random.Range(0f, 1f);
                settings.Weight = UnityEngine.Random.Range(-0.5f, 0.5f); // Full range
                settings.Height = UnityEngine.Random.Range(0.85f, 1.15f); // Wide range
                
                // Random skin tone
                settings.SkinColor = SkinTones[UnityEngine.Random.Range(0, SkinTones.Count)];
                
                // Any hair color
                settings.HairColor = HairColors[UnityEngine.Random.Range(0, HairColors.Count)];

                // Hair styles can be more varied for inmates
                settings.HairPath = GetInmateHairPath(crimeType);
                
                // Set basic face/body features to avoid faceless appearance  
                // Note: EyeColor property might not exist, will be handled in ApplyRandomFaceFeatures
                
                // Add some body/face variation
                ApplyRandomFaceFeatures(settings);

                ModLogger.Debug($"Created inmate avatar settings for prisoner {prisonerID} ({crimeType})");
                return settings;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating inmate avatar settings: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Apply avatar settings to an avatar component
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void ApplyAvatarSettings(
#if !MONO
            Il2CppScheduleOne.AvatarFramework.Avatar avatar,
            Il2CppScheduleOne.AvatarFramework.AvatarSettings settings
#else
            ScheduleOne.AvatarFramework.Avatar avatar,
            ScheduleOne.AvatarFramework.AvatarSettings settings
#endif
        )
        {
            try
            {
                if (avatar == null || settings == null)
                {
                    ModLogger.Error("Avatar or settings is null in ApplyAvatarSettings");
                    return;
                }

                // Apply the avatar settings
                avatar.LoadAvatarSettings(settings);
                
                // Fix SmoothVelocityCalculator animation speeds
                FixSmoothVelocityCalculator(avatar.gameObject);
                
                ModLogger.Debug($"Avatar settings applied to {avatar.gameObject.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying avatar settings: {e.Message}");
            }
        }

        /// <summary>
        /// Fix the SmoothVelocityCalculator animation speed settings
        /// </summary>
        private static void FixSmoothVelocityCalculator(GameObject npc)
        {
            try
            {
#if !MONO
                var smoothVelocity = npc.GetComponent<Il2CppScheduleOne.Tools.SmoothedVelocityCalculator>();
#else
                var smoothVelocity = npc.GetComponent<ScheduleOne.Tools.SmoothedVelocityCalculator>();
#endif

                if (smoothVelocity != null)
                {
                    // Fix animation speed mapping:
                    // At 2.5-3 speed -> should be 0.5 animation speed  
                    // At 5.5-6 speed -> should be 1.0 animation speed
                    
                    // Configure SmoothedVelocityCalculator parameters based on actual component
                    ModLogger.Info($"Found SmoothedVelocityCalculator on {npc.name}, applying speed fixes");
                    
                    // Adjust sampling parameters for better animation speed mapping
                    smoothVelocity.SampleLength = 0.1f;           // Faster sampling for responsive animation
                    smoothVelocity.MaxReasonableVelocity = 15f;   // Lower max velocity for more accurate mapping
                    
                    ModLogger.Debug($"✓ SmoothVelocityCalculator configured for {npc.name}");
                }
                else
                {
                    ModLogger.Debug($"No SmoothVelocityCalculator found on {npc.name}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error fixing SmoothVelocityCalculator: {e.Message}");
            }
        }

        /// <summary>
        /// Apply guard uniform by copying from existing Officer NPC
        /// </summary>
        private static void ApplyGuardClothing(GameObject npc, 
#if !MONO
            Il2CppScheduleOne.AvatarFramework.Avatar avatar
#else
            ScheduleOne.AvatarFramework.Avatar avatar
#endif
        )
        {
            try
            {
                // Debug: List all available NPCs to understand what we have
                var allAvailableNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >()
                    .Where(npcComp => npcComp != null && npcComp.gameObject != npc)
                    .Take(20)
                    .ToArray();
                    
                ModLogger.Info($"Available NPCs for guard appearance: {string.Join(", ", allAvailableNPCs.Select(n => n.name))}");
                
                // Find existing NPCs that might be officers (broader search)
                var officerNPCs = allAvailableNPCs
                    .Where(npcComp => npcComp.name.Contains("Officer") || 
                                      npcComp.name.Contains("Police") || 
                                      npcComp.name.Contains("Cop"))
                    .Select(npcComp => npcComp.gameObject)
                    .ToArray();
                
                GameObject sourceNPC = null;
                
                if (officerNPCs.Length > 0)
                {
                    sourceNPC = officerNPCs[UnityEngine.Random.Range(0, officerNPCs.Length)];
                    ModLogger.Info($"Found {officerNPCs.Length} officer NPCs, using: {sourceNPC.name} for guard diversity");
                }
                else
                {
                    ModLogger.Warn("No Officer NPCs found, using diverse civilian NPCs for guard appearance");
                    // Fallback: find any well-dressed civilian NPC for diversity
                    var civilianNPCs = allAvailableNPCs
                        .Where(npcComp => !npcComp.name.StartsWith("JailGuard") && 
                                          !npcComp.name.StartsWith("JailInmate") &&
                                          !npcComp.name.Contains("Arms") &&
                                          !npcComp.name.Contains("Dealer") &&
                                          !npcComp.name.Contains("Cartel"))
                        .Select(npcComp => npcComp.gameObject)
                        .ToArray();
                        
                    if (civilianNPCs.Length == 0)
                    {
                        ModLogger.Error("No suitable NPCs found to copy appearance from");
                        return;
                    }
                    
                    sourceNPC = civilianNPCs[UnityEngine.Random.Range(0, civilianNPCs.Length)];
                    ModLogger.Info($"Using diverse civilian NPC for guard appearance: {sourceNPC.name} (total available: {civilianNPCs.Length})");
                }
                
                // Find the Avatar component on the source NPC
                var sourceAvatar = sourceNPC.GetComponentInChildren<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.Avatar
#else
                    ScheduleOne.AvatarFramework.Avatar
#endif
                >();
                
                if (sourceAvatar?.CurrentSettings == null)
                {
                    ModLogger.Error($"Source NPC {sourceNPC.name} has no valid avatar settings, guard {npc.name} will keep Billy's appearance");
                    return;
                }
                
                // Copy the ENTIRE avatar settings from the source NPC
                var copiedSettings = UnityEngine.Object.Instantiate(sourceAvatar.CurrentSettings);
                avatar.LoadAvatarSettings(copiedSettings);
                
                ModLogger.Info($"✓ Copied complete appearance from {sourceNPC.name} to guard {npc.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error copying guard appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Apply inmate appearance by finding and copying Billy's avatar
        /// </summary>
        private static void ApplyInmateClothing(GameObject npc,
#if !MONO
            Il2CppScheduleOne.AvatarFramework.Avatar avatar
#else
            ScheduleOne.AvatarFramework.Avatar avatar
#endif
        )
        {
            try
            {
                ModLogger.Info($"Setting up inmate appearance for {npc.name} using working civilian references");
                
                // Skip Billy - use any civilian NPC that has working CurrentSettings
                // Priority list of NPCs known to work (from previous successful runs)
                string[] knownWorkingNPCs = { "Chris", "Thomas", "Jeff", "Doris", "Brad", "Kevin", "Keith", "Jack", "Marco" };
                
                foreach (var npcName in knownWorkingNPCs)
                {
                    var workingNPC = FindSpecificNPC(npcName);
                    if (workingNPC != null)
                    {
                        var workingAvatar = workingNPC.GetComponentInChildren<
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.Avatar
#else
                            ScheduleOne.AvatarFramework.Avatar
#endif
                        >();
                        
                        if (workingAvatar?.CurrentSettings != null)
                        {
                            // Copy working civilian appearance
                            var inmateSettings = UnityEngine.Object.Instantiate(workingAvatar.CurrentSettings);
                            avatar.LoadAvatarSettings(inmateSettings);
                            
                            ModLogger.Info($"✓ Applied working civilian appearance from {workingNPC.name} to inmate {npc.name}");
                            return;
                        }
                    }
                }
                
                // Fallback: Find any civilian NPC and use their appearance
                var fallbackNPC = FindCivilianNPC(npc);
                if (fallbackNPC != null)
                {
                    var fallbackAvatar = fallbackNPC.GetComponentInChildren<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Avatar
#else
                        ScheduleOne.AvatarFramework.Avatar
#endif
                    >();
                    
                    if (fallbackAvatar?.CurrentSettings != null)
                    {
                        var fallbackSettings = UnityEngine.Object.Instantiate(fallbackAvatar.CurrentSettings);
                        avatar.LoadAvatarSettings(fallbackSettings);
                        
                        ModLogger.Info($"✓ Applied fallback civilian appearance from {fallbackNPC.name} to inmate {npc.name}");
                        return;
                    }
                }
                
                ModLogger.Error($"Could not find any suitable reference NPC for inmate {npc.name} - inmate will appear naked/faceless!");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying inmate clothing: {e.Message}");
            }
        }



        /// <summary>
        /// Get professional hair path for guards
        /// </summary>
        private static string GetProfessionalHairPath()
        {
            // These would need to be mapped to actual game hair asset paths
            var professionalHairs = new string[]
            {
                "Hair/Short_Professional_01",
                "Hair/Short_Professional_02", 
                "Hair/Buzzcut_01",
                "Hair/Short_Clean_01"
            };
            
            return professionalHairs[UnityEngine.Random.Range(0, professionalHairs.Length)];
        }

        /// <summary>
        /// Get hair path for inmates based on crime type
        /// </summary>
        private static string GetInmateHairPath(string crimeType)
        {
            // More varied hair styles for inmates
            var inmateHairs = new string[]
            {
                "Hair/Short_Messy_01",
                "Hair/Medium_Unkempt_01",
                "Hair/Buzzcut_Rough_01",
                "Hair/Long_Disheveled_01",
                "Hair/Shaved_01"
            };
            
            return inmateHairs[UnityEngine.Random.Range(0, inmateHairs.Length)];
        }

        /// <summary>
        /// Get random eye color
        /// </summary>
        private static Color GetRandomEyeColor()
        {
            var eyeColors = new Color[]
            {
                new Color(0.4f, 0.2f, 0.1f), // Brown
                new Color(0.2f, 0.4f, 0.8f), // Blue  
                new Color(0.3f, 0.6f, 0.3f), // Green
                new Color(0.3f, 0.3f, 0.3f), // Gray
                new Color(0.1f, 0.1f, 0.1f)  // Dark brown/black
            };
            return eyeColors[UnityEngine.Random.Range(0, eyeColors.Length)];
        }

        /// <summary>
        /// Apply random face features to avoid faceless appearance using actual AvatarSettings properties
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void ApplyRandomFaceFeatures(
#if !MONO
            Il2CppScheduleOne.AvatarFramework.AvatarSettings settings
#else
            ScheduleOne.AvatarFramework.AvatarSettings settings
#endif
        )
        {
            try
            {
                // Configure eye properties based on actual AvatarSettings structure
                settings.EyebrowScale = UnityEngine.Random.Range(0.8f, 1.2f);
                settings.EyebrowThickness = UnityEngine.Random.Range(0.7f, 1.3f);
                settings.EyebrowRestingHeight = UnityEngine.Random.Range(-0.1f, 0.1f);
                settings.EyebrowRestingAngle = UnityEngine.Random.Range(-5f, 5f);
                
                // Set eye colors for left and right eyelids
                var eyeColor = GetRandomEyeColor();
                settings.LeftEyeLidColor = eyeColor;
                settings.RightEyeLidColor = eyeColor;
                
                // Set eyeball material and tint
                settings.EyeballMaterialIdentifier = "Default"; // Default eye material
                settings.EyeBallTint = eyeColor;
                settings.PupilDilation = UnityEngine.Random.Range(0.3f, 0.7f);
                
                // Add some random face layers for variation (scars, freckles, etc.)
                AddRandomFaceLayers(settings);
                
                // Add some body layers for clothing/tattoos  
                AddRandomBodyLayers(settings);
                
                ModLogger.Debug("Applied realistic face features using AvatarSettings properties");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error applying face features: {e.Message}");
            }
        }

        /// <summary>
        /// Add random face layers for character variation
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void AddRandomFaceLayers(
#if !MONO
            Il2CppScheduleOne.AvatarFramework.AvatarSettings settings
#else
            ScheduleOne.AvatarFramework.AvatarSettings settings
#endif
        )
        {
            // Clear existing face layers
            settings.FaceLayerSettings.Clear();
            
            // Add some random face features
            if (UnityEngine.Random.Range(0f, 1f) < 0.3f) // 30% chance of facial hair
            {
                settings.FaceLayerSettings.Add(new 
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = "Face/FacialHair/Beard_01",
                    layerTint = settings.HairColor
                });
            }
            
            if (UnityEngine.Random.Range(0f, 1f) < 0.2f) // 20% chance of scars/marks
            {
                settings.FaceLayerSettings.Add(new 
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                    ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
                {
                    layerPath = "Face/Scars/Scar_01",
                    layerTint = Color.white
                });
            }
        }

        /// <summary>
        /// Add basic body layers (will be overridden by clothing application)
        /// </summary>
#if !MONO
        [HideFromIl2Cpp]
#endif
        private static void AddRandomBodyLayers(
#if !MONO
            Il2CppScheduleOne.AvatarFramework.AvatarSettings settings
#else
            ScheduleOne.AvatarFramework.AvatarSettings settings
#endif
        )
        {
            // Initialize empty - clothing will be applied separately
            settings.BodyLayerSettings.Clear();
            settings.AccessorySettings.Clear();
            
            // Add basic skin layer
            settings.BodyLayerSettings.Add(new 
#if !MONO
                Il2CppScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#else
                ScheduleOne.AvatarFramework.AvatarSettings.LayerSetting
#endif
            {
                layerPath = "Body/Skin/Basic",
                layerTint = settings.SkinColor
            });
        }

        /// <summary>
        /// Search for hazmat suit in all NPCs in the world by looking for AvatarFramework.Accessory components
        /// </summary>
        private static GameObject FindHazmatSuitInWorld()
        {
            try
            {
                ModLogger.Info("Searching for hazmat suit in world...");
                
                // Find all AvatarFramework.Accessory components in the scene
                var allAccessories = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.Accessory
#else
                    ScheduleOne.AvatarFramework.Accessory
#endif
                >();
                
                ModLogger.Info($"Found {allAccessories.Length} total accessories in world");
                
                foreach (var accessory in allAccessories)
                {
                    if (accessory?.gameObject?.name != null)
                    {
                        var name = accessory.gameObject.name.ToLower();
                        if (name.Contains("hazmat") || name.Contains("suit"))
                        {
                            ModLogger.Info($"✓ Found potential hazmat suit: {accessory.gameObject.name}");
                            return accessory.gameObject;
                        }
                    }
                }
                
                // If no hazmat found by name, look for any accessory that might be clothing
                ModLogger.Warn("No hazmat suit found by name, looking for any body/torso accessory");
                foreach (var accessory in allAccessories)
                {
                    if (accessory?.gameObject?.name != null)
                    {
                        var name = accessory.gameObject.name.ToLower();
                        if (name.Contains("body") || name.Contains("torso") || name.Contains("chest") || 
                            name.Contains("shirt") || name.Contains("jacket"))
                        {
                            ModLogger.Info($"✓ Using clothing accessory as fallback: {accessory.gameObject.name}");
                            return accessory.gameObject;
                        }
                    }
                }
                
                ModLogger.Error("No suitable clothing accessory found in world");
                return null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error searching for hazmat suit: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clear all clothing items from a BodyContainer
        /// </summary>
        private static void ClearBodyContainerClothing(Transform bodyContainer)
        {
            try
            {
                var existingItems = bodyContainer.GetComponentsInChildren<GameObject>();
                int clearedCount = 0;
                
                foreach (var item in existingItems)
                {
                    if (item != bodyContainer.gameObject)
                    {
                        // Check if it's clothing/accessory by looking for Accessory component
                        var accessory = item.GetComponent<
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.Accessory
#else
                            ScheduleOne.AvatarFramework.Accessory
#endif
                        >();
                        
                        if (accessory != null)
                        {
                            UnityEngine.Object.DestroyImmediate(item);
                            clearedCount++;
                        }
                    }
                }
                
                ModLogger.Info($"✓ Cleared {clearedCount} clothing items from BodyContainer");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error clearing BodyContainer clothing: {e.Message}");
            }
        }

        /// <summary>
        /// Find Billy NPC in the world to use as inmate reference
        /// </summary>
        private static GameObject FindBillyNPC()
        {
            try
            {
                var allNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >();
                
                // Look for Billy specifically
                foreach (var npcComp in allNPCs)
                {
                    if (npcComp?.gameObject?.name != null)
                    {
                        var name = npcComp.gameObject.name.ToLower();
                        if (name.Contains("billy") || name.Contains("kramer") || name.Contains("billy_kramer"))
                        {
                            ModLogger.Info($"✓ Found Billy NPC: {npcComp.gameObject.name}");
                            return npcComp.gameObject;
                        }
                    }
                }
                
                ModLogger.Warn("Billy NPC not found in world");
                return null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error finding Billy NPC: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Find a specific NPC by name
        /// </summary>
        private static GameObject FindSpecificNPC(string targetName)
        {
            try
            {
                var allNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >();
                
                foreach (var npcComp in allNPCs)
                {
                    if (npcComp?.gameObject?.name != null && npcComp.gameObject.name.Equals(targetName))
                    {
                        return npcComp.gameObject;
                    }
                }
                
                return null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error finding specific NPC {targetName}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Find a reliable male NPC for inmate appearance
        /// </summary>
        private static GameObject FindReliableMaleNPC()
        {
            try
            {
                var allNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >();
                
                // Priority list of reliable male NPCs for inmate appearance
                string[] preferredMales = { "Brad", "Kevin", "Keith", "Jack", "Marco", "Stan", "Tobias" };
                
                foreach (var preferredName in preferredMales)
                {
                    foreach (var npcComp in allNPCs)
                    {
                        if (npcComp?.gameObject?.name != null && npcComp.gameObject.name.Equals(preferredName))
                        {
                            ModLogger.Info($"✓ Found reliable male NPC: {npcComp.gameObject.name}");
                            return npcComp.gameObject;
                        }
                    }
                }
                
                // Fallback: find any male civilian
                foreach (var npcComp in allNPCs)
                {
                    if (npcComp?.gameObject?.name != null)
                    {
                        var name = npcComp.gameObject.name;
                        if (!name.Contains("Officer") && !name.Contains("Police") && 
                            !name.StartsWith("JailGuard") && !name.StartsWith("JailInmate") &&
                            !name.StartsWith("Player") && 
                            // Assume these are male based on common names
                            (name.Contains("Brad") || name.Contains("Kevin") || name.Contains("Keith") || 
                             name.Contains("Jack") || name.Contains("Marco") || name.Contains("Stan") || 
                             name.Contains("Tobias") || name.Contains("Jeff") || name.Contains("Chris") || 
                             name.Contains("Thomas") || name.Contains("Billy")))
                        {
                            ModLogger.Info($"✓ Found male civilian NPC: {npcComp.gameObject.name}");
                            return npcComp.gameObject;
                        }
                    }
                }
                
                ModLogger.Warn("No reliable male NPCs found for inmate appearance");
                return null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error finding reliable male NPC: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Find a suitable civilian NPC for fallback appearance
        /// </summary>
        private static GameObject FindCivilianNPC(GameObject excludeNPC)
        {
            try
            {
                var allNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >()
                    .Where(npcComp => npcComp != null && npcComp.gameObject != excludeNPC)
                    .Where(npcComp => !npcComp.name.Contains("Officer") &&
                                      !npcComp.name.Contains("Police") && 
                                      !npcComp.name.Contains("Cop") &&
                                      !npcComp.name.Contains("Arms") &&
                                      !npcComp.name.Contains("Dealer") &&
                                      !npcComp.name.Contains("Cartel") &&
                                      !npcComp.name.Contains("Supplier") &&
                                      !npcComp.name.StartsWith("JailGuard") && 
                                      !npcComp.name.StartsWith("JailInmate") &&
                                      !npcComp.name.StartsWith("Player"))
                    .Select(npcComp => npcComp.gameObject)
                    .ToArray();
                
                if (allNPCs.Length > 0)
                {
                    var selectedNPC = allNPCs[UnityEngine.Random.Range(0, allNPCs.Length)];
                    ModLogger.Info($"✓ Found civilian NPC: {selectedNPC.name}");
                    return selectedNPC;
                }
                
                ModLogger.Warn("No suitable civilian NPCs found");
                return null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error finding civilian NPC: {e.Message}");
                return null;
            }
        }

    }
}