using System;
using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.AvatarFramework;
using ScheduleOne.NPCs;
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
        /// </summary>
        /// <param name="role">The role of the NPC</param>
        /// <param name="firstName">First name for variation</param>
        /// <returns>Avatar settings object or null</returns>
        public static object GetAppearanceForRole(BaseNPCSpawner.NPCRole role, string firstName)
        {
            try
            {
                // Initialize cache if needed
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
        /// </summary>
        private static object GetFallbackAppearance()
        {
            if (avatarSettingsCache.ContainsKey("fallback"))
            {
                ModLogger.Info("✓ Using fallback appearance");
                return avatarSettingsCache["fallback"];
            }

            // Try to get any available appearance
            foreach (var kvp in avatarSettingsCache)
            {
                ModLogger.Info($"✓ Using available appearance: {kvp.Key}");
                return kvp.Value;
            }

            ModLogger.Warn("⚠️ No cached appearance settings available - NPC may appear as marshmallow");
            return null;
        }

        /// <summary>
        /// Create basic avatar settings if no cached ones are available
        /// This is a last resort to prevent marshmallow appearance
        /// </summary>
        private static object CreateBasicAvatarSettings(BaseNPCSpawner.NPCRole role)
        {
            try
            {
                ModLogger.Info($"🎨 Creating basic avatar settings for {role}");

#if !MONO
                var settings = ScriptableObject.CreateInstance<Il2CppScheduleOne.AvatarFramework.AvatarSettings>();
#else
                var settings = ScriptableObject.CreateInstance<ScheduleOne.AvatarFramework.AvatarSettings>();
#endif

                // Set basic appearance parameters
                switch (role)
                {
                    case BaseNPCSpawner.NPCRole.PrisonGuard:
                    case BaseNPCSpawner.NPCRole.IntakeOfficer:
                        // Guard appearance: professional, clean-cut
                        settings.SkinColor = new Color(0.9f, 0.8f, 0.7f);
                        settings.Height = 1.0f;
                        settings.Gender = 0.8f; // More masculine
                        settings.Weight = 0.3f;
                        settings.HairColor = new Color(0.4f, 0.2f, 0.1f);
                        break;

                    case BaseNPCSpawner.NPCRole.PrisonInmate:
                        // Inmate appearance: varied, rougher
                        settings.SkinColor = new Color(0.8f, 0.7f, 0.6f);
                        settings.Height = 0.9f;
                        settings.Gender = 0.7f;
                        settings.Weight = 0.4f;
                        settings.HairColor = Color.black;
                        break;

                    default:
                        // Default civilian appearance
                        settings.SkinColor = new Color(0.85f, 0.75f, 0.65f);
                        settings.Height = 0.95f;
                        settings.Gender = 0.5f;
                        settings.Weight = 0.35f;
                        settings.HairColor = new Color(0.6f, 0.4f, 0.2f);
                        break;
                }

                ModLogger.Info($"✓ Created basic avatar settings for {role}");
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