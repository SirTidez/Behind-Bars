using System.Collections.Generic;
using UnityEngine;
using Behind_Bars.Helpers;
using Behind_Bars.Systems.Crimes;
using Behind_Bars.Systems.CrimeTracking;
using System;

#if !MONO
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne.PlayerScripts;
using ScheduleOne.ItemFramework;
using ScheduleOne.Product;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Law;
using ScheduleOne.UI;
#endif

namespace Behind_Bars.Systems.CrimeDetection
{
    /// <summary>
    /// Legal status classification for items
    /// </summary>
    public enum ELegalStatus
    {
        Legal,
        ControlledSubstance,
        LowSeverityDrug,
        ModerateSeverityDrug,
        HighSeverityDrug,
        IllegalWeapon
    }

    /// <summary>
    /// Defines why contraband is being searched. Weapons are a parole-condition
    /// violation, not a general possession crime for players who are not on parole.
    /// </summary>
    public enum ContrabandSearchContext
    {
        Arrest,
        Parole
    }

    /// <summary>
    /// System for detecting contraband items in player inventory during searches
    /// </summary>
    public class ContrabandDetectionSystem
    {
        private CrimeDetectionSystem _crimeDetectionSystem;
        
        public ContrabandDetectionSystem(CrimeDetectionSystem crimeDetectionSystem)
        {
            _crimeDetectionSystem = crimeDetectionSystem;
            ModLogger.Info("Contraband detection system initialized");
        }
        
        /// <summary>
        /// Perform a contraband search on a player and detect illegal items
        /// </summary>
        public List<CrimeInstance> PerformContrabandSearch(
            Player player,
            ContrabandSearchContext context = ContrabandSearchContext.Arrest)
        {
            var detectedCrimes = new List<CrimeInstance>();
            
            ModLogger.Debug($"Starting contraband search for player: {player?.name}");
            
            if (player == null)
            {
                ModLogger.Debug("Player is null!");
                return detectedCrimes;
            }
            
            var inventory = player._inventory;
            if (inventory == null)
            {
                ModLogger.Debug("Player inventory is null!");
                return detectedCrimes;
            }

            ModLogger.Debug($"Player: {player.name}, Inventory exists: {inventory != null}");

            // Use the native player inventory collection for this runtime.
            if (inventory != null && inventory.Length > 0)
            {
                ModLogger.Debug($"Using Player.Inventory array with {inventory.Length} slots");
                var inventorySlotsFromArray = new List<object>();
                foreach (var slot in inventory)
                {
                    if (slot != null) inventorySlotsFromArray.Add(slot);
                }
                return ProcessInventorySlots(inventorySlotsFromArray, player.transform.position, detectedCrimes, context);
            }
            else
            {
                ModLogger.Debug("Player.Inventory array is null/empty!");
                return detectedCrimes;
            }
        }
        
