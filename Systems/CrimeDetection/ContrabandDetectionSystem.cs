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
            
            ModLogger.Info($"[CONTRABAND DEBUG] Starting contraband search for player: {player?.name}");
            
            if (player == null)
            {
                ModLogger.Error("[CONTRABAND DEBUG] Player is null!");
                return detectedCrimes;
            }
            
            if (player.Inventory == null)
            {
                ModLogger.Error("[CONTRABAND DEBUG] Player.Inventory is null!");
                return detectedCrimes;
            }
            
            ModLogger.Info($"[CONTRABAND DEBUG] Player: {player.name}, Inventory exists: {player.Inventory != null}");
            
            // Fallback: try to use the player.Inventory array directly since PlayerInventory component isn't accessible
            var inventory = player.Inventory;
            if (inventory != null && inventory.Length > 0)
            {
                ModLogger.Info($"[CONTRABAND DEBUG] Using Player.Inventory array with {inventory.Length} slots");
                var inventorySlotsFromArray = new List<ItemSlot>();
                foreach (var slot in inventory)
                {
                    if (slot != null) inventorySlotsFromArray.Add(slot);
                }
                return ProcessInventorySlots(inventorySlotsFromArray, player.transform.position, detectedCrimes);
            }
            else
            {
                ModLogger.Error("[CONTRABAND DEBUG] Player.Inventory array is null/empty!");
                return detectedCrimes;
            }
        }
        
        private List<CrimeInstance> ProcessInventorySlots(List<ItemSlot> inventorySlots, Vector3 playerPosition, List<CrimeInstance> detectedCrimes)
        {
            // Track drug quantities for trafficking detection
            int totalDrugQuantity = 0;
            var drugsByType = new Dictionary<ELegalStatus, int>();
            
            foreach (var slot in inventorySlots)
            {
                if (slot?.ItemInstance == null)
                {
                    ModLogger.Info($"[CONTRABAND DEBUG] Empty slot or null item instance");
                    continue;
                }
                    
                var itemInstance = slot.ItemInstance;
                ModLogger.Info($"[CONTRABAND DEBUG] Checking item: {itemInstance?.GetType().Name}");
                
                // Check if it's a product (drug)
                if (itemInstance is ProductItemInstance productInstance)
                {
                    var crimeInstance = ProcessProductItem(productInstance, playerPosition);
                    if (crimeInstance != null)
                    {
                        detectedCrimes.Add(crimeInstance);
                        
                        // Track for trafficking detection
                        var legalStatus = GetProductLegalStatus(productInstance);
                        if (legalStatus != ELegalStatus.Legal)
                        {
                            if (!drugsByType.ContainsKey(legalStatus))
                                drugsByType[legalStatus] = 0;
                            drugsByType[legalStatus] += productInstance.Amount;
                            totalDrugQuantity += productInstance.Amount;
                        }
                    }
                }
                // SPECIAL CASE: Check for WeedInstance specifically (it's not a ProductItemInstance)
                else if (itemInstance != null)
                {
                    string itemTypeName = itemInstance.GetType().Name;
                    ModLogger.Info($"[CONTRABAND DEBUG] Checking for drugs in item type: '{itemTypeName}'");
                    
                    // Check for various drug types by name
                    bool isWeed = itemTypeName.Contains("Weed") || itemTypeName.Equals("WeedInstance", StringComparison.OrdinalIgnoreCase);
                    bool isCocaine = itemTypeName.Contains("Cocaine") || itemTypeName.Contains("Coke");
                    bool isHeroin = itemTypeName.Contains("Heroin") || itemTypeName.Contains("Smack");
                    bool isMeth = itemTypeName.Contains("Meth") || itemTypeName.Contains("Crystal");
                    
                    if (isWeed || isCocaine || isHeroin || isMeth)
                    {
                        ModLogger.Info($"[CONTRABAND DEBUG] ✓ FOUND DRUG: {itemTypeName}");
                        
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
                        
                        ModLogger.Info($"[CONTRABAND DEBUG] Added drug possession charge for {itemTypeName}");
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
        private CrimeInstance ProcessProductItem(ProductItemInstance productInstance, Vector3 location)
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
                ModLogger.Info($"Detected {crime.CrimeName}: {productInstance.Definition.name} x{productInstance.Amount}");
                return new CrimeInstance(crime, location, severity);
            }
            
            return null;
        }
        
        /// <summary>
        /// Process a weapon item and determine if it's illegal
        /// </summary>
        private CrimeInstance ProcessWeaponItem(ItemInstance itemInstance, Vector3 location)
        {
            // In Schedule I, most weapons are likely illegal for civilians to carry
            // This is a simplified check - could be enhanced with weapon licensing system
            var crime = new WeaponPossession();
            float severity = 1.2f;
            
            ModLogger.Info($"Detected illegal weapon: {itemInstance.Definition.name}");
            return new CrimeInstance(crime, location, severity);
        }
        
        /// <summary>
        /// Get the legal status of a product
        /// </summary>
        private ELegalStatus GetProductLegalStatus(ProductItemInstance productInstance)
        {
            if (productInstance == null)
                return ELegalStatus.Legal;

            // First check the actual instance type - most reliable method
            ModLogger.Info($"[CONTRABAND DEBUG] Checking ProductItemInstance type: {productInstance.GetType().Name}");
            
            if (productInstance is WeedInstance weedInstance)
            {
                int amount = weedInstance.Amount;
                ModLogger.Info($"[CONTRABAND DEBUG] ✓ DETECTED WeedInstance as contraband! Amount: {amount}");
                
                // Determine severity based on amount
                if (amount >= 50)
                {
                    ModLogger.Info($"[CONTRABAND DEBUG] Large weed amount ({amount}) = HIGH SEVERITY (trafficking level)");
                    return ELegalStatus.HighSeverityDrug;
                }
                else if (amount >= 20)
                {
                    ModLogger.Info($"[CONTRABAND DEBUG] Moderate weed amount ({amount}) = MODERATE SEVERITY (dealing level)");
                    return ELegalStatus.ModerateSeverityDrug;
                }
                else
                {
                    ModLogger.Info($"[CONTRABAND DEBUG] Small weed amount ({amount}) = LOW SEVERITY (personal use)");
                    return ELegalStatus.LowSeverityDrug;
                }
            }
            
            // Check for other specific drug instance types with quantity consideration
            string instanceType = productInstance.GetType().Name;
            int drugAmount = productInstance.Amount;
            
            if (instanceType.Contains("Cocaine"))
            {
                ModLogger.Info($"[CONTRABAND DEBUG] ✓ DETECTED {instanceType} Amount: {drugAmount}");
                // Cocaine is always high severity, but amount affects trafficking charges later
                return drugAmount >= 10 ? ELegalStatus.HighSeverityDrug : ELegalStatus.ModerateSeverityDrug;
            }
            else if (instanceType.Contains("Heroin"))
            {
                ModLogger.Info($"[CONTRABAND DEBUG] ✓ DETECTED {instanceType} Amount: {drugAmount}");
                // Heroin is always high severity due to danger, regardless of amount
                return ELegalStatus.HighSeverityDrug;
            }
            else if (instanceType.Contains("Meth"))
            {
                ModLogger.Info($"[CONTRABAND DEBUG] ✓ DETECTED {instanceType} Amount: {drugAmount}");
                // Meth severity based on amount
                return drugAmount >= 15 ? ELegalStatus.HighSeverityDrug : ELegalStatus.ModerateSeverityDrug;
            }
                
            // Fallback: Check product definition name if instance type check fails
            if (productInstance.Definition is ProductDefinition productDef)
            {
                var productName = productDef.name.ToLower();
                ModLogger.Info($"[CONTRABAND DEBUG] Checking product definition name: '{productName}'");
                
                // Check for common drug names in definition
                if (productName.Contains("weed") || productName.Contains("cannabis") || productName.Contains("marijuana"))
                {
                    ModLogger.Info($"[CONTRABAND DEBUG] ✓ DETECTED weed by definition name!");
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
                ModLogger.Info($"[CONTRABAND DEBUG] No ProductDefinition found for {instanceType}");
            }
            
            ModLogger.Info($"[CONTRABAND DEBUG] {instanceType} determined to be legal");
            return ELegalStatus.Legal;
        }
        
        /// <summary>
        /// Check if an item is a weapon
        /// </summary>
        private bool IsWeapon(ItemInstance itemInstance)
        {
            if (itemInstance?.Definition == null)
                return false;
                
            var itemName = itemInstance.Definition.name.ToLower();
            
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
    }
}