using System;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using HarmonyLib;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Equipping;
#else
using ScheduleOne.ItemFramework;
using ScheduleOne;
using ScheduleOne.Equipping;
#endif

namespace Behind_Bars.Systems.Jail
{
    /// <summary>
    /// Custom equippable component for prison items that shows them properly when held
    /// Based on CSEC mod's ModuleEquippable implementation
    /// </summary>
    public class PrisonItemEquippable : Equippable_Viewmodel
    {
        public override void Equip(ItemInstance item)
        {
            base.Equip(item);
            
            // Set transform to show the item properly in the player's hand
            // These values are similar to CSEC's ModuleEquippable positioning
            base.transform.localPosition = new Vector3(0.2f, -0.15f, 0.25f);
            base.transform.localEulerAngles = new Vector3(0f, 45f, 0f);
            base.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            
            ModLogger.Info($"Prison item equipped: {item?.ID}");
        }
        
        public override void Unequip()
        {
            ModLogger.Info($"Prison item unequipped");
            base.Unequip();
        }
    }

    /// <summary>
    /// Registers prison items with Schedule I's item framework so they can exist in player inventory
    /// </summary>
    [HarmonyPatch(typeof(Registry), "_GetItem")]
    public static class PrisonItemRegistry
    {
        private static bool itemsRegistered = false;
        
        // Prison item definitions
        private static readonly Dictionary<string, PrisonItemInfo> PrisonItems = new Dictionary<string, PrisonItemInfo>
        {
            ["behindbars.bedroll"] = new PrisonItemInfo
            {
                id = "behindbars.bedroll",
                name = "Prison Bed Roll",
                description = "A basic sleeping mat provided to inmates",
                category = EItemCategory.Consumable,
                iconResourcePath = "Behind_Bars.Icons.behindbars.bedroll",
                prefabName = "assets/behindbars/bedroll.prefab"
            },
            ["behindbars.sheetsnpillows"] = new PrisonItemInfo
            {
                id = "behindbars.sheetsnpillows", 
                name = "Prison Sheets & Pillow",
                description = "Basic bedding provided to inmates",
                category = EItemCategory.Consumable,
                iconResourcePath = "Behind_Bars.Icons.behindbars.sheetsnpillows",
                prefabName = "assets/behindbars/pillowandsheets.prefab"
            },
            ["behindbars.cup"] = new PrisonItemInfo
            {
                id = "behindbars.cup",
                name = "Prison Cup", 
                description = "Standard issue drinking cup for inmates",
                category = EItemCategory.Consumable,
                iconResourcePath = "Behind_Bars.Icons.behindbars.cup",
                prefabName = "assets/behindbars/jailcup.prefab"
            },
            ["behindbars.toothbrush"] = new PrisonItemInfo
            {
                id = "behindbars.toothbrush",
                name = "Prison Toothbrush",
                description = "Basic hygiene item provided to inmates", 
                category = EItemCategory.Consumable,
                iconResourcePath = "Behind_Bars.Icons.behindbars.toothbrush",
                prefabName = "assets/behindbars/JailToothBrush.prefab"
            }
        };
        
        public static void Prefix(Registry __instance, string ID)
        {
            if (!itemsRegistered)
            {
                RegisterPrisonItems(__instance);
                itemsRegistered = true;
            }
        }
        