        private List<CrimeInstance> ProcessInventorySlots(
            List<object> inventorySlots,
            Vector3 playerPosition,
            List<CrimeInstance> detectedCrimes,
            ContrabandSearchContext context)
        {
            // Track drug quantities for trafficking detection
            int totalDrugQuantity = 0;
            var drugsByType = new Dictionary<ELegalStatus, int>();
            
            foreach (var slot in inventorySlots)
            {
                var itemInstance = GetSlotItemInstance(slot);
                if (itemInstance == null)
                {
                    ModLogger.Debug("Empty slot or null item instance");
                    continue;
                }

                ModLogger.Debug($"Checking item: {itemInstance?.GetType().Name}");
                
                // Weapon possession is enforced only as a parole condition. Do this before
                // the permissive product-like fallback, which accepts several native item
                // shapes and would otherwise consume the weapon path.
                if (context == ContrabandSearchContext.Parole && IsWeapon(itemInstance))
                {
                    var crimeInstance = ProcessWeaponItem(itemInstance, playerPosition);
                    if (crimeInstance != null)
                    {
                        detectedCrimes.Add(crimeInstance);
                    }

                    continue;
                }

                // Check if it's a product (drug)
                if (IsProductLikeItem(itemInstance))
                {
                    var crimeInstance = ProcessProductItem(itemInstance, playerPosition);
                    if (crimeInstance != null)
                    {
                        detectedCrimes.Add(crimeInstance);
                        
                        // Track for trafficking detection
                        var legalStatus = GetProductLegalStatus(itemInstance);
                        if (legalStatus != ELegalStatus.Legal)
                        {
                            if (!drugsByType.ContainsKey(legalStatus))
                                drugsByType[legalStatus] = 0;
                            var amount = GetItemAmount(itemInstance);
                            drugsByType[legalStatus] += amount;
                            totalDrugQuantity += amount;
                        }
                    }
                }
                // SPECIAL CASE: Check for WeedInstance specifically (it's not a ProductItemInstance)
                else if (itemInstance != null)
                {
                    string itemTypeName = itemInstance.GetType().Name;
                    ModLogger.Debug($"Checking for drugs in item type: '{itemTypeName}'");
                    
                    // Check for various drug types by name
                    bool isWeed = itemTypeName.Contains("Weed") || itemTypeName.Equals("WeedInstance", StringComparison.OrdinalIgnoreCase);
                    bool isCocaine = itemTypeName.Contains("Cocaine") || itemTypeName.Contains("Coke");
                    bool isHeroin = itemTypeName.Contains("Heroin") || itemTypeName.Contains("Smack");
                    bool isMeth = itemTypeName.Contains("Meth") || itemTypeName.Contains("Crystal");
                    
                    if (isWeed || isCocaine || isHeroin || isMeth)
                    {
                        ModLogger.Debug($"✓ FOUND DRUG: {itemTypeName}");
                        
                        // Determine drug severity
                        Crime drugCrime;
                        if (isWeed)
                            drugCrime = new DrugPossessionLow();
                        else if (isCocaine || isMeth)
                            drugCrime = new DrugPossessionModerate();
                        else
                            drugCrime = new DrugPossessionHigh(); // Heroin
                        
                        var drugCrimeInstance = new CrimeInstance(drugCrime, playerPosition, 2.0f);
                        detectedCrimes.Add(drugCrimeInstance);
                        
                        // Track as appropriate severity drug
                        var drugLevel = isWeed ? ELegalStatus.LowSeverityDrug :
                                       isCocaine || isMeth ? ELegalStatus.ModerateSeverityDrug :
                                       ELegalStatus.HighSeverityDrug;
                                       
                        if (!drugsByType.ContainsKey(drugLevel))
                            drugsByType[drugLevel] = 0;
                        drugsByType[drugLevel] += 1;
                        totalDrugQuantity += 1;
                        
                        ModLogger.Debug($"Added drug possession charge for {itemTypeName}");
                        continue; // Move to next item
                    }
                }
            }
            
            // Check for drug trafficking (large quantities suggest dealing)
            if (totalDrugQuantity >= 20) // Threshold for trafficking
            {
                var traffickingCrime = new DrugTraffickingCrime();
                var traffickingInstance = new CrimeInstance(traffickingCrime, playerPosition, 3.0f);
                detectedCrimes.Add(traffickingInstance);
                ModLogger.Info($"Drug trafficking detected: {totalDrugQuantity} total drug units");
            }
            
            if (detectedCrimes.Count > 0)
            {
                ModLogger.Info($"Contraband search found {detectedCrimes.Count} crimes");
            }
            else
            {
                ModLogger.Info($"Contraband search completed - no illegal items found");
            }
            
            return detectedCrimes;
        }
        
