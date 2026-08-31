using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MelonLoader;
using HarmonyLib;
using Behind_Bars.Helpers;
using BBHelpers = Behind_Bars.Helpers.Helpers;



#if !MONO
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime;
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
#if !MONO
        public PrisonItemEquippable(System.IntPtr ptr) : base(ptr) { }
#endif

        /// <summary>
        /// Equip the prison item through the base viewmodel and apply the fixed hand-space
        /// transform used by the custom prison-item prefab.
        /// </summary>
        public override void Equip(ItemInstance item)
        {
            base.Equip(item);
            
            // Set transform to show the item properly in the player's hand
            // These values are similar to CSEC's ModuleEquippable positioning
            base.transform.localPosition = new Vector3(0.2f, -0.15f, 0.25f);
            base.transform.localEulerAngles = new Vector3(0f, 45f, 0f);
            base.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            
            ModLogger.Info($"Prison item equipped: {PrisonItemRegistry.GetItemInstanceIdentifier(item)}");
        }
        
        /// <summary>
        /// Log the removal of the prison item and delegate viewmodel cleanup to the base class.
        /// </summary>
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
        // This is a one-time registration-attempt guard, not proof that every definition reached
        // the game's registry.  RegisterPrisonItems catches its own failures, after which callers
        // still set this flag; registration is also not synchronized across concurrent callers.
        private static bool itemsRegistered = false;
        
        // Prison item definitions
        private static readonly Dictionary<string, PrisonItemInfo> PrisonItems = new Dictionary<string, PrisonItemInfo>
        {
            ["behindbars.bedroll"] = new PrisonItemInfo
            {
                id = "behindbars.bedroll",
                name = "Prison Bed Roll",
                description = "A basic sleeping mat provided to inmates",
                categoryName = "Consumable",
                iconResourcePath = "Behind_Bars.Icons.behindbars.bedroll",
                prefabName = "BedRoll"
            },
            ["behindbars.sheetsnpillows"] = new PrisonItemInfo
            {
                id = "behindbars.sheetsnpillows", 
                name = "Prison Sheets & Pillow",
                description = "Basic bedding provided to inmates",
                categoryName = "Consumable",
                iconResourcePath = "Behind_Bars.Icons.behindbars.sheetsnpillows",
                prefabName = "PillowAndSheets"
            },
            ["behindbars.cup"] = new PrisonItemInfo
            {
                id = "behindbars.cup",
                name = "Prison Cup", 
                description = "Standard issue drinking cup for inmates",
                categoryName = "Consumable",
                iconResourcePath = "Behind_Bars.Icons.behindbars.cup",
                prefabName = "JailCup"
            },
            ["behindbars.toothbrush"] = new PrisonItemInfo
            {
                id = "behindbars.toothbrush",
                name = "Prison Toothbrush",
                description = "Basic hygiene item provided to inmates", 
                categoryName = "Consumable",
                iconResourcePath = "Behind_Bars.Icons.behindbars.toothbrush",
                prefabName = "JailToothBrush"
            }
        };
        
        /// <summary>
        /// Harmony prefix for the registry item lookup.  The requested ID is intentionally not
        /// changed; the prefix only makes a best-effort one-time registration attempt before the
        /// original lookup executes.
        /// </summary>
        public static void Prefix(Registry __instance, string ID)
        {
            if (!itemsRegistered)
            {
                RegisterPrisonItems(__instance);
                itemsRegistered = true;
            }
        }

        /// <summary>
        /// Attempt to register the prison definitions before a caller needs them.  A missing
        /// runtime registry or an exception escaping the setup leaves the guard unset so a later
        /// call may retry; the registration routine catches its own failures, so a partial/failed
        /// attempt may still set the guard when that routine returns.
        /// </summary>
        public static void EnsureRegistered()
        {
            if (itemsRegistered)
            {
                return;
            }

            try
            {
#if MONO
                var registry = ScheduleOne.Registry.Instance;
#else
                var registry = Il2CppScheduleOne.Registry.Instance;
#endif
                if (registry == null)
                {
                    ModLogger.Warn("PrisonItemRegistry: registry instance is null during EnsureRegistered");
                    return;
                }

                RegisterPrisonItems(registry);
                itemsRegistered = true;
                ModLogger.Info("PrisonItemRegistry: explicitly registered prison items before use");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PrisonItemRegistry: EnsureRegistered failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Build and add the configured prison definitions using the active runtime's item types.
        /// Asset-bundle prefabs and embedded icons are optional best-effort enrichments; reflection
        /// supplies compatibility fallbacks for definition members.  Icon/prefab failures are
        /// logged and skipped where possible, while an exception outside those inner guards can
        /// stop the remaining loop; a return does not guarantee every definition was registered.
        /// </summary>
        private static void RegisterPrisonItems(Registry registry)
        {
            try
            {
                ModLogger.Debug("Registering prison items with Schedule I item framework...");

                Core.EnsureJailBundleLoaded("prison item registration");
                
                foreach (var kvp in PrisonItems)
                {
                    var itemInfo = kvp.Value;

                    // Create item definition via the runtime-appropriate item framework type.
#if MONO
                    var itemDef = ScriptableObject.CreateInstance<BuildableItemDefinition>();
#else
                    // Use the generated wrapper constructor so reflection later observes the native
                    // ItemDefinition wrapper, rather than only its base ScriptableObject wrapper.
                    object itemDef = new BuildableItemDefinition();
#endif
                    if (itemDef == null)
                    {
                        ModLogger.Error($"Failed to create BuildableItemDefinition for {itemInfo.name}");
                        continue;
                    }
                    SetItemDefinitionValue(itemDef, "name", itemInfo.name);
                    SetItemDefinitionValue(itemDef, "ID", itemInfo.id);
                    SetItemDefinitionValue(itemDef, "Name", itemInfo.name);
                    SetItemDefinitionValue(itemDef, "Description", itemInfo.description);
                    AssignItemCategory(itemDef, itemInfo.categoryName);
                    
                    // Set as inventory-only item
                    SetItemDefinitionValue(itemDef, "StackLimit", 1);
                    SetItemDefinitionValue(itemDef, "BasePurchasePrice", 0f); // Free items
                    SetItemDefinitionValue(itemDef, "ResellMultiplier", 0f); // Cannot be sold
                    
                    // Load icon from embedded resources
                    try
                    {
                        var icon = LoadIconFromResources(itemInfo.iconResourcePath);
                        if (icon != null)
                        {
                            SetItemDefinitionValue(itemDef, "Icon", icon);
                            ModLogger.Debug($"✓ Loaded icon for {itemInfo.name}");
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
                        if (!string.IsNullOrEmpty(itemInfo.prefabName) && Behind_Bars.Core.CachedJailBundle != null)
                        {
                            GameObject prefab = null;
#if MONO
                            // Try multiple variations following JailDoor pattern
                            prefab = Behind_Bars.Core.CachedJailBundle.LoadAsset<GameObject>(itemInfo.prefabName) ??
                                    Behind_Bars.Core.CachedJailBundle.LoadAsset<GameObject>(itemInfo.prefabName.ToLower()) ??
                                    Behind_Bars.Core.CachedJailBundle.LoadAsset<GameObject>(GetFullAssetPath(itemInfo.prefabName));
#else
                            // Try multiple variations following JailDoor pattern  
                            prefab = Behind_Bars.Core.CachedJailBundle.LoadAsset(itemInfo.prefabName, Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                                    Behind_Bars.Core.CachedJailBundle.LoadAsset(itemInfo.prefabName.ToLower(), Il2CppType.Of<GameObject>())?.TryCast<GameObject>() ??
                                    Behind_Bars.Core.CachedJailBundle.LoadAsset(GetFullAssetPath(itemInfo.prefabName), Il2CppType.Of<GameObject>())?.TryCast<GameObject>();
#endif
                            if (prefab != null)
                            {
                                // Set up prefab for inventory/equipping
                                SetupItemPrefab(itemDef, prefab, itemInfo);
                                ModLogger.Debug($"✓ Loaded prefab for {itemInfo.name}");
                            }
                            else
                            {
                                ModLogger.Warn($"⚠ Could not load prefab {itemInfo.prefabName} for {itemInfo.name}");
                            }
                        }
                        else if (string.IsNullOrEmpty(itemInfo.prefabName))
                        {
                            ModLogger.Debug($"ℹ No prefab defined for {itemInfo.name} (icon-only item)");
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
                    var addToRegistryMethod = registry.GetType().GetMethod("AddToRegistry");
                    if (addToRegistryMethod != null)
                    {
                        addToRegistryMethod.Invoke(registry, new object[] { itemDef });
                    }
                    
                    ModLogger.Debug($"✓ Registered prison item: {itemInfo.name} ({itemInfo.id})");
                }
                
                ModLogger.Debug($"Successfully registered {PrisonItems.Count} prison items");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error registering prison items: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Convert GameObject name to full asset path for fallback loading
        /// </summary>
        private static string GetFullAssetPath(string gameObjectName)
        {
            // Convert GameObject names to their corresponding asset paths
            return gameObjectName switch
            {
                "BedRoll" => "assets/behindbars/bedroll.prefab",
                "PillowAndSheets" => "assets/behindbars/pillowandsheets.prefab", 
                "JailCup" => "assets/behindbars/jailcup.prefab",
                "JailToothBrush" => "assets/behindbars/jailtoothbrush.prefab",
                _ => $"assets/behindbars/{gameObjectName.ToLower()}.prefab"
            };
        }
        
        /// <summary>
        /// Derive a repeatable GUID from an item identifier using an MD5 digest.  The current
        /// registration path computes this value for diagnostics/compatibility but does not pass it
        /// to <c>AddToRegistry</c>, so it is not currently the registry's authoritative identity.
        /// </summary>
        private static Guid GenerateDeterministicGuid(string input)
        {
            using (var provider = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = provider.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                return new Guid(hash);
            }
        }
        
        /// <summary>
        /// Load a PNG manifest resource and convert it to a Unity sprite.  Missing resources,
        /// invalid image data, and exceptions return <c>null</c> after logging so icon failure does
        /// not prevent the rest of item registration.
        /// </summary>
        private static Sprite LoadIconFromResources(string resourcePath)
        {
            try
            {
                ModLogger.Debug($"Loading icon from embedded resources: {resourcePath}");
                
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
                            
                            ModLogger.Debug($"Successfully loaded icon sprite from {resourcePath}");
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
        
        /// <summary>
        /// Mutate an item prefab for world interaction and viewmodel use.  Missing collider and
        /// rigidbody components are added, any existing generic equippable is removed before the
        /// custom component is attempted, and setup errors are logged without escaping.
        /// </summary>
        private static void SetupItemPrefab(object itemDef, GameObject prefab, PrisonItemInfo itemInfo)
        {
            try
            {
                ModLogger.Debug($"Setting up prefab for {itemInfo.name}");
                
                // Ensure prefab has proper colliders for world interaction
                var collider = prefab.GetComponent<Collider>();
                if (collider == null)
                {
                    // Add a simple box collider if none exists
                    var boxCollider = prefab.AddComponent<BoxCollider>();
                    boxCollider.isTrigger = false;
                    ModLogger.Debug($"Added BoxCollider to {itemInfo.name} prefab");
                }
                
                // Ensure prefab has a rigidbody for physics
                var rigidbody = prefab.GetComponent<Rigidbody>();
                if (rigidbody == null)
                {
                    rigidbody = prefab.AddComponent<Rigidbody>();
                    rigidbody.mass = 0.1f; // Light objects
                    rigidbody.drag = 1f;
                    rigidbody.angularDrag = 5f;
                    ModLogger.Debug($"Added Rigidbody to {itemInfo.name} prefab");
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
                    var prisonEquippable = BBHelpers.AddComponentSafe<PrisonItemEquippable>(prefab);
                    ModLogger.Debug($"Added PrisonItemEquippable component to {itemInfo.name}");
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"Could not add PrisonItemEquippable component to {itemInfo.name}: {ex.Message}");
                }
                
                // Set up layers and tags appropriately
                prefab.layer = LayerMask.NameToLayer("Default");
                prefab.tag = "Untagged";
                
                ModLogger.Debug($"Prefab setup completed for {itemInfo.name}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Error setting up prefab for {itemInfo.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Best-effort assign an item category through a reflected property or field.  Missing,
        /// read-only, invalid, or otherwise incompatible category members only produce diagnostics;
        /// they do not abort registration of the remaining definition.
        /// </summary>
        private static void AssignItemCategory(object itemDef, string categoryName)
        {
            try
            {
                if (itemDef == null)
                {
                    return;
                }

                var itemDefType = itemDef.GetType();
                var categoryProperty = itemDefType.GetProperty("Category");
                var categoryField = itemDefType.GetField("Category");
                Type categoryType = categoryProperty?.PropertyType ?? categoryField?.FieldType;

                if (categoryType == null)
                {
                    ModLogger.Warn($"Could not find Category field or property on prison item definition {GetItemDefinitionLabel(itemDef)}");
                    return;
                }

                var categoryValue = Enum.Parse(categoryType, categoryName, ignoreCase: true);

                if (categoryProperty != null && categoryProperty.CanWrite)
                {
                    categoryProperty.SetValue(itemDef, categoryValue);
                    return;
                }

                if (categoryField != null)
                {
                    categoryField.SetValue(itemDef, categoryValue);
                    return;
                }

                ModLogger.Warn($"Category member exists but is not writable on prison item definition {GetItemDefinitionLabel(itemDef)}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"Failed to assign category '{categoryName}' to prison item definition {GetItemDefinitionLabel(itemDef)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Set a definition member through a writable property first, then a field.  If neither
        /// member exists or the object is null, this compatibility helper silently leaves the
        /// definition unchanged.
        /// </summary>
        private static void SetItemDefinitionValue(object itemDef, string memberName, object value)
        {
            if (itemDef == null)
            {
                return;
            }

            var type = itemDef.GetType();
            var property = type.GetProperty(memberName);
            if (property != null && property.CanWrite)
            {
                property.SetValue(itemDef, value);
                return;
            }

            var field = type.GetField(memberName);
            if (field != null)
            {
                field.SetValue(itemDef, value);
            }
        }

        /// <summary>
        /// Produce a diagnostic label from a reflected <c>Name</c>/<c>name</c> member, falling back
        /// to the runtime type name or <c>null</c> for an absent definition.
        /// </summary>
        private static string GetItemDefinitionLabel(object itemDef)
        {
            if (itemDef == null)
            {
                return "null";
            }

            var type = itemDef.GetType();
            var nameProperty = type.GetProperty("Name") ?? type.GetProperty("name");
            var nameValue = nameProperty?.GetValue(itemDef)?.ToString();
            return string.IsNullOrWhiteSpace(nameValue) ? type.Name : nameValue;
        }

        /// <summary>
        /// Resolve an item definition through a non-generic public <c>GetItem(string)</c> method.
        /// Reflection deliberately keeps Mono/IL2CPP wrapper types out of the exposed signature;
        /// invalid input, missing methods, and invocation failures return <c>null</c>.
        /// </summary>
        public static object GetRegistryItemDefinition(object registry, string itemId)
        {
            if (registry == null || string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            try
            {
                var getItemMethod = registry.GetType()
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "GetItem" || method.IsGenericMethodDefinition)
                        {
                            return false;
                        }

                        var parameters = method.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
                    });

                if (getItemMethod == null)
                {
                    ModLogger.Warn($"PrisonItemRegistry: Could not resolve non-generic GetItem(string) on registry type {registry.GetType().FullName}");
                    return null;
                }

                return getItemMethod.Invoke(registry, new object[] { itemId });
            }
            catch (Exception ex)
            {
                ModLogger.Error($"PrisonItemRegistry: Failed to resolve item definition for '{itemId}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Return the most useful stable identifier available on an item instance: reflected ID,
        /// then reflected Name, then the runtime type name.  A null instance is reported as
        /// <c>"null"</c> for diagnostics.
        /// </summary>
        public static string GetItemInstanceIdentifier(object itemInstance)
        {
            if (itemInstance == null)
            {
                return "null";
            }

            var type = itemInstance.GetType();
            var idProperty = type.GetProperty("ID");
            if (idProperty != null)
            {
                var idValue = idProperty.GetValue(itemInstance)?.ToString();
                if (!string.IsNullOrWhiteSpace(idValue))
                {
                    return idValue;
                }
            }

            var nameProperty = type.GetProperty("Name");
            if (nameProperty != null)
            {
                var nameValue = nameProperty.GetValue(itemInstance)?.ToString();
                if (!string.IsNullOrWhiteSpace(nameValue))
                {
                    return nameValue;
                }
            }

            return type.Name;
        }
        
        
        /// <summary>
        /// Returns whether the exact, case-sensitive item ID exists in the static prison-item definition dictionary.
        /// The current dictionary lookup expects a non-null key; this method does not normalize or resolve aliases.
        /// </summary>
        public static bool IsPrisonItem(string itemId)
        {
            return PrisonItems.ContainsKey(itemId);
        }
        
        /// <summary>
        /// Returns the live dictionary-key view for all registered prison item IDs, not a snapshot. Enumeration reflects later
        /// registry mutations and preserves the dictionary's key comparer/order behavior.
        /// </summary>
        public static IEnumerable<string> GetPrisonItemIds()
        {
            return PrisonItems.Keys;
        }

        /// <summary>
        /// Returns the configured display name for an exact, case-sensitive prison item ID, or the original ID when the ID
        /// is unknown, blank, or has no configured name. A null ID is normalized to an empty string for the return value.
        /// </summary>
        public static string GetPrisonItemDisplayName(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return itemId ?? string.Empty;
            }

            return PrisonItems.TryGetValue(itemId, out var itemInfo) && !string.IsNullOrWhiteSpace(itemInfo.name)
                ? itemInfo.name
                : itemId;
        }
    }
    
    /// <summary>
    /// Information about a prison item for registration
    /// </summary>
    public class PrisonItemInfo
    {
        // These fields are the registration recipe: the framework ID/display metadata, optional
        // embedded icon resource, and optional asset-bundle prefab name.  They are mutable because
        // the definitions are initialized as object literals and consumed by reflection later.
        public string id;
        public string name;
        public string description;
        public string categoryName;
        public string iconResourcePath;
        public string prefabName;
    }
}