        private static void RegisterPrisonItems(Registry registry)
        {
            try
            {
                ModLogger.Info("Registering prison items with Schedule I item framework...");
                
                foreach (var kvp in PrisonItems)
                {
                    var itemInfo = kvp.Value;
                    
                    // Create BuildableItemDefinition for each prison item
                    var itemDef = ScriptableObject.CreateInstance<BuildableItemDefinition>();
                    itemDef.name = itemInfo.name;
                    
                    // Set basic properties using the correct API
                    itemDef.ID = itemInfo.id;
                    itemDef.Name = itemInfo.name;
                    itemDef.Description = itemInfo.description;
                    itemDef.Category = itemInfo.category;
                    
                    // Set as inventory-only item
                    itemDef.StackLimit = 1;
                    itemDef.BasePurchasePrice = 0f; // Free items
                    itemDef.ResellMultiplier = 0f; // Cannot be sold
                    
                    // Load icon from embedded resources
                    try
                    {
                        var icon = LoadIconFromResources(itemInfo.iconResourcePath);
                        if (icon != null)
                        {
                            itemDef.Icon = icon;
                            ModLogger.Info($"✓ Loaded icon for {itemInfo.name}");
                        }
                        else
                        {
                            ModLogger.Warn($"⚠ Could not load icon for {itemInfo.name} at {itemInfo.iconResourcePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"Error loading icon for {itemInfo.name}: {ex.Message}");
                    }
                    
                    // Load prefab from asset bundle (if available)
                    try
                    {
                        if (!string.IsNullOrEmpty(itemInfo.prefabName) && AssetManager.bundle != null)
                        {
#if MONO
                            var prefab = AssetManager.bundle.LoadAsset<GameObject>(itemInfo.prefabName);
#else
                            var prefab = AssetManager.bundle.LoadAsset(itemInfo.prefabName, Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                            if (prefab != null)
                            {
                                // Set up prefab for inventory/equipping
                                SetupItemPrefab(itemDef, prefab, itemInfo);
                                ModLogger.Info($"✓ Loaded prefab for {itemInfo.name}");
                            }
                            else
                            {
                                ModLogger.Warn($"⚠ Could not load prefab {itemInfo.prefabName} for {itemInfo.name}");
                            }
                        }
                        else if (string.IsNullOrEmpty(itemInfo.prefabName))
                        {
                            ModLogger.Info($"ℹ No prefab defined for {itemInfo.name} (icon-only item)");
                        }
                        else
                        {
                            ModLogger.Warn($"⚠ AssetBundle not loaded, skipping prefab for {itemInfo.name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Error($"Error loading prefab for {itemInfo.name}: {ex.Message}");
                    }
                    
                    // Create deterministic GUID from item ID
                    var guid = GenerateDeterministicGuid(itemInfo.id);
                    
                    // Add to registry
                    registry.AddToRegistry(itemDef);
                    
                    ModLogger.Info($"✓ Registered prison item: {itemInfo.name} ({itemInfo.id})");
                }
                
                ModLogger.Info($"Successfully registered {PrisonItems.Count} prison items");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error registering prison items: {ex.Message}");
            }
        }
        
        private static Guid GenerateDeterministicGuid(string input)
        {
            using (var provider = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = provider.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                return new Guid(hash);
            }
        }
        
        private static Sprite LoadIconFromResources(string resourcePath)
        {
            try
            {
                ModLogger.Info($"Loading icon from embedded resources: {resourcePath}");
                
                // Load texture from embedded resources using assembly
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourcePath + ".png"))
                {
                    if (stream != null)
                    {
                        // Read the stream into a byte array
                        byte[] imageData = new byte[stream.Length];
                        stream.Read(imageData, 0, (int)stream.Length);
                        
                        // Create texture from byte array
                        var texture = new Texture2D(2, 2);
                        if (texture.LoadImage(imageData))
                        {
                            // Convert texture to sprite
                            var sprite = Sprite.Create(
                                texture,
                                new Rect(0, 0, texture.width, texture.height),
                                new Vector2(0.5f, 0.5f),
                                100f
                            );
                            
                            ModLogger.Info($"Successfully loaded icon sprite from {resourcePath}");
                            return sprite;
                        }
                        else
                        {
                            ModLogger.Error($"Failed to load image data for {resourcePath}");
                            return null;
                        }
                    }
                    else
                    {
                        ModLogger.Warn($"Embedded resource not found: {resourcePath}.png");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error loading icon from embedded resources {resourcePath}: {ex.Message}");
                return null;
            }
        }
        
        private static void SetupItemPrefab(BuildableItemDefinition itemDef, GameObject prefab, PrisonItemInfo itemInfo)
        {
            try
            {
                ModLogger.Info($"Setting up prefab for {itemInfo.name}");
                
                // Ensure prefab has proper colliders for world interaction
                var collider = prefab.GetComponent<Collider>();
                if (collider == null)
                {
                    // Add a simple box collider if none exists
                    var boxCollider = prefab.AddComponent<BoxCollider>();
                    boxCollider.isTrigger = false;
                    ModLogger.Info($"Added BoxCollider to {itemInfo.name} prefab");
                }
                
                // Ensure prefab has a rigidbody for physics
                var rigidbody = prefab.GetComponent<Rigidbody>();
                if (rigidbody == null)
                {
                    rigidbody = prefab.AddComponent<Rigidbody>();
                    rigidbody.mass = 0.1f; // Light objects
                    rigidbody.drag = 1f;
                    rigidbody.angularDrag = 5f;
                    ModLogger.Info($"Added Rigidbody to {itemInfo.name} prefab");
                }
                
                // Add PrisonItemEquippable component for proper inventory display and holding
                try
                {
                    // Remove any existing equippable components first
                    var existingEquippable = prefab.GetComponent<Equippable>();
                    if (existingEquippable != null)
                    {
                        GameObject.DestroyImmediate(existingEquippable);
                    }
                    
                    // Add our custom PrisonItemEquippable component
                    var prisonEquippable = prefab.AddComponent<PrisonItemEquippable>();
                    ModLogger.Info($"Added PrisonItemEquippable component to {itemInfo.name}");
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"Could not add PrisonItemEquippable component to {itemInfo.name}: {ex.Message}");
                }
                
                // Set up layers and tags appropriately
                prefab.layer = LayerMask.NameToLayer("Default");
                prefab.tag = "Untagged";
                
                ModLogger.Info($"Prefab setup completed for {itemInfo.name}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error setting up prefab for {itemInfo.name}: {ex.Message}");
            }
        }
        
        
        /// <summary>
        /// Check if an item ID is a registered prison item
        /// </summary>
        public static bool IsPrisonItem(string itemId)
        {
            return PrisonItems.ContainsKey(itemId);
        }
        
        /// <summary>
        /// Get all registered prison item IDs
        /// </summary>
        public static IEnumerable<string> GetPrisonItemIds()
        {
            return PrisonItems.Keys;
        }
    }
    
    /// <summary>
    /// Information about a prison item for registration
    /// </summary>
    public class PrisonItemInfo
    {
        public string id;
        public string name;
        public string description;
        public EItemCategory category;
        public string iconResourcePath;
        public string prefabName;
    }
}