        /// <summary>
        /// Process a product item and determine if it's contraband
        /// </summary>
        private CrimeInstance ProcessProductItem(object productInstance, Vector3 location)
        {
            var legalStatus = GetProductLegalStatus(productInstance);
            
            Crime crime = null;
            float severity = 1.0f;
            
            switch (legalStatus)
            {
                case ELegalStatus.LowSeverityDrug:
                    crime = new DrugPossessionLow();
                    severity = 1.0f;
                    break;
                case ELegalStatus.ModerateSeverityDrug:
                    crime = new DrugPossessionModerate();
                    severity = 1.5f;
                    break;
                case ELegalStatus.HighSeverityDrug:
                    crime = new DrugPossessionHigh();
                    severity = 2.0f;
                    break;
                case ELegalStatus.ControlledSubstance:
                    crime = new DrugPossessionLow(); // Treat controlled substances as low severity
                    severity = 0.8f;
                    break;
            }
            
            if (crime != null)
            {
                ModLogger.Info($"Detected {crime.CrimeName}: {GetItemDefinitionName(productInstance)} x{GetItemAmount(productInstance)}");
                return new CrimeInstance(crime, location, severity);
            }
            
            return null;
        }
        
        /// <summary>
        /// Process a weapon item and determine if it's illegal
        /// </summary>
        private CrimeInstance ProcessWeaponItem(object itemInstance, Vector3 location)
        {
            // In Schedule I, most weapons are likely illegal for civilians to carry
            // This is a simplified check - could be enhanced with weapon licensing system
            var crime = new WeaponPossession();
            float severity = 1.2f;
            
            ModLogger.Info($"Detected illegal weapon: {GetItemDefinitionName(itemInstance)}");
            return new CrimeInstance(crime, location, severity);
        }
        
        /// <summary>
        /// Get the legal status of a product
        /// </summary>
        private ELegalStatus GetProductLegalStatus(object productInstance)
        {
            if (productInstance == null)
                return ELegalStatus.Legal;

            // First check the actual instance type - most reliable method
            string instanceType = productInstance.GetType().Name;
            ModLogger.Debug($"Checking ProductItemInstance type: {instanceType}");

            int drugAmount = GetItemAmount(productInstance);

            if (IsWeedLikeItem(productInstance))
            {
                ModLogger.Debug($"✓ DETECTED WeedInstance as contraband! Amount: {drugAmount}");
                
                // Determine severity based on amount
                if (drugAmount >= 50)
                {
                    ModLogger.Debug($"Large weed amount ({drugAmount}) = HIGH SEVERITY (trafficking level)");
                    return ELegalStatus.HighSeverityDrug;
                }
                else if (drugAmount >= 20)
                {
                    ModLogger.Debug($"Moderate weed amount ({drugAmount}) = MODERATE SEVERITY (dealing level)");
                    return ELegalStatus.ModerateSeverityDrug;
                }
                else
                {
                    ModLogger.Debug($"Small weed amount ({drugAmount}) = LOW SEVERITY (personal use)");
                    return ELegalStatus.LowSeverityDrug;
                }
            }

            // Check for other specific drug instance types with quantity consideration
            if (instanceType.Contains("Cocaine"))
            {
                ModLogger.Debug($"✓ DETECTED {instanceType} Amount: {drugAmount}");
                // Cocaine is always high severity, but amount affects trafficking charges later
                return drugAmount >= 10 ? ELegalStatus.HighSeverityDrug : ELegalStatus.ModerateSeverityDrug;
            }
            else if (instanceType.Contains("Heroin"))
            {
                ModLogger.Debug($"✓ DETECTED {instanceType} Amount: {drugAmount}");
                // Heroin is always high severity due to danger, regardless of amount
                return ELegalStatus.HighSeverityDrug;
            }
            else if (instanceType.Contains("Meth"))
            {
                ModLogger.Debug($"✓ DETECTED {instanceType} Amount: {drugAmount}");
                // Meth severity based on amount
                return drugAmount >= 15 ? ELegalStatus.HighSeverityDrug : ELegalStatus.ModerateSeverityDrug;
            }
                
            // Fallback: Check product definition name if instance type check fails
            var productDefinition = GetItemDefinitionObject(productInstance);
            if (productDefinition != null)
            {
                var productName = GetDefinitionName(productDefinition).ToLower();
                ModLogger.Debug($"Checking product definition name: '{productName}'");
                
                // Check for common drug names in definition
                if (productName.Contains("weed") || productName.Contains("cannabis") || productName.Contains("marijuana"))
                {
                    ModLogger.Debug($"✓ DETECTED weed by definition name!");
                    return ELegalStatus.LowSeverityDrug;
                }
                else if (productName.Contains("cocaine") || productName.Contains("coke"))
                {
                    return ELegalStatus.HighSeverityDrug;
                }
                else if (productName.Contains("meth") || productName.Contains("crystal"))
                {
                    return ELegalStatus.HighSeverityDrug;
                }
                else if (productName.Contains("pill") || productName.Contains("pharmaceutical"))
                {
                    return ELegalStatus.ModerateSeverityDrug;
                }
            }
            else
            {
                ModLogger.Debug($"No ProductDefinition found for {instanceType}");
            }
            
            ModLogger.Debug($"{instanceType} determined to be legal");
            return ELegalStatus.Legal;
        }
        
