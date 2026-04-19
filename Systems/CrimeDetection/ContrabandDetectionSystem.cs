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
        public List<CrimeInstance> PerformContrabandSearch(Player player)
        {
            var detectedCrimes = new List<CrimeInstance>();
            
            ModLogger.Debug($"Starting contraband search for player: {player?.name}");
            
            if (player == null)
            {
                ModLogger.Debug("Player is null!");
                return detectedCrimes;
            }
            
            if (player.Inventory == null)
            {
                ModLogger.Debug("Player.Inventory is null!");
                return detectedCrimes;
            }
            
            ModLogger.Debug($"Player: {player.name}, Inventory exists: {player.Inventory != null}");
            
            // Fallback: try to use the player.Inventory array directly since PlayerInventory component isn't accessible
            var inventory = player.Inventory;
            if (inventory != null && inventory.Length > 0)
            {
                ModLogger.Debug($"Using Player.Inventory array with {inventory.Length} slots");
                var inventorySlotsFromArray = new List<object>();
                foreach (var slot in inventory)
                {
                    if (slot != null) inventorySlotsFromArray.Add(slot);
                }
                return ProcessInventorySlots(inventorySlotsFromArray, player.transform.position, detectedCrimes);
            }
            else
            {
                ModLogger.Debug("Player.Inventory array is null/empty!");
                return detectedCrimes;
            }
        }
        
        private List<CrimeInstance> ProcessInventorySlots(List<object> inventorySlots, Vector3 playerPosition, List<CrimeInstance> detectedCrimes)
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
                // Check if it's a weapon
                else if (IsWeapon(itemInstance))
                {
                    var crimeInstance = ProcessWeaponItem(itemInstance, playerPosition);
                    if (crimeInstance != null)
                    {
                        detectedCrimes.Add(crimeInstance);
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
            var itemDefinition = GetItemDefinitionObject(itemInstance);
            if (itemDefinition == null)
                return false;
                
            var itemName = GetDefinitionName(itemDefinition).ToLower();
            
            // Check for weapon keywords
            return itemName.Contains("gun") || 
                   itemName.Contains("pistol") || 
                   itemName.Contains("rifle") || 
                   itemName.Contains("shotgun") || 
                   itemName.Contains("knife") || 
                   itemName.Contains("blade") || 
                   itemName.Contains("weapon") ||
                   itemName.Contains("taser") ||
                   itemName.Contains("baton");
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

            var definitionProperty = itemInstance.GetType().GetProperty("Definition");
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

            return null;
        }

        private static string GetItemDefinitionName(object itemInstance)
        {
            return GetDefinitionName(GetItemDefinitionObject(itemInstance));
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
                var nameProperty = definitionType.GetProperty("name") ?? definitionType.GetProperty("Name");
                if (nameProperty != null)
                {
                    var value = nameProperty.GetValue(definition) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                var nameField = definitionType.GetField("name") ?? definitionType.GetField("Name");
                if (nameField != null)
                {
                    var value = nameField.GetValue(definition) as string;
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
