using Behind_Bars.Helpers;
using UnityEngine;

#if !MONO
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Framework;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.Dialogue;
using ScheduleOne.ItemFramework;
using ScheduleOne.Messaging;
using ScheduleOne.NPCs;
using ScheduleOne.NPCs.Framework;
#endif


namespace Behind_Bars.Utils
{
    /// <summary>
    /// Keeps NPC data mutations on the native data model as the game API evolves.
    /// </summary>
    internal static class NPCCompatibility
    {
        /// <summary>
        /// Gives a newly prepared native NPC its own framework data object before the object is
        /// activated. The data is built from the object's original data surface, not its runtime
        /// copy: the latter invokes the game's inventory deep-copy path before its null collections
        /// have been initialized on a fresh NPCDataObject.
        /// </summary>
        /// <param name="npc">The native-backed NPC whose data references should be initialized.</param>
        /// <returns><c>true</c> when the NPC receives non-null native NPC data;
        /// otherwise <c>false</c>.</returns>
        /// <remarks>Allocates and retains a native NPCDataObject, initializes
        /// its required collections/dialogue database, and mutates the NPC's
        /// private/runtime data surfaces. The helper does not unload or clean up
        /// the created object on failure.</remarks>
        internal static bool TryInitializeFreshData(NPC npc)
        {
            if (npc == null)
            {
                return false;
            }

            try
            {
                var dataObject = ScriptableObject.CreateInstance<NPCDataObject>();
                if (dataObject == null)
                {
                    ModLogger.Error("Cannot prepare native NPC data: NPCDataObject creation failed");
                    return false;
                }

                dataObject.hideFlags = HideFlags.DontUnloadUnusedAsset;
                dataObject.Initialize();
                var originalData = dataObject.GetOriginalData();
                if (originalData == null)
                {
                    ModLogger.Error("Cannot prepare native NPC data: NPCDataObject returned no original data");
                    return false;
                }

                PrepareConstructionData(originalData);

#if !MONO
                // The IL2CPP wrapper exposes both fields as native-backed surfaces. Assigning the
                // original data avoids NPCDataObject.GetRuntimeData(), which performs a deep copy.
                npc._npcData = dataObject;
                npc.NPCData = originalData;
#else
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic;
                var dataObjectField = typeof(NPC).GetField("_npcData", flags);
                var runtimeDataField = typeof(NPC).GetField("<NPCData>k__BackingField", flags);
                if (dataObjectField == null || runtimeDataField == null)
                {
                    ModLogger.Error("Cannot prepare native NPC data: required Mono backing fields were not found");
                    return false;
                }

                dataObjectField.SetValue(npc, dataObject);
                runtimeDataField.SetValue(npc, originalData);
#endif
                return npc.NPCData != null;
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Cannot prepare native NPC data for {npc.name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Establishes the collections a native NPCData deep-copy assumes are non-null. This stays
        /// intentionally small: jail NPCs are plain NPCs and do not need dealer or supplier data.
        /// </summary>
        /// <param name="data">The freshly created NPC data to prepare.</param>
        /// <remarks>Throws <see cref="System.InvalidOperationException"/> when
        /// required inventory, messaging, or dialogue data is absent. Runtime
        /// compilation branches allocate the appropriate managed or IL2CPP
        /// empty-array representations before resolving the dialogue database.</remarks>
        private static void PrepareConstructionData(NPCData data)
        {
            if (data == null)
            {
                throw new System.InvalidOperationException("The native NPCDataObject returned no data.");
            }

            if (data.Inventory == null)
            {
                throw new System.InvalidOperationException("The native NPCDataObject returned no inventory data.");
            }

            if (data.Messaging == null)
            {
                throw new System.InvalidOperationException("The native NPCDataObject returned no messaging data.");
            }

#if !MONO
            data.Inventory.RandomInventoryItems ??= new Il2CppReferenceArray<Inventory.WeightedItem>(0);
            data.Inventory.StartingInventoryItems ??= new Il2CppReferenceArray<ItemDefinition>(0);
            data.Messaging.ConversationCategories ??= new Il2CppStructArray<EConversationCategory>(0);
#else
            data.Inventory.RandomInventoryItems ??= System.Array.Empty<Inventory.WeightedItem>();
            data.Inventory.StartingInventoryItems ??= System.Array.Empty<ItemDefinition>();
            data.Messaging.ConversationCategories ??= System.Array.Empty<EConversationCategory>();
#endif

            EnsureDialogueDatabase(data);
        }

        /// <summary>
        /// DialogueHandler.Initialize requires a concrete dialogue database during NPC.Awake.
        /// Plain jail NPCs deliberately do not inherit the employee donor's employee-only database;
        /// use the game's default database and then a loaded native database as a final fallback.
        /// </summary>
        /// <param name="data">The NPC data whose dialogue database should be filled.</param>
        /// <remarks>Existing assignments are retained. Otherwise the fallback
        /// order is DialogueManager.DefaultDatabase, then the first loaded
        /// <see cref="DialogueDatabase"/> returned by Unity's resource search;
        /// an empty result throws <see cref="System.InvalidOperationException"/>.</remarks>
        private static void EnsureDialogueDatabase(NPCData data)
        {
            if (data.Dialogue == null)
            {
                throw new System.InvalidOperationException("The native NPCDataObject returned no dialogue data.");
            }

            if (data.Dialogue.DialogueDatabase != null)
            {
                return;
            }

            var dialogueManager = Singleton<DialogueManager>.Instance;
            if (dialogueManager != null)
            {
                data.Dialogue.DialogueDatabase = dialogueManager.DefaultDatabase;
            }

            if (data.Dialogue.DialogueDatabase == null)
            {
                var databases = Resources.FindObjectsOfTypeAll<DialogueDatabase>();
                foreach (var database in databases)
                {
                    if (database != null)
                    {
                        data.Dialogue.DialogueDatabase = database;
                        break;
                    }
                }
            }

            if (data.Dialogue.DialogueDatabase == null)
            {
                throw new System.InvalidOperationException("No native dialogue database is loaded for the jail NPC.");
            }
        }

        /// <summary>
        /// Writes identity fields to an NPC's native BasicInfo data.
        /// </summary>
        /// <param name="npc">The NPC to mutate.</param>
        /// <param name="firstName">The first name to assign.</param>
        /// <param name="lastName">The last name to assign; whitespace-only values clear the last-name flag.</param>
        /// <param name="id">The identifier to assign.</param>
        /// <returns><c>true</c> when native BasicInfo was available and values
        /// were assigned; otherwise <c>false</c>.</returns>
        /// <remarks>No trimming, normalization, uniqueness validation, or
        /// other input validation is performed.</remarks>
        internal static bool ConfigureIdentity(NPC npc, string firstName, string lastName, string id)
        {
            if (npc == null)
            {
                return false;
            }

            var basicInfo = npc.NPCData?.BasicInfo;
            if (basicInfo == null)
            {
                ModLogger.Error($"Cannot configure NPC identity for {npc.name}: native NPCData.BasicInfo is unavailable");
                return false;
            }

            basicInfo.FirstName = firstName;
            basicInfo.LastName = lastName;
            basicInfo.HasLastName = !string.IsNullOrWhiteSpace(lastName);
            basicInfo.ID = id;
            return true;
        }

        /// <summary>
        /// Writes maximum-health and invincibility settings to an NPC's native health data.
        /// </summary>
        /// <param name="npc">The NPC whose native data should be updated.</param>
        /// <param name="health">The health component associated with the NPC; it must be non-null.</param>
        /// <param name="maxHealth">The maximum health value to assign.</param>
        /// <param name="invincible">Whether the native health data should be invincible.</param>
        /// <returns><c>true</c> when native health data was available and values
        /// were assigned; otherwise <c>false</c>.</returns>
        /// <remarks>The <paramref name="health"/> parameter is only null-checked
        /// by this helper; the values are not range-validated and the supplied
        /// component is not otherwise mutated.</remarks>
        internal static bool ConfigureHealth(NPC npc, NPCHealth health, float maxHealth, bool invincible)
        {
            if (npc == null || health == null)
            {
                return false;
            }

            var healthData = npc.NPCData?.Health;
            if (healthData == null)
            {
                ModLogger.Error($"Cannot configure health for {npc.name}: native NPCData.Health is unavailable");
                return false;
            }

            healthData.MaxHealth = maxHealth;
            healthData.Invincible = invincible;
            return true;
        }
    }
}