        /// <summary>
        /// Check if an item is a weapon
        /// </summary>
        private bool IsWeapon(object itemInstance)
        {
            if (itemInstance == null)
            {
                return false;
            }

            // Depending on the native item shape, the usable identifier can live on
            // the instance type, its definition, or a direct Name field/property.
            // Search all three without treating every IntegerItemInstance as a weapon;
            // that would incorrectly classify unrelated stackable items.
            var itemTypeName = itemInstance.GetType().Name;
            var definitionName = GetItemDefinitionName(itemInstance);
            var directName = GetDirectItemName(itemInstance);
            var identity = $"{itemTypeName} {definitionName} {directName}";
            bool isWeapon = identity.IndexOf("gun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("pistol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("rifle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("shotgun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("m1911", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("handgun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("revolver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("firearm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("knife", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("blade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("machete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("weapon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("taser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("baton", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("nightstick", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isWeapon)
            {
                ModLogger.Info($"[PAROLE SEARCH] Weapon identity matched: type='{itemTypeName}', definition='{definitionName}', direct='{directName}'");
            }

            return isWeapon;
        }
        
        /// <summary>
        /// Add detected contraband crimes to the crime detection system
        /// </summary>
        public void ProcessContrabandCrimes(List<CrimeInstance> contrabandCrimes, Player player)
        {
            foreach (var crimeInstance in contrabandCrimes)
            {
                // Add to our cumulative crime record
                _crimeDetectionSystem.CrimeRecord.AddCrime(crimeInstance);
                
                // Add to Schedule I's native crime system for immediate police response
                if (player.IsOwner)
                {
                    player.CrimeData.AddCrime(crimeInstance.Crime);
                    
                    // Set appropriate pursuit level based on severity
                    if (crimeInstance.Severity >= 2.0f) // High severity drugs
                    {
                        if (player.CrimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                        {
                            player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Arresting);
                        }
                        else
                        {
                            player.CrimeData.Escalate();
                        }
                    }
                    else if (player.CrimeData.CurrentPursuitLevel == PlayerCrimeData.EPursuitLevel.None)
                    {
                        player.CrimeData.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.Investigating);
                    }
                }
            }
            
            ModLogger.Info($"Processed {contrabandCrimes.Count} contraband crimes for {player.name}");
        }

        private static object GetItemDefinitionObject(object itemInstance)
        {
            if (itemInstance == null)
            {
                return null;
            }

            var instanceType = itemInstance.GetType();
            var definitionProperty = instanceType.GetProperty("Definition");
            if (definitionProperty != null)
            {
                try
                {
                    return definitionProperty.GetValue(itemInstance);
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Failed to read item definition from {itemInstance.GetType().Name}: {ex.Message}");
                }
            }

            var definitionField = instanceType.GetField("Definition");
            if (definitionField != null)
            {
                try
                {
                    return definitionField.GetValue(itemInstance);
                }
                catch (Exception ex)
                {
                    ModLogger.Debug($"Failed to read item definition field from {instanceType.Name}: {ex.Message}");
                }
            }

            return null;
        }

        private static string GetItemDefinitionName(object itemInstance)
        {
            return GetDefinitionName(GetItemDefinitionObject(itemInstance));
        }

        private static string GetDirectItemName(object itemInstance)
        {
            if (itemInstance == null)
            {
                return string.Empty;
            }

            try
            {
                var instanceType = itemInstance.GetType();
                var nameProperty = instanceType.GetProperty("Name") ?? instanceType.GetProperty("name") ??
                                   instanceType.GetProperty("ID") ?? instanceType.GetProperty("id");
                string propertyValue = nameProperty?.GetValue(itemInstance)?.ToString();
                if (!string.IsNullOrWhiteSpace(propertyValue))
                {
                    return propertyValue;
                }

                var nameField = instanceType.GetField("Name") ?? instanceType.GetField("name") ??
                                instanceType.GetField("ID") ?? instanceType.GetField("id");
                string fieldValue = nameField?.GetValue(itemInstance)?.ToString();
                if (!string.IsNullOrWhiteSpace(fieldValue))
                {
                    return fieldValue;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Failed to read direct item name from {itemInstance.GetType().Name}: {ex.Message}");
            }

            return string.Empty;
        }

        private static int GetItemAmount(object itemInstance)
        {
            if (itemInstance == null)
            {
                return 0;
            }

            try
            {
                var amountProperty = itemInstance.GetType().GetProperty("Amount");
                if (amountProperty != null)
                {
                    var value = amountProperty.GetValue(itemInstance);
                    if (value is int amount)
                    {
                        return amount;
                    }
                }

                var amountField = itemInstance.GetType().GetField("Amount");
                if (amountField != null)
                {
                    var value = amountField.GetValue(itemInstance);
                    if (value is int amount)
                    {
                        return amount;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Failed to read item amount from {itemInstance.GetType().Name}: {ex.Message}");
            }

            return 1;
        }

        private static string GetDefinitionName(object definition)
        {
            if (definition == null)
            {
                return "Unknown";
            }

            var definitionType = definition.GetType();

            try
            {
                var nameProperty = definitionType.GetProperty("name") ?? definitionType.GetProperty("Name") ??
                                   definitionType.GetProperty("ID") ?? definitionType.GetProperty("id");
                if (nameProperty != null)
                {
                    var value = nameProperty.GetValue(definition)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                var nameField = definitionType.GetField("name") ?? definitionType.GetField("Name") ??
                                definitionType.GetField("ID") ?? definitionType.GetField("id");
                if (nameField != null)
                {
                    var value = nameField.GetValue(definition)?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Failed to read definition name from {definitionType.Name}: {ex.Message}");
            }

            return definitionType.Name;
        }

        private static object GetSlotItemInstance(object slot)
        {
            if (slot == null)
            {
                return null;
            }

            try
            {
                var property = slot.GetType().GetProperty("ItemInstance");
                return property?.GetValue(slot);
            }
            catch (Exception ex)
            {
                ModLogger.Debug($"Failed to read ItemInstance from {slot.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static bool IsProductLikeItem(object itemInstance)
        {
            if (itemInstance == null)
            {
                return false;
            }

            var typeName = itemInstance.GetType().Name;
            return typeName.Contains("Product", StringComparison.OrdinalIgnoreCase) || HasProperty(itemInstance, "Amount");
        }

        private static bool IsWeedLikeItem(object itemInstance)
        {
            if (itemInstance == null)
            {
                return false;
            }

            var typeName = itemInstance.GetType().Name;
            return typeName.Contains("Weed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return false;
            }

            return instance.GetType().GetProperty(propertyName) != null;
        }

    }
}